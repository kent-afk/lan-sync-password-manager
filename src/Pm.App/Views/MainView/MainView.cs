namespace Pm.App.Views.MainView;

public sealed partial class MainView : UserControl
{
    private Strings _s = Strings.Current;
    private readonly ISecretStore _secrets;
    private readonly IAppHost _host;
    private readonly string _home;
    private readonly Panel _root = new();
    private readonly ScaleTransform _zoom = new(1, 1);

    private VaultSession? _session;
    private Grid? _mainScreen;
    private Grid? _overlay;
    private Grid? _tour;
    private StackPanel? _form;
    private PairingOffer? _pairOffer;
    private DispatcherTimer? _timer;
    private DispatcherTimer? _clipboardTimer;
    private string? _copiedPassword;
    private bool _booted;
    private bool _keysHooked;
    private bool _syncing;
    private bool _pairConfirmation;
    private bool _confirmingDelete;
    private string? _address;

    private readonly List<Entry> _visible = new();
    private Entry? _selected;

    private readonly TextBox _search = Ui.Input(Strings.Current.Search);
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly TextBlock _status = new() { Foreground = Ui.Faint, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _meta = new() { Foreground = Ui.Faint, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _title = Ui.Input(Strings.Current.Name);
    private readonly TextBlock _titleError = new() { Foreground = Ui.Danger, FontSize = 11, IsVisible = false, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _formTitle = new() { FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextStrong, Margin = new Thickness(0, 0, 0, 16) };
    private readonly TextBox _login = Ui.Input(Strings.Current.Login);
    private readonly TextBox _password = Ui.Input(Strings.Current.Password);
    private readonly TextBox _url = Ui.Input(Strings.Current.Address);
    private readonly TextBox _notes = Ui.Input(Strings.Current.Notes);

    private Button? _addButton;
    private Button? _pairButton;
    private Button? _revealButton;
    private Button? _deleteButton;
    private Button? _confirmDeleteButton;
    private Button? _cancelDeleteButton;

    public MainView(ISecretStore secrets, string home, IAppHost host)
    {
        _secrets = secrets;
        _home = home;
        _host = host;

        _password.PasswordChar = '●';
        _password.RevealPassword = false;
        _notes.AcceptsReturn = true;
        _notes.TextWrapping = TextWrapping.Wrap;
        _notes.Height = 110;

        Ui.Install(Styles);
        AddHandler(PointerPressedEvent, OnBackgroundPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, (_, _) => ResetLockTimer(), RoutingStrategies.Tunnel);
        ActualThemeVariantChanged += (_, _) => Ui.Apply(ActualThemeVariant);
        _title.TextChanged += (_, _) =>
        {
            _title.BorderBrush = Ui.Line;
            _titleError.IsVisible = false;
        };

        Background = Ui.Well;
        Content = new LayoutTransformControl { LayoutTransform = _zoom, Child = _root };
        SizeChanged += (_, _) => ClampZoom();
        DetachedFromVisualTree += (_, _) =>
        {
            _lockTimer?.Stop();
            _lockTimer = null;
            _clipboardTimer?.Stop();
            _clipboardTimer = null;
            _ = ClearCopiedClipboardAsync();
        };

        AttachedToVisualTree += (_, _) =>
        {
            Ui.Apply(ActualThemeVariant);
            if (!_keysHooked && TopLevel.GetTopLevel(this) is { } top)
            {
                top.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
                if (top is Window window)
                    window.Closing += (_, _) =>
                    {
                        _lockTimer?.Stop();
                        _lockTimer = null;
                        _clipboardTimer?.Stop();
                        _clipboardTimer = null;
                        _ = ClearCopiedClipboardAsync();
                    };
                _keysHooked = true;
            }
            if (_booted) return;
            _booted = true;
            _ = BootAsync();
        };
    }

    private void OnBackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        ResetLockTimer();
        if (e.Source is not Visual source) return;

        for (var current = source; current is not null; current = current.GetVisualParent())
        {
            if (current is InputElement { Focusable: true })
                return;
        }

        TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        ResetLockTimer();
        var ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;

        if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add))
        {
            SetZoom(_zoom.ScaleX + 0.1);
            e.Handled = true;
            return;
        }

        if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
        {
            SetZoom(_zoom.ScaleX - 0.1);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.D0)
        {
            SetZoom(1.0);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape) return;

        if (_overlay is not null) CloseOverlay();
        else if (_tour is not null) FinishTour();
        else return;

        e.Handled = true;
    }

    private double MaxZoom => Math.Max(1.0, Math.Min(1.5, Bounds.Width / 840.0));

    private void SetZoom(double value)
    {
        var zoom = Math.Clamp(Math.Round(value, 2), 0.8, MaxZoom);
        _zoom.ScaleX = _zoom.ScaleY = zoom;
        SetStatus(_s.Zoom((int)Math.Round(zoom * 100)));
    }

    private void ClampZoom()
    {
        if (_zoom.ScaleX > MaxZoom) SetZoom(MaxZoom);
    }

    // ---------- helpers ----------

    private void SetStatus(string text)
    {
        if (Dispatcher.UIThread.CheckAccess()) _status.Text = text;
        else Dispatcher.UIThread.Post(() => _status.Text = text);
    }
}
