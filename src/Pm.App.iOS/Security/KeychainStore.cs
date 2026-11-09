using Foundation;
using Pm.Core.Storage;
using Security;

namespace Pm.App.iOS;

/// <summary>iOS Keychain, readable after the first unlock of the device, never synced to iCloud.</summary>
public sealed class KeychainStore : ISecretStore
{
    private const string Service = "dev.pm.vault";

    public string Name => "iOS Keychain";
    public bool Secure => true;

    public byte[]? Get(string key)
    {
        using var query = new SecRecord(SecKind.GenericPassword) { Service = Service, Account = key };
        using var match = SecKeyChain.QueryAsRecord(query, out var status);

        if (status == SecStatusCode.ItemNotFound) return null;
        if (status != SecStatusCode.Success || match?.ValueData is null)
            throw new SecretStoreException($"Keychain: {status}");

        return match.ValueData.ToArray();
    }

    public void Set(string key, byte[] value)
    {
        using var query = new SecRecord(SecKind.GenericPassword) { Service = Service, Account = key };
        using var update = new SecRecord(SecKind.GenericPassword)
        {
            ValueData = NSData.FromArray(value),
            Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly,
        };

        // Update is atomic: if it fails, the previous key stays usable. Remove-then-add could lose it.
        var status = SecKeyChain.Update(query, update);
        if (status == SecStatusCode.ItemNotFound)
        {
            using var record = new SecRecord(SecKind.GenericPassword)
            {
                Service = Service,
                Account = key,
                ValueData = NSData.FromArray(value),
                Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly,
            };
            status = SecKeyChain.Add(record);
        }

        if (status != SecStatusCode.Success)
            throw new SecretStoreException($"Keychain: {status}");
    }

    public void Delete(string key)
    {
        using var query = new SecRecord(SecKind.GenericPassword) { Service = Service, Account = key };
        var status = SecKeyChain.Remove(query);
        if (status is not (SecStatusCode.Success or SecStatusCode.ItemNotFound))
            throw new SecretStoreException($"Keychain: {status}");
    }
}
