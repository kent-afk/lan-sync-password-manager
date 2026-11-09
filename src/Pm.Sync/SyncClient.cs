using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Pm.Core;
using Pm.Core.Crypto;
using Pm.Core.Vault;

namespace Pm.Sync;

public static class SyncClient
{
    public static async Task<(int Conflicts, int Live)> SyncAsync(VaultSession session, string address)
    {
        address = Network.NormalizeAddress(address);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        // Refuse to exchange stores with a different format before it can overwrite anything.
        var info = await http.GetFromJsonAsync<InfoResponse>($"http://{address}/info", PmSyncJsonContext.Default.InfoResponse)
                   ?? throw new InvalidOperationException("устройство не ответило");
        if (info.Format != Protocol.Format)
            throw new InvalidOperationException("версии Pm не совпадают; обновите оба устройства");

        byte[] store;
        Keyset keyset;
        byte[] keysetSignature;
        lock (session.Gate)
        {
            session.ReloadKeyset();
            session.ReloadStore();
            store = session.SealStore();
            keyset = session.Keyset;
            keysetSignature = Crypto.Sign(session.Key.Rsa, keyset.BodyBytes());
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var request = new SyncRequest
            {
                Format = Protocol.Format,
                DeviceId = session.Config.DeviceId,
                SentAt = sentAt,
                Body = store,
                Signature = SyncAuth.Sign(session.Key, session.Config.DeviceId, sentAt, store),
                Keyset = keyset,
                KeysetSignature = keysetSignature,
            };

            var response = await http.PostAsJsonAsync($"http://{address}/sync", request, PmSyncJsonContext.Default.SyncRequest);
            if (response.StatusCode == System.Net.HttpStatusCode.UpgradeRequired)
                throw new InvalidOperationException("версии Pm не совпадают; обновите оба устройства");
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest && HasError(response, Protocol.ClockSkew))
                throw new InvalidOperationException("часы устройств расходятся больше чем на 5 минут");
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                var changed = await response.Content.ReadFromJsonAsync(PmSyncJsonContext.Default.SyncResponse);
                if (changed?.Keyset is null)
                    throw new InvalidOperationException("сервер не прислал новый keys.bin");

                lock (session.Gate)
                {
                    session.AdoptKeyset(changed.Keyset);
                    session.ReloadStore();
                    store = session.SealStore();
                    keyset = session.Keyset;
                    keysetSignature = Crypto.Sign(session.Key.Rsa, keyset.BodyBytes());
                }
                continue;
            }

            response.EnsureSuccessStatusCode();
            var sync = await response.Content.ReadFromJsonAsync(PmSyncJsonContext.Default.SyncResponse)
                       ?? throw new InvalidOperationException("пустой ответ синхронизации");
            if (sync.Format != Protocol.Format)
                throw new InvalidOperationException("версии Pm не совпадают; обновите оба устройства");
            if (sync.Keyset is null || sync.Store is null)
                throw new InvalidOperationException("ответ синхронизации неполный");

            lock (session.Gate)
                return session.Apply(sync.Keyset, sync.Store);
        }

        throw new InvalidOperationException("сервер повторно запросил смену ключа");
    }

    /// <summary>Convenience for the CLI and tests: no interactive fingerprint check.</summary>
    public static async Task<PairResult> PairAsync(VaultSession session, string address, string code, int myPort)
    {
        var info = await GetPairInfoAsync(session, address);
        return await CompletePairAsync(session, address, info, code, myPort);
    }

    /// <summary>Step 1: fetch the public key so the user can compare fingerprints before sending the code proof.</summary>
    public static async Task<PairInfo> GetPairInfoAsync(VaultSession session, string address)
    {
        address = Network.NormalizeAddress(address);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var info = await http.GetFromJsonAsync<InfoResponse>($"http://{address}/info", PmSyncJsonContext.Default.InfoResponse)
                   ?? throw new InvalidOperationException("устройство не ответило");
        if (info.Format != Protocol.Format)
            throw new InvalidOperationException("версии Pm не совпадают; обновите оба устройства");
        return new PairInfo(info.DeviceId, info.Label, info.PubKey, Crypto.Fingerprint(info.PubKey),
            session.OwnFingerprint);
    }

    /// <summary>Step 2: the user has confirmed the fingerprint; send the proof and adopt the peer's keyset.</summary>
    public static async Task<PairResult> CompletePairAsync(VaultSession session, string address, PairInfo info,
        string code, int myPort)
    {
        address = Network.NormalizeAddress(address);
        // The responder waits for its user to compare fingerprints before it wraps the vault key.
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(6) };

        var proof = Convert.ToBase64String(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(code), session.PublicKey));

        var request = new PairRequest
        {
            Format = Protocol.Format,
            DeviceId = session.Config.DeviceId,
            Label = session.Config.Label,
            PubKey = session.PublicKey,
            Proof = proof,
            Port = myPort,
        };

        var response = await http.PostAsJsonAsync($"http://{address}/pair", request, PmSyncJsonContext.Default.PairRequest);
        var result = await response.Content.ReadFromJsonAsync(PmSyncJsonContext.Default.PairResponse)
                     ?? throw new InvalidOperationException("пустой ответ при сопряжении");

        if (!result.Ok)
            throw new InvalidOperationException(result.Error ?? "сопряжение отклонено");
        if (result.Format != Protocol.Format)
            throw new InvalidOperationException("версии Pm не совпадают; обновите оба устройства");
        if (result.Keyset is null || result.Store is null || string.IsNullOrEmpty(result.Proof))
            throw new InvalidOperationException("ответ сопряжения неполный");

        byte[] responderProof;
        try
        {
            responderProof = Convert.FromBase64String(result.Proof);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("ответ сопряжения повреждён");
        }

        var expected = SyncAuth.PairProof(code, info.PubKey, session.PublicKey, result.Keyset, result.Store);
        if (!CryptographicOperations.FixedTimeEquals(expected, responderProof))
            throw new InvalidOperationException("сопряжение не подтвердило код");

        session.Adopt(result.Keyset, result.Store);
        lock (session.Gate)
        {
            session.Config.Peers.RemoveAll(p => p.Id == info.DeviceId);
            session.Config.Peers.Add(new Peer
            {
                Id = info.DeviceId,
                Label = info.Label,
                Address = address,
                Confirmed = true,
            });
            session.SaveConfig();
        }

        return new PairResult(info.DeviceId, info.Label, info.Fingerprint, session.OwnFingerprint);
    }

    public static async Task<(int Peers, int Conflicts)> SyncAllAsync(VaultSession session,
        Func<CancellationToken, Task<IReadOnlyList<PeerCandidate>>>? discover = null)
    {
        var conflicts = 0;
        var peers = 0;
        List<Peer> targets;
        lock (session.Gate)
            targets = session.Config.Peers.ToList();

        if (discover is not null && targets.Count > 0)
        {
            try
            {
                var found = await discover(CancellationToken.None);
                foreach (var peer in targets)
                {
                    var match = found.FirstOrDefault(f => f.Id == peer.Id);
                    if (match is not null) peer.Address = match.Address;
                }
            }
            catch
            {
                // discovery is best effort; stored addresses still work
            }
        }

        var errors = new List<Exception>();
        foreach (var peer in targets)
        {
            if (!peer.Confirmed) continue;

            try
            {
                var (c, _) = await SyncAsync(session, peer.Address);
                conflicts += c;
                peers++;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null)
            {
                // peer offline is normal: sync is best effort
            }
            catch (TaskCanceledException)
            {
                // connect timeout is normal: sync is best effort
            }
            catch (Exception ex)
            {
                errors.Add(new Exception($"{peer.Label}: {ex.Message}", ex));
            }
        }

        if (errors.Count > 0)
            throw new Exception(string.Join("; ", errors.Select(e => e.Message)));

        return (peers, conflicts);
    }

    private static bool HasError(HttpResponseMessage response, string value) =>
        response.Headers.TryGetValues(Protocol.ErrorHeader, out var values) && values.Contains(value);
}
