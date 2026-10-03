using System.IO;
using System.Windows;

namespace SilkroadAtlas;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException+=(s,a)=>{MessageBox.Show(a.Exception.Message,"Silkroad Atlas",MessageBoxButton.OK,MessageBoxImage.Error);a.Handled=true;};
        var dataArg=Array.IndexOf(e.Args,"--data");
        var data=dataArg>=0 && e.Args.Length>dataArg+1?Path.GetFullPath(e.Args[dataArg+1]):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SilkroadAtlas","catalog.json");
        MainWindow=new MainWindow(data);MainWindow.Show();
    }
}
