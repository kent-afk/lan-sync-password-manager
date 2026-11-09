namespace Pm.Core.Storage;

public static class Files
{
    /// <summary>Vault directories are private even when the process umask is permissive.</summary>
    public static void EnsurePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Fix permissions left by older builds. No-op on Windows.</summary>
    public static void MakePrivate(params string[] paths)
    {
        if (OperatingSystem.IsWindows()) return;
        foreach (var path in paths)
        {
            if (File.Exists(path))
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>Write-then-rename so a reader never sees a half-written vault.</summary>
    public static void WriteAtomic(string path, byte[] bytes, bool secret = false)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = path + "." + Guid.NewGuid().ToString("n") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (secret && !OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            using (var stream = new FileStream(temp, options))
            {
                stream.Write(bytes);
                // shortcut: the temp file is durable, but the parent directory entry is not fsynced.
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Cross-process write lock. All processes sharing a vault must use the same path.</summary>
    public static IDisposable Lock(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) EnsurePrivateDirectory(directory);

        var until = DateTime.UtcNow.AddSeconds(3);
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < until)
            {
                Thread.Sleep(25);
            }
        }
    }
}
