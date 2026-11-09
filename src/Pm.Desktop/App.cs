using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Pm.App;
using Pm.App.Design;
using Pm.App.Settings;
using Pm.App.Settings.Ui;
using Pm.Core.Storage;

namespace Pm.Desktop;

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var settings = UiSettings.Load(Paths.Home);
        UiSettings.ApplyLanguage();
        Strings.Reload();
        RequestedThemeVariant = UiSettings.Variant(settings.Theme);
        Ui.Apply(ActualThemeVariant);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();

        base.OnFrameworkInitializationCompleted();
    }
}
