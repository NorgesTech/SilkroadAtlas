using System.Text;
using System.Text.Json;

namespace SilkroadAtlas.Core;

public static class Storage
{
    public static readonly JsonSerializerOptions Json=new(){WriteIndented=true,PropertyNameCaseInsensitive=true};
    public static Catalog Load(string path)=>JsonSerializer.Deserialize<Catalog>(File.ReadAllText(path),Json)??throw new InvalidDataException("Catalog is empty.");
    public static void Save(string path,Catalog catalog)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp=path+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(catalog,Json),new UTF8Encoding(false));
        File.Move(temp,path,true);
    }
    public static string Csv(IEnumerable<ServerEntry> servers)
    {
        static string Q(string s){if(s.Length>0 && "=+-@\t\r".Contains(s[0]))s="'"+s;return "\""+s.Replace("\"","\"\"")+"\"";}
        var sb=new StringBuilder("Name,Website,Cap,Race,Rates,Bot policy,Discord links,Sources,Last listed UTC,Favorite\r\n");
        foreach(var e in servers)sb.AppendLine(string.Join(',',new[]{e.Name,e.Website,e.CapLabel,e.RaceLabel,e.RateLabel,e.BotLabel,string.Join(" | ",e.Discord.Select(d=>d.Url)),string.Join(" | ",e.Evidence.Select(v=>v.Url).Distinct()),e.LastSeen.ToString("O"),e.Favorite.ToString()}.Select(Q)));
        return sb.ToString();
    }
}
