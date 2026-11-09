using System.Security.Cryptography;
using System.Text.Json;
using Pm.Core.Crypto;
using Pm.Core.Storage;

namespace Pm.Core.Vault;

/// <summary>Unlocked state: device key, keyset, and the decrypted vault. Everything on disk stays encrypted.</summary>
public sealed class VaultSession : IDisposable
{
    public string Home { get; }
    public Config Config { get; private set; }
    public DeviceKey Key { get; }
    public Keyset Keyset { get; private set; }
    public byte[] VaultKey { get; private set; }
    public Vault Vault { get; private set; }

    /// <summary>Serialises the HTTP handlers that mutate the vault.</summary>
    public object Gate { get; } = new();

    public byte[] PublicKey => Key.PublicKey;
    public string OwnFingerprint => Crypto.Crypto.Fingerprint(PublicKey);

    private VaultSession(string home, Config config, DeviceKey key, Keyset keyset, byte[] vaultKey, Vault vault)
    {
        Home = home;
        Config = config;
        Key = key;
        Keyset = keyset;
        VaultKey = vaultKey;
        Vault = vault;
    }

    public static VaultSession Init(string home, string label, string password)
    {
        if (File.Exists(Path.Combine(home, "device.key")))
            throw new InvalidOperationException($"устройство уже создано в {home}");

        Files.EnsurePrivateDirectory(home);
        var key = DeviceKey.Create();
        key.Save(Path.Combine(home, "device.key"), password);
        return Create(home, label, key);
    }

    /// <summary>First run with a key already held by the platform keychain: no password file at all.</summary>
    public static VaultSession Create(string home, string label, DeviceKey key)
    {
        if (Paths.AnyExistsIn(home))
            throw new InvalidOperationException($"хранилище уже создано или неполное в {home}");

        Files.EnsurePrivateDirectory(home);

        var id = DeviceKey.NewDeviceId();
        var config = new Config { DeviceId = id, Label = label };
        var vaultKey = Crypto.Crypto.Random(Crypto.Crypto.KeySize);

        var keyset = new Keyset { Rev = 1 };
        keyset.Add(id, label, key.PublicKey, vaultKey);
        keyset.SignWith(key);

        var session = new VaultSession(home, config, key, keyset, vaultKey, new Vault());
        session.SaveConfig();
        session.SaveKeyset();
        session.SaveStore();
        return session;
    }

    public static VaultSession Unlock(string home, string password)
    {
        var keyPath = Path.Combine(home, "device.key");
        if (!File.Exists(keyPath)) throw new InvalidOperationException("хранилище не создано, выполните: pm init");

        return UnlockWithKey(home, DeviceKey.Load(keyPath, password));
    }

    public static VaultSession UnlockWithKey(string home, DeviceKey key)
    {
        // A reader must not see a keyset written by a rekey and a store from before it.
        Files.EnsurePrivateDirectory(home);
        using var fileLock = Files.Lock(Path.Combine(home, ".lock"));
        Files.MakePrivate(
            Path.Combine(home, "keys.bin"),
            Path.Combine(home, "store.bin"),
            Path.Combine(home, "config.json"),
            Path.Combine(home, "keys.bin.prev"),
            Path.Combine(home, "store.bin.prev"));

        var config = Config.Load(Path.Combine(home, "config.json"));
        var keyset = Keyset.Load(Path.Combine(home, "keys.bin"));

        if (!keyset.VerifyAny())
            throw new CryptographicException("keys.bin подписан неизвестным ключом");

        var me = keyset.Find(config.DeviceId)
                 ?? throw new CryptographicException("это устройство отсутствует в keys.bin");
        var vaultKey = Crypto.Crypto.Unwrap(key.Rsa, me.Wrap);

        Vault vault;
        try
        {
            vault = VaultFile.Load(Path.Combine(home, "store.bin"), vaultKey);
        }
        catch (Exception ex) when (IsStoreError(ex))
        {
            var recovered = TryRecoverStore(home, config.DeviceId, keyset, vaultKey, key);
            if (recovered is null) throw;
            vault = recovered;
        }

        TryDeletePrevious(home);
        return new VaultSession(home, config, key, keyset, vaultKey, vault);
    }

    public byte[] SealStore() => VaultFile.Seal(Vault, VaultKey);

    public void SaveStore() => WithWriteLock(SaveStoreCore);

    private void SaveStoreCore() =>
        Files.WriteAtomic(Path.Combine(Home, "store.bin"), VaultFile.Seal(Vault, VaultKey), secret: true);

    public void SaveKeyset() => Keyset.Save(Path.Combine(Home, "keys.bin"));

    public void SaveConfig() => Config.Save(Path.Combine(Home, "config.json"));

    /// <summary>Pick up a keyset written by another process (CLI pairing next to the desktop).</summary>
    public void ReloadKeyset() => WithWriteLock(ReloadKeysetCore);

    private void ReloadKeysetCore()
    {
        var keyset = Keyset.Load(Path.Combine(Home, "keys.bin"));
        if (!keyset.VerifyAny())
            throw new CryptographicException("keys.bin подписан неизвестным ключом");
        if (keyset.Rev < Keyset.Rev) return;
        var me = keyset.Find(Config.DeviceId)
                 ?? throw new CryptographicException("это устройство отсутствует в keys.bin");
        VaultKey = Crypto.Crypto.Unwrap(Key.Rsa, me.Wrap);
        Keyset = keyset;
        Config = Pm.Core.Vault.Config.Load(Path.Combine(Home, "config.json"));
    }

    /// <summary>Pick up writes made by another process (CLI next to a running server).</summary>
    public void ReloadStore() => WithWriteLock(ReloadStoreCore);

    private void ReloadStoreCore()
    {
        var path = Path.Combine(Home, "store.bin");
        if (!File.Exists(path)) return;

        try
        {
            Vault = VaultFile.Open(File.ReadAllBytes(path), VaultKey);
        }
        catch (Exception ex) when (IsStoreError(ex))
        {
            // Another process may have rekeyed the vault after this session was opened.
            try { ReloadKeysetCore(); } catch { }

            try
            {
                Vault = VaultFile.Open(File.ReadAllBytes(path), VaultKey);
            }
            catch (Exception retry) when (IsStoreError(retry))
            {
                var recovered = TryRecoverStore(Home, Config.DeviceId, Keyset, VaultKey, Key);
                if (recovered is null) throw;
                Vault = recovered;
            }
        }

        TryDeletePrevious(Home);
    }

    public Entry AddEntry(string title, string login, string password, string url, string notes) =>
        WithWriteLock(() =>
        {
            ReloadKeysetCore();
            ReloadStoreCore();
            var entry = new Entry
            {
                Version = Vault.NextVersion(),
                Device = Config.DeviceId,
                Title = title,
                Login = login,
                Password = password,
                Url = url,
                Notes = notes,
            };
            Vault.Entries.Add(entry);
            SaveStoreCore();
            return entry;
        });

    public Entry UpdateEntry(Entry updated) =>
        WithWriteLock(() =>
        {
            ReloadKeysetCore();
            ReloadStoreCore();
            var entry = Vault.Entries.FirstOrDefault(e => e.Id == updated.Id)
                        ?? throw new InvalidOperationException("запись не найдена");
            if (entry.Deleted)
                throw new InvalidOperationException("запись удалена");
            if (updated.Version != entry.Version)
                throw new InvalidOperationException("запись изменена на другом устройстве; выберите её заново");

            updated.Version = Vault.NextVersion();
            updated.Device = Config.DeviceId;
            entry.Title = updated.Title;
            entry.Login = updated.Login;
            entry.Password = updated.Password;
            entry.Url = updated.Url;
            entry.Notes = updated.Notes;
            entry.Version = updated.Version;
            entry.Device = updated.Device;
            SaveStoreCore();
            return entry;
        });

    public void DeleteEntry(string id) =>
        WithWriteLock(() =>
        {
            ReloadKeysetCore();
            ReloadStoreCore();
            var entry = Vault.Entries.FirstOrDefault(e => e.Id == id)
                        ?? throw new InvalidOperationException("запись не найдена");
            entry.Deleted = true;
            entry.DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            entry.Login = entry.Password = entry.Url = entry.Notes = "";
            entry.Version = Vault.NextVersion();
            entry.Device = Config.DeviceId;
            SaveStoreCore();
        });

    public (int Conflicts, int Live) Apply(byte[] remoteStore) =>
        ApplyCore(Keyset, remoteStore, senderSentKeyset: false);

    public (int Conflicts, int Live) Apply(Keyset keyset, byte[] remoteStore) =>
        ApplyCore(keyset, remoteStore, senderSentKeyset: true);

    private (int Conflicts, int Live) ApplyCore(Keyset keyset, byte[] remoteStore, bool senderSentKeyset) =>
        WithWriteLock(() =>
        {
            if (!Keyset.Devices.Any(d => keyset.VerifyWith(d.PubKey)))
                throw new CryptographicException("новый keys.bin подписан неизвестным ключом");
            if (keyset.Rev < Keyset.Rev)
                throw new CryptographicException("устаревший keys.bin");
            var me = keyset.Find(Config.DeviceId)
                     ?? throw new CryptographicException("это устройство отсутствует в keys.bin");
            var incomingKey = Crypto.Crypto.Unwrap(Key.Rsa, me.Wrap);

            ReloadStoreCore();

            var oldKey = VaultKey;
            var (resolved, resolvedKey) = ResolveKeyset(keyset, incomingKey);
            var keyChanged = !resolvedKey.AsSpan().SequenceEqual(oldKey);
            var keysetChanged = !resolved.BodyBytes().AsSpan().SequenceEqual(Keyset.BodyBytes());

            Vault remote;
            try
            {
                remote = VaultFile.Open(remoteStore, resolvedKey);
            }
            catch (Exception ex) when (IsStoreError(ex))
            {
                // An old client sends no keyset. A failure may simply mean it is behind, so answer 409.
                if (!senderSentKeyset) throw;

                // The resolution chose a different key than the sender used. Save it and ask for a retry.
                if (!resolvedKey.AsSpan().SequenceEqual(incomingKey))
                {
                    Keyset = resolved;
                    VaultKey = resolvedKey;
                    if (keyChanged) SaveRekeyed();
                    else SaveKeyset();
                    throw new CryptographicException("keys.bin сменил ключ; повторите синхронизацию", ex);
                }

                // The server used the sender's key, so a store that does not open is corrupt.
                if (keyChanged)
                {
                    Keyset = resolved;
                    VaultKey = resolvedKey;
                    SaveRekeyed();
                }
                else if (keysetChanged)
                {
                    Keyset = resolved;
                    SaveKeyset();
                }

                throw new InvalidDataException("store в запросе не открывается ключом из keys.bin", ex);
            }

            var (merged, conflicts, changed) = Merger.Merge(Vault, remote);
            var peersBefore = Config.Peers.Count;
            Config.Peers.RemoveAll(p => resolved.Find(p.Id) is null);
            var peersChanged = Config.Peers.Count != peersBefore;

            Keyset = resolved;
            VaultKey = resolvedKey;
            Vault = merged;

            if (keyChanged)
            {
                SaveRekeyed();
                if (peersChanged) SaveConfig();
            }
            else
            {
                if (keysetChanged) SaveKeyset();
                if (changed) SaveStoreCore();
                if (peersChanged) SaveConfig();
            }

            return (conflicts, Vault.Entries.Count(e => !e.Deleted));
        });

    /// <summary>
    /// Reconcile two keysets. Revocations are monotonic: a device in either Removed list never returns.
    /// A newer keyset wins, but local revocations still apply. Equal-rev branches are unioned; when their
    /// vault keys differ, a fresh key is generated so neither branch's removed device keeps access.
    /// </summary>
    private (Keyset Resolved, byte[] Key) ResolveKeyset(Keyset incoming, byte[] incomingKey)
    {
        if (incoming.BodyBytes().AsSpan().SequenceEqual(Keyset.BodyBytes()))
            return (Keyset, VaultKey);

        var removed = new HashSet<string>(Keyset.Removed ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        removed.UnionWith(incoming.Removed ?? Enumerable.Empty<string>());
        var incomingRemoved = new HashSet<string>(incoming.Removed ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        var localRemoved = new HashSet<string>(Keyset.Removed ?? Enumerable.Empty<string>(), StringComparer.Ordinal);

        var devices = new Dictionary<string, DeviceInfo>(StringComparer.Ordinal);
        foreach (var device in incoming.Devices)
            if (!removed.Contains(device.Id)) devices[device.Id] = device.Clone();
        foreach (var device in Keyset.Devices)
            if (!removed.Contains(device.Id)) devices.TryAdd(device.Id, device.Clone());

        // A newer keyset normally wins. Rotate only if it would hand a locally revoked device the key.
        var key = incomingKey;
        var rotate = false;
        if (incoming.Rev > Keyset.Rev)
            rotate = localRemoved.Any(id => incoming.Find(id) is not null);
        else if (!incomingKey.AsSpan().SequenceEqual(VaultKey))
            rotate = true;
        else
            key = VaultKey;

        if (rotate)
        {
            key = Crypto.Crypto.Random(Crypto.Crypto.KeySize);
            foreach (var device in devices.Values)
                device.Wrap = Crypto.Crypto.Wrap(device.PubKey, key);
        }
        else if (incoming.Rev > Keyset.Rev)
        {
            // Local-only devices are absent from the incoming keyset; wrap them under the adopted key.
            foreach (var device in devices.Values)
                if (incoming.Find(device.Id) is null)
                    device.Wrap = Crypto.Crypto.Wrap(device.PubKey, key);
        }

        var sameAsIncoming = !rotate &&
            devices.Count == incoming.Devices.Count &&
            devices.Keys.All(id => incoming.Find(id) is not null) &&
            removed.SetEquals(incomingRemoved);
        if (sameAsIncoming)
            return (incoming, key);

        var resolved = new Keyset
        {
            Rev = Math.Max(Keyset.Rev, incoming.Rev) + 1,
            Devices = devices.Values.ToList(),
            Removed = removed.OrderBy(id => id, StringComparer.Ordinal).ToList(),
        };
        resolved.SignWith(Key);
        return (resolved, key);
    }

    /// <summary>After pairing: take the peer's keyset, unwrap the vault key, merge its store into ours.</summary>
    public void Adopt(Keyset keyset, byte[] storeBytes) =>
        WithWriteLock(() =>
        {
            if (!keyset.VerifyAny())
                throw new CryptographicException("сопряжение дало keys.bin с неверной подписью");

            var me = keyset.Find(Config.DeviceId)
                     ?? throw new CryptographicException("сопрягающая сторона не добавила это устройство");
            var vaultKey = Crypto.Crypto.Unwrap(Key.Rsa, me.Wrap);
            var remote = VaultFile.Open(storeBytes, vaultKey);
            ReloadStoreCore();
            var (merged, _, changed) = Merger.Merge(Vault, remote);

            var oldKey = VaultKey;
            var (resolved, resolvedKey) = ResolveKeyset(keyset, vaultKey);
            var keyChanged = !resolvedKey.AsSpan().SequenceEqual(oldKey);
            var peersBefore = Config.Peers.Count;
            Config.Peers.RemoveAll(p => resolved.Find(p.Id) is null);
            var peersChanged = Config.Peers.Count != peersBefore;

            Keyset = resolved;
            VaultKey = resolvedKey;
            Vault = merged;

            if (keyChanged)
            {
                SaveRekeyed();
            }
            else
            {
                SaveKeyset();
                if (changed) SaveStoreCore();
            }

            if (peersChanged) SaveConfig();
        });

    /// <summary>Server side of pairing: wrap the vault key for the new device and sign the keyset.</summary>
    public AddDeviceResult AddDevice(string id, string label, byte[] publicKey) =>
        WithWriteLock(() =>
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException("пустой идентификатор устройства");
            if (id == Config.DeviceId)
                throw new InvalidOperationException("нельзя сопрячь собственное устройство");
            if (publicKey.Length == 0)
                throw new InvalidOperationException("пустой публичный ключ");

            ReloadKeysetCore();
            var existing = Keyset.Find(id);
            if (existing is not null && !existing.PubKey.AsSpan().SequenceEqual(publicKey))
                throw new InvalidOperationException(
                    "устройство с этим идентификатором уже сопряжено с другим ключом; сначала отзовите его");

            // Pairing a device is the explicit way to undo its revocation.
            var wasRemoved = Keyset.Removed?.Contains(id) == true;
            Keyset.Removed?.Remove(id);
            if (existing is not null)
            {
                existing.Label = label;
                existing.Wrap = Crypto.Crypto.Wrap(publicKey, VaultKey);
            }
            else
            {
                Keyset.Add(id, label, publicKey, VaultKey);
            }

            Keyset.Rev++;
            Keyset.SignWith(Key);
            SaveKeyset();
            return new AddDeviceResult(Keyset.Find(id)!, wasRemoved);
        });

    /// <summary>Remove a device, rotate the store key, and re-wrap it for the remaining devices.</summary>
    public bool RemoveDevice(string id) =>
        WithWriteLock(() =>
        {
            if (id == Config.DeviceId)
                throw new InvalidOperationException("нельзя отозвать собственное устройство");

            ReloadKeysetCore();
            var device = Keyset.Find(id);
            if (device is null) return false;

            ReloadStoreCore();
            Keyset.Devices.Remove(device);
            Keyset.Removed ??= new List<string>();
            if (!Keyset.Removed.Contains(id)) Keyset.Removed.Add(id);
            VaultKey = Crypto.Crypto.Random(Crypto.Crypto.KeySize);
            foreach (var remaining in Keyset.Devices)
                remaining.Wrap = Crypto.Crypto.Wrap(remaining.PubKey, VaultKey);

            Keyset.Rev++;
            Keyset.SignWith(Key);
            SaveRekeyed();
            Config.Peers.RemoveAll(p => p.Id == id);
            SaveConfig();
            return true;
        });

    /// <summary>Take a newer keyset from a peer and re-encrypt the local store under its vault key.</summary>
    public void AdoptKeyset(Keyset keyset) =>
        WithWriteLock(() =>
        {
            if (!Keyset.Devices.Any(d => keyset.VerifyWith(d.PubKey)))
                throw new CryptographicException("новый keys.bin подписан неизвестным ключом");
            if (keyset.Rev < Keyset.Rev)
                throw new CryptographicException("устаревший keys.bin");
            var me = keyset.Find(Config.DeviceId)
                     ?? throw new CryptographicException("это устройство отсутствует в keys.bin");

            ReloadStoreCore();
            var incomingKey = Crypto.Crypto.Unwrap(Key.Rsa, me.Wrap);
            var (resolved, resolvedKey) = ResolveKeyset(keyset, incomingKey);
            var keyChanged = !resolvedKey.AsSpan().SequenceEqual(VaultKey);
            var peersBefore = Config.Peers.Count;
            Config.Peers.RemoveAll(p => resolved.Find(p.Id) is null);
            var peersChanged = Config.Peers.Count != peersBefore;

            VaultKey = resolvedKey;
            Keyset = resolved;

            if (keyChanged) SaveRekeyed();
            else SaveKeyset();
            if (peersChanged) SaveConfig();
        });

    /// <summary>
    /// A rekey writes two files. If the process dies between them, this restores the previous pair and
    /// reseals the data under the current key.
    /// </summary>
    private static Vault? TryRecoverStore(string home, string deviceId, Keyset currentKeyset,
        byte[] currentKey, DeviceKey key)
    {
        var previousKeys = Path.Combine(home, "keys.bin.prev");
        var previousStore = Path.Combine(home, "store.bin.prev");
        if (!File.Exists(previousKeys) || !File.Exists(previousStore)) return null;

        try
        {
            var keyset = Keyset.Load(previousKeys);
            if (!keyset.VerifyAny()) return null;
            // The previous signer may be the device that was just revoked. It is still a known
            // device if it is in the current keyset or in the monotonic Removed list.
            var known = currentKeyset.Devices.Select(d => d.PubKey)
                .Concat((currentKeyset.Removed ?? new List<string>())
                    .Select(id => keyset.Find(id)?.PubKey)
                    .OfType<byte[]>())
                .ToList();
            known.Add(key.PublicKey);
            if (!known.Any(p => keyset.VerifyWith(p))) return null;
            var me = keyset.Find(deviceId);
            if (me is null) return null;

            var previousKey = Crypto.Crypto.Unwrap(key.Rsa, me.Wrap);
            var vault = VaultFile.Open(File.ReadAllBytes(previousStore), previousKey);
            Files.WriteAtomic(Path.Combine(home, "store.bin"), VaultFile.Seal(vault, currentKey), secret: true);
            TryDelete(previousKeys);
            TryDelete(previousStore);
            return vault;
        }
        catch
        {
            return null;
        }
    }

    private void SaveRekeyed()
    {
        BackupKeyMaterial();
        SaveKeyset();
        SaveStoreCore();
        TryDeletePrevious(Home);
    }

    private void BackupKeyMaterial()
    {
        var keys = Path.Combine(Home, "keys.bin");
        var store = Path.Combine(Home, "store.bin");
        if (!File.Exists(keys) || !File.Exists(store)) return;

        Files.WriteAtomic(keys + ".prev", File.ReadAllBytes(keys), secret: true);
        Files.WriteAtomic(store + ".prev", File.ReadAllBytes(store), secret: true);
    }

    private static void TryDeletePrevious(string home)
    {
        TryDelete(Path.Combine(home, "keys.bin.prev"));
        TryDelete(Path.Combine(home, "store.bin.prev"));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static bool IsStoreError(Exception ex) =>
        ex is CryptographicException or InvalidDataException or JsonException;

    private void WithWriteLock(Action action)
    {
        lock (Gate)
        {
            using var fileLock = Files.Lock(Path.Combine(Home, ".lock"));
            action();
        }
    }

    private T WithWriteLock<T>(Func<T> action)
    {
        lock (Gate)
        {
            using var fileLock = Files.Lock(Path.Combine(Home, ".lock"));
            return action();
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            Key.Dispose();
            Vault = new Vault();
            VaultKey = Array.Empty<byte>();
        }
    }
}

public sealed record AddDeviceResult(DeviceInfo Device, bool WasRemoved);
