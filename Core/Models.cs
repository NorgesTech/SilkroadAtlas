using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SilkroadAtlas.Core;

public sealed class SourceConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Kind { get; set; } = "Website";
    public string Language { get; set; } = "";
    public string Notes { get; set; } = "";
    public bool RequireAdSignal { get; set; }
    public bool Enabled { get; set; } = true;
    public string Status { get; set; } = "Not checked";
    public DateTimeOffset? LastAttempt { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public int LastCount { get; set; }
    [JsonIgnore] public bool CanCrawl => Kind != "Reference";
    [JsonIgnore] public string SourceInfo => string.Join(" • ",new[]{Kind=="Reference"?"Community reference":Kind=="Forum"?"Forum advertisements":"Website / directory",Language,Notes}.Where(s=>s.Length>0));
    [JsonIgnore] public string CheckedLabel => LastAttempt is {} t ? t.ToLocalTime().ToString("dd MMM yyyy • HH:mm") : "Awaiting first refresh";
    public static List<SourceConfig> Defaults() => [
        new() { Id="nostalgic", Name="Nostalgic.gg", Url="https://nostalgic.gg/en/silkroad-online", Kind="Nostalgic" },
        new() { Id="arena", Name="Arena Top100", Url="https://www.arena-top100.com/silkroad-private-servers/", Kind="Arena" },
        new() { Id="epvp", Name="elitepvpers", Url="https://www.elitepvpers.com/forum/sro-pserver-advertising/", Kind="Forum",Language="English" },
        ..ForumSources()
    ];
    public static List<SourceConfig> ForumSources()
    {
        using var stream=typeof(SourceConfig).Assembly.GetManifestResourceStream("Core.ForumSources.json")!;
        return System.Text.Json.JsonSerializer.Deserialize<List<SourceConfig>>(stream,Storage.Json)??[];
    }
}

public sealed class Observation
{
    public string SourceId { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public int? Cap { get; set; }
    public string Rates { get; set; } = "";
    public string Race { get; set; } = "";
    public string BotPolicy { get; set; } = "";
    public DateTimeOffset SeenAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonIgnore] public string Label => $"{SourceName} • {SeenAt.ToLocalTime():dd MMM yyyy HH:mm}";
    [JsonIgnore] public string Facts => string.Join(" • ", new[] {Cap is {} n ? $"Cap {n}" : "Cap unknown", Rates, Race, BotPolicy}.Where(x=>x.Length>0));
}

public sealed class DiscordCommunity
{
    public string Url { get; set; } = "";
    public string Name { get; set; } = "";
    public string GuildId { get; set; } = "";
    public string IconUrl { get; set; } = "";
    public int? Members { get; set; }
    public int? Online { get; set; }
    public string Status { get; set; } = "Not checked";
    public DateTimeOffset? CheckedAt { get; set; }
    public string SourceUrl { get; set; } = "";
    [JsonIgnore] public string CountLabel => Members is {} m ? $"{m:N0} Discord members • {Online?.ToString("N0") ?? "?"} online" : "Discord population unavailable";
    [JsonIgnore] public string CheckedLabel => CheckedAt is {} t ? $"{Status} • {t.ToLocalTime():dd MMM HH:mm}" : Status;
}

public sealed class ServerEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Website { get; set; } = "";
    public bool Favorite { get; set; }
    public List<Observation> Evidence { get; set; } = [];
    public List<DiscordCommunity> Discord { get; set; } = [];
    [JsonIgnore] public string Initials => string.Concat(Name.Split(' ',StringSplitOptions.RemoveEmptyEntries).Take(2).Select(x=>x[..1])).ToUpperInvariant();
    [JsonIgnore] public string Star => Favorite ? "★" : "☆";
    [JsonIgnore] public DateTimeOffset LastSeen => Evidence.Count>0 ? Evidence.Max(x=>x.SeenAt) : DateTimeOffset.MinValue;
    [JsonIgnore] public string CapLabel => Evidence.Where(x=>x.Cap.HasValue).Select(x=>$"{x.Cap}").Distinct().ToArray() is {Length:>0} caps ? "CAP " + string.Join(" / ",caps) : "CAP ?";
    [JsonIgnore] public string RaceLabel => string.Join(" / ", Evidence.Select(x=>x.Race).Where(x=>x.Length>0).Distinct()) is {Length:>0} s ? s : "Race unknown";
    [JsonIgnore] public string RateLabel => string.Join(" / ", Evidence.Select(x=>x.Rates).Where(x=>x.Length>0).Distinct()) is {Length:>0} s ? s : "Rates unknown";
    [JsonIgnore] public string BotLabel => string.Join(" / ", Evidence.Select(x=>x.BotPolicy).Where(x=>x.Length>0).Distinct()) is {Length:>0} s ? s : "Bot policy unknown";
    [JsonIgnore] public string SourceLabel => string.Join(" + ",Evidence.Select(x=>x.SourceName).Distinct());
    [JsonIgnore] public string SeenLabel => $"Listed {LastSeen.ToLocalTime():dd MMM yyyy} • game status unverified";
    [JsonIgnore] public string DiscordLabel => Discord.Count>0 ? $"{Discord.Count} Discord link{(Discord.Count>1 ? "s" : "")}" : "No Discord found";
    [JsonIgnore] public bool HasDiscord => Discord.Count>0;
    [JsonIgnore] public string SearchText => $"{Name} {Website} {CapLabel} {RaceLabel} {RateLabel} {BotLabel} {SourceLabel} {string.Join(' ',Evidence.Select(x=>x.Title))}";
}

public sealed class Catalog
{
    public int SchemaVersion { get; set; } = 1;
    public List<ServerEntry> Servers { get; set; } = [];
    public List<SourceConfig> Sources { get; set; } = SourceConfig.Defaults();
    public List<string> KnownBuiltInSourceIds { get; set; } = [];
    public int PageLimit { get; set; } = 5;
    public bool AutoRefresh { get; set; }
    public DateTimeOffset? LastRefresh { get; set; }
}

public static class CatalogMerge
{
    public static void AddBuiltInSources(Catalog catalog)
    {
        // Version 1.0 did not record removals. Never resurrect its three old defaults.
        if(catalog.KnownBuiltInSourceIds.Count==0)catalog.KnownBuiltInSourceIds.AddRange(["nostalgic","arena","epvp"]);
        foreach(var source in SourceConfig.Defaults())
        {
            if(!catalog.KnownBuiltInSourceIds.Contains(source.Id) && !catalog.Sources.Any(s=>s.Id==source.Id || s.Url==source.Url))catalog.Sources.Add(source);
            if(!catalog.KnownBuiltInSourceIds.Contains(source.Id))catalog.KnownBuiltInSourceIds.Add(source.Id);
        }
    }
    public static string NameKey(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{N}]", "");
    public static void Merge(List<ServerEntry> all, IEnumerable<ServerEntry> incoming)
    {
        foreach (var entry in incoming)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.Evidence.Count==0) continue;
            var site = UrlRules.SiteKey(entry.Website);
            var matches = all.Where(x =>
                (PageParser.IsThreadUrl(entry.Evidence[0].Url) && x.Evidence.Any(e=>entry.Evidence.Any(n=>e.Url==n.Url))) ||
                (site.Length>0 && site==UrlRules.SiteKey(x.Website)) ||
                x.Discord.Any(d=>entry.Discord.Any(n=>d.Url==n.Url)) ||
                (NameKey(x.Name)==NameKey(entry.Name) && (site.Length==0 || x.Website.Length==0))).ToList();
            if(matches.Count==0) { all.Add(entry); continue; }
            var old=matches[0];
            // A first post can connect an indexed thread to an existing directory record.
            foreach(var duplicate in matches.Skip(1)){Combine(old,duplicate);all.Remove(duplicate);}
            Combine(old,entry);
        }
    }
    private static void Combine(ServerEntry old,ServerEntry entry)
    {
            if(ReferenceEquals(old,entry))return;
            old.Favorite|=entry.Favorite;
            if(old.Website.Length==0) old.Website=entry.Website;
            foreach(var evidence in entry.Evidence)
            {
                if(old.Evidence.Any(x=>x.SourceId==evidence.SourceId && x.Url==evidence.Url && x.SeenAt>evidence.SeenAt)) continue;
                old.Evidence.RemoveAll(x=>x.SourceId==evidence.SourceId && x.Url==evidence.Url);
                old.Evidence.Add(evidence);
            }
            foreach(var discord in entry.Discord)
                if(!old.Discord.Any(x=>x.Url==discord.Url)) old.Discord.Add(discord);
    }
}
