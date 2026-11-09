namespace Pm.Core.Storage;

/// <summary>Platform keychain. Get returns null only when the entry is missing; failures throw.</summary>
public interface ISecretStore
{
    string Name { get; }

    /// <summary>False when the secret is only as safe as the user's file permissions.</summary>
    bool Secure { get; }

    byte[]? Get(string key);
    void Set(string key, byte[] value);
    void Delete(string key);
}

public sealed class SecretStoreException(string message) : Exception(message);
