using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace SilkroadAtlas.Core;

public static class UrlRules
{
    public static string PublicUrl(string? raw, string? basis=null)
    {
        raw=WebUtility.HtmlDecode(raw??"").Trim();
        if(raw.Length==0 || raw.StartsWith('#')) return "";
        if(!Uri.TryCreate(raw,UriKind.Absolute,out var uri) && basis!=null && Uri.TryCreate(basis,UriKind.Absolute,out var b))
            Uri.TryCreate(b,raw,out uri);
        if(uri==null || (uri.Scheme!="https" && uri.Scheme!="http") || uri.UserInfo.Length>0 || !uri.IsDefaultPort) return "";
        if(uri.IsLoopback || !uri.Host.Contains('.') || uri.Host.EndsWith(".local",StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".internal",StringComparison.OrdinalIgnoreCase)) return "";
        if(IPAddress.TryParse(uri.Host.Trim('[',']'),out var ip) && !IsPublic(ip)) return "";
        return new UriBuilder(uri){Fragment=""}.Uri.AbsoluteUri;
    }
    public static bool IsPublic(IPAddress ip)
    {
        if(ip.IsIPv4MappedToIPv6) ip=ip.MapToIPv4();
        if(IPAddress.IsLoopback(ip)) return false;
        var b=ip.GetAddressBytes();
        if(b.Length==16) return (b[0]&0xe0)==0x20; // global unicast only
        return b[0]!=0 && b[0]!=10 && b[0]!=127 && b[0]<224 &&
            !(b[0]==169 && b[1]==254) && !(b[0]==172 && b[1]>=16 && b[1]<=31) &&
            !(b[0]==192 && (b[1]==168 || b[1]==0)) && !(b[0]==100 && b[1]>=64 && b[1]<=127) && !(b[0]==198 && b[1] is 18 or 19);
    }
    public static string SiteKey(string raw)
    {
        var url=PublicUrl(raw);
        if(url.Length==0) return "";
        var u=new Uri(url);
        if(u.Host.Contains("discord") || u.Host.Contains("elitepvpers") || u.Host.Contains("arena-top100") || u.Host.Contains("nostalgic.gg")) return "";
        return Regex.Replace(u.Host,"^www\\.","")+u.AbsolutePath.TrimEnd('/')+u.Query;
    }
    public static string DiscordUrl(string raw)
    {
        var s=PublicUrl(raw);
        if(s.Length==0) return "";
        var u=new Uri(s); string code="";
        if(u.Host is "discord.gg" or "www.discord.gg") code=u.AbsolutePath.Trim('/');
        else if(u.Host is "discord.com" or "www.discord.com" or "discordapp.com" or "www.discordapp.com")
        {
            var m=Regex.Match(u.AbsolutePath,@"^/invite/([A-Za-z0-9_-]+)/?$");
            if(m.Success) code=m.Groups[1].Value;
        }
        return Regex.IsMatch(code,@"^[A-Za-z0-9_-]{2,100}$") ? "https://discord.gg/"+code : "";
    }
}

public sealed record ParsedPage(List<ServerEntry> Servers,List<string> NextPages,List<string> DetailPages,bool Recognized=false);

public static class PageParser
{
    private static readonly TimeSpan RegexLimit=TimeSpan.FromSeconds(2);
    public static string Clean(string text) => Regex.Replace(WebUtility.HtmlDecode(text),@"\s+"," ",RegexOptions.None,RegexLimit).Trim();
    private static string Text(HtmlNode node) => Clean(string.Join(" ", node.DescendantsAndSelf().Where(n=>n.NodeType==HtmlNodeType.Text).Select(n=>n.InnerText)));
    public static Observation Facts(string title, SourceConfig source, string url)
    {
        var o=new Observation {Title=Clean(title)[..Math.Min(Clean(title).Length,300)],SourceId=source.Id,SourceName=source.Name,Url=url};
        title=Regex.Replace(title,@"https?://\S+", " ",RegexOptions.IgnoreCase,RegexLimit);
        var m=Regex.Match(title,@"(?:\bcap[\s:_-]*(\d{2,3})\b|\b(\d{2,3})[\s_-]*(?:cap|lv)\b)",RegexOptions.IgnoreCase,RegexLimit);
        if(m.Success && int.TryParse(m.Groups[1].Success?m.Groups[1].Value:m.Groups[2].Value,out int cap) && cap>=20 && cap<=200) o.Cap=cap;
        m=Regex.Match(title,@"(?:\b(?:EXP|rates?)[\s:/-]*x?\s*\d+(?:\.\d+)?x?|\bx\d+(?:\.\d+)?\b|\b\d+(?:\.\d+)?x\b)",RegexOptions.IgnoreCase,RegexLimit);
        if(m.Success) o.Rates=m.Value.Trim();
        bool ch=Regex.IsMatch(title,@"\b(ch|chn|chinese|china)\b",RegexOptions.IgnoreCase,RegexLimit);
        bool eu=Regex.IsMatch(title,@"\b(eu|eur|european|europe)\b",RegexOptions.IgnoreCase,RegexLimit);
        o.Race=ch && eu?"CH + EU":ch?"CH":eu?"EU":"";
        if(Regex.IsMatch(title,@"\b(no[n]?[\s-]*bots?|anti[\s-]*bots?)\b",RegexOptions.IgnoreCase,RegexLimit)) o.BotPolicy="No bots";
        if(Regex.IsMatch(title,@"\bmacro\b",RegexOptions.IgnoreCase,RegexLimit)) o.BotPolicy=o.BotPolicy.Length>0?"Macro / no external bots":"Macro";
        return o;
    }
    public static ParsedPage Parse(string html, SourceConfig source, string pageUrl)
    {
        var doc=new HtmlDocument();doc.LoadHtml(html);
        return source.Kind switch {
            "Nostalgic"=>new(ParseNostalgic(doc,source,pageUrl),[],[]),
            "Arena"=>ParseArena(doc,source,pageUrl),
            "Forum"=>ParseForum(doc,source,pageUrl),
            _=>ParseWebsite(doc,source,pageUrl)
        };
    }
    private static IEnumerable<HtmlNode> Nodes(HtmlNode n,string xpath) => n.SelectNodes(xpath)?.AsEnumerable() ?? [];
    public static List<string> Invites(string text)
    {
        text=WebUtility.HtmlDecode(text).Replace("\\/","/");
        return Regex.Matches(text,@"https?://(?:www\.)?(?:discord\.gg/|discord(?:app)?\.com/invite/)[A-Za-z0-9_-]+",RegexOptions.IgnoreCase,RegexLimit)
            .Select(x=>UrlRules.DiscordUrl(x.Value)).Where(x=>x.Length>0).Distinct().ToList();
    }
    private static ServerEntry Entry(string name,string website,string facts,SourceConfig source,string url,IEnumerable<string> invites)
    {
        var shortName=Clean(name).Split('|')[0].Trim();
        if(shortName.Length>100) shortName=shortName[..100].Trim();
        var site=UrlRules.PublicUrl(website,url);var invite=UrlRules.DiscordUrl(site);
        if(invite.Length>0){invites=invites.Append(invite);site="";}
        var evidence=Facts(facts,source,url);evidence.Title=Clean(name)[..Math.Min(Clean(name).Length,300)];
        return new() {Name=shortName,Website=site,Evidence=[evidence],
            Discord=invites.Distinct().Select(x=>new DiscordCommunity{Url=x,SourceUrl=url,Name=shortName}).ToList()};
    }
    private static List<ServerEntry> ParseNostalgic(HtmlDocument doc,SourceConfig source,string url)
    {
        var result=new List<ServerEntry>();var seen=new HashSet<string>();
        foreach(var script in doc.DocumentNode.Descendants("script"))
        {
            var raw=script.InnerText;
            var match=Regex.Match(raw,@"self\.__next_f\.push\(\[1,(""(?:[^""\\]|\\.)*"")\]\)",RegexOptions.Singleline,RegexLimit);
            if(match.Success) {try {raw=JsonSerializer.Deserialize<string>(match.Groups[1].Value)??"";}catch(JsonException){continue;}}
            foreach(Match start in Regex.Matches(raw,@"\{""id""\s*:",RegexOptions.None,RegexLimit))
            {
                try
                {
                    var bytes=System.Text.Encoding.UTF8.GetBytes(raw[start.Index..]);
                    var reader=new Utf8JsonReader(bytes);
                    using var item=JsonDocument.ParseValue(ref reader);var e=item.RootElement;
                    if(!e.TryGetProperty("discordUrl",out _) || !e.TryGetProperty("name",out _)) continue;
                    if(e.TryGetProperty("game",out var game) && Str(game,"slug")!="silkroad-online") continue;
                    var slug=Str(e,"slug");if(slug.Length==0 || !seen.Add(slug)) continue;
                    var name=Str(e,"name");var website=Str(e,"websiteUrl");if(website.Length==0) website=Str(e,"serverUrl");
                    string facts=name+" "+Str(e,"rates");
                    foreach(var prop in new[]{"tags","versions"})
                        if(e.TryGetProperty(prop,out var values) && values.ValueKind==JsonValueKind.Array)
                            facts+=" "+string.Join(' ',values.EnumerateArray().Select(v=>v.ValueKind==JsonValueKind.String?v.GetString():Str(v,"name")));
                    var link="https://nostalgic.gg/en/servers/"+Uri.EscapeDataString(slug);
                    // Use the actual public detail link if the route differs.
                    var anchor=doc.DocumentNode.Descendants("a").FirstOrDefault(a=>a.GetAttributeValue("href","").EndsWith("/"+slug,StringComparison.Ordinal));
                    link=anchor is null?url:UrlRules.PublicUrl(anchor.GetAttributeValue("href",""),url);
                    result.Add(Entry(name,website,facts,source,link,Invites(Str(e,"discordUrl"))));
                }catch(JsonException){/* Unrelated or incomplete framework payload. */}
            }
        }
        return result;
    }
    private static string Str(JsonElement e,string prop)=>e.ValueKind==JsonValueKind.Object && e.TryGetProperty(prop,out var v) && v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
    private static ParsedPage ParseArena(HtmlDocument doc,SourceConfig source,string url)
    {
        var rows=new List<ServerEntry>();
        foreach(var tr in doc.DocumentNode.Descendants("tr"))
        {
            var a=tr.SelectSingleNode(".//h2/a[@href]");if(a==null) continue;
            var site=UrlRules.PublicUrl(a.GetAttributeValue("href",""),url);
            if(site.Length==0 || new Uri(site).Host.Contains("arena-top100")) continue;
            var title=Clean(a.InnerText);if(title.Length<2) continue;
            // Extract brief factual metadata, not advertiser prose.
            var text=Text(tr);
            rows.Add(Entry(title,site,text,source,url,Invites(tr.InnerHtml)));
        }
        var next=Nodes(doc.DocumentNode,"//a[@rel='next']").Select(a=>UrlRules.PublicUrl(a.GetAttributeValue("href",""),Base(doc,url))).Where(x=>x.Length>0).ToList();
        return new(rows,next,[]);
    }
    private static string Base(HtmlDocument doc,string url) => UrlRules.PublicUrl(doc.DocumentNode.SelectSingleNode("//base[@href]")?.GetAttributeValue("href",""),url) is {Length:>0} b?b:url;
    private static bool HasClass(HtmlNode node,string value) => node.GetAttributeValue("class","").Split(' ',StringSplitOptions.RemoveEmptyEntries).Contains(value);
    public static bool IsThreadUrl(string url) => Regex.IsMatch(url,@"/(?:threads?/|konular?/|konu/|Thread-|showthread\.php|viewtopic\.php)|sro-pserver-advertising/\d|/forum/\d[^/]*\.html",RegexOptions.IgnoreCase,RegexLimit);
    public static string ThreadUrl(string raw,string basis)
    {
        var link=UrlRules.PublicUrl(raw,basis);if(link.Length==0)return "";
        var u=new UriBuilder(link);
        u.Path=Regex.Replace(u.Path,@"/(?:unread|latest|page-\d+|post-\d+)/?$","/",RegexOptions.IgnoreCase,RegexLimit);
        if(Regex.IsMatch(u.Path,@"(?:showthread|viewtopic)\.php$",RegexOptions.IgnoreCase))
        {
            var t=Regex.Match(u.Query,@"(?:[?&])t=(\d+)");if(t.Success)u.Query="t="+t.Groups[1].Value;
        }
        else if(u.Path.StartsWith("/Thread-",StringComparison.OrdinalIgnoreCase))u.Query="";
        return u.Uri.AbsoluteUri;
    }
    private static bool IsAdvertisement(string title,bool requireSignal)
    {
        if(title.Length<3 || Regex.IsMatch(title,@"\b(rules?|guidelines?|template|reglas?|normas?|guide|help|request|pregunta|ayuda)\b|advertising.*information|section information|kurallar|kuralları|örnek.*tanıtım|tani?tim.*örne|nasıl.*konu|nasıl olmalı|nedir|^Silkroad 120 CAP Pvp Server$|find your server|how to create|sticky thread offer|duyurular|duyurusu|قوانين|قواعد|ar[ıi]yorum|busco servidor",RegexOptions.IgnoreCase,RegexLimit)) return false;
        return !requireSignal || Regex.IsMatch(title,@"\bcap\b|\b\d{2,3}\s*(?:cap|lv)\b|\b(?:PVE|PVP|CH/EU|EU/CH|rates|grand opening)\b",RegexOptions.IgnoreCase,RegexLimit);
    }
    private static ParsedPage ParseForum(HtmlDocument doc,SourceConfig source,string url)
    {
        var entries=new List<ServerEntry>();var details=new List<string>();var root=doc.DocumentNode;
        var anchors=Nodes(root,"//a[starts-with(@id,'thread_title_')] | //*[contains(concat(' ',normalize-space(@class),' '),' structItem-title ')]/a[@href] | //h3[contains(concat(' ',normalize-space(@class),' '),' title ')]/a[@href] | //span[starts-with(@id,'tid_') or starts-with(@id,'subject_')]/a[@href] | //a[contains(concat(' ',normalize-space(@class),' '),' topictitle ')] | //a[contains(concat(' ',normalize-space(@class),' '),' thread-title ')]").ToList();
        // Do not import related-thread widgets from an announcement page.
        bool detailPage=IsThreadUrl(url);
        if(!detailPage)
        foreach(var a in anchors)
        {
            var title=Text(a);if(!IsAdvertisement(title,source.RequireAdSignal))continue;
            var link=ThreadUrl(a.GetAttributeValue("href",""),url);
            if(!IsThreadUrl(link) || new Uri(link).Host.Replace("www.","")!=new Uri(url).Host.Replace("www.",""))continue;
            if(details.Contains(link))continue;
            entries.Add(Entry(title,"",title,source,link,[]));details.Add(link);
        }
        if(detailPage)
        {
            var post=root.SelectSingleNode("//*[starts-with(@id,'post_message_')] | //article[contains(concat(' ',normalize-space(@class),' '),' message-body ')]//*[contains(concat(' ',normalize-space(@class),' '),' bbWrapper ')] | //blockquote[contains(concat(' ',normalize-space(@class),' '),' messageText ')] | //div[contains(concat(' ',normalize-space(@class),' '),' post_body ')] | //div[contains(concat(' ',normalize-space(@class),' '),' postbody ')]/div[contains(concat(' ',normalize-space(@class),' '),' content ')] | //div[contains(concat(' ',normalize-space(@class),' '),' post-content ')]");
            if(post!=null)
            {
                var heading=root.SelectSingleNode("//h1") ?? root.SelectSingleNode("//h2[contains(concat(' ',normalize-space(@class),' '),' topic-title ')]") ?? root.SelectSingleNode("//title");
                var title=heading==null?source.Name:Text(heading);
                foreach(var n in post.Descendants().Where(n=>HasClass(n,"bbCodeBlock--quote") || HasClass(n,"signature") || HasClass(n,"message-signature") || n.Name is "script" or "style").ToList())n.Remove();
                var links=post.Descendants("a").Select(a=>new { Url=UrlRules.PublicUrl(a.GetAttributeValue("href",""),url),Label=Text(a) }).Where(x=>x.Url.Length>0).ToList();
                var sites=links.Where(x=>IsServerWebsite(x.Url,url)).OrderByDescending(x=>Regex.IsMatch(x.Label,@"website|web site|official|register|anasayfa|kayıt|sitio|web sitesi|home page",RegexOptions.IgnoreCase));
                var site=sites.Select(x=>x.Url).FirstOrDefault()??"";
                if(IsAdvertisement(title,source.RequireAdSignal))entries.Add(Entry(title,site,title+" "+Text(post),source,ThreadUrl(url,url),Invites(post.InnerHtml)));
                return new(entries,[],[],true);
            }
            return new([],[],[],false);
        }
        var next=Nodes(root,"//link[@rel='next'] | //a[@rel='next' or @title='Next Page - Results' or contains(@class,'pageNav-jump--next') or contains(@class,'pagination_next')]")
            .Select(a=>UrlRules.PublicUrl(a.GetAttributeValue("href",""),url)).Where(x=>x.Length>0 && !IsThreadUrl(x)).Distinct().Take(1).ToList();
        if(next.Count==0)
            next=root.Descendants("a").Where(a=>Clean(a.InnerText) is ">" or "»" or "Next" or "Sonraki" or "Siguiente").Select(a=>UrlRules.PublicUrl(a.GetAttributeValue("href",""),url))
                .Where(x=>x.Length>0 && !IsThreadUrl(x) && new Uri(x).Host==new Uri(url).Host && (new Uri(x).AbsolutePath.TrimEnd('/').StartsWith(new Uri(source.Url).AbsolutePath.TrimEnd('/')))).Distinct().Take(1).ToList();
        bool recognized=anchors.Count>0 || root.SelectSingleNode("//*[@data-template='forum_view']")!=null || Regex.IsMatch(Text(root),@"There are no threads|No hay temas|no threads to display|Henüz konu|Konu bulunamadı",RegexOptions.IgnoreCase,RegexLimit);
        return new(entries,next,details,recognized);
    }
    private static bool IsServerWebsite(string link,string forum)
    {
        if(UrlRules.SiteKey(link).Length==0)return false;
        var u=new Uri(link);if(u.Host.Replace("www.","")==new Uri(forum).Host.Replace("www.",""))return false;
        if(Regex.IsMatch(u.Host,@"(?:^|\.)(?:youtube\.com|youtu\.be|imgur\.com|facebook\.com|virustotal\.com|twitter\.com|x\.com|imgbb\.com|postimg\.cc|google\.com|epvpimg\.com|mediafire\.com|mega\.nz|drive\.google\.com|discord\.com|discord\.gg|tiktok\.com|instagram\.com|twitch\.tv)$",RegexOptions.IgnoreCase))return false;
        if(SourceConfig.ForumSources().Any(s=>new Uri(s.Url).Host.Replace("www.","")==u.Host.Replace("www.","")))return false;
        return !Regex.IsMatch(u.AbsolutePath,@"\.(?:jpg|jpeg|gif|png|webp|zip|rar|exe|msi|7z)$",RegexOptions.IgnoreCase);
    }
    private static ParsedPage ParseWebsite(HtmlDocument doc,SourceConfig source,string url)
    {
        foreach(var n in doc.DocumentNode.Descendants().Where(n=>n.Name is "script" or "style" or "noscript").ToList()) n.Remove();
        string title=source.Name;
        var text=Text(doc.DocumentNode);
        return new([Entry(title,url,text,source,url,Invites(doc.DocumentNode.InnerHtml))],[],[]);
    }
}
