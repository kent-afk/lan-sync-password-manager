using System.Text.Json;
using System.Text.Json.Serialization;
using Pm.Core.Crypto;
using Pm.Core.Storage;

namespace Pm.Core.Vault;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
}

public sealed class Entry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public long Version { get; set; }
    public string Device { get; set; } = "";
    public string Title { get; set; } = "";
    public string Login { get; set; } = "";
    public string Password { get; set; } = "";
    public string Url { get; set; } = "";
    public string Notes { get; set; } = "";
    public bool Deleted { get; set; }
    public long DeletedAt { get; set; }

    public Entry Clone() => (Entry)MemberwiseClone();

    public bool SameContent(Entry other) =>
        Title == other.Title && Login == other.Login && Password == other.Password &&
        Url == other.Url && Notes == other.Notes && Deleted == other.Deleted;
}

public sealed class Vault
{
    public List<Entry> Entries { get; set; } = new();

    public long MaxVersion() => Entries.Count == 0 ? 0 : Entries.Max(e => e.Version);

    public long NextVersion() => MaxVersion() + 1;
}

public sealed class DeviceInfo
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public byte[] PubKey { get; set; } = Array.Empty<byte>();
    public byte[] Wrap { get; set; } = Array.Empty<byte>();

    public DeviceInfo Clone() => (DeviceInfo)MemberwiseClone();
}

public sealed class KeysetBody
{
    public long Rev { get; set; }
    public List<DeviceInfo> Devices { get; set; } = new();

    /// <summary>Devices removed by any branch. Omitted when empty so old signatures stay valid.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Removed { get; set; }
}

public sealed class Keyset
{
    public long Rev { get; set; }
    public List<DeviceInfo> Devices { get; set; } = new();
    public byte[] Sig { get; set; } = Array.Empty<byte>();

    /// <summary>Monotonic revocation list. A removed device can only return through explicit re-pairing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Removed { get; set; }

    public DeviceInfo? Find(string id) => Devices.FirstOrDefault(d => d.Id == id);

    public byte[] BodyBytes()
    {
        var body = new KeysetBody
        {
            Rev = Rev,
            Devices = Devices.OrderBy(d => d.Id, StringComparer.Ordinal).ToList(),
            Removed = Removed is { Count: > 0 }
                ? Removed.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList()
                : null,
        };
        return JsonSerializer.SerializeToUtf8Bytes(body, Json.Options);
    }

    public void SignWith(DeviceKey key)
    {
        Sig = Crypto.Crypto.Sign(key.Rsa, BodyBytes());
    }

    public bool VerifyWith(byte[] publicKey)
    {
        try
        {
            return Crypto.Crypto.Verify(publicKey, BodyBytes(), Sig);
        }
        catch
        {
            return false;
        }
    }

    public bool VerifyAny()
    {
        foreach (var d in Devices)
            if (VerifyWith(d.PubKey)) return true;
        return false;
    }

    public DeviceInfo Add(string id, string label, byte[] publicKey, byte[] vaultKey)
    {
        // Explicit re-pairing is the only path that clears a revocation.
        Removed?.Remove(id);

        var existing = Find(id);
        if (existing is not null) return existing;

        var info = new DeviceInfo
        {
            Id = id,
            Label = label,
            PubKey = publicKey,
            Wrap = Crypto.Crypto.Wrap(publicKey, vaultKey),
        };
        Devices.Add(info);
        return info;
    }

    public void Save(string path) =>
        Files.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(this, Json.Options), secret: true);

    public static Keyset Load(string path) =>
        JsonSerializer.Deserialize<Keyset>(File.ReadAllBytes(path), Json.Options)
        ?? throw new InvalidDataException("keys.bin пуст или повреждён");
}

public sealed class Peer
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Address { get; set; } = "";

    /// <summary>False until the newly paired device completes its first sync; the server does not initiate to it.</summary>
    public bool Confirmed { get; set; } = true;
}

public sealed class Config
{
    public string DeviceId { get; set; } = "";
    public string Label { get; set; } = "";
    public int Port { get; set; } = 47821;
    public bool TourShown { get; set; }
    public List<Peer> Peers { get; set; } = new();

    public void Save(string path) =>
        Files.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(this, Json.Options), secret: true);

    public static Config Load(string path) =>
        JsonSerializer.Deserialize<Config>(File.ReadAllBytes(path), Json.Options)
        ?? throw new InvalidDataException("config.json пуст или повреждён");
}
