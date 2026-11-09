using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Pm.Core;
using Pm.Core.Vault;

namespace Pm.Sync;

/// <summary>UDP broadcast discovery. One instance per process: it both answers and asks.</summary>
public sealed class PeerDiscovery : IDisposable
{
    private const int DiscoveryPort = 47830;
    private const string Ask = "PM1?";

    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, PeerCandidate> _seen = new();

    private string _id = "";
    private string _label = "";
    private int _port;
    private Func<bool>? _pairing;

    public PeerDiscovery()
    {
        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.EnableBroadcast = true;
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        _ = Task.Run(ReceiveLoopAsync);
    }

    public void Advertise(string id, string label, int port, Func<bool> pairing)
    {
        _id = id;
        _label = label;
        _port = port;
        _pairing = pairing;
    }

    public async Task<IReadOnlyList<PeerCandidate>> FindAsync(TimeSpan timeout)
    {
        _seen.Clear();
        var payload = Encoding.UTF8.GetBytes(Ask);
        var until = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < until)
        {
            try
            {
                await _udp.SendAsync(payload, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
            }
            catch
            {
                // broadcast can be blocked; the caller still has manual entry
            }

            await Task.Delay(300);
        }

        return _seen.Values.OrderBy(p => p.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task ReceiveLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await _udp.ReceiveAsync(_cts.Token);
            }
            catch
            {
                return;
            }

            var text = Encoding.UTF8.GetString(packet.Buffer);
            if (text == Ask)
            {
                if (_id.Length == 0 || _port == 0) continue;
                var reply = JsonSerializer.Serialize(
                    new PeerCandidate(_id, _label, $"{LocalAddress(packet.RemoteEndPoint)}:{_port}", _pairing?.Invoke() ?? false),
                    Json.Options);
                try
                {
                    await _udp.SendAsync(Encoding.UTF8.GetBytes("PM1 " + reply), packet.RemoteEndPoint);
                }
                catch
                {
                    // requester vanished
                }
            }
            else if (text.StartsWith("PM1 ", StringComparison.Ordinal))
            {
                try
                {
                    var peer = JsonSerializer.Deserialize<PeerCandidate>(text[4..], Json.Options);
                    if (peer is not null) _seen[peer.Id] = peer;
                }
                catch
                {
                    // ignore malformed replies
                }
            }
        }
    }

    private static string LocalAddress(IPEndPoint remote)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(remote);
            return ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
        }
        catch
        {
            return Network.LanAddress();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
        _cts.Dispose();
    }
}
