namespace Pm.Secrets;

/// <summary>Last resort: 0600 file. Not a keychain, and the UI says so.</summary>
public sealed class FileStore : FileSecretStore
{
    public FileStore() : base(".key") { }

    public override string Name => "файл (0600)";
    public override bool Secure => false;

    protected override byte[] Protect(byte[] value) => value;
    protected override byte[] Unprotect(byte[] value) => value;

    protected override void SecureFile(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
