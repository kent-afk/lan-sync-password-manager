using System.Security.Cryptography;
using Pm.Core.Storage;

namespace Pm.Core.Crypto;

/// <summary>RSA keypair whose private half is stored on disk encrypted with the device passphrase.</summary>
public sealed class DeviceKey : IDisposable
{
    private static readonly byte[] Magic = "PMK1"u8.ToArray();

    public RSA Rsa { get; }
    public byte[] PublicKey => Rsa.ExportSubjectPublicKeyInfo();

    private DeviceKey(RSA rsa) => Rsa = rsa;

    public static DeviceKey Create() => new(Crypto.NewRsaKey());

    public static DeviceKey FromPrivateKey(byte[] pkcs8)
    {
        var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(pkcs8, out _);
        return new DeviceKey(rsa);
    }

    public byte[] ExportPrivateKey() => Rsa.ExportPkcs8PrivateKey();

    public static DeviceKey Load(string path, string password) =>
        FromProtected(File.ReadAllBytes(path), password);

    public static DeviceKey FromProtected(byte[] data, string password)
    {
        if (data.Length < 4 + 16 + Crypto.NonceSize + Crypto.TagSize ||
            !data.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("device.key повреждён");

        var salt = data[4..20];
        var nonce = data[20..(20 + Crypto.NonceSize)];
        var sealedBytes = data[(20 + Crypto.NonceSize)..];
        var key = Crypto.DeriveKey(password, salt);
        var pkcs8 = Crypto.Open(key, nonce, sealedBytes);
        var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(pkcs8, out _);
        return new DeviceKey(rsa);
    }

    public byte[] ExportProtected(string password)
    {
        var salt = Crypto.Random(16);
        var key = Crypto.DeriveKey(password, salt);
        var (nonce, sealedBytes) = Crypto.Seal(key, Rsa.ExportPkcs8PrivateKey());
        return Magic.Concat(salt).Concat(nonce).Concat(sealedBytes).ToArray();
    }

    public void Save(string path, string password) =>
        Files.WriteAtomic(path, ExportProtected(password), secret: true);

    public void Dispose() => Rsa.Dispose();

    public static string NewDeviceId() => Crypto.Hex(Crypto.Random(8));

    public static string GeneratePassword(int length = 20)
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#$%^&*-_=+";
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }
}
