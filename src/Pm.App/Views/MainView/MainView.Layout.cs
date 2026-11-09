namespace Pm.App.Views.MainView;

public sealed partial class MainView
{
    // ---------- main screen ----------

    private void ShowMain()
    {
        DetachReusable();
        _root.Children.Clear();
        _mainScreen = BuildMain();
        _root.Children.Add(_mainScreen);
        RefreshList();
        AnimateIn(_mainScreen);
    }

    private void DetachReusable()
    {
        Detach(_search);
        Detach(_rows);
        Detach(_status);
        Detach(_meta);
        Detach(_title);
        Detach(_titleError);
        Detach(_formTitle);
        Detach(_login);
        Detach(_password);
        Detach(_url);
        Detach(_notes);
    }

    private static void Detach(Control control)
    {
        switch (control.Parent)
        {
            case Panel panel:
                panel.Children.Remove(control);
                break;
            case Decorator decorator:
                decorator.Child = null;
                break;
            case ContentPresenter presenter:
                presenter.Content = null;
                break;
            case ContentControl content:
                content.Content = null;
                break;
        }
    }

    private static void AnimateIn(Control screen)
    {
        screen.Opacity = 0;
        var slide = new TranslateTransform(0, 10);
        screen.RenderTransform = slide;
        slide.Transitions = new Transitions
        {
            new DoubleTransition { Property = TranslateTransform.YProperty, Duration = TimeSpan.FromMilliseconds(300), Easing = Ui.Ease },
        };
        screen.Transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(300), Easing = Ui.Ease },
        };

        Dispatcher.UIThread.Post(() =>
        {
            screen.Opacity = 1;
            slide.Y = 0;
        });
    }

    private Grid BuildMain()
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            ColumnDefinitions = new ColumnDefinitions("280,*"),
            Background = Ui.Well,
        };

        var top = BuildTopBar();
        Grid.SetRow(top, 0);
        Grid.SetColumnSpan(top, 2);
        grid.Children.Add(top);

        _search.TextChanged -= OnSearchChanged;
        _search.TextChanged += OnSearchChanged;

        var listHost = new Border
        {
            Child = new ScrollViewer
            {
                Content = _rows,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
            Background = Ui.Well,
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(0, 1, 1, 0),
            Padding = new Thickness(8, 8),
        };
        Grid.SetRow(listHost, 1);
        Grid.SetColumn(listHost, 0);
        grid.Children.Add(listHost);

        _form = BuildEditor();
        var editor = new ScrollViewer
        {
            Content = _form,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(30, 22, 30, 22),
        };
        editor.SizeChanged += (_, _) => FitForm(editor);
        Grid.SetRow(editor, 1);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);

        var bottom = BuildStatusBar();
        Grid.SetRow(bottom, 2);
        Grid.SetColumnSpan(bottom, 2);
        grid.Children.Add(bottom);

        return grid;
    }

    private void FitForm(ScrollViewer editor)
    {
        if (_form is null) return;
        var available = editor.Bounds.Width - editor.Padding.Left - editor.Padding.Right - 16;
        _form.Width = Math.Clamp(available, 0, 560);
    }

    private Control BuildTopBar()
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        _addButton = Ui.Button(_s.Add, NewEntry, primary: true);
        _pairButton = Ui.Button(_s.Pair, ShowPairing);
        actions.Children.Add(_addButton);
        actions.Children.Add(_pairButton);
        actions.Children.Add(Ui.Button(_s.Sync, () => _ = SyncAsync(true)));
        _lockButton = Ui.Button(_s.Lock, () => _ = LockAsync());
        _lockButton.IsVisible = _passphraseEnabled;
        actions.Children.Add(_lockButton);

        var settings = Ui.Ghost("⚙", ShowSettings);
        settings.FontSize = 16;
        ToolTip.SetTip(settings, _s.Settings);
        AutomationProperties.SetName(settings, _s.Settings);
        actions.Children.Add(settings);

        var tour = Ui.Ghost("?", () => ShowTour(force: true));
        ToolTip.SetTip(tour, _s.Tour);
        AutomationProperties.SetName(tour, _s.Tour);
        actions.Children.Add(tour);

        _search.MaxWidth = 320;
        _search.MinWidth = 160;
        _search.Margin = new Thickness(0, 0, 16, 0);
        _search.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(_search, _s.Search);

        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_search, 0);
        Grid.SetColumn(actions, 1);
        bar.Children.Add(_search);
        bar.Children.Add(actions);

        return new Border
        {
            Background = Ui.Surface,
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 10),
            Child = bar,
        };
    }

    private StackPanel BuildEditor()
    {
        var panel = new StackPanel
        {
            Spacing = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _formTitle.Text = _selected is null ? _s.NewEntry : _s.Editing;
        panel.Children.Add(_formTitle);

        var titleField = Ui.Field(_s.Name, _title);
        if (titleField is StackPanel titleStack) titleStack.Children.Add(_titleError);
        panel.Children.Add(titleField);
        panel.Children.Add(Ui.Field(_s.Login, _login));

        var passwordButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 0, 0, 0) };
        _revealButton = Ui.Button(_password.RevealPassword ? _s.HidePassword : _s.Reveal, ToggleReveal, small: true);
        passwordButtons.Children.Add(_revealButton);
        passwordButtons.Children.Add(Ui.Button(_s.Generate, () => _password.Text = DeviceKey.GeneratePassword(), small: true));
        passwordButtons.Children.Add(Ui.Button(_s.Copy, CopyPassword, small: true));

        var passwordRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(passwordButtons, Dock.Right);
        passwordRow.Children.Add(passwordButtons);
        passwordRow.Children.Add(_password);
        panel.Children.Add(Ui.Field(_s.Password, passwordRow));

        panel.Children.Add(Ui.Field(_s.Address, _url));
        panel.Children.Add(Ui.Field(_s.Notes, _notes));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(Ui.Button(_s.Save, SaveEntry, primary: true));
        _deleteButton = Ui.Button(_s.Delete, DeleteEntry);
        _confirmDeleteButton = Ui.Button(_s.DeleteConfirm, DeleteEntry, danger: true);
        _confirmDeleteButton.IsVisible = false;
        _cancelDeleteButton = Ui.Button(_s.Cancel, ResetDelete, small: true);
        _cancelDeleteButton.IsVisible = false;
        buttons.Children.Add(_deleteButton);
        buttons.Children.Add(_confirmDeleteButton);
        buttons.Children.Add(_cancelDeleteButton);
        panel.Children.Add(buttons);

        return panel;
    }

    private Control BuildStatusBar()
    {
        var dock = new DockPanel { LastChildFill = true };
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _meta.HorizontalAlignment = HorizontalAlignment.Right;
        _meta.MaxWidth = 360;
        _meta.TextTrimming = TextTrimming.CharacterEllipsis;
        DockPanel.SetDock(_meta, Dock.Right);
        dock.Children.Add(_meta);
        dock.Children.Add(_status);

        return new Border
        {
            Background = Ui.Surface,
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 8),
            Child = dock,
        };
    }
}
