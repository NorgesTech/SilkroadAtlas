using System.IO;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using SilkroadAtlas.Core;

namespace SilkroadAtlas;

public sealed record CommunityRow(string ServerName,DiscordCommunity Community,ImageSource? Icon);
public partial class MainWindow : Window
{
    private Catalog catalog=new();
    private readonly string dataPath;
    private readonly DiscoveryService discovery=new();
    private CancellationTokenSource? operation;
    private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromHours(1)};
    private bool ready;
    private string page="servers";
    private ServerEntry? selected;
    private List<ServerEntry> visible=[];
    public MainWindow(string path)
    {
        dataPath=path;InitializeComponent();
        string warning="";
        try
        {
            catalog=Storage.Load(File.Exists(path)?path:Path.Combine(AppContext.BaseDirectory,"Assets","seed.json"));
        }
        catch(Exception ex) when(ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            warning="Could not load catalog: "+ex.Message;
            // Preserve an unreadable user file before any future write.
            if(File.Exists(path))try{File.Copy(path,path+".unreadable-"+DateTimeOffset.UtcNow.ToUnixTimeSeconds());}catch(IOException){}
            try{catalog=Storage.Load(Path.Combine(AppContext.BaseDirectory,"Assets","seed.json"));}catch{catalog=new();}
        }
        if(File.Exists(path))
        {
            try{CatalogMerge.Merge(catalog.Servers,Storage.Load(Path.Combine(AppContext.BaseDirectory,"Assets","seed.json")).Servers);}
            catch(Exception ex) when(ex is IOException or System.Text.Json.JsonException or InvalidDataException){warning="Bundled snapshot unavailable; your saved catalog is still loaded.";}
        }
        CatalogMerge.AddBuiltInSources(catalog);
        CapFilter.Items.Add("All caps");
        foreach(var n in catalog.Servers.SelectMany(s=>s.Evidence).Where(e=>e.Cap.HasValue).Select(e=>e.Cap!.Value).Distinct().Order())CapFilter.Items.Add(n.ToString());
        CapFilter.SelectedIndex=0;RaceFilter.SelectedIndex=0;SortFilter.SelectedIndex=0;
        PageLimitBox.SelectedIndex=catalog.PageLimit switch {15=>1,40=>2,100=>3,_=>0};AutoRefreshBox.IsChecked=catalog.AutoRefresh;
        ready=true;UpdateAll();SwitchPage("servers");
        StatusText.Text=warning.Length>0?warning:catalog.LastRefresh is {} refreshed?$"Saved catalog • last refresh attempt {refreshed.ToLocalTime():dd MMM yyyy HH:mm}. See Sources for coverage.":"Bundled source snapshot • select Refresh sources to gather current listings.";
        timer.Tick+=async (_,_)=>{if(catalog.AutoRefresh && operation==null)await RefreshCatalog();};timer.Start();
        Closing+=(_,_)=>{timer.Stop();operation?.Cancel();Save();};
    }
    private void Save()
    {
        try{Storage.Save(dataPath,catalog);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){MessageBox.Show(this,"Changes are in memory, but could not be saved: "+ex.Message,"Catalog storage",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
    private void UpdateAll()
    {
        ServerCount.Text=catalog.Servers.Count.ToString("N0");DiscordCount.Text=catalog.Servers.SelectMany(x=>x.Discord).Select(x=>x.Url).Distinct().Count().ToString("N0");
        FilterSources();
        var cap=CapFilter.SelectedItem?.ToString()??"All caps";
        var knownCaps=catalog.Servers.SelectMany(x=>x.Evidence).Where(x=>x.Cap.HasValue).Select(x=>x.Cap!.Value).Distinct().Order().Select(x=>x.ToString()).ToArray();
        ready=false;CapFilter.Items.Clear();CapFilter.Items.Add("All caps");foreach(var item in knownCaps)CapFilter.Items.Add(item);CapFilter.SelectedItem=CapFilter.Items.Contains(cap)?cap:"All caps";ready=true;
        ApplyFilter();RefreshCommunities();ShowDetails(selected);
    }
    private void Navigate(object sender,RoutedEventArgs e)=>SwitchPage((string)((Button)sender).Tag);
    private void SwitchPage(string target)
    {
        page=target;
        DirectoryPage.Visibility=target is "servers" or "favorites"?Visibility.Visible:Visibility.Collapsed;
        CommunitiesPage.Visibility=target=="discord"?Visibility.Visible:Visibility.Collapsed;SourcesPage.Visibility=target=="sources"?Visibility.Visible:Visibility.Collapsed;
        foreach(var b in new[]{ServersNav,DiscordNav,FavoritesNav,SourcesNav})b.Background=(string)b.Tag==target?new SolidColorBrush(Color.FromRgb(38,48,57)):Brushes.Transparent;
        PageTitle.Text=target=="favorites"?"Your saved destinations.":"Find your next Silkroad adventure.";
        PageDescription.Text=target=="favorites"?"The servers you want to come back to.":"Explore private servers and the communities behind them.";
        ApplyFilter();
    }
    private void FilterChanged(object sender,RoutedEventArgs e){if(ready)ApplyFilter();}
    private void ApplyFilter()
    {
        if(!ready)return;
        var query=SearchBox.Text.Trim();var cap=CapFilter.SelectedItem?.ToString();var race=(RaceFilter.SelectedItem as ComboBoxItem)?.Content.ToString();
        var list=catalog.Servers.Where(s=>(page!="favorites" || s.Favorite) && (query.Length==0 || s.SearchText.Contains(query,StringComparison.OrdinalIgnoreCase)) && (DiscordOnly.IsChecked!=true || s.HasDiscord));
        if(int.TryParse(cap,out int n))list=list.Where(s=>s.Evidence.Any(x=>x.Cap==n));
        if(race is "CH" or "EU" or "CH + EU")list=list.Where(s=>s.Evidence.Any(x=>x.Race==race));
        visible=(SortFilter.SelectedIndex switch {1=>list.OrderByDescending(x=>x.LastSeen).ThenBy(x=>x.Name),2=>list.OrderByDescending(x=>x.Evidence.Select(e=>e.SourceId).Distinct().Count()).ThenBy(x=>x.Name),_=>list.OrderBy(x=>x.Name,StringComparer.OrdinalIgnoreCase)}).ToList();
        var previous=selected;ServerList.ItemsSource=visible;
        ResultCount.Text=$"{visible.Count:N0} results   /   Search names, caps, rates, or sources";
        EmptyState.Visibility=visible.Count==0?Visibility.Visible:Visibility.Collapsed;
        ServerList.SelectedItem=previous!=null && visible.Contains(previous)?previous:visible.FirstOrDefault();
        if(visible.Count==0)ShowDetails(null);
    }
    private void ResetFilters(object sender,RoutedEventArgs e){ready=false;SearchBox.Clear();CapFilter.SelectedIndex=0;RaceFilter.SelectedIndex=0;SortFilter.SelectedIndex=0;DiscordOnly.IsChecked=false;ready=true;ApplyFilter();}
    private void SelectServer(object sender,SelectionChangedEventArgs e)=>ShowDetails(ServerList.SelectedItem as ServerEntry);
    private void ShowDetails(ServerEntry? server)
    {
        selected=server;DetailName.Text=server?.Name??"Choose a server";DetailSeen.Text=server?.SeenLabel??"Select a listing to explore its sources and community.";
        DetailFacts.Text=server==null?"":$"{server.CapLabel}   •   {server.RaceLabel}\n{server.RateLabel}\n{server.BotLabel}";
        DetailWebsite.Text=server?.Website??"";VisitButton.IsEnabled=server?.Website.Length>0;ScanButton.IsEnabled=server?.Website.Length>0 && operation==null;
        DetailDiscord.ItemsSource=server?.Discord;EvidenceList.ItemsSource=server?.Evidence.OrderByDescending(x=>x.SeenAt).ToList();
    }
    private void FavoriteClick(object sender,RoutedEventArgs e){if(((Button)sender).Tag is ServerEntry s){s.Favorite=!s.Favorite;Save();ApplyFilter();}e.Handled=true;}
    private void OpenLink(object sender,RoutedEventArgs e)=>Open(((Button)sender).Tag as string??"");
    private void VisitWebsite(object sender,RoutedEventArgs e)=>Open(selected?.Website??"");
    private void Open(string raw)
    {
        var url=UrlRules.PublicUrl(raw);if(url.Length==0){StatusText.Text="This link is not a public HTTP or HTTPS address.";return;}
        try{Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}catch(Exception ex){StatusText.Text="Could not open browser: "+ex.Message;}
    }
    private void SetBusy(bool busy)
    {
        RefreshButton.IsEnabled=!busy;VerifyButton.IsEnabled=!busy;AddSourceButton.IsEnabled=!busy;SourceList.IsEnabled=!busy;
        PageLimitBox.IsEnabled=!busy;AutoRefreshBox.IsEnabled=!busy;CancelButton.Visibility=busy?Visibility.Visible:Visibility.Collapsed;BusyBar.Visibility=busy?Visibility.Visible:Visibility.Collapsed;
        ScanButton.IsEnabled=!busy && selected?.Website.Length>0;
    }
    private async Task Work(Func<CancellationToken,Task<string>> action)
    {
        if(operation!=null)return;operation=new();SetBusy(true);
        try{StatusText.Text=await action(operation.Token);}
        catch(OperationCanceledException){StatusText.Text="Cancelled • previously collected listings are retained.";}
        catch(Exception ex){StatusText.Text=DiscoveryService.Friendly(ex);}
        finally{operation.Dispose();operation=null;SetBusy(false);Save();UpdateAll();}
    }
    private void Cancel(object sender,RoutedEventArgs e)=>operation?.Cancel();
    private async void RefreshClick(object sender,RoutedEventArgs e)=>await RefreshCatalog();
    private async Task RefreshCatalog()=>await Work(async ct=>
    {
        int successful=0,enabled=catalog.Sources.Count(x=>x.Enabled && x.CanCrawl);var before=catalog.Servers.Count;
        if(enabled==0)return "No sources enabled. Choose at least one in Sources & settings.";
        var progress=new Progress<string>(s=>StatusText.Text=s);
        foreach(var source in catalog.Sources.Where(x=>x.Enabled && x.CanCrawl).ToList())
        {
            ct.ThrowIfCancellationRequested();source.LastAttempt=DateTimeOffset.UtcNow;source.Status="Checking…";
            try
            {
                var result=await discovery.Crawl(source,catalog.PageLimit,progress,ct);
                CatalogMerge.Merge(catalog.Servers,result.Servers);source.Status=result.Status;source.LastCount=result.Servers.Count;
                if(result.Successful){source.LastSuccess=DateTimeOffset.UtcNow;successful++;}
            }
            catch(OperationCanceledException){source.Status="Cancelled • previous data retained";throw;}
            catch(Exception ex){source.Status=DiscoveryService.Friendly(ex);}
            Save();UpdateAll();
        }
        catalog.LastRefresh=DateTimeOffset.UtcNow;
        return $"Refresh finished • {catalog.Servers.Count-before:N0} new listings • {successful}/{enabled} sources completed. See Sources for limits or errors.";
    });
    private async void ScanWebsite(object sender,RoutedEventArgs e)
    {
        var server=selected;if(server==null)return;
        await Work(async ct=>{StatusText.Text="Looking for public Discord invites on "+server.Name;var links=await discovery.ScanWebsite(server,ct);int added=0;foreach(var link in links)if(!server.Discord.Any(d=>d.Url==link.Url)){server.Discord.Add(link);added++;}return $"Website checked • {added} new Discord links found for {server.Name}.";});
    }
    private void CommunityFilterChanged(object sender,RoutedEventArgs e){if(ready)RefreshCommunities();}
    private void RefreshCommunities()
    {
        var query=CommunitySearch.Text.Trim();
        CommunityList.ItemsSource=catalog.Servers.SelectMany(s=>s.Discord.Select(d=>new {Server=s,Community=d})).GroupBy(x=>x.Community.Url)
            .Select(g=>new CommunityRow(string.Join(" • ",g.Select(x=>x.Server.Name).Distinct()),g.First().Community,LoadIcon(g.First().Community)))
            .Where(r=>query.Length==0 || (r.ServerName+" "+r.Community.Name+" "+r.Community.Url).Contains(query,StringComparison.OrdinalIgnoreCase)).OrderBy(r=>r.Community.Name).ToList();
    }
    private string IconPath(DiscordCommunity c)=>Path.Combine(Path.GetDirectoryName(dataPath)!,"icons",Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(c.IconUrl)))+".png");
    private ImageSource? LoadIcon(DiscordCommunity c)
    {
        if(c.IconUrl.Length==0)return null;var path=IconPath(c);if(!File.Exists(path))return null;
        try{var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.DecodePixelWidth=64;bitmap.UriSource=new Uri(path);bitmap.EndInit();bitmap.Freeze();return bitmap;}catch{return null;}
    }
    private async void VerifyDiscord(object sender,RoutedEventArgs e)
    {
        // Only check the visible search results, so a single community can be refreshed quickly.
        var list=(CommunityList.ItemsSource as IEnumerable<CommunityRow>)?.ToList()??[];
        await Work(async ct=>
        {
            int done=0;
            foreach(var row in list)
            {
                ct.ThrowIfCancellationRequested();var community=row.Community;
                StatusText.Text=$"Checking Discord invite {++done}/{list.Count} • {community.Name}";
                await discovery.Verify(community,ct);
                foreach(var copy in catalog.Servers.SelectMany(x=>x.Discord).Where(x=>x.Url==community.Url && x!=community))
                {copy.Name=community.Name;copy.GuildId=community.GuildId;copy.IconUrl=community.IconUrl;copy.Members=community.Members;copy.Online=community.Online;copy.Status=community.Status;copy.CheckedAt=community.CheckedAt;}
                if(community.IconUrl.Length>0 && !File.Exists(IconPath(community)))
                {try{var bytes=await discovery.Icon(community.IconUrl,ct);Directory.CreateDirectory(Path.GetDirectoryName(IconPath(community))!);await File.WriteAllBytesAsync(IconPath(community),bytes,ct);}catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}catch{/* Fallback community symbol remains visible. */}}
                RefreshCommunities();
                if(community.Status.StartsWith("Rate limited"))return "Discord rate limit reached • remaining invites were left unchecked. Try again later.";
            }
            return $"Checked {done} public Discord invites • counts describe Discord members, not game players.";
        });
    }
    private void SourceToggle(object sender,RoutedEventArgs e){Save();FilterSources();}
    private void SourceSearchChanged(object sender,RoutedEventArgs e){if(ready)FilterSources();}
    private void FilterSources()
    {
        var text=SourceSearch.Text.Trim();
        SourceList.ItemsSource=catalog.Sources.Where(s=>text.Length==0 || (s.Name+" "+s.Language+" "+s.Notes+" "+s.Url).Contains(text,StringComparison.OrdinalIgnoreCase)).ToList();
        SourceSummary.Text=$"{catalog.Sources.Count(s=>s.Kind=="Forum")} forum sections • {catalog.Sources.Count(s=>s.Kind=="Reference")} community references • {catalog.Sources.Count(s=>s.Enabled && s.CanCrawl)} enabled importers";
    }
    private void ShowSourceListings(object sender,RoutedEventArgs e)
    {
        if(((Button)sender).Tag is not SourceConfig source)return;
        ready=false;CapFilter.SelectedIndex=0;RaceFilter.SelectedIndex=0;DiscordOnly.IsChecked=false;SearchBox.Text=source.Name;ready=true;SwitchPage("servers");
    }
    private async void RefreshSource(object sender,RoutedEventArgs e)
    {
        if(((Button)sender).Tag is not SourceConfig source || !source.CanCrawl)return;
        await Work(async ct=>{source.LastAttempt=DateTimeOffset.UtcNow;try{var r=await discovery.Crawl(source,catalog.PageLimit,new Progress<string>(s=>StatusText.Text=s),ct);CatalogMerge.Merge(catalog.Servers,r.Servers);source.LastCount=r.Servers.Count;source.Status=r.Status;if(r.Successful)source.LastSuccess=DateTimeOffset.UtcNow;return source.Name+" • "+r.Status;}catch(OperationCanceledException){source.Status="Cancelled • previous data retained";throw;}});
    }
    private void RemoveSource(object sender,RoutedEventArgs e)
    {
        if(operation!=null)return;
        if(((Button)sender).Tag is SourceConfig source){catalog.Sources.Remove(source);Save();UpdateAll();StatusText.Text="Source removed; previously discovered listings remain in your catalog.";}
    }
    private void SettingsChanged(object sender,RoutedEventArgs e)
    {
        if(!ready)return;catalog.PageLimit=int.Parse(((ComboBoxItem)PageLimitBox.SelectedItem).Tag.ToString()!);catalog.AutoRefresh=AutoRefreshBox.IsChecked==true;Save();
    }
    private void AddSource(object sender,RoutedEventArgs e)
    {
        var dialog=new SourceDialog{Owner=this};if(dialog.ShowDialog()!=true || dialog.Result==null)return;
        if(catalog.Sources.Any(s=>s.Url==dialog.Result.Url)){StatusText.Text="That source is already in your list.";return;}
        catalog.Sources.Add(dialog.Result);Save();UpdateAll();StatusText.Text="Source added. Refresh sources to read its public listings.";
    }
    private void Export(object sender,RoutedEventArgs e)
    {
        var dialog=new SaveFileDialog{Filter="CSV spreadsheet (*.csv)|*.csv",FileName="Silkroad-Atlas.csv"};
        if(dialog.ShowDialog(this)==true)try{File.WriteAllText(dialog.FileName,Storage.Csv(visible),new UTF8Encoding(true));StatusText.Text=$"Exported {visible.Count} server results.";}catch(Exception ex){StatusText.Text="Export failed: "+ex.Message;}
    }
    private void ExportBackup(object sender,RoutedEventArgs e)
    {
        var dialog=new SaveFileDialog{Filter="Catalog backup (*.json)|*.json",FileName="Silkroad-Atlas-backup.json"};
        if(dialog.ShowDialog(this)==true)try{Storage.Save(dialog.FileName,catalog);StatusText.Text="Full catalog backup exported.";}catch(Exception ex){StatusText.Text="Export failed: "+ex.Message;}
    }
}

public sealed class SourceDialog : Window
{
    public SourceConfig? Result {get;private set;}
    public SourceDialog()
    {
        Style=(Style)Application.Current.FindResource(typeof(Window));Title="Add a public source";Width=550;Height=450;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var body=new StackPanel{Margin=new Thickness(27)};Content=body;
        body.Children.Add(new TextBlock{Text="Add to your Atlas",FontSize=24,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,18)});
        body.Children.Add(new TextBlock{Text="Server or source name",Margin=new Thickness(0,0,0,6)});var name=new TextBox();body.Children.Add(name);
        body.Children.Add(new TextBlock{Text="Public website or forum URL",Margin=new Thickness(0,14,0,6)});var url=new TextBox{Text="https://"};body.Children.Add(url);
        var kind=new ComboBox{Margin=new Thickness(0,14,0,10),ItemsSource=new[]{"Server website","Forum advertisement index or thread","Arena Top100 directory","Nostalgic Silkroad directory","Community reference (link only)"},SelectedIndex=0};body.Children.Add(kind);
        var error=new TextBlock{Foreground=Brushes.Salmon,FontSize=11,Margin=new Thickness(0,0,0,10)};body.Children.Add(error);
        var button=new Button{Content="Add source",Style=(Style)Application.Current.FindResource("Primary"),HorizontalAlignment=HorizontalAlignment.Right,IsDefault=true};body.Children.Add(button);
        button.Click+=(_,_)=>{var valid=UrlRules.PublicUrl(url.Text);if(valid.Length==0 || name.Text.Trim().Length<2){error.Text="Enter a name and a public HTTP or HTTPS address.";return;}Result=new(){Name=name.Text.Trim(),Url=valid,Kind=kind.SelectedIndex switch{1=>"Forum",2=>"Arena",3=>"Nostalgic",4=>"Reference",_=>"Website"},Enabled=kind.SelectedIndex!=4};DialogResult=true;};
    }
}
