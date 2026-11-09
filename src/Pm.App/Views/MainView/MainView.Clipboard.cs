namespace Pm.App.Views.MainView;

public sealed partial class MainView
{
    private async void CopyPassword()
    {
        var text = _password.Text;
        if (string.IsNullOrEmpty(text)) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        try
        {
            await clipboard.SetTextAsync(text);
            _copiedPassword = text;
            SetStatus(_s.Copied);

            _clipboardTimer?.Stop();
            _clipboardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _clipboardTimer.Tick += async (_, _) =>
            {
                _clipboardTimer?.Stop();
                _clipboardTimer = null;
                await ClearCopiedClipboardAsync();
            };
            _clipboardTimer.Start();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private async Task ClearCopiedClipboardAsync()
    {
        var text = _copiedPassword;
        _copiedPassword = null;
        if (text is null) return;

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;
            if (await clipboard.TryGetTextAsync() == text) await clipboard.ClearAsync();
        }
        catch
        {
            // the clipboard is shared with other apps
        }
    }
}
