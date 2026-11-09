using System.Security.Cryptography;
using System.Text;
using Pm.Cli.Diagnostics;
using Pm.Core;
using Pm.Core.Crypto;
using Pm.Core.Storage;
using Pm.Core.Vault;
using Pm.Net;
using Pm.Secrets;
using Pm.Sync;

namespace Pm.Cli.Commands;

public static class Cli
{
    private static readonly HashSet<string> BareFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "generate", "help",
    };

    public static async Task<int> RunAsync(string[] args)
    {
        var command = args.Length == 0 ? "help" : args[0];
        var (pos, flags) = Parse(args.Skip(1).ToArray());

        switch (command.ToLowerInvariant())
        {
            case "help":
            case "--help":
            case "-h":
                Help();
                return 0;

            case "init":
                return Init(flags);

            case "pair-wait":
                return await PairWait();

            case "pair":
                return await Pair(pos);

            case "serve":
                return await Serve();

            case "sync":
                return await Sync(pos);

            case "add":
                return Add(pos, flags);

            case "ls":
            case "list":
                return List(flags);

            case "get":
                return Get(pos);

            case "rm":
            case "delete":
                return Remove(pos);

            case "revoke":
                return Revoke(pos);

            case "keychain-status":
                return KeychainStatus();

            case "recover":
                return Recover();

            case "gen":
                Console.WriteLine(DeviceKey.GeneratePassword(pos.Count > 0 && int.TryParse(pos[0], out var n) ? n : 20));
                return 0;

            case "open":
                return Open();

            case "selfcheck":
                return await SelfCheck.RunAsync();

            case "selfcheck-lock-child":
                return LockChild(pos);

            default:
                Console.Error.WriteLine($"неизвестная команда: {command}");
                Help();
                return 1;
        }
    }

    private static void Help()
    {
        Console.WriteLine("""
                          pm — менеджер паролей в локальной сети

                            pm                          открыть приложение
                            pm gui                      открыть приложение
                            pm --help                   эта справка

                            pm init [--label ИМЯ]        создать устройство
                            pm pair-wait                 показать адрес и код для второго устройства
                            pm pair АДРЕС КОД            сопрячься с первым устройством
                            pm serve                     принимать синхронизацию
                            pm sync [АДРЕС]              синхронизировать (без адреса — со всеми)
                            pm add НАЗВАНИЕ [--login Л] [--password П] [--url U] [--notes N] [--generate]
                            pm ls [--search ТЕКСТ]
                            pm get ЗАПРОС
                            pm rm ЗАПРОС
                            pm revoke ID                 отозвать устройство
                            pm keychain-status           что лежит в ключнице
                            pm recover                   снять passphrase старым ключом
                            pm gen [ДЛИНА]
                            pm open                      адрес и отпечаток устройства
                            pm selfcheck

                          Пароль устройства можно передать через переменную PM_PASS.
                          Отпечатки подтверждаются вопросом; PM_CONFIRM=1 пропускает его.
                          """);
    }

    private static int Init(Dictionary<string, string?> flags)
    {
        var label = flags.GetValueOrDefault("label") ?? Environment.MachineName;
        if (Paths.Exists)
        {
            Console.Error.WriteLine("хранилище уже создано");
            return 1;
        }

        var store = SecretStores.Create();
        var result = VaultOpener.Open(Paths.Home, label, store, create: true);
        using var session = result.Session!;

        Console.WriteLine($"создано:    {Paths.Home}");
        Console.WriteLine($"устройство: {session.Config.Label} ({session.Config.DeviceId})");
        Console.WriteLine($"отпечаток:  {session.OwnFingerprint}");
        Console.WriteLine($"ключ:       {store.Name}");
        if (!store.Secure)
            Console.WriteLine("внимание: ключница недоступна, ключ лежит в файле 0600");
        return 0;
    }

    private static async Task<int> PairWait()
    {
        using var session = Unlock();
        var ticket = new PairingTicket();
        await using var server = await LanServer.StartAsync(session, "0.0.0.0", session.Config.Port, ticket);

        Console.WriteLine($"адрес: {server.Address}");
        Console.WriteLine($"код:   {ticket.Code}");
        Console.WriteLine("ожидание второго устройства, 5 минут...");

        var finished = await Task.WhenAny(ticket.Pending.Task, Task.Delay(TimeSpan.FromMinutes(5)));
        if (finished != ticket.Pending.Task)
        {
            Console.Error.WriteLine("время истекло");
            return 2;
        }

        var pending = await ticket.Pending.Task;
        Console.WriteLine($"устройство: {pending.Label}");
        Console.WriteLine($"ваш отпечаток:        {pending.OwnFingerprint}");
        Console.WriteLine($"отпечаток собеседника: {pending.Fingerprint}");

        var confirmed = Confirm();
        ticket.Approve(confirmed);
        if (!confirmed)
        {
            Console.Error.WriteLine("сопряжение отменено");
            return 3;
        }

        var peer = await ticket.Completed.Task;
        Console.WriteLine($"сопряжено: {peer.Label}");
        Console.WriteLine($"ваш отпечаток:      {peer.OwnFingerprint}");
        Console.WriteLine($"отпечаток собеседника: {peer.Fingerprint}");
        Console.WriteLine("сверьте отпечаток собеседника с его собственным отпечатком.");
        return 0;
    }

    private static async Task<int> Pair(List<string> pos)
    {
        if (pos.Count < 2)
        {
            Console.Error.WriteLine("нужно: pm pair АДРЕС КОД");
            return 1;
        }

        using var session = Unlock();
        var info = await SyncClient.GetPairInfoAsync(session, pos[0]);
        Console.WriteLine($"устройство: {info.Label}");
        Console.WriteLine($"ваш отпечаток:        {info.OwnFingerprint}");
        Console.WriteLine($"отпечаток собеседника: {info.Fingerprint}");
        if (!Confirm())
        {
            Console.Error.WriteLine("сопряжение отменено");
            return 3;
        }

        var peer = await SyncClient.CompletePairAsync(session, pos[0], info, pos[1], session.Config.Port);
        Console.WriteLine($"сопряжено: {peer.Label}");
        Console.WriteLine($"ваш отпечаток:      {peer.OwnFingerprint}");
        Console.WriteLine($"отпечаток собеседника: {peer.Fingerprint}");
        return 0;
    }

    private static async Task<int> Serve()
    {
        using var session = Unlock();
        await using var server = await LanServer.StartAsync(session, "0.0.0.0", session.Config.Port);
        Console.WriteLine($"слушаю {server.Address}");
        Console.WriteLine($"отпечаток: {session.OwnFingerprint}");
        Console.WriteLine("Ctrl+C — выход");
        await Task.Delay(Timeout.Infinite);
        return 0;
    }

    private static async Task<int> Sync(List<string> pos)
    {
        using var session = Unlock();

        if (pos.Count > 0)
        {
            var (conflicts, live) = await SyncClient.SyncAsync(session, pos[0]);
            Console.WriteLine($"синхронизировано с {pos[0]}: записей {live}, конфликтов {conflicts}");
            return 0;
        }

        if (session.Config.Peers.Count == 0)
        {
            Console.WriteLine("нет сопряжённых устройств");
            return 0;
        }

        var (peers, total) = await SyncClient.SyncAllAsync(session);
        Console.WriteLine($"синхронизировано устройств: {peers} из {session.Config.Peers.Count}, конфликтов {total}");
        return peers == 0 ? 2 : 0;
    }

    private static int Add(List<string> pos, Dictionary<string, string?> flags)
    {
        using var session = Unlock();

        var title = pos.Count > 0 ? string.Join(' ', pos) : Prompt("название: ");
        if (string.IsNullOrWhiteSpace(title))
        {
            Console.Error.WriteLine("название пустое");
            return 1;
        }

        var login = flags.GetValueOrDefault("login") ?? Prompt("логин: ");
        var password = flags.GetValueOrDefault("password")
                       ?? (flags.ContainsKey("generate")
                           ? DeviceKey.GeneratePassword()
                           : AskPassword("пароль: "));
        var url = flags.GetValueOrDefault("url") ?? "";
        var notes = flags.GetValueOrDefault("notes") ?? "";

        var entry = session.AddEntry(title, login, password, url, notes);
        Console.WriteLine($"добавлено: {entry.Title} ({entry.Id[..8]})");
        return 0;
    }

    private static int List(Dictionary<string, string?> flags)
    {
        using var session = Unlock();
        var search = flags.GetValueOrDefault("search");
        var entries = session.Vault.Entries
            .Where(e => !e.Deleted)
            .Where(e => search is null ||
                        e.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        e.Login.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        e.Url.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (entries.Count == 0)
        {
            Console.WriteLine("пусто");
            return 0;
        }

        foreach (var e in entries)
            Console.WriteLine($"{e.Id[..8]}  {Cut(e.Title, 28),-28}  {Cut(e.Login, 24),-24}  {Cut(e.Url, 34)}");
        return 0;
    }

    private static int Get(List<string> pos)
    {
        if (pos.Count == 0)
        {
            Console.Error.WriteLine("нужно: pm get ЗАПРОС");
            return 1;
        }

        using var session = Unlock();
        var found = Find(session.Vault, pos[0]);
        if (found.Count == 0)
        {
            Console.Error.WriteLine("не найдено");
            return 1;
        }

        if (found.Count > 1)
        {
            Console.Error.WriteLine("найдено несколько, уточните:");
            foreach (var e in found) Console.Error.WriteLine($"  {e.Id[..8]}  {e.Title}  {e.Login}");
            return 1;
        }

        var entry = found[0];
        Console.WriteLine($"название: {entry.Title}");
        Console.WriteLine($"логин:    {entry.Login}");
        Console.WriteLine($"пароль:   {entry.Password}");
        if (entry.Url.Length > 0) Console.WriteLine($"адрес:    {entry.Url}");
        if (entry.Notes.Length > 0) Console.WriteLine($"заметки:  {entry.Notes}");
        return 0;
    }

    private static int Remove(List<string> pos)
    {
        if (pos.Count == 0)
        {
            Console.Error.WriteLine("нужно: pm rm ЗАПРОС");
            return 1;
        }

        using var session = Unlock();
        var found = Find(session.Vault, pos[0]);
        if (found.Count != 1)
        {
            Console.Error.WriteLine(found.Count == 0 ? "не найдено" : "найдено несколько, уточните");
            return 1;
        }

        session.DeleteEntry(found[0].Id);
        Console.WriteLine($"удалено: {found[0].Title}");
        return 0;
    }

    private static int LockChild(List<string> pos)
    {
        if (pos.Count < 2) return 1;
        using var session = VaultSession.Unlock(pos[0], pos[1]);
        session.AddEntry("child-" + Environment.ProcessId, "u", "p", "", "");
        return 0;
    }

    private static int KeychainStatus()
    {
        Paths.EnsureExists();
        var store = SecretStores.Create();
        var pass = store.Get(SecretKeys.PassForHome(Paths.Home));
        var plain = store.Get(SecretKeys.ForHome(Paths.Home));

        Console.WriteLine($"папка:       {Paths.Home}");
        Console.WriteLine($"ключница:    {store.Name}");
        Console.WriteLine($"passphrase:  {(pass is null ? "нет" : "есть")}");
        Console.WriteLine($"старый ключ: {(plain is null ? "нет" : "есть")}");
        if (plain is not null)
            Console.WriteLine("восстановление: pm recover");
        return 0;
    }

    private static int Recover()
    {
        Paths.EnsureExists();
        var store = SecretStores.Create();
        var keyId = SecretKeys.ForHome(Paths.Home);
        var blob = store.Get(keyId);
        if (blob is null)
        {
            Console.Error.WriteLine("старый ключ не найден; восстановление невозможно");
            return 1;
        }

        using var key = DeviceKey.FromPrivateKey(blob);
        using var session = VaultSession.UnlockWithKey(Paths.Home, key);
        VaultSecurity.Disable(Paths.Home, store, key);

        Console.WriteLine($"passphrase снят; записей: {session.Vault.Entries.Count(e => !e.Deleted)}");
        Console.WriteLine("установите новый passphrase в настройках.");
        return 0;
    }

    private static int Revoke(List<string> pos)
    {
        if (pos.Count == 0)
        {
            Console.Error.WriteLine("нужно: pm revoke ID");
            return 1;
        }

        using var session = Unlock();
        if (!session.RemoveDevice(pos[0]))
        {
            Console.Error.WriteLine("устройство не найдено");
            return 1;
        }

        Console.WriteLine("устройство отозвано");
        return 0;
    }

    private static int Open()
    {
        using var session = Unlock();
        Console.WriteLine($"устройство: {session.Config.Label} ({session.Config.DeviceId})");
        Console.WriteLine($"адрес:      {Network.LanAddress()}:{session.Config.Port}");
        Console.WriteLine($"отпечаток:  {session.OwnFingerprint}");
        Console.WriteLine($"записей:    {session.Vault.Entries.Count(e => !e.Deleted)}");
        foreach (var peer in session.Config.Peers)
            Console.WriteLine($"сопряжено:  {peer.Label}  {peer.Address}  {peer.Id}");
        return 0;
    }

    private static VaultSession Unlock()
    {
        Paths.EnsureExists();
        var store = SecretStores.Create();
        var result = VaultOpener.Open(Paths.Home, Environment.MachineName, store);

        if (result.State == VaultOpenState.NeedsPassphrase)
        {
            var passphrase = AskPassword("пароль: ");
            try
            {
                result = VaultOpener.Open(Paths.Home, Environment.MachineName, store, passphrase: passphrase);
            }
            catch (CryptographicException)
            {
                throw new Exception("неверный пароль");
            }
        }

        if (result.State == VaultOpenState.NeedsLegacyPassword)
        {
            var password = AskPassword("старый пароль устройства: ");
            try
            {
                result = VaultOpener.Open(Paths.Home, Environment.MachineName, store, password);
            }
            catch (CryptographicException)
            {
                throw new Exception("неверный пароль");
            }

            Console.Error.WriteLine("ключ перенесён в ключницу");
        }

        return result.Session ?? throw new Exception("не удалось открыть хранилище");
    }

    private static List<Entry> Find(Vault vault, string query)
    {
        var live = vault.Entries.Where(e => !e.Deleted).ToList();
        var byId = live.Where(e => e.Id.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byId.Count > 0) return byId;

        return live.Where(e =>
            e.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            e.Login.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            e.Url.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static string AskPassword(string prompt)
    {
        if (Environment.GetEnvironmentVariable("PM_PASS") is { Length: > 0 } fromEnv) return fromEnv;

        Console.Write(prompt);
        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0) buffer.Length--;
                continue;
            }

            if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
        }

        Console.WriteLine();
        return buffer.ToString();
    }

    private static bool Confirm()
    {
        if (Environment.GetEnvironmentVariable("PM_CONFIRM") is "1" or "y" or "yes") return true;

        Console.Write("отпечатки совпадают? [y/N] ");
        return (Console.ReadLine()?.Trim().ToLowerInvariant()) is "y" or "yes";
    }

    private static string Prompt(string prompt)
    {
        Console.Write(prompt);
        return Console.ReadLine()?.Trim() ?? "";
    }

    private static string Cut(string value, int width) =>
        value.Length <= width ? value : value[..(width - 1)] + "…";

    private static (List<string> Pos, Dictionary<string, string?> Flags) Parse(string[] args)
    {
        var pos = new List<string>();
        var flags = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--"))
            {
                pos.Add(args[i]);
                continue;
            }

            var name = args[i][2..];
            if (!BareFlags.Contains(name) && i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                flags[name] = args[++i];
            }
            else
            {
                flags[name] = null;
            }
        }

        return (pos, flags);
    }
}