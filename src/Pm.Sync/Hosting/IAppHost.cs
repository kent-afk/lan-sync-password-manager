using Pm.Core;
using Pm.Core.Vault;
using Pm.Sync;

namespace Pm.Sync.Hosting;

/// <summary>What the shared UI needs from the platform. iOS answers null where it cannot listen.</summary>
public interface IAppHost
{
    bool CanOfferPairing { get; }
    Task<string?> StartAsync(VaultSession session);
    Task StopAsync();
    Task<IReadOnlyList<PeerCandidate>> DiscoverAsync(TimeSpan timeout);
    Task<PairingOffer?> OfferPairingAsync(VaultSession session);
    Task<PairResult> JoinAsync(VaultSession session, string address, string code, Func<PairInfo, Task<bool>> confirm);
    Task<(int Peers, int Conflicts)> SyncAsync(VaultSession session);
}

public sealed class PairingOffer
{
    private readonly Func<ValueTask> _dispose;
    private readonly Action<bool> _approve;

    public PairingOffer(string address, string code, Task<PairResult> completed, Task<PairPending> pending,
        DateTimeOffset expires, Action<bool> approve, Func<ValueTask> dispose)
    {
        Address = address;
        Code = code;
        Completed = completed;
        Pending = pending;
        Expires = expires;
        _approve = approve;
        _dispose = dispose;
    }

    public string Address { get; }
    public string Code { get; }
    public Task<PairResult> Completed { get; }
    public Task<PairPending> Pending { get; }
    public DateTimeOffset Expires { get; }

    public void Approve(bool ok) => _approve(ok);

    public ValueTask DisposeAsync() => _dispose();
}
