using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SilkroadAtlas.Core;

public sealed class RobotsPolicy
{
    private readonly List<(string pattern,bool allow)> rules=[];
    public double DelaySeconds { get; private set; }=1.2;
    public static RobotsPolicy Parse(string text)
    {
        var groups=new List<(List<string> agents,List<(string,bool)> rules,double delay)>();
        List<string> agents=[];List<(string,bool)> current=[];double delay=1.2;bool hadRule=false;
        void Flush(){if(agents.Count>0)groups.Add((agents,current,delay));agents=[];current=[];delay=1.2;hadRule=false;}
        foreach(var line in text.Split('\n'))
        {
            var clean=line.Split('#')[0].Trim();int p=clean.IndexOf(':');if(p<0) continue;
            var key=clean[..p].Trim().ToLowerInvariant();var value=clean[(p+1)..].Trim();
            if(key=="user-agent"){if(hadRule)Flush();agents.Add(value.ToLowerInvariant());}
            else if(key is "allow" or "disallow"){hadRule=true;if(value.Length>0)current.Add((value,key=="allow"));}
            else if(key=="crawl-delay"){hadRule=true;if(double.TryParse(value,System.Globalization.CultureInfo.InvariantCulture,out var d)) delay=Math.Clamp(d,1.2,120);}
        }
        Flush();var policy=new RobotsPolicy();
        var exact=groups.Where(g=>g.agents.Any(a=>a!="*" && "silkroadatlas".Contains(a))).ToList();
        var chosen=exact.Count>0?exact:groups.Where(g=>g.agents.Contains("*")).ToList();
        foreach(var group in chosen){policy.rules.AddRange(group.rules);policy.DelaySeconds=Math.Max(policy.DelaySeconds,group.delay);}
        return policy;
    }
    public bool Allows(string path)
    {
        var hits=rules.Where(r=>Regex.IsMatch(path,"^"+Regex.Escape(r.pattern).Replace("\\*",".*").Replace("\\$","$"),RegexOptions.None,TimeSpan.FromMilliseconds(150)))
            .OrderByDescending(r=>r.pattern.TrimEnd('$').Replace("*","").Length).ThenByDescending(r=>r.allow).ToList();
        return hits.Count==0 || hits[0].allow;
    }
}

public sealed class PublicHttp : IDisposable
{
    private readonly HttpClient client;
    private readonly Dictionary<string,RobotsPolicy> policies=[];
    private readonly Dictionary<string,DateTimeOffset> lastRequest=[];
    public PublicHttp()
    {
        var handler=new SocketsHttpHandler {AllowAutoRedirect=false,AutomaticDecompression=DecompressionMethods.All,ConnectTimeout=TimeSpan.FromSeconds(12)};
        // Resolve each actual connection and reject private addresses, including redirects.
        handler.ConnectCallback=async (context,ct)=>{
            var ips=await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host,ct);
            if(ips.Length==0 || ips.Any(ip=>!UrlRules.IsPublic(ip))) throw new HttpRequestException("Only public internet addresses are allowed.");
            Exception? last=null;
            foreach(var ip in ips){var socket=new Socket(ip.AddressFamily,SocketType.Stream,ProtocolType.Tcp);try{await socket.ConnectAsync(new IPEndPoint(ip,context.DnsEndPoint.Port),ct);return new NetworkStream(socket,ownsSocket:true);}catch(Exception ex){socket.Dispose();last=ex;}}
            throw new HttpRequestException("Unable to connect.",last);
        };
        client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(25)};
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SilkroadAtlas/1.1 (+public-server-directory; desktop)");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html, application/json;q=0.9, */*;q=0.5");
    }
    public async Task<byte[]> Get(string url,bool robots,CancellationToken ct)
    {
        for(int redirect=0;redirect<=5;redirect++)
        {
            url=UrlRules.PublicUrl(url);if(url.Length==0)throw new HttpRequestException("Unsupported or non-public URL.");
            var uri=new Uri(url);var origin=uri.GetLeftPart(UriPartial.Authority);double delay=1.2;
            if(robots)
            {
                if(!policies.TryGetValue(origin,out var policy))
                {
                    try{var bytes=await Get(origin+"/robots.txt",false,ct);policy=RobotsPolicy.Parse(Encoding.UTF8.GetString(bytes));}
                    catch(HttpRequestException ex) when(ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone){policy=RobotsPolicy.Parse("");}
                    policies[origin]=policy!;
                }
                if(!policy!.Allows(uri.PathAndQuery))throw new HttpRequestException("This path is disallowed by robots.txt.");
                delay=policy.DelaySeconds;
            }
            if(lastRequest.TryGetValue(uri.Host,out var last))
            {
                var wait=TimeSpan.FromSeconds(delay)-(DateTimeOffset.UtcNow-last);
                if(wait>TimeSpan.Zero)await Task.Delay(wait,ct);
            }
            lastRequest[uri.Host]=DateTimeOffset.UtcNow;
            using var response=await client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,ct);
            if((int)response.StatusCode is >=300 and <=399 && response.Headers.Location is {} location)
            {
                url=new Uri(uri,location).AbsoluteUri;continue;
            }
            if((int)response.StatusCode==429) throw new HttpRequestException("Rate limited by source; try again later.",null,response.StatusCode);
            response.EnsureSuccessStatusCode();
            if(response.Content.Headers.ContentLength>6_000_000)throw new HttpRequestException("Response exceeds 6 MB limit.");
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(25));
            using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);using var memory=new MemoryStream();var buffer=new byte[16384];
            int read;while((read=await stream.ReadAsync(buffer,timeout.Token))>0){if(memory.Length+read>6_000_000)throw new HttpRequestException("Response exceeds 6 MB limit.");memory.Write(buffer,0,read);}
            return memory.ToArray();
        }
        throw new HttpRequestException("Too many redirects.");
    }
    public async Task<string> Html(string url,CancellationToken ct)=>Encoding.UTF8.GetString(await Get(url,true,ct));
    public void Dispose()=>client.Dispose();
}

public sealed record CrawlResult(List<ServerEntry> Servers,string Status,bool Successful);
public sealed class DiscoveryService : IDisposable
{
    private readonly PublicHttp http=new();
    public async Task<CrawlResult> Crawl(SourceConfig source,int maxPages,IProgress<string>? progress,CancellationToken ct)
    {
        if(!source.CanCrawl)return new([],"Reference only • open in browser; no automatic ad import",true);
        var results=new List<ServerEntry>();var queue=new Queue<string>();queue.Enqueue(source.Url);
        var visited=new HashSet<string>();var details=new List<string>();int pages=0;string problem="";
        while(queue.Count>0 && pages<Math.Clamp(maxPages,1,100))
        {
            ct.ThrowIfCancellationRequested();var url=queue.Dequeue();if(!visited.Add(url))continue;
            try
            {
                progress?.Report($"Reading {source.Name} • page {pages+1}");
                var parsed=PageParser.Parse(await http.Html(url,ct),source,url);pages++;
                CatalogMerge.Merge(results,parsed.Servers);details.AddRange(parsed.DetailPages);
                foreach(var next in parsed.NextPages.Where(n=>new Uri(n).Host==new Uri(source.Url).Host))if(!visited.Contains(next))queue.Enqueue(next);
                if(parsed.Servers.Count==0 && !parsed.Recognized){problem="No listings recognized; page may have changed or require a browser.";break;}
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or JsonException){problem=Friendly(ex);break;}
        }
        // First posts hold the actual server links. Bound this extra work per refresh.
        int detailFailures=0;
        foreach(var url in details.Distinct().Take(12))
        {
            ct.ThrowIfCancellationRequested();
            try{progress?.Report($"Reading forum announcement • {source.Name}");var parsed=PageParser.Parse(await http.Html(url,ct),source,url);if(!parsed.Recognized)detailFailures++;CatalogMerge.Merge(results,parsed.Servers);}
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or JsonException){detailFailures++;if(ex is HttpRequestException{StatusCode:HttpStatusCode.TooManyRequests})break;}
        }
        var status=problem.Length>0?$"{(results.Count>0?"Partial • ":"")}{problem}":$"{results.Count} listings • {pages} page{(pages==1?"":"s")}";
        if(queue.Count>0 && problem.Length==0)status+=" • page limit reached";
        if(detailFailures>0)status+=$" • {detailFailures} announcements unavailable";
        if(details.Count>12)status+=$" • first posts: 12/{details.Distinct().Count()} checked";
        return new(results,status,problem.Length==0 && detailFailures==0 && pages>0);
    }
    public async Task<List<DiscordCommunity>> ScanWebsite(ServerEntry entry,CancellationToken ct)
    {
        if(entry.Website.Length==0)return [];
        var html=await http.Html(entry.Website,ct);
        return PageParser.Invites(html).Select(u=>new DiscordCommunity {Url=u,Name=entry.Name,SourceUrl=entry.Website}).ToList();
    }
    public async Task Verify(DiscordCommunity community,CancellationToken ct)
    {
        var link=UrlRules.DiscordUrl(community.Url);if(link.Length==0){community.Status="Unsupported invite";return;}
        try
        {
            var code=new Uri(link).AbsolutePath.Trim('/');
            using var doc=JsonDocument.Parse(await http.Get("https://discord.com/api/v10/invites/"+Uri.EscapeDataString(code)+"?with_counts=true",false,ct));
            var root=doc.RootElement;
            if(!root.TryGetProperty("guild",out var guild)){community.Status="No public community information";return;}
            community.GuildId=guild.GetProperty("id").GetString()??"";
            community.Name=guild.GetProperty("name").GetString()??community.Name;
            community.IconUrl=guild.TryGetProperty("icon",out var icon) && icon.ValueKind==JsonValueKind.String? $"https://cdn.discordapp.com/icons/{community.GuildId}/{icon.GetString()}.png?size=128":"";
            community.Members=root.TryGetProperty("approximate_member_count",out var members)?members.GetInt32():null;
            community.Online=root.TryGetProperty("approximate_presence_count",out var online)?online.GetInt32():null;
            community.Status="Invite valid";
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(HttpRequestException ex) when(ex.StatusCode==HttpStatusCode.NotFound){community.Status="Expired or unavailable";community.Members=null;community.Online=null;community.IconUrl="";}
        catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or JsonException){community.Status=Friendly(ex);community.Members=null;community.Online=null;}
        finally{community.CheckedAt=DateTimeOffset.UtcNow;}
    }
    public async Task<byte[]> Icon(string url,CancellationToken ct)
    {
        if(!Uri.TryCreate(url,UriKind.Absolute,out var u) || u.Host!="cdn.discordapp.com" || !u.AbsolutePath.StartsWith("/icons/"))throw new HttpRequestException("Unsupported icon host.");
        return await http.Get(url,false,ct);
    }
    public static string Friendly(Exception ex)=>ex is TaskCanceledException?"Request timed out":ex is HttpRequestException{StatusCode:HttpStatusCode.Forbidden}?"Blocked by source (HTTP 403); open in browser":ex is HttpRequestException{StatusCode:HttpStatusCode.TooManyRequests}?"Rate limited; try again later":ex.Message.Length>170?ex.Message[..170]:ex.Message;
    public void Dispose()=>http.Dispose();
}
