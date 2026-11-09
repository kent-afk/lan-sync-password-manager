using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Pm.Core;
using Pm.Core.Crypto;
using Pm.Core.Vault;

namespace Pm.Sync;

/// <summary>Wire-format marker. Peers refuse to sync or pair across different formats.</summary>
public static class Protocol
{
    public const int Format = 1;
    public const string ErrorHeader = "X-Pm-Error";
    public const string VersionMismatch = "version-mismatch";
    public const string ClockSkew = "clock-skew";
}

public sealed class InfoResponse
{
    public int Format { get; set; }
    public string DeviceId { get; set; } = "";
    public string Label { get; set; } = "";
    public byte[] PubKey { get; set; } = Array.Empty<byte>();
}

public sealed class PairRequest
{
    public int? Format { get; set; }
    public string DeviceId { get; set; } = "";
    public string Label { get; set; } = "";
    public byte[] PubKey { get; set; } = Array.Empty<byte>();
    public string Proof { get; set; } = "";
    public int Port { get; set; }
}

public sealed class PairResponse
{
    public int Format { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public Keyset? Keyset { get; set; }
    public byte[]? Store { get; set; }

    /// <summary>HMAC of the code over both keys and the returned keyset/store. Proves the responder knows the code.</summary>
    public string? Proof { get; set; }
}

public sealed record PairResult(string DeviceId, string Label, string Fingerprint, string OwnFingerprint = "",
    bool WasRemoved = false);

/// <summary>Public pairing data shown to the user before the code proof is sent.</summary>
public sealed record PairInfo(string DeviceId, string Label, byte[] PubKey, string Fingerprint, string OwnFingerprint);

public sealed record PairPending(string DeviceId, string Label, string Fingerprint, string OwnFingerprint,
    bool WasRemoved = false);

public sealed class SyncRequest
{
    public int? Format { get; set; }
    public string DeviceId { get; set; } = "";
    public long SentAt { get; set; }
    public byte[] Body { get; set; } = Array.Empty<byte>();
    public byte[] Signature { get; set; } = Array.Empty<byte>();

    /// <summary>The sender's keyset, so a newer revocation can reach a peer it talks to.</summary>
    public Keyset? Keyset { get; set; }

    /// <summary>Signature of <see cref="Keyset"/> with the sender's device key, so a sniffer cannot swap it.</summary>
    public byte[] KeysetSignature { get; set; } = Array.Empty<byte>();
}

public sealed class SyncResponse
{
    public int Format { get; set; }
    public Keyset? Keyset { get; set; }
    public byte[]? Store { get; set; }
}

public static class SyncAuth
{
    public static byte[] Payload(string deviceId, long sentAt, byte[] body) =>
        Encoding.UTF8.GetBytes($"{deviceId}\n{sentAt}\n").Concat(SHA256.HashData(body)).ToArray();

    public static byte[] PairProof(string code, byte[] responderPublicKey, byte[] joinerPublicKey,
        Keyset keyset, byte[] store) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(code),
            SHA256.HashData(responderPublicKey
                .Concat(joinerPublicKey)
                .Concat(keyset.BodyBytes())
                .Concat(store)
                .ToArray()));

    public static byte[] Sign(DeviceKey key, string deviceId, long sentAt, byte[] body) =>
        Pm.Core.Crypto.Crypto.Sign(key.Rsa, Payload(deviceId, sentAt, body));

    public static bool Verify(byte[] publicKey, string deviceId, long sentAt, byte[] body, byte[] signature)
    {
        try
        {
            return Pm.Core.Crypto.Crypto.Verify(publicKey, Payload(deviceId, sentAt, body), signature);
        }
        catch
        {
            return false;
        }
    }
}

public sealed record PeerCandidate(string Id, string Label, string Address, bool Pairing)
{
    public string Display => Pairing ? $"{Label} · готов к сопряжению" : Label;
}

// shortcut: the 6-digit HMAC proof can be brute-forced offline from a captured pairing request;
// the online attempt limit below does not stop a sniffer. Move to a PAKE if the LAN is hostile.
public sealed class PairingTicket
{
    private const int MaxFailures = 5;
    private int _failures;
    private int _consumed;
    private int _begun;

    public string Code { get; } = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    public DateTimeOffset Expires { get; } = DateTimeOffset.UtcNow.AddMinutes(5);
    public TaskCompletionSource<PairResult> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Fires when a device has proved the code and waits for the local user to compare fingerprints.</summary>
    public TaskCompletionSource<PairPending> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource<bool> _approval = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> Approval => _approval.Task;

    public bool IsValid =>
        Volatile.Read(ref _consumed) == 0 &&
        DateTimeOffset.UtcNow < Expires &&
        Volatile.Read(ref _failures) < MaxFailures;

    public bool TryBegin() =>
        IsValid && Interlocked.CompareExchange(ref _begun, 1, 0) == 0;

    public void Fail() => Interlocked.Increment(ref _failures);

    public bool TryConsume() =>
        IsValid && Interlocked.CompareExchange(ref _consumed, 1, 0) == 0;

    public void Approve(bool ok) => _approval.TrySetResult(ok);

    public void Abort(string message) => Completed.TrySetException(new InvalidOperationException(message));
}

public static class Network
{
    // shortcut: interface choice is a heuristic; use a route lookup if the host has several LANs.
    public static string LanAddress()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
            .Where(ni => ni.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Where(ni => !IsVirtual(ni.Name))
            .SelectMany(ni =>
            {
                var properties = ni.GetIPProperties();
                var gateway = properties.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !g.Address.Equals(IPAddress.Any));
                return properties.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                                !IPAddress.IsLoopback(a.Address))
                    .Select(a => (Address: a.Address, Gateway: gateway, Type: ni.NetworkInterfaceType));
            })
            .ToList();

        var best = candidates
            .OrderByDescending(c => c.Gateway)
            .ThenByDescending(c => c.Type is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet)
            .FirstOrDefault();

        return best.Address?.ToString() ?? "127.0.0.1";
    }

    private static bool IsVirtual(string name) =>
        name.StartsWith("docker", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("veth", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("br-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("virbr", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("tun", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("tap", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("utun", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("wg", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("ppp", StringComparison.OrdinalIgnoreCase);

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static string NormalizeAddress(string address)
    {
        address = address.Trim();
        if (address.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) address = address[7..];
        return address.TrimEnd('/');
    }
}


