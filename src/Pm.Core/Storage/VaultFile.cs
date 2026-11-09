using System.Text.Json;
using Pm.Core.Vault;

namespace Pm.Core.Storage;

public static class VaultFile
{
    private static readonly byte[] Magic = "PMV1"u8.ToArray();

    public static byte[] Seal(Vault.Vault vault, byte[] vaultKey)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(vault, PmJsonContext.Default.Vault);
        var (nonce, sealedBytes) = Crypto.Crypto.Seal(vaultKey, json);
        return Magic.Concat(nonce).Concat(sealedBytes).ToArray();
    }

    public static Vault.Vault Open(byte[] data, byte[] vaultKey)
    {
        if (data.Length < Magic.Length + Crypto.Crypto.NonceSize + Crypto.Crypto.TagSize ||
            !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("store.bin повреждён или не является хранилищем Pm");

        var nonce = data[Magic.Length..(Magic.Length + Crypto.Crypto.NonceSize)];
        var sealedBytes = data[(Magic.Length + Crypto.Crypto.NonceSize)..];
        var json = Crypto.Crypto.Open(vaultKey, nonce, sealedBytes);
        var vault = JsonSerializer.Deserialize<Vault.Vault>(json, PmJsonContext.Default.Vault)
                    ?? throw new InvalidDataException("store.bin пуст");

        // Tombstones from older builds may still carry the secret fields. Scrub on every open.
        foreach (var entry in vault.Entries)
        {
            if (!entry.Deleted) continue;
            entry.Login = entry.Password = entry.Url = entry.Notes = "";
        }

        return vault;
    }

    public static Vault.Vault Load(string path, byte[] vaultKey) => Open(File.ReadAllBytes(path), vaultKey);
}
