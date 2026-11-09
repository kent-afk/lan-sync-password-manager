using Pm.App.Settings.Ui;

namespace Pm.App.Views.MainView;

public sealed partial class MainView
{
    // ---------- bootstrap ----------

    private async Task BootAsync()
    {
        var firstRun = !Paths.ExistsIn(_home);
        Control? splash = firstRun ? ShowSplash() : null;
        var started = DateTime.UtcNow;

        VaultOpenResult result;
        try
        {
            result = await Task.Run(() => VaultOpener.Open(_home, Environment.MachineName, _secrets, create: true));
        }
        catch (Exception ex)
        {
            if (splash is not null) _root.Children.Remove(splash);
            ShowFatal(ex.Message);
            return;
        }

        if (result.State == VaultOpenState.NeedsPassphrase)
        {
            if (splash is not null)
            {
                _root.Children.Remove(splash);
                splash = null;
            }

            var unlocked = await ShowPassphraseUnlockAsync();
            if (unlocked is null) return;
            result = new VaultOpenResult(VaultOpenState.Opened, unlocked);
        }

        if (result.State == VaultOpenState.NeedsLegacyPassword)
        {
            if (splash is not null) _root.Children.Remove(splash);
            ShowMigration();
            return;
        }

        if (splash is not null)
        {
            var remain = TimeSpan.FromMilliseconds(1600) - (DateTime.UtcNow - started);
            if (remain > TimeSpan.Zero) await Task.Delay(remain);
            await FadeOutAsync(splash);
            _root.Children.Remove(splash);
        }

        if (firstRun && !VaultSecurity.IsEnabled(_home, _secrets) && !UiSettings.Current.PassphraseAsked)
            await OfferPassphraseAsync(result.Session!);

        Attach(result.Session!);
    }

    private Control ShowSplash()
    {
        var title = new TextBlock
        {
            Text = _s.Greeting,
            FontSize = 64,
            FontWeight = FontWeight.SemiBold,
            Foreground = Ui.TextStrong,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0,
        };

        var scale = new ScaleTransform(0.96, 0.96);
        title.RenderTransform = scale;
        title.RenderTransformOrigin = RelativePoint.Center;
        scale.Transitions = new Transitions
        {
            new DoubleTransition { Property = ScaleTransform.ScaleXProperty, Duration = TimeSpan.FromMilliseconds(520), Easing = Ui.Ease },
            new DoubleTransition { Property = ScaleTransform.ScaleYProperty, Duration = TimeSpan.FromMilliseconds(520), Easing = Ui.Ease },
        };
        title.Transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(420), Easing = Ui.Ease },
        };

        var stack = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                title,
                new TextBlock
                {
                    Text = _s.GreetingSub,
                    FontSize = 15,
                    Foreground = Ui.Text,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = _s.GreetingHint,
                    FontSize = 12,
                    Foreground = Ui.Faint,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 18, 0, 0),
                },
            },
        };

        var splash = new Grid { Background = Ui.Well, Children = { stack } };
        _root.Children.Add(splash);
        Dispatcher.UIThread.Post(() =>
        {
            title.Opacity = 1;
            scale.ScaleX = 1;
            scale.ScaleY = 1;
        });
        return splash;
    }

    private static async Task FadeOutAsync(Control control)
    {
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(240),
            Easing = Ui.EaseIn,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 1d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 0d) } },
            },
        };
        await animation.RunAsync(control);
    }

    private void Attach(VaultSession session)
    {
        _session = session;
        _passphraseEnabled = VaultSecurity.IsEnabled(_home, _secrets);
        ShowMain();
        StartLockTimer();
        _ = StartHostAsync();
        StartTimer();
        _ = SyncAsync(false);
        if (!session.Config.TourShown) _ = ShowTourWhenReadyAsync();
    }

    private async Task ShowTourWhenReadyAsync()
    {
        await Task.Delay(550);
        if (_session is not null && !_session.Config.TourShown) ShowTour();
    }

    private void ShowFatal(string message)
    {
        _root.Children.Clear();
        var text = new TextBlock
        {
            Text = _s.Fatal + "\n\n" + message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ui.Text,
            FontSize = 13,
            MaxWidth = 460,
        };
        var retry = Ui.Button(_s.Retry, () =>
        {
            _booted = false;
            _root.Children.Clear();
            _ = BootAsync();
        }, primary: true);

        var stack = new StackPanel { Spacing = 18, Children = { text, retry } };
        _root.Children.Add(Ui.Card(stack, 520));
    }

    private void ShowMigration()
    {
        _root.Children.Clear();
        var password = Ui.Input(_s.Password);
        password.PasswordChar = '●';
        var error = new TextBlock { Foreground = Ui.Danger, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var go = Ui.Button(_s.Continue, () => _ = MigrateAsync(password, error), primary: true);

        var stack = new StackPanel
        {
            Spacing = 12,
            Width = 380,
            Children =
            {
                new TextBlock { Text = _s.MigrateTitle, FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextStrong },
                new TextBlock { Text = _s.MigrateBody, FontSize = 13, Foreground = Ui.Text, TextWrapping = TextWrapping.Wrap },
                PasswordField(_s.Password, password),
                error,
                go,
            },
        };

        _root.Children.Add(Ui.Card(stack, 460));
        Dispatcher.UIThread.Post(() => password.Focus());
    }

    private async Task MigrateAsync(TextBox password, TextBlock error)
    {
        error.Text = "";
        try
        {
            var result = await Task.Run(() =>
                VaultOpener.Open(_home, Environment.MachineName, _secrets, password.Text ?? "", create: true));
            if (result.Session is null)
            {
                error.Text = _s.Fatal;
                return;
            }

            _root.Children.Clear();
            Attach(result.Session);
        }
        catch (Exception ex)
        {
            error.Text = ex.Message;
        }
    }
}
