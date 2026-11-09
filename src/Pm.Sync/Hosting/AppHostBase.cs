using Pm.Core.Vault;
using Pm.Sync;

namespace Pm.Sync.Hosting;

/// <summary>Shared discovery and sync lifecycle. Platforms override only what they can do.</summary>
public abstract class AppHostBase : IAppHost
{
    private PeerDiscovery? _discovery;

    protected PeerDiscovery Discovery =>
        _discovery ?? throw new InvalidOperationException("Host is not started.");

    public abstract bool CanOfferPairing { get; }

    public virtual Task<string?> StartAsync(VaultSession session)
    {
        _discovery = new PeerDiscovery();
        return Task.FromResult<string?>(null);
    }

    public virtual Task StopAsync()
    {
        _discovery?.Dispose();
        _discovery = null;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PeerCandidate>> DiscoverAsync(TimeSpan timeout) =>
        _discovery is null
            ? Task.FromResult<IReadOnlyList<PeerCandidate>>(Array.Empty<PeerCandidate>())
            : _discovery.FindAsync(timeout);

    public abstract Task<PairingOffer?> OfferPairingAsync(VaultSession session);

    public virtual async Task<PairResult> JoinAsync(VaultSession session, string address, string code,
        Func<PairInfo, Task<bool>> confirm)
    {
        var info = await SyncClient.GetPairInfoAsync(session, address);
        if (!await confirm(info))
            throw new InvalidOperationException("сопряжение отменено");
        return await SyncClient.CompletePairAsync(session, address, info, code, JoinPort(session));
    }

    protected virtual int JoinPort(VaultSession session) => 0;

    public Task<(int Peers, int Conflicts)> SyncAsync(VaultSession session) =>
        SyncClient.SyncAllAsync(session, _ => DiscoverAsync(TimeSpan.FromSeconds(1.2)));
}
