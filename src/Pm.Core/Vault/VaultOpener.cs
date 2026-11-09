using System.Security.Cryptography;
using Pm.Core.Crypto;
using Pm.Core.Storage;

namespace Pm.Core.Vault;

public enum VaultOpenState
{
    Opened,
    Created,
    NeedsLegacyPassword,
    NeedsPassphrase,
}

public sealed record VaultOpenResult(VaultOpenState State, VaultSession? Session, bool Migrated = false);

public static class SecretKeys
{
    /// <summary>Keychain entry name, unique per vault directory.</summary>
    public static string ForHome(string home) =>
        "device-key-" + Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(home))))[..16].ToLowerInvariant();

    /// <summary>Encrypted device key for passphrase-protected vaults.</summary>
    public static string PassForHome(string home) => ForHome(home) + "-pass";
}

/// <summary>Opens the keychain-held device key, migrating old password files once, or creates a fresh vault.</summary>
public static class VaultOpener
{
    public static VaultOpenResult Open(string home, string label, ISecretStore store,
        string? legacyPassword = null, bool create = false, string? passphrase = null)
    {
        var keyId = SecretKeys.ForHome(home);
        var passId = SecretKeys.PassForHome(home);

        if (Paths.ExistsIn(home))
        {
            var protectedKey = store.Get(passId);
            if (protectedKey is not null)
            {
                if (passphrase is null)
                    return new VaultOpenResult(VaultOpenState.NeedsPassphrase, null);

                var protectedDevice = DeviceKey.FromProtected(protectedKey, passphrase);
                return new VaultOpenResult(VaultOpenState.Opened, VaultSession.UnlockWithKey(home, protectedDevice));
            }

            var blob = store.Get(keyId);
            if (blob is not null)
                return new VaultOpenResult(VaultOpenState.Opened, VaultSession.UnlockWithKey(home, DeviceKey.FromPrivateKey(blob)));

            var legacy = Path.Combine(home, "device.key");
            if (File.Exists(legacy))
            {
                if (legacyPassword is null)
                    return new VaultOpenResult(VaultOpenState.NeedsLegacyPassword, null);

                var key = DeviceKey.Load(legacy, legacyPassword);
                store.Set(keyId, key.ExportPrivateKey());
                var migrated = legacy + ".migrated";
                File.Move(legacy, migrated, true);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(migrated, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return new VaultOpenResult(VaultOpenState.Opened, VaultSession.UnlockWithKey(home, key), Migrated: true);
            }

            throw new SecretStoreException("хранилище есть, но ключ устройства не найден ни в ключнице, ни в device.key");
        }

        if (!create)
            throw new InvalidOperationException($"хранилище не создано: {home}");
        if (Paths.AnyExistsIn(home))
            throw new InvalidDataException($"хранилище неполное: {home}");

        // Two first-run processes must not overwrite each other's keychain key.
        using var createLock = Files.Lock(Path.Combine(home, ".create"));
        if (Paths.AnyExistsIn(home))
            throw new InvalidDataException($"хранилище неполное: {home}");

        var fresh = DeviceKey.Create();
        store.Delete(passId);
        store.Set(keyId, fresh.ExportPrivateKey());
        return new VaultOpenResult(VaultOpenState.Created, VaultSession.Create(home, label, fresh));
    }
}
