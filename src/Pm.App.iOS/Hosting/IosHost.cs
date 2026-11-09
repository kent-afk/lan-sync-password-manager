using Pm.Sync.Hosting;
using Pm.Core.Vault;

namespace Pm.App.iOS;

/// <summary>The phone never listens: it starts sync while the app is open.</summary>
public sealed class IosHost : AppHostBase
{
    public override bool CanOfferPairing => false;

    public override Task<PairingOffer?> OfferPairingAsync(VaultSession session) =>
        Task.FromResult<PairingOffer?>(null);
}
