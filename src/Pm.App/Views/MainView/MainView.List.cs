namespace Pm.App.Views.MainView;

public sealed partial class MainView
{
    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => RefreshList();

    private void RefreshList()
    {
        if (_session is null) return;

        var query = _search.Text?.Trim() ?? "";
        _visible.Clear();
        _visible.AddRange(_session.Vault.Entries
            .Where(e => !e.Deleted)
            .Where(e => query.Length == 0 ||
                        e.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        e.Login.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        e.Url.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase));

        _rows.Children.Clear();
        if (_visible.Count == 0)
        {
            _rows.Children.Add(new TextBlock
            {
                Text = query.Length == 0 ? _s.EmptyVault : _s.NoMatches,
                Foreground = Ui.Faint,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12, 16, 12, 0),
            });
        }

        foreach (var entry in _visible)
        {
            var entryCopy = entry;
            var title = new TextBlock
            {
                Text = entry.Title,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = Ui.TextStrong,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var login = new TextBlock
            {
                Text = entry.Login,
                FontSize = 11,
                Foreground = Ui.Faint,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                MaxWidth = 110,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var selected = _selected?.Id == entry.Id;

            var rowGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(title, 0);
            Grid.SetColumn(login, 1);
            rowGrid.Children.Add(title);
            rowGrid.Children.Add(login);

            var row = new Button
            {
                Content = rowGrid,
                Padding = new Thickness(10, 8),
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                MinHeight = 34,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetName(row, $"{entry.Title}, {entry.Login}");
            row.Click += (_, _) => SelectEntry(entryCopy);

            var menu = new ContextMenu();
            var edit = new MenuItem { Header = _s.Edit };
            edit.Click += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                SelectEntry(entryCopy);
                _title.Focus();
                _title.SelectAll();
            });
            var delete = new MenuItem { Header = _s.Delete, Foreground = Ui.Danger };
            delete.Click += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                SelectEntry(entryCopy);
                DeleteEntry();
                _confirmDeleteButton?.BringIntoView();
            });
            menu.Items.Add(edit);
            menu.Items.Add(new Separator());
            menu.Items.Add(delete);
            row.ContextMenu = menu;

            var rowHost = new Border
            {
                Background = selected ? Ui.AccentSoft : Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
            };

            Button? more = null;
            more = Ui.Ghost("⋯", () => menu.Open(row));
            more.FontSize = 14;
            more.Opacity = selected ? 1 : 0;
            more.IsHitTestVisible = selected;
            ToolTip.SetTip(more, _s.Actions);
            AutomationProperties.SetName(more, _s.Actions);

            var rowContent = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(row, 0);
            Grid.SetColumn(more, 1);
            rowContent.Children.Add(row);
            rowContent.Children.Add(more);
            rowHost.Child = rowContent;

            rowHost.PointerEntered += (_, _) =>
            {
                more.Opacity = 1;
                more.IsHitTestVisible = true;
            };
            rowHost.PointerExited += (_, _) =>
            {
                more.Opacity = selected ? 1 : 0;
                more.IsHitTestVisible = selected;
            };
            _rows.Children.Add(rowHost);
        }

        UpdateMeta();
    }

    /// <summary>After a sync, show the new values if the form has no unsaved edits.</summary>
    private void RefreshSelected()
    {
        if (_session is null || _selected is null) return;

        var current = _session.Vault.Entries.FirstOrDefault(e => e.Id == _selected.Id && !e.Deleted);
        if (current is null) return;

        var unchanged = _title.Text == _selected.Title &&
                        _login.Text == _selected.Login &&
                        _password.Text == _selected.Password &&
                        _url.Text == _selected.Url &&
                        _notes.Text == _selected.Notes;
        if (!unchanged) return;

        _selected = current;
        _title.Text = current.Title;
        _login.Text = current.Login;
        _password.Text = current.Password;
        _url.Text = current.Url;
        _notes.Text = current.Notes;
    }

    private void UpdateMeta()
    {
        if (_session is null)
        {
            _meta.Text = "";
            return;
        }

        var address = _address ?? "локальная сеть";
        int peers;
        lock (_session.Gate)
            peers = _session.Config.Peers.Count;
        _meta.Text = _s.Meta(_session.Config.Label, _visible.Count, peers + 1, address);
    }
}
