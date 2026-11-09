namespace Pm.App.Views.MainView;

public sealed partial class MainView
{
    private sealed record DeviceRow(string Id, string Label, string Fingerprint, string Address, bool Local);

    private Control BuildDeviceSettings()
    {
        var panel = new StackPanel { Spacing = 6 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock
        {
            Text = _s.Devices,
            FontSize = 11,
            Foreground = Ui.Faint,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var refresh = Ui.Ghost("↻", ShowSettings);
        refresh.FontSize = 14;
        ToolTip.SetTip(refresh, _s.Refresh);
        Grid.SetColumn(refresh, 1);
        header.Children.Add(refresh);
        panel.Children.Add(header);

        if (_session is null) return panel;

        List<DeviceRow> rows;
        lock (_session.Gate)
        {
            var localId = _session.Config.DeviceId;
            var addresses = _session.Config.Peers
                .GroupBy(p => p.Id)
                .ToDictionary(g => g.Key, g => g.First().Address);
            rows = _session.Keyset.Devices
                .Select(d => new DeviceRow(
                    d.Id,
                    string.IsNullOrWhiteSpace(d.Label) ? ShortId(d.Id) : d.Label,
                    Crypto.Fingerprint(d.PubKey),
                    d.Id == localId ? _address ?? "" : addresses.GetValueOrDefault(d.Id, ""),
                    d.Id == localId))
                .OrderBy(r => r.Local ? 0 : 1)
                .ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (rows.Count == 1)
        {
            panel.Children.Add(new TextBlock
            {
                Text = _s.NoOtherDevices,
                FontSize = 12,
                Foreground = Ui.Faint,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        foreach (var row in rows)
            panel.Children.Add(DeviceRowControl(row));

        if (rows.Count > 1)
        {
            panel.Children.Add(new TextBlock
            {
                Text = _s.RevokeWarning,
                FontSize = 11,
                Foreground = Ui.Faint,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        return panel;
    }

    private Control DeviceRowControl(DeviceRow row)
    {
        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(new TextBlock
        {
            Text = row.Local ? $"{row.Label} · {_s.ThisDevice}" : row.Label,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Ui.TextStrong,
            TextWrapping = TextWrapping.Wrap,
        });

        var details = $"#{ShortId(row.Id)} · {row.Fingerprint}";
        if (!string.IsNullOrWhiteSpace(row.Address)) details += $" · {row.Address}";
        content.Children.Add(new TextBlock
        {
            Text = details,
            FontSize = 10,
            Foreground = Ui.Faint,
            TextWrapping = TextWrapping.Wrap,
        });

        if (!row.Local)
        {
            var armed = false;
            Button? revoke = null;
            revoke = Ui.Button(_s.Revoke, () =>
            {
                if (!armed)
                {
                    armed = true;
                    if (revoke is not null) revoke.Content = _s.ConfirmRevoke;
                    return;
                }

                RevokeDevice(row.Id);
            }, danger: true, small: true);
            content.Children.Add(revoke);
        }

        return new Border
        {
            BorderBrush = Ui.LineFaint,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Child = content,
        };
    }

    private void RevokeDevice(string id)
    {
        if (_session is null) return;
        try
        {
            if (!_session.RemoveDevice(id))
            {
                SetStatus(_s.DeviceNotFound);
                return;
            }

            RefreshList();
            SetStatus(_s.Revoked);
            ShowSettings();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private static string ShortId(string id) => id.Length > 8 ? id[..8] : id;
}
