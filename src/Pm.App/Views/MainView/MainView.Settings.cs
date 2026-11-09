using Pm.App.Settings.Ui;

namespace Pm.App.Views.MainView;

public sealed partial class MainView
{
    // ---------- settings ----------

    private void ShowSettings()
    {
        if (_session is null) return;
        CloseOverlay();

        var panel = new StackPanel { Spacing = 12, Width = 320 };
        panel.Children.Add(new TextBlock
        {
            Text = _s.Settings,
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Foreground = Ui.TextStrong,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{_s.Keychain}: {_secrets.Name}",
            FontSize = 11,
            Foreground = Ui.Faint,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{_s.Home}: {_home}",
            FontSize = 11,
            Foreground = Ui.Faint,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock { Text = _s.Language, FontSize = 11, Foreground = Ui.Faint });
        var languageBox = new ComboBox
        {
            ItemsSource = new[] { _s.LanguageSystem, _s.LanguageRussian, _s.LanguageEnglish },
            SelectedIndex = UiSettings.Current.Language switch { "ru" => 1, "en" => 2, _ => 0 },
            Width = 220,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        languageBox.SelectionChanged += (_, _) =>
        {
            if (languageBox.SelectedIndex >= 0)
                ApplyLanguage(languageBox.SelectedIndex switch { 1 => "ru", 2 => "en", _ => "system" });
        };
        panel.Children.Add(languageBox);

        panel.Children.Add(new Border { Height = 1, Background = Ui.LineFaint, Margin = new Thickness(0, 6) });
        panel.Children.Add(new TextBlock { Text = _s.Theme, FontSize = 11, Foreground = Ui.Faint });

        var current = UiSettings.Current.Theme;
        panel.Children.Add(ThemeOption("system", _s.ThemeSystem, current));
        panel.Children.Add(ThemeOption("light", _s.ThemeLight, current));
        panel.Children.Add(ThemeOption("dark", _s.ThemeDark, current));

        panel.Children.Add(new Border { Height = 1, Background = Ui.LineFaint, Margin = new Thickness(0, 6) });
        panel.Children.Add(BuildDeviceSettings());

        panel.Children.Add(new Border { Height = 1, Background = Ui.LineFaint, Margin = new Thickness(0, 6) });
        panel.Children.Add(BuildPassphraseSettings());

        var close = Ui.Button(_s.Close, CloseOverlay);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(close);

        var card = Ui.Card(new ScrollViewer { Content = panel, MaxHeight = 520 }, 400);
        var dim = new Grid { Background = Ui.Dim, Children = { card } };
        dim.PointerPressed += (_, e) =>
        {
            if (e.Source == dim) CloseOverlay();
        };

        _root.Children.Add(dim);
        _overlay = dim;
    }

    private void ApplyLanguage(string key)
    {
        UiSettings.SaveLanguage(_home, key);
        UiSettings.ApplyLanguage();
        Strings.Reload();
        _s = Strings.Current;

        CloseOverlay();
        _status.Text = "";
        SetPlaceholder(_search, _s.Search);
        SetPlaceholder(_title, _s.Name);
        SetPlaceholder(_login, _s.Login);
        SetPlaceholder(_password, _s.Password);
        SetPlaceholder(_url, _s.Address);
        SetPlaceholder(_notes, _s.Notes);
        if (_titleError.IsVisible) _titleError.Text = _s.NeedTitle;
        if (TopLevel.GetTopLevel(this) is Window window) window.Title = _s.AppTitle;

        ShowMain();
        ShowSettings();
    }

    private static void SetPlaceholder(TextBox box, string text)
    {
        box.PlaceholderText = text;
        AutomationProperties.SetName(box, text);
    }

    private RadioButton ThemeOption(string key, string label, string current)
    {
        var radio = new RadioButton
        {
            Content = label,
            GroupName = "theme",
            IsChecked = current == key,
        };
        radio.IsCheckedChanged += (_, _) =>
        {
            if (radio.IsChecked == true) ApplyTheme(key);
        };
        return radio;
    }

    private void ApplyTheme(string key)
    {
        UiSettings.Save(_home, key);
        if (Application.Current is { } app)
            app.RequestedThemeVariant = UiSettings.Variant(key);
    }
}
