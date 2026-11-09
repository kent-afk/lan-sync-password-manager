using System.Security.Cryptography;
using System.Text;
using Pm.Core.Storage;

namespace Pm.Secrets;

/// <summary>File-backed secret store. Subclasses only decide how the bytes are protected.</summary>
public abstract class FileSecretStore : ISecretStore
{
    private readonly string _directory;
    private readonly string _extension;

    protected FileSecretStore(string extension)
    {
        _extension = extension;
        _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pm", "secrets");
        Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public abstract string Name { get; }
    public abstract bool Secure { get; }

    public byte[]? Get(string key)
    {
        var path = PathFor(key);
        return File.Exists(path) ? Unprotect(File.ReadAllBytes(path)) : null;
    }

    public void Set(string key, byte[] value)
    {
        var path = PathFor(key);
        Files.WriteAtomic(path, Protect(value), secret: true);
        SecureFile(path);
    }

    public void Delete(string key)
    {
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);
    }

    protected abstract byte[] Protect(byte[] value);
    protected abstract byte[] Unprotect(byte[] value);

    /// <summary>Hook for stores that need filesystem permissions instead of OS-level encryption.</summary>
    protected virtual void SecureFile(string path) { }

    private string PathFor(string key) =>
        Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant() + _extension);
}
