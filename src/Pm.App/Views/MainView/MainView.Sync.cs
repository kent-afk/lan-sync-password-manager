namespace Pm.App.Views.MainView;

public sealed partial class MainView
{
    // ---------- sync ----------

    private async Task StartHostAsync()
    {
        if (_session is null) return;
        try
        {
            _address = await _host.StartAsync(_session);
            SetStatus(_address is null ? _s.SyncOnOpen : _s.Listening(_address));
            UpdateMeta();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private async Task SyncAsync(bool announce)
    {
        if (_session is null || _syncing || _pairConfirmation) return;
        if (_session.Config.Peers.Count == 0)
        {
            if (announce) SetStatus(_s.NoPeers);
            return;
        }

        _syncing = true;
        if (announce) SetStatus(_s.Syncing);
        try
        {
            var (peers, conflicts) = await _host.SyncAsync(_session);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SetStatus(peers == 0 ? _s.PeersOffline : _s.Synced(peers, conflicts));
                RefreshList();
                RefreshSelected();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => SetStatus(_s.SyncError(ex.Message)));
        }
        finally
        {
            _syncing = false;
        }
    }

    private void StartTimer()
    {
        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => _ = SyncAsync(false);
        _timer.Start();
    }
}
