using Avalonia.Controls;
using Avalonia.Platform;
using Pm.App.Design;
using Pm.App.Views;
using Pm.App.Views.MainView;
using Pm.Core.Storage;
using Pm.Desktop.Hosting;
using Pm.Secrets;

namespace Pm.Desktop;

public sealed class MainWindow : Window
{
    public MainWindow()
    {
        Title = Strings.Current.AppTitle;
        Width = 1060;
        Height = 720;
        MinWidth = 840;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Well;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://pm-desktop/Assets/pm.png")));

        Content = new MainView(SecretStores.Create(), Paths.Home, new DesktopHost());
    }
}
