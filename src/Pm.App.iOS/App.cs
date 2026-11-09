using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Pm.App.Design;
using Pm.App.Settings;
using Pm.App.Views;
using Pm.Core.Storage;

namespace Pm.App.iOS;

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
        if (ApplicationLifetime is ISingleViewApplicationLifetime single)
            single.MainView = new MainView(new KeychainStore(), Paths.Home, new IosHost());

        base.OnFrameworkInitializationCompleted();
    }
}
