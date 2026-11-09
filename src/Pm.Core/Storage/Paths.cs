namespace Pm.Core.Storage;

public static class Paths
{
    public static string Home =>
        Environment.GetEnvironmentVariable("PM_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pm");

    public static bool Exists =>
        File.Exists(Path.Combine(Home, "keys.bin")) &&
        File.Exists(Path.Combine(Home, "store.bin")) &&
        File.Exists(Path.Combine(Home, "config.json"));

    public static bool ExistsIn(string home) =>
        File.Exists(Path.Combine(home, "keys.bin")) &&
        File.Exists(Path.Combine(home, "store.bin")) &&
        File.Exists(Path.Combine(home, "config.json"));

    public static bool AnyExistsIn(string home) =>
        File.Exists(Path.Combine(home, "keys.bin")) ||
        File.Exists(Path.Combine(home, "store.bin")) ||
        File.Exists(Path.Combine(home, "config.json"));

    public static void EnsureExists()
    {
        if (!Exists) throw new InvalidOperationException("хранилище не создано, выполните: pm init");
        Directory.CreateDirectory(Home);
    }
}
