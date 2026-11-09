using Pm.Sync.Hosting;
using Pm.Core.Vault;
using Pm.Net;
using Pm.Sync;

namespace Pm.Desktop.Hosting;

/// <summary>Desktop can listen, advertise itself, and offer pairing codes.</summary>
public sealed class DesktopHost : AppHostBase, IAsyncDisposable
{
    private LanServer? _server;

    public override bool CanOfferPairing => true;

    public override async Task<string?> StartAsync(VaultSession session)
    {
        await base.StartAsync(session);

        try
        {
            _server = await LanServer.StartAsync(session, "0.0.0.0", session.Config.Port);
        }
        catch
        {
            _server = await LanServer.StartAsync(session, "0.0.0.0", 0);
            session.Config.Port = _server.Port;
            session.SaveConfig();
        }

        Discovery.Advertise(session.Config.DeviceId, session.Config.Label, _server.Port,
            () => _server?.Ticket is { IsValid: true });
        return _server.Address;
    }

    public override async Task StopAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
            _server = null;
        }

        await base.StopAsync();
    }

    public override Task<PairingOffer?> OfferPairingAsync(VaultSession session)
    {
        if (_server is null) return Task.FromResult<PairingOffer?>(null);

        var ticket = new PairingTicket();
        _server.Ticket = ticket;
        var offer = new PairingOffer(_server.Address, ticket.Code, ticket.Completed.Task, ticket.Pending.Task,
            ticket.Expires, ticket.Approve, () =>
            {
                ticket.Approve(false);
                if (_server is not null) _server.Ticket = null;
                return ValueTask.CompletedTask;
            });
        return Task.FromResult<PairingOffer?>(offer);
    }

    protected override int JoinPort(VaultSession session) => session.Config.Port;

    public ValueTask DisposeAsync() => new(StopAsync());
}
