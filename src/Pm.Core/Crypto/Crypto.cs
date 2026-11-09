using System.Security.Cryptography;
using System.Text;

namespace Pm.Core.Crypto;

public static class Crypto
{
    public const int Pbkdf2Iterations = 600_000;
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    public static byte[] Random(int count) => RandomNumberGenerator.GetBytes(count);

    public static byte[] DeriveKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);

    /// <summary>Seals with AES-256-GCM. Returns nonce and ciphertext with the 16-byte tag appended.</summary>
    public static (byte[] Nonce, byte[] Sealed) Seal(byte[] key, byte[] plain)
    {
        var nonce = Random(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plain, cipher, tag);
        return (nonce, cipher.Concat(tag).ToArray());
    }

    public static byte[] Open(byte[] key, byte[] nonce, byte[] sealedBytes)
    {
        if (sealedBytes.Length < TagSize) throw new CryptographicException("данные короче тега");
        var body = sealedBytes.AsSpan(0, sealedBytes.Length - TagSize);
        var tag = sealedBytes.AsSpan(sealedBytes.Length - TagSize);
        var plain = new byte[body.Length];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Decrypt(nonce, body, tag, plain);
        return plain;
    }

    public static RSA NewRsaKey() => RSA.Create(4096);

    public static byte[] Wrap(byte[] publicKey, byte[] data)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
        return rsa.Encrypt(data, RSAEncryptionPadding.OaepSHA256);
    }

    public static byte[] Unwrap(RSA privateKey, byte[] data) =>
        privateKey.Decrypt(data, RSAEncryptionPadding.OaepSHA256);

    public static byte[] Sign(RSA privateKey, byte[] data) =>
        privateKey.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

    public static bool Verify(byte[] publicKey, byte[] data, byte[] signature)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
        return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }

    public static string Fingerprint(byte[] publicKey) =>
        string.Join("-", Convert.ToHexString(SHA256.HashData(publicKey))[..12].Chunk(4).Select(c => new string(c)));

    public static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
