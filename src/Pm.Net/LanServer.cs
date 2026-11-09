using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pm.Core;
using Pm.Core.Crypto;
using Pm.Core.Vault;
using Pm.Sync;

namespace Pm.Net;

/// <summary>One HTTP node: accepts syncs and, while a pairing ticket is set, pairings.</summary>
public sealed class LanServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    public int Port { get; }
    public string Address { get; }
    public PairingTicket? Ticket { get; set; }

    private LanServer(WebApplication app, int port, string address, PairingTicket? ticket)
    {
        _app = app;
        Port = port;
        Address = address;
        Ticket = ticket;
    }

    public static async Task<LanServer> StartAsync(VaultSession session, string bindIp, int port,
        PairingTicket? ticket = null, bool allowSync = true)
    {
        if (port == 0) port = Network.FreePort();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 8 * 1024 * 1024;
            options.Listen(System.Net.IPAddress.Parse(bindIp), port);
        });
        var app = builder.Build();

        LanServer? self = null;

        app.MapGet("/info", () => Results.Json(new InfoResponse
        {
            Format = Protocol.Format,
            DeviceId = session.Config.DeviceId,
            Label = session.Config.Label,
            PubKey = session.PublicKey,
        }, Json.Options));

        app.MapPost("/pair", async (HttpContext ctx) =>
        {
            var active = self?.Ticket;
            if (active is null || !active.IsValid)
                return Results.Json(new PairResponse { Format = Protocol.Format, Ok = false, Error = "код истёк или недействителен" },
                    Json.Options, statusCode: StatusCodes.Status403Forbidden);

            PairRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<PairRequest>(ctx.Request.Body, Json.Options);
            }
            catch
            {
                return Results.BadRequest();
            }

            if (request is null || request.PubKey.Length < 64 || request.DeviceId.Length == 0)
                return Results.BadRequest();

            if (request.Format != Protocol.Format)
            {
                ctx.Response.Headers[Protocol.ErrorHeader] = Protocol.VersionMismatch;
                return Results.Json(new PairResponse
                {
                    Format = Protocol.Format,
                    Ok = false,
                    Error = "версии Pm не совпадают; обновите оба устройства",
                }, Json.Options, statusCode: StatusCodes.Status426UpgradeRequired);
            }

            byte[] proof;
            try
            {
                proof = Convert.FromBase64String(request.Proof);
            }
            catch
            {
                return Results.BadRequest();
            }

            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(active.Code), request.PubKey);
            if (!CryptographicOperations.FixedTimeEquals(expected, proof))
            {
                active.Fail();
                return Results.Json(new PairResponse { Format = Protocol.Format, Ok = false, Error = "неверный код" },
                    Json.Options, statusCode: StatusCodes.Status403Forbidden);
            }

            // Reject an identity that cannot be paired before asking the user to confirm a fingerprint.
            session.ReloadKeyset();
            if (request.DeviceId == session.Config.DeviceId)
                return Results.Json(new PairResponse { Format = Protocol.Format, Ok = false, Error = "нельзя сопрячь собственное устройство" },
                    Json.Options, statusCode: StatusCodes.Status409Conflict);
            var existing = session.Keyset.Find(request.DeviceId);
            if (existing is not null && !existing.PubKey.AsSpan().SequenceEqual(request.PubKey))
                return Results.Json(new PairResponse
                {
                    Format = Protocol.Format,
                    Ok = false,
                    Error = "устройство с этим идентификатором уже сопряжено с другим ключом",
                }, Json.Options, statusCode: StatusCodes.Status409Conflict);
            var wasRemoved = session.Keyset.Removed?.Contains(request.DeviceId) == true;

            if (!active.TryBegin())
                return Results.Json(new PairResponse { Format = Protocol.Format, Ok = false, Error = "код уже используется" },
                    Json.Options, statusCode: StatusCodes.Status403Forbidden);

            // Unlike the code, the fingerprint check needs the local user. The vault key is not wrapped
            // and the store is not sent until both users have seen and confirmed the fingerprints.
            active.Pending.TrySetResult(new PairPending(request.DeviceId, request.Label,
                Crypto.Fingerprint(request.PubKey), session.OwnFingerprint, wasRemoved));

            bool approved;
            try
            {
                var remaining = active.Expires - DateTimeOffset.UtcNow;
                approved = remaining > TimeSpan.Zero && await active.Approval.WaitAsync(remaining);
            }
            catch (TimeoutException)
            {
                approved = false;
            }

            if (!approved || !active.TryConsume())
            {
                active.Abort("сопряжение не подтверждено");
                return Results.Json(new PairResponse { Format = Protocol.Format, Ok = false, Error = "сопряжение отклонено" },
                    Json.Options, statusCode: StatusCodes.Status403Forbidden);
            }

            if (self is not null) self.Ticket = null;

            try
            {
                lock (session.Gate)
                {
                    session.ReloadStore();
                    var added = session.AddDevice(request.DeviceId, request.Label, request.PubKey);

                    var remote = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
                    session.Config.Peers.RemoveAll(p => p.Id == request.DeviceId);
                    session.Config.Peers.Add(new Peer
                    {
                        Id = request.DeviceId,
                        Label = request.Label,
                        Address = $"{remote}:{request.Port}",
                        Confirmed = false,
                    });
                    session.SaveConfig();

                    active.Completed.TrySetResult(new PairResult(request.DeviceId, request.Label,
                        Crypto.Fingerprint(request.PubKey), session.OwnFingerprint, added.WasRemoved));

                    var store = session.SealStore();
                    var responseProof = Convert.ToBase64String(SyncAuth.PairProof(
                        active.Code, session.PublicKey, request.PubKey, session.Keyset, store));

                    return Results.Json(new PairResponse
                    {
                        Format = Protocol.Format,
                        Ok = true,
                        Keyset = session.Keyset,
                        Store = store,
                        Proof = responseProof,
                    }, Json.Options);
                }
            }
            catch (InvalidOperationException ex)
            {
                active.Abort(ex.Message);
                return Results.Json(new PairResponse { Format = Protocol.Format, Ok = false, Error = ex.Message },
                    Json.Options, statusCode: StatusCodes.Status409Conflict);
            }
            catch (Exception ex)
            {
                active.Abort(ex.Message);
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }
        });

        if (allowSync)
        {
            app.MapPost("/sync", async (HttpContext ctx) =>
            {
                SyncRequest? request;
                try
                {
                    request = await JsonSerializer.DeserializeAsync<SyncRequest>(ctx.Request.Body, Json.Options);
                }
                catch (JsonException)
                {
                    return Results.StatusCode(StatusCodes.Status400BadRequest);
                }

                if (request is null || request.DeviceId.Length == 0 || request.Body.Length == 0)
                    return Results.StatusCode(StatusCodes.Status400BadRequest);

                if (request.Format != Protocol.Format)
                {
                    ctx.Response.Headers[Protocol.ErrorHeader] = Protocol.VersionMismatch;
                    return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
                }

                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                const long window = 5 * 60 * 1000;
                if (request.SentAt < now - window || request.SentAt > now + window)
                {
                    ctx.Response.Headers[Protocol.ErrorHeader] = Protocol.ClockSkew;
                    return Results.StatusCode(StatusCodes.Status400BadRequest);
                }

                try
                {
                    lock (session.Gate)
                    {
                        session.ReloadKeyset();
                        var device = session.Keyset.Find(request.DeviceId);
                        if (device is null ||
                            !SyncAuth.Verify(device.PubKey, request.DeviceId, request.SentAt, request.Body, request.Signature))
                            return Results.StatusCode(StatusCodes.Status403Forbidden);

                        if (request.Keyset is not null &&
                            (request.KeysetSignature.Length == 0 ||
                             !Crypto.Verify(device.PubKey, request.Keyset.BodyBytes(), request.KeysetSignature)))
                            return Results.StatusCode(StatusCodes.Status403Forbidden);

                        // another process may have written since this node unlocked
                        session.ReloadStore();
                        if (request.Keyset is null) session.Apply(request.Body);
                        else session.Apply(request.Keyset, request.Body);

                        var peer = session.Config.Peers.FirstOrDefault(p => p.Id == request.DeviceId);
                        if (peer is { Confirmed: false })
                        {
                            peer.Confirmed = true;
                            session.SaveConfig();
                        }

                        return Results.Json(new SyncResponse
                        {
                            Format = Protocol.Format,
                            Keyset = session.Keyset,
                            Store = session.SealStore(),
                        }, Json.Options);
                    }
                }
                catch (CryptographicException)
                {
                    return Results.Json(new SyncResponse { Format = Protocol.Format, Keyset = session.Keyset }, Json.Options,
                        statusCode: StatusCodes.Status409Conflict);
                }
                catch (InvalidDataException)
                {
                    return Results.StatusCode(StatusCodes.Status400BadRequest);
                }
            });
        }

        await app.StartAsync();

        var display = bindIp is "0.0.0.0" or "::" ? Network.LanAddress() : bindIp;
        var address = $"{display}:{port}";

        self = new LanServer(app, port, address, ticket);
        return self;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
