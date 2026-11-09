using Pm.Core.Crypto;
using Pm.Core.Storage;

namespace Pm.Core.Vault;

public static class VaultSecurity
{
    public static bool IsEnabled(string home, ISecretStore store)
    {
        try
        {
            return store.Get(SecretKeys.PassForHome(home)) is not null;
        }
        catch
        {
            return false;
        }
    }

    public static void Enable(string home, ISecretStore store, DeviceKey key, string passphrase)
    {
        var passId = SecretKeys.PassForHome(home);
        var keyId = SecretKeys.ForHome(home);

        try
        {
            store.Set(passId, key.ExportProtected(passphrase));
            VerifyProtected(store, passId, key, passphrase);
        }
        catch
        {
            try { store.Delete(passId); } catch { }
            throw;
        }

        try
        {
            store.Delete(keyId);
            if (store.Get(keyId) is not null)
                throw new SecretStoreException("ключница не удалила старый ключ");
        }
        catch
        {
            // Do not leave both the passphrase blob and the plain key behind.
            try { store.Delete(passId); } catch { }
            throw;
        }
    }

    public static void Change(string home, ISecretStore store, DeviceKey key, string passphrase)
    {
        var passId = SecretKeys.PassForHome(home);
        var previous = store.Get(passId);

        try
        {
            store.Set(passId, key.ExportProtected(passphrase));
            VerifyProtected(store, passId, key, passphrase);
        }
        catch
        {
            try
            {
                if (previous is not null) store.Set(passId, previous);
                else store.Delete(passId);
            }
            catch { }
            throw;
        }
    }

    public static void Disable(string home, ISecretStore store, DeviceKey key)
    {
        var passId = SecretKeys.PassForHome(home);
        var keyId = SecretKeys.ForHome(home);

        store.Set(keyId, key.ExportPrivateKey());
        try
        {
            var stored = store.Get(keyId)
                         ?? throw new SecretStoreException("ключница не вернула сохранённый ключ");
            using var verified = DeviceKey.FromPrivateKey(stored);
            if (!verified.PublicKey.SequenceEqual(key.PublicKey))
                throw new SecretStoreException("ключница сохранила другой ключ");

            store.Delete(passId);
            if (store.Get(passId) is not null)
                throw new SecretStoreException("ключница не удалила passphrase");
        }
        catch
        {
            // The passphrase stays in force if the plain key cannot be verified/removed.
            try { store.Delete(keyId); } catch { }
            throw;
        }
    }

    private static void VerifyProtected(ISecretStore store, string passId, DeviceKey key, string passphrase)
    {
        var stored = store.Get(passId)
                     ?? throw new SecretStoreException("ключница не вернула сохранённый ключ");
        using var verified = DeviceKey.FromProtected(stored, passphrase);
        if (!verified.PublicKey.SequenceEqual(key.PublicKey))
            throw new SecretStoreException("ключница сохранила другой ключ");
    }

    public static bool Verify(string home, ISecretStore store, string passphrase)
    {
        try
        {
            var blob = store.Get(SecretKeys.PassForHome(home));
            if (blob is null) return false;
            using var key = DeviceKey.FromProtected(blob, passphrase);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
