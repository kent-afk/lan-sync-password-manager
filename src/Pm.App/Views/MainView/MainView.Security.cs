using System.Security.Cryptography;
using Pm.App.Settings.Ui;

namespace Pm.App.Views.MainView;

public sealed partial class MainView
{
    private static readonly TimeSpan LockAfterIdle = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SleepGap = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan LockTick = TimeSpan.FromSeconds(15);

    private Button? _lockButton;
    private bool _passphraseEnabled;
    private bool _locking;
    private DispatcherTimer? _lockTimer;
    private DateTime _lastActivity = DateTime.UtcNow;
    private DateTime _lastTick = DateTime.UtcNow;

    private void StartLockTimer()
    {
        _lockTimer?.Stop();
        _lockTimer = null;
        _lastActivity = DateTime.UtcNow;
        _lastTick = DateTime.UtcNow;
        if (!_passphraseEnabled) return;

        _lockTimer = new DispatcherTimer { Interval = LockTick };
        _lockTimer.Tick += (_, _) => CheckLock();
        _lockTimer.Start();
    }

    private void StopLockTimer()
    {
        _lockTimer?.Stop();
        _lockTimer = null;
    }

    private void ResetLockTimer() => _lastActivity = DateTime.UtcNow;

    private void CheckLock()
    {
        if (_session is null || !_passphraseEnabled) return;

        var now = DateTime.UtcNow;
        // A long gap between ticks means the process was suspended (sleep/background).
        var slept = now - _lastTick > SleepGap;
        _lastTick = now;
        if (slept || now - _lastActivity >= LockAfterIdle)
            _ = LockAsync();
    }

    private Grid AddModal(Control card)
    {
        var dim = new Grid { Background = Ui.Dim, Children = { card } };
        _root.Children.Add(dim);
        return dim;
    }

    private void RemoveModal(Control modal) => _root.Children.Remove(modal);

    private static TextBox SecretInput(string watermark)
    {
        var box = Ui.Input(watermark);
        box.PasswordChar = '●';
        return box;
    }

    private Control PasswordField(string label, TextBox box)
    {
        AutomationProperties.SetName(box, label);
        var reveal = Ui.Ghost("👁", () => box.RevealPassword = !box.RevealPassword);
        ToolTip.SetTip(reveal, _s.Reveal);

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(reveal, Dock.Right);
        row.Children.Add(reveal);
        row.Children.Add(box);
        return Ui.Field(label, row);
    }

    private async Task<VaultSession?> ShowPassphraseUnlockAsync()
    {
        var tcs = new TaskCompletionSource<VaultSession?>();
        var password = SecretInput(_s.Passphrase);
        password.MaxLength = 30;
        var error = new TextBlock
        {
            Foreground = Ui.Danger,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        Grid? modal = null;
        Button? unlock = null;
        unlock = Ui.Button(_s.Unlock, () => _ = TryUnlock(), primary: true);

        var panel = new StackPanel
        {
            Spacing = 12,
            Width = 340,
            Children =
            {
                new TextBlock { Text = _s.LockedTitle, FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextStrong },
                new TextBlock { Text = _s.LockedBody, FontSize = 13, Foreground = Ui.Text, TextWrapping = TextWrapping.Wrap },
                PasswordField(_s.Passphrase, password),
                error,
                unlock,
            },
        };

        modal = AddModal(Ui.Card(panel, 420));
        Dispatcher.UIThread.Post(() => password.Focus());
        password.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            _ = TryUnlock();
        };

        async Task TryUnlock()
        {
            error.IsVisible = false;
            unlock!.IsEnabled = false;
            try
            {
                // libsecret/keychain access must stay on the UI thread.
                var result = VaultOpener.Open(_home, Environment.MachineName, _secrets,
                    passphrase: password.Text ?? "");
                if (result.Session is null)
                {
                    error.Text = _s.PassphraseWrong;
                    error.IsVisible = true;
                    await Task.Delay(600);
                    return;
                }

                RemoveModal(modal!);
                tcs.TrySetResult(result.Session);
            }
            catch (SecretStoreException ex)
            {
                error.Text = ex.Message;
                error.IsVisible = true;
                await Task.Delay(600);
            }
            catch (CryptographicException ex)
            {
                var candidate = password.Text ?? "";
                var valid = VaultSecurity.Verify(_home, _secrets, candidate);
                error.Text = valid ? _s.PassphraseVaultError(ex.Message) : _s.PassphraseWrong;
                error.IsVisible = true;
                await Task.Delay(600);
            }
            catch (Exception ex)
            {
                error.Text = ex.Message;
                error.IsVisible = true;
                await Task.Delay(600);
            }
            finally
            {
                unlock!.IsEnabled = true;
            }
        }

        return await tcs.Task;
    }

    private async Task LockAsync()
    {
        if (_locking || !_passphraseEnabled || _session is null) return;

        _locking = true;
        StopLockTimer();
        try
        {
            _timer?.Stop();
            _timer = null;
            try
            {
                await _host.StopAsync();
            }
            catch
            {
                // the lock screen must still appear
            }

            var session = _session;
            _session = null;
            ClearSensitiveUi();
            _root.Children.Clear();
            session?.Dispose();

            var unlocked = await ShowPassphraseUnlockAsync();
            if (unlocked is null) return;

            _root.Children.Clear();
            Attach(unlocked);
        }
        finally
        {
            _locking = false;
        }
    }

    /// <summary>Drop references to plaintext before and after the lock.</summary>
    private void ClearSensitiveUi()
    {
        CloseOverlay();
        CloseTour();
        _clipboardTimer?.Stop();
        _clipboardTimer = null;
        _ = ClearCopiedClipboardAsync();
        _selected = null;
        _visible.Clear();
        _rows.Children.Clear();
        _title.Text = _login.Text = _password.Text = _url.Text = _notes.Text = "";
        _search.Text = "";
        _formTitle.Text = _s.NewEntry;
        _titleError.IsVisible = false;
        _status.Text = "";
        _meta.Text = "";
        ResetDelete();
        ResetReveal();
    }

    private async Task OfferPassphraseAsync(VaultSession session)
    {
        while (true)
        {
            if (!await ShowPassphrasePromptAsync())
            {
                UiSettings.Current.PassphraseAsked = true;
                UiSettings.Save(_home, UiSettings.Current.Theme);
                return;
            }

            if (await ShowPassphraseSetupAsync(session, change: false))
            {
                UiSettings.Current.PassphraseAsked = true;
                UiSettings.Save(_home, UiSettings.Current.Theme);
                return;
            }
        }
    }

    private async Task<bool> ShowPassphrasePromptAsync()
    {
        var tcs = new TaskCompletionSource<bool>();
        Grid? modal = null;
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        actions.Children.Add(Ui.Ghost(_s.Later, () =>
        {
            RemoveModal(modal!);
            tcs.TrySetResult(false);
        }));
        actions.Children.Add(Ui.Button(_s.Set, () =>
        {
            RemoveModal(modal!);
            tcs.TrySetResult(true);
        }, primary: true));

        var panel = new StackPanel
        {
            Spacing = 10,
            Width = 360,
            Children =
            {
                new TextBlock { Text = _s.PassphrasePromptTitle, FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextStrong, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = _s.PassphrasePromptBody, FontSize = 13, Foreground = Ui.Text, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = _s.PassphraseWarning, FontSize = 12, Foreground = Ui.Danger, TextWrapping = TextWrapping.Wrap },
                actions,
            },
        };

        modal = AddModal(Ui.Card(panel, 440));
        return await tcs.Task;
    }

    private sealed record PassphraseDialog(Grid Modal, TextBlock Error, StackPanel Actions, TaskCompletionSource<bool> Result);

    private PassphraseDialog ShowPassphraseDialog(StackPanel panel)
    {
        var error = new TextBlock
        {
            Foreground = Ui.Danger,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var result = new TaskCompletionSource<bool>();
        Grid? modal = null;
        actions.Children.Add(Ui.Ghost(_s.Cancel, () =>
        {
            RemoveModal(modal!);
            result.TrySetResult(false);
        }));

        panel.Children.Add(error);
        panel.Children.Add(actions);
        modal = AddModal(Ui.Card(new ScrollViewer { Content = panel, MaxHeight = 480 }, 440));
        return new PassphraseDialog(modal, error, actions, result);
    }

    private async Task<bool> ShowPassphraseSetupAsync(VaultSession session, bool change)
    {
        var current = change ? SecretInput(_s.PassphraseCurrent) : null;
        var passphrase = SecretInput(_s.Passphrase);
        var confirm = SecretInput(_s.PassphraseConfirm);
        if (current is not null) current.MaxLength = 30;
        passphrase.MaxLength = 30;
        confirm.MaxLength = 30;

        var panel = new StackPanel
        {
            Spacing = 10,
            Width = 360,
            Children =
            {
                new TextBlock { Text = _s.PassphraseTitle, FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextStrong },
                new TextBlock { Text = _s.PassphraseWarning, FontSize = 12, Foreground = Ui.Danger, TextWrapping = TextWrapping.Wrap },
            },
        };
        if (current is not null) panel.Children.Add(PasswordField(_s.PassphraseCurrent, current));
        panel.Children.Add(PasswordField(_s.Passphrase, passphrase));
        panel.Children.Add(PasswordField(_s.PassphraseConfirm, confirm));

        var dialog = ShowPassphraseDialog(panel);
        dialog.Actions.Children.Add(Ui.Button(_s.Save, Save, primary: true));
        Dispatcher.UIThread.Post(() => (current ?? passphrase).Focus());

        void Save()
        {
            var error = dialog.Error;
            error.IsVisible = false;
            var value = passphrase.Text ?? "";
            var repeat = confirm.Text ?? "";

            if (value.Length is < 4 or > 30)
            {
                error.Text = _s.PassphraseTooShort;
                error.IsVisible = true;
                return;
            }

            if (value != repeat)
            {
                error.Text = _s.PassphraseMismatch;
                error.IsVisible = true;
                return;
            }

            try
            {
                if (change)
                {
                    if (current is null || !VaultSecurity.Verify(_home, _secrets, current.Text ?? ""))
                    {
                        error.Text = _s.PassphraseWrong;
                        error.IsVisible = true;
                        return;
                    }

                    VaultSecurity.Change(_home, _secrets, session.Key, value);
                }
                else
                {
                    VaultSecurity.Enable(_home, _secrets, session.Key, value);
                }

                _passphraseEnabled = true;
                UpdateLockButton();
                StartLockTimer();
                UiSettings.Current.PassphraseAsked = true;
                UiSettings.Save(_home, UiSettings.Current.Theme);
                RemoveModal(dialog.Modal);
                dialog.Result.TrySetResult(true);
            }
            catch (Exception ex)
            {
                error.Text = ex.Message;
                error.IsVisible = true;
            }
        }

        return await dialog.Result.Task;
    }

    private async Task<bool> ShowCurrentPassphraseAsync()
    {
        var password = SecretInput(_s.PassphraseCurrent);
        password.MaxLength = 30;

        var panel = new StackPanel
        {
            Spacing = 10,
            Width = 360,
            Children =
            {
                new TextBlock { Text = _s.RemovePassphrase, FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextStrong },
                new TextBlock { Text = _s.PassphraseWarning, FontSize = 12, Foreground = Ui.Danger, TextWrapping = TextWrapping.Wrap },
                PasswordField(_s.PassphraseCurrent, password),
            },
        };

        var dialog = ShowPassphraseDialog(panel);
        dialog.Actions.Children.Add(Ui.Button(_s.RemovePassphrase, () =>
        {
            if (!VaultSecurity.Verify(_home, _secrets, password.Text ?? ""))
            {
                dialog.Error.Text = _s.PassphraseWrong;
                dialog.Error.IsVisible = true;
                return;
            }

            RemoveModal(dialog.Modal);
            dialog.Result.TrySetResult(true);
        }, danger: true));

        Dispatcher.UIThread.Post(() => password.Focus());
        return await dialog.Result.Task;
    }

    private Control BuildPassphraseSettings()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = _s.PassphraseTitle, FontSize = 11, Foreground = Ui.Faint });
        panel.Children.Add(new TextBlock
        {
            Text = _passphraseEnabled ? _s.PassphraseOn : _s.PassphraseOff,
            FontSize = 12,
            Foreground = Ui.Text,
        });

        if (_passphraseEnabled)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            actions.Children.Add(Ui.Button(_s.ChangePassphrase, () => _ = ChangePassphraseAsync(), small: true));
            actions.Children.Add(Ui.Button(_s.RemovePassphrase, () => _ = RemovePassphraseAsync(), small: true, danger: true));
            panel.Children.Add(actions);
        }
        else
        {
            panel.Children.Add(Ui.Button(_s.SetPassphrase, () => _ = SetPassphraseAsync(), small: true, primary: true));
        }

        return panel;
    }

    private async Task SetPassphraseAsync()
    {
        if (_session is null) return;
        CloseOverlay();
        if (await ShowPassphraseSetupAsync(_session, change: false))
            SetStatus(_s.PassphraseSet);
        ShowSettings();
    }

    private async Task ChangePassphraseAsync()
    {
        if (_session is null) return;
        CloseOverlay();
        if (await ShowPassphraseSetupAsync(_session, change: true))
            SetStatus(_s.PassphraseSet);
        ShowSettings();
    }

    private async Task RemovePassphraseAsync()
    {
        if (_session is null) return;
        CloseOverlay();
        if (!await ShowCurrentPassphraseAsync())
        {
            ShowSettings();
            return;
        }

        try
        {
            VaultSecurity.Disable(_home, _secrets, _session.Key);
            _passphraseEnabled = false;
            StopLockTimer();
            UpdateLockButton();
            SetStatus(_s.PassphraseRemoved);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }

        ShowSettings();
    }

    private void UpdateLockButton()
    {
        if (_lockButton is not null) _lockButton.IsVisible = _passphraseEnabled;
    }
}
