using System.Net;
using System.Text.Json;
using SilkroadAtlas.Core;

if(args.FirstOrDefault()=="--parse-forums")
{
    using var sections=JsonDocument.Parse(File.ReadAllText(Path.Combine(args[1],"sections.json")));int i=0;
    foreach(var section in sections.RootElement.EnumerateArray())
    {
        var path=Path.Combine(args[1],$"section-{i++}.html");if(!File.Exists(path))continue;
        var url=section.GetProperty("url").GetString()!;var src=SourceConfig.ForumSources().FirstOrDefault(s=>s.Url==url);if(src==null)continue;
        var p=PageParser.Parse(File.ReadAllText(path),src,url);
        Console.WriteLine($"{src.Name}: {p.Servers.Count} ads; recognized={p.Recognized}; next={string.Join(',',p.NextPages)}");
        foreach(var s in p.Servers.Take(2))Console.WriteLine($"  {s.Name} -> {s.Evidence[0].Url}");
    }
    return;
}
if(args.FirstOrDefault()=="--forums")
{
    var catalog=Storage.Load(args[1]);CatalogMerge.AddBuiltInSources(catalog);
    using var slots=new SemaphoreSlim(4);var gate=new object();
    await Task.WhenAll(catalog.Sources.Where(s=>s.Kind=="Forum" && s.Id!="epvp" && (args.Length<4 || args[3].Split(',').Contains(s.Id))).GroupBy(s=>new Uri(s.Url).Host).Select(async group=>
    {
        await slots.WaitAsync();try
        {
            using var svc=new DiscoveryService();
            foreach(var s in group)
            {
                s.LastAttempt=DateTimeOffset.UtcNow;
                var result=await svc.Crawl(s,int.Parse(args[2]),null,CancellationToken.None);
                lock(gate)
                {
                    CatalogMerge.Merge(catalog.Servers,result.Servers);s.Status=result.Status;s.LastCount=result.Servers.Count;
                    if(result.Successful)s.LastSuccess=DateTimeOffset.UtcNow;
                    Storage.Save(args[1],catalog);Console.WriteLine($"{s.Name}: {result.Status}");
                }
            }
        }finally{slots.Release();}
    }));
    Console.WriteLine($"CATALOG: {catalog.Servers.Count} listings; {catalog.Servers.SelectMany(s=>s.Discord).Select(d=>d.Url).Distinct().Count()} invites");return;
}
if(args.FirstOrDefault()=="--fetch")
{
    using var http=new PublicHttp();File.WriteAllText(args[2],await http.Html(args[1],CancellationToken.None));return;
}
if(args.FirstOrDefault()=="--finalize")
{
    var c=Storage.Load(args[1]);CatalogMerge.AddBuiltInSources(c);var consolidated=new List<ServerEntry>();CatalogMerge.Merge(consolidated,c.Servers);c.Servers=consolidated;Storage.Save(args[1],c);
    var ids=c.Sources.Where(s=>s.Kind=="Forum").Select(s=>s.Id).ToHashSet();
    File.WriteAllText(args[2],Storage.Csv(c.Servers.Where(s=>s.Evidence.Any(e=>ids.Contains(e.SourceId)))),new System.Text.UTF8Encoding(true));
    Console.WriteLine($"{consolidated.Count} listings; {consolidated.SelectMany(s=>s.Discord).Select(d=>d.Url).Distinct().Count()} invites");return;
}
if(args.FirstOrDefault()=="--seed")
{
    var catalog=new Catalog();
    foreach(var seedSource in catalog.Sources)
    {
        var files=Directory.GetFiles(args[1],seedSource.Id+"*.html");
        foreach(var file in files)
        {
            var seedParsed=PageParser.Parse(File.ReadAllText(file),seedSource,seedSource.Url);
            CatalogMerge.Merge(catalog.Servers,seedParsed.Servers);
            Console.WriteLine($"{Path.GetFileName(file)}: {seedParsed.Servers.Count} entries; {seedParsed.NextPages.Count} next pages");
            if(seedSource.Id=="nostalgic" && seedParsed.Servers.Count<80)throw new Exception("Nostalgic parser regression");
            if(seedSource.Id=="arena" && seedParsed.Servers.Count<15)throw new Exception("Arena parser regression");
        }
        seedSource.Status=files.Length>0?$"Bundled snapshot • {files.Length} page(s) • refresh for current data":"Not checked • open or refresh this source";
    }
    Storage.Save(args[2],catalog);
    Console.WriteLine($"SEED: {catalog.Servers.Count} servers; {catalog.Servers.SelectMany(s=>s.Discord).Select(d=>d.Url).Distinct().Count()} invites");return;
}
if(args.FirstOrDefault()=="--live")
{
    using var service=new DiscoveryService();var cat=Storage.Load(args[1]);
    foreach(var s in cat.Sources)
    {
        s.LastAttempt=DateTimeOffset.UtcNow;
        var result=await service.Crawl(s,int.Parse(args[2]),new Progress<string>(Console.WriteLine),CancellationToken.None);
        Console.WriteLine($"{s.Name}: {result.Status}; success={result.Successful}");
        CatalogMerge.Merge(cat.Servers,result.Servers);s.Status=result.Status;s.LastCount=result.Servers.Count;
        if(result.Successful)s.LastSuccess=DateTimeOffset.UtcNow;
    }
    cat.LastRefresh=DateTimeOffset.UtcNow;Storage.Save(args[1],cat);return;
}
if(args.FirstOrDefault()=="--discord")
{
    using var service=new DiscoveryService();var c=new DiscordCommunity{Url=args[1]};await service.Verify(c,CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(c,Storage.Json));
    if(c.IconUrl.Length>0)Console.WriteLine($"Icon downloaded: {(await service.Icon(c.IconUrl,CancellationToken.None)).Length} bytes");return;
}

int passed=0;
void Check(bool value,string name){if(!value)throw new Exception("FAIL: "+name);Console.WriteLine("PASS: "+name);passed++;}
var source=new SourceConfig{Id="test",Name="Test",Kind="Arena",Url="https://example.com/"};
var obs=PageParser.Facts("D11 | 110 CAP | CH/EU | No Bots | EXP x5",source,source.Url);
Check(obs.Cap==110 && obs.Race=="CH + EU" && obs.BotPolicy=="No bots" && obs.Rates=="EXP x5","Extract cap, race, bot policy and rates");
Check(PageParser.Facts("Cap-80 Chinese-only Macro",source,source.Url).Cap==80,"Hyphenated cap");
Check(PageParser.Facts("A new adventure",source,source.Url).Cap==null,"Do not invent missing cap");
Check(PageParser.Facts("Asgard https://asgard-online.eu",source,source.Url).Race=="","Do not infer European race from a website domain");
Check(UrlRules.DiscordUrl("https://discord.com/invite/AbC_123?x=1")=="https://discord.gg/AbC_123","Normalize Discord invite while preserving case");
Check(UrlRules.DiscordUrl("https://discord.gg.evil.com/abc")=="","Reject deceptive invite host");
Check(UrlRules.DiscordUrl("https://discord.com/channels/123/456")=="","Do not treat private channels as public invites");
Check(UrlRules.PublicUrl("file:///C:/Windows/win.ini")=="" && UrlRules.PublicUrl("javascript:alert(1)")=="","Reject executable and local link schemes");
Check(UrlRules.PublicUrl("https://127.0.0.1/")=="" && UrlRules.PublicUrl("http://192.168.1.1")=="","Reject private network URLs");
Check(!UrlRules.IsPublic(IPAddress.Parse("::ffff:192.168.1.1")),"Reject IPv4-mapped private address");
Check(UrlRules.PublicUrl("https://user:pass@example.com/")=="","Reject embedded credentials");
Check(UrlRules.PublicUrl("/next","https://example.com/first")=="https://example.com/next","Resolve relative page link");
var invites=PageParser.Invites("https://discord.gg/AAbb & https://discord.com/invite/AAbb https://discord.gg/XY_12");
Check(invites.Count==2,"Deduplicate equivalent Discord invites");
var html="""<html><base href="https://example.com/"><table><tr><td><h2><a href="https://play-example.com">Alpha | Cap 100 CH</a></h2><a href="https://discord.gg/Ab12">Discord</a> EXP 5x</td></tr><tr><h2>Advertisement</h2></tr></table><a rel="next" href="rank/2/">Next</a></html>""";
var parsed=PageParser.Parse(html,source,source.Url);
Check(parsed.Servers.Count==1 && parsed.Servers[0].Name=="Alpha" && parsed.Servers[0].Discord.Count==1,"Arena row scoped extraction excludes advertisement");
Check(parsed.NextPages.Single()=="https://example.com/rank/2/","Arena pagination uses document base");
var payload="""x:["$","$L1",null,{"initialServers":[{"id":1,"name":"Bravo","slug":"bravo","websiteUrl":"https://bravo.example.com","discordUrl":"https://discord.gg/bb22","rates":"x2","versions":["Cap 90"],"game":{"slug":"silkroad-online"}},{"id":2,"name":"Other game","slug":"other","discordUrl":"https://discord.gg/other","game":{"slug":"tibia"}}]}]""";
source.Kind="Nostalgic";
var nextHtml="<script>self.__next_f.push([1,"+JsonSerializer.Serialize(payload)+"])</script><a href='/en/server/bravo'>Bravo</a>";
var next=PageParser.Parse(nextHtml,source,source.Url);
Check(next.Servers.Count==1 && next.Servers[0].Name=="Bravo" && next.Servers[0].Evidence[0].Cap==90,"Decode Next.js payload and isolate Silkroad game");
Check(next.Servers[0].Evidence[0].Url=="https://example.com/en/server/bravo","Use observed detail URL");
source.Kind="Forum";
var forum=PageParser.Parse("""<a id="thread_title_1" href="/forum/1-alpha.html">Alpha | Cap 80</a><a id="thread_title_2" href="/rules.html">Advertising rules</a><a rel="next" href="/forum/page2.html">Next</a>""",source,source.Url);
Check(forum.Servers.Count==1 && forum.DetailPages.Count==1 && forum.NextPages.Count==1,"Forum index excludes rules and queues announcements");
var thread=PageParser.Parse("""<h1>Alpha | Cap 80</h1><div id="post_message_1"><a href="https://alpha.example.com">Website</a><a href="https://discord.gg/alpha">Discord</a></div><div id="post_message_2"><a href="https://discord.gg/unrelated">Reply signature</a></div>""",source,"https://example.com/forum/1-alpha.html");
Check(thread.Servers[0].Website=="https://alpha.example.com/" && thread.Servers[0].Discord.Count==1,"Only read the first forum post, not reply signatures");
var xf=PageParser.Parse("""<html data-template="forum_view"><div class="structItem-title"><a href="/forums/test/?prefix_id=1">PvP</a><a href="/threads/alpha.1/unread">Alpha | 80 CAP</a></div><div class="structItem-title"><a href="/threads/rules.2/">Forum rules</a></div><a class="pageNav-jump pageNav-jump--next" href="/forums/test/page-2">Next</a></html>""",source,source.Url);
Check(xf.Recognized && xf.Servers.Count==1 && xf.DetailPages[0]=="https://example.com/threads/alpha.1/" && xf.NextPages.Count==1,"XenForo index ignores prefixes, rules and unread suffixes");
var xfPost=PageParser.Parse("""<title>Unrelated forum title</title><h1>Alpha | 80 CAP</h1><article class="message-body"><div class="bbWrapper"><div class="bbCodeBlock--quote"><a href="https://discord.gg/quoted">Quote</a></div><a href="https://sro360.com/">Forum</a><a href="https://play-alpha.example.com/">Official website</a><a href="https://discord.gg/alpha">Discord</a></div></article><article class="message-body"><div class="bbWrapper">https://discord.gg/reply</div></article>""",source,xf.DetailPages[0]);
Check(xfPost.Servers.Single().Name=="Alpha" && xfPost.Servers[0].Discord.Single().Url.EndsWith("/alpha") && xfPost.Servers[0].Website=="https://play-alpha.example.com/","XenForo first post prefers heading and excludes quotes and forum links");
xf.Servers[0].Favorite=true;CatalogMerge.Merge(xf.Servers,xfPost.Servers);
Check(xf.Servers.Count==1 && xf.Servers[0].Favorite && xf.Servers[0].HasDiscord,"Enrich indexed announcement without duplicating or losing favorite");
var mybb=PageParser.Parse("""<span id="tid_10"><a href="/Thread-Alpha?pid=123">Alpha | CAP 90</a></span>""",source,source.Url);
Check(mybb.Servers.Count==1 && mybb.DetailPages[0]=="https://example.com/Thread-Alpha","MyBB topic index canonicalization");
Check(PageParser.Parse("""<h1>Alpha</h1><div class="post_body">https://discord.gg/mybb</div><div class="post_body">https://discord.gg/reply</div>""",source,mybb.DetailPages[0]).Servers[0].Discord.Count==1,"MyBB first post scope");
var custom=PageParser.Parse("""<link rel="next" href="/category/16/servers/page/2"><a class="thread-title" href="/thread/123/alpha">Alpha | CAP 80</a>""",source,source.Url);
Check(custom.Servers.Count==1 && custom.NextPages.Count==1 && PageParser.Parse("""<h1>Alpha</h1><div class="post-content">https://discord.gg/custom</div>""",source,custom.DetailPages[0]).Servers[0].HasDiscord,"Custom forum first post and head-link pagination");
var joined=new List<ServerEntry>{new(){Name="Directory Alpha",Website="https://play-alpha.example.com/",Evidence=[obs]},new(){Name="Pinned Alpha",Favorite=true,Evidence=[PageParser.Facts("Pinned Alpha",source,xf.DetailPages[0])]}};
CatalogMerge.Merge(joined,xfPost.Servers);
Check(joined.Count==1 && joined[0].Favorite && joined[0].Evidence.Count==2 && joined[0].HasDiscord,"First post connects and consolidates directory and indexed-thread records");
Check(PageParser.Parse("""<html data-template="forum_view"><p>No threads yet</p></html>""",source,source.Url).Recognized,"Recognize empty XenForo section");
Check(!PageParser.Parse("<title>Verify you are human</title>",source,source.Url).Recognized,"Do not report challenge page as empty success");
var migration=new Catalog{Sources=[new(){Id="arena",Url="https://www.arena-top100.com/silkroad-private-servers/",Enabled=false}]};
CatalogMerge.AddBuiltInSources(migration);int migrated=migration.Sources.Count;var removed=migration.Sources.Last().Id;migration.Sources.RemoveAll(s=>s.Id==removed);CatalogMerge.AddBuiltInSources(migration);
Check(migration.Sources.Count==migrated-1 && !migration.Sources[0].Enabled && migration.Sources.All(s=>s.Id!="epvp" && s.Id!=removed),"Source migration preserves disabled and removed sources across restarts");
Check(SourceConfig.ForumSources().Select(s=>s.Id).Distinct().Count()==SourceConfig.ForumSources().Count && SourceConfig.ForumSources().Where(s=>!s.CanCrawl).All(s=>!s.Enabled),"Forum manifest is unique and reference communities are not crawled");
using(var svc=new DiscoveryService())Check((await svc.Crawl(new(){Kind="Reference",Url="https://example.com"},5,null,CancellationToken.None)).Servers.Count==0,"References never become server listings");
source.Kind="Website";
var web=PageParser.Parse("<title>Server</title><a href='https://discord.gg/site'>Community</a><script>secret code CAP 140</script><p>Cap 80 EU</p>",source,source.Url);
Check(web.Servers[0].Evidence[0].Cap==80 && web.Servers[0].Discord.Count==1,"Website scan ignores executable script content");
var servers=parsed.Servers;servers[0].Favorite=true;var newEntry=JsonSerializer.Deserialize<ServerEntry>(JsonSerializer.Serialize(servers[0]))!;
newEntry.Evidence[0].Cap=120;newEntry.Evidence[0].SourceId="another";newEntry.Evidence[0].SourceName="Another";
CatalogMerge.Merge(servers,[newEntry]);
Check(servers.Count==1 && servers[0].Favorite && servers[0].Evidence.Count==2,"Cross-source deduplication preserves favorites and evidence");
Check(servers[0].CapLabel.Contains("100") && servers[0].CapLabel.Contains("120"),"Expose conflicting source caps");
CatalogMerge.Merge(servers,[newEntry]);Check(servers[0].Evidence.Count==2,"Refresh updates evidence without accumulating duplicates");
var older=JsonSerializer.Deserialize<ServerEntry>(JsonSerializer.Serialize(newEntry))!;older.Evidence[0].SeenAt=DateTimeOffset.UtcNow.AddYears(-1);older.Evidence[0].Cap=20;
CatalogMerge.Merge(servers,[older]);Check(servers[0].Evidence.All(e=>e.Cap!=20),"Older bundled evidence cannot replace newer observations");
var robots=RobotsPolicy.Parse("User-agent: *\nDisallow: /private\nAllow: /private/open\nDisallow: /*?token=*\nCrawl-delay: 3");
Check(!robots.Allows("/private/data") && robots.Allows("/private/open/page") && !robots.Allows("/page?token=abc"),"Robots longest match and wildcard rules");
Check(robots.DelaySeconds==3,"Respect crawl delay");
Check(RobotsPolicy.Parse("User-agent: *\nDisallow: /\nUser-agent: SilkroadAtlas\nAllow: /").Allows("/servers"),"Bot-specific robots group takes precedence");
var malicious=new ServerEntry{Name="=HYPERLINK(1)",Evidence=[obs]};Check(Storage.Csv([malicious]).Contains("\"'=HYPERLINK"),"Prevent spreadsheet formula injection in CSV export");
var temp=Path.Combine(Path.GetTempPath(),"silkroad-atlas-tests-"+Guid.NewGuid()+".json");
try{Storage.Save(temp,new Catalog{Servers=servers});var loaded=Storage.Load(temp);Check(loaded.Servers[0].Favorite && loaded.Servers[0].Evidence.Count==2,"Atomic catalog round trip preserves saved state");}finally{File.Delete(temp);}
Console.WriteLine($"{passed} tests passed.");
