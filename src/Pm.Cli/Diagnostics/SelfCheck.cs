using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Pm.Core;
using Pm.Core.Crypto;
using Pm.Core.Storage;
using Pm.Core.Vault;
using Pm.Net;
using Pm.Secrets;
using Pm.Sync;

namespace Pm.Cli.Diagnostics;

public static class SelfCheck
{
    public static async Task<int> RunAsync()
    {
        var failed = 0;
        var root = Path.Combine(Path.GetTempPath(), "pm-selfcheck-" + Guid.NewGuid().ToString("n")[..8]);

        void Step(string name, Action action)
        {
            try
            {
                action();
                Console.WriteLine($"  ok  {name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"FAIL  {name}: {ex.Message}");
            }
        }

        async Task StepAsync(string name, Func<Task> action)
        {
            try
            {
                await action();
                Console.WriteLine($"  ok  {name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"FAIL  {name}: {ex.Message}");
            }
        }

        async Task<PairResult> PairAsync(VaultSession joiner, LanServer host, PairingTicket ticket, int port)
        {
            var pairing = SyncClient.PairAsync(joiner, host.Address, ticket.Code, port);
            var pending = await ticket.Pending.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert(pending.DeviceId == joiner.Config.DeviceId, "билет получил чужое устройство");
            ticket.Approve(true);
            return await pairing;
        }

        try
        {
            Console.WriteLine("шифрование");

            Step("AES-GCM туда и обратно", () =>
            {
                var key = Crypto.Random(32);
                var plain = System.Text.Encoding.UTF8.GetBytes("пароль-42");
                var (nonce, sealedBytes) = Crypto.Seal(key, plain);
                Assert(Crypto.Open(key, nonce, sealedBytes).SequenceEqual(plain));
            });

            Step("AES-GCM отвергает подмену", () =>
            {
                var key = Crypto.Random(32);
                var (nonce, sealedBytes) = Crypto.Seal(key, System.Text.Encoding.UTF8.GetBytes("x"));
                sealedBytes[0] ^= 1;
                Expect<CryptographicException>(() => Crypto.Open(key, nonce, sealedBytes));
            });

            Step("passphrase защищает ключ", () =>
            {
                using var key = DeviceKey.Create();
                var blob = key.ExportProtected("long-passphrase");
                using var loaded = DeviceKey.FromProtected(blob, "long-passphrase");
                Assert(loaded.PublicKey.SequenceEqual(key.PublicKey));
                Expect<CryptographicException>(() => DeviceKey.FromProtected(blob, "wrong"));
            });

            Step("RSA-ключ заворачивается и разворачивается", () =>
            {
                using var rsa = RSA.Create(2048);
                var pub = rsa.ExportSubjectPublicKeyInfo();
                var secret = Crypto.Random(32);
                Assert(Crypto.Unwrap(rsa, Crypto.Wrap(pub, secret)).SequenceEqual(secret));
            });

            Step("подпись проверяется, подделка отвергается", () =>
            {
                using var rsa = RSA.Create(2048);
                var pub = rsa.ExportSubjectPublicKeyInfo();
                var data = System.Text.Encoding.UTF8.GetBytes("keys");
                var sig = Crypto.Sign(rsa, data);
                Assert(Crypto.Verify(pub, data, sig));
                data[0] ^= 1;
                Assert(!Crypto.Verify(pub, data, sig));
            });

            Console.WriteLine("слияние");

            Step("более новая версия побеждает", () =>
            {
                var local = new Vault { Entries = { new Entry { Id = "1", Version = 2, Device = "a", Title = "new" } } };
                var remote = new Vault { Entries = { new Entry { Id = "1", Version = 1, Device = "b", Title = "old" } } };
                var (merged, conflicts, _) = Merger.Merge(local, remote);
                Assert(merged.Entries.Single().Title == "new" && conflicts == 0);
            });

            Step("параллельные правки сохраняют обе версии", () =>
            {
                var local = new Vault { Entries = { new Entry { Id = "1", Version = 2, Device = "a", Title = "t", Password = "from-a" } } };
                var remote = new Vault { Entries = { new Entry { Id = "1", Version = 2, Device = "b", Title = "t", Password = "from-b" } } };
                var (merged, conflicts, _) = Merger.Merge(local, remote);
                Assert(conflicts == 1, "конфликт не найден");
                Assert(merged.Entries.Count == 2, "должно быть две записи");
                Assert(merged.Entries.Any(e => e.Password == "from-a") && merged.Entries.Any(e => e.Password == "from-b"),
                    "потеряна одна из версий");
            });

            Step("надгробие не воскресает", () =>
            {
                var local = new Vault
                {
                    Entries = { new Entry { Id = "1", Version = 2, Device = "a", Deleted = true, DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } },
                };
                var remote = new Vault { Entries = { new Entry { Id = "1", Version = 1, Device = "b", Title = "old" } } };
                var (merged, _, _) = Merger.Merge(local, remote);
                Assert(merged.Entries.Single().Deleted);
            });

            Step("старое надгробие не воскресает", () =>
            {
                var local = new Vault
                {
                    Entries = { new Entry { Id = "1", Version = 2, Device = "a", Deleted = true, DeletedAt = DateTimeOffset.UtcNow.AddDays(-100).ToUnixTimeMilliseconds() } },
                };
                var remote = new Vault { Entries = { new Entry { Id = "1", Version = 1, Device = "b", Title = "old" } } };
                var (merged, _, _) = Merger.Merge(local, remote);
                Assert(merged.Entries.Single().Deleted, "удалённый пароль вернулся после 90 дней");
            });

            Step("надгробие побеждает параллельную правку", () =>
            {
                var local = new Vault
                {
                    Entries = { new Entry { Id = "1", Version = 2, Device = "a", Deleted = true, DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } },
                };
                var remote = new Vault
                {
                    Entries = { new Entry { Id = "1", Version = 2, Device = "b", Title = "live", Password = "secret" } },
                };
                var (merged, _, _) = Merger.Merge(local, remote);
                Assert(merged.Entries.Count == 1, "создана живая копия удалённого секрета");
                Assert(merged.Entries[0].Deleted && merged.Entries[0].Password == "", "удаление проиграло правке");
            });

            Step("надгробие побеждает более новую правку", () =>
            {
                var local = new Vault
                {
                    Entries = { new Entry { Id = "1", Version = 2, Device = "a", Deleted = true, DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } },
                };
                var remote = new Vault
                {
                    Entries = { new Entry { Id = "1", Version = 3, Device = "b", Title = "live", Password = "secret" } },
                };
                var (merged, _, _) = Merger.Merge(local, remote);
                Assert(merged.Entries.Count == 1 && merged.Entries[0].Deleted,
                    "более новая правка воскресила удалённую запись");
            });

            Step("одинаковое слияние не считается изменением", () =>
            {
                var local = new Vault { Entries = { new Entry { Id = "1", Version = 2, Device = "a", Title = "same" } } };
                var (_, conflicts, changed) = Merger.Merge(local, local);
                Assert(conflicts == 0 && !changed, "слияние без изменений помечено как изменение");
            });

            Step("надгробие не хранит секреты", () =>
            {
                var key = Crypto.Random(32);
                var vault = new Vault
                {
                    Entries =
                    {
                        new Entry
                        {
                            Id = "1", Deleted = true, Login = "login", Password = "secret",
                            Url = "https://example", Notes = "notes",
                        },
                    },
                };

                var opened = VaultFile.Open(VaultFile.Seal(vault, key), key);
                var entry = opened.Entries.Single();
                Assert(entry.Login == "" && entry.Password == "" && entry.Url == "" && entry.Notes == "",
                    "секреты остались в надгробии");
            });

            Directory.CreateDirectory(root);
            using var a = VaultSession.Init(Path.Combine(root, "a"), "A", "pass-a");
            using var b = VaultSession.Init(Path.Combine(root, "b"), "B", "pass-b");
            using var c = VaultSession.Init(Path.Combine(root, "c-rekey"), "C", "pass-c");

            Console.WriteLine("локальное хранилище");

            Step("удаление стирает поля секретов", () =>
            {
                using var session = VaultSession.Init(Path.Combine(root, "delete"), "D", "pass-d");
                var entry = session.AddEntry("site", "login", "secret", "https://example", "notes");
                session.DeleteEntry(entry.Id);
                var deleted = session.Vault.Entries.Single(e => e.Id == entry.Id);
                Assert(deleted.Deleted && deleted.Password == "" && deleted.Login == "" &&
                       deleted.Url == "" && deleted.Notes == "", "секреты остались в надгробии");
            });

            Step("неверный пароль не открывает ключ", () =>
                Expect<CryptographicException>(() => VaultSession.Unlock(a.Home, "wrong-pass")));

            Step("повреждённый store.bin отвергается", () =>
            {
                var sealedBytes = a.SealStore();
                sealedBytes[^1] ^= 1;
                Expect<CryptographicException>(() => VaultFile.Open(sealedBytes, a.VaultKey));
            });

            Step("неполное хранилище не пересоздаётся", () =>
            {
                var partial = Path.Combine(root, "partial");
                Directory.CreateDirectory(partial);
                File.WriteAllBytes(Path.Combine(partial, "keys.bin"), new byte[] { 1 });
                File.WriteAllBytes(Path.Combine(partial, "config.json"), new byte[] { 1 });
                Expect<InvalidDataException>(() => VaultOpener.Open(partial, "P", new FileStore(), create: true));
            });

            if (!OperatingSystem.IsWindows())
            {
                Step("секретный файл создаётся 0600", () =>
                {
                    var path = Path.Combine(root, "secret.bin");
                    Files.WriteAtomic(path, new byte[] { 1 }, secret: true);
                    var mode = File.GetUnixFileMode(path);
                    Assert(mode == (UnixFileMode.UserRead | UnixFileMode.UserWrite), mode.ToString());
                });

                Step("каталог и файлы хранилища закрыты", () =>
                {
                    var home = Path.Combine(root, "permissions");
                    using (var session = VaultSession.Init(home, "P", "pass-p"))
                    {
                        var dir = File.GetUnixFileMode(home);
                        Assert((dir & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                       UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0,
                            $"каталог: {dir}");

                        foreach (var name in new[] { "store.bin", "keys.bin", "config.json", "device.key" })
                        {
                            var mode = File.GetUnixFileMode(Path.Combine(home, name));
                            Assert((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                           UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0,
                                $"{name}: {mode}");
                        }
                    }
                });
            }

            Step("первый запуск с ключницей открывается повторно", () =>
            {
                var home = Path.Combine(root, "keychain");
                var store = new MemorySecretStore();
                using (var created = VaultOpener.Open(home, "K", store, create: true).Session!)
                    created.AddEntry("first", "u", "p", "", "");

                var reopened = VaultOpener.Open(home, "K", store);
                Assert(reopened.State == VaultOpenState.Opened);
                using var session = reopened.Session!;
                Assert(session.Vault.Entries.Any(e => e.Title == "first"), "запись первого запуска потеряна");
            });

            Step("рассинхрон keys.bin и store.bin восстанавливается из .prev", () =>
            {
                var home = Path.Combine(root, "recover");
                using var original = VaultSession.Init(home, "R", "pass");
                original.AddEntry("recover-me", "u", "p", "", "");

                var keysPath = Path.Combine(home, "keys.bin");
                var storePath = Path.Combine(home, "store.bin");
                File.Copy(keysPath, keysPath + ".prev", true);
                File.Copy(storePath, storePath + ".prev", true);

                var newKey = Crypto.Random(Crypto.KeySize);
                var keyset = new Keyset { Rev = original.Keyset.Rev + 1 };
                foreach (var device in original.Keyset.Devices)
                    keyset.Add(device.Id, device.Label, device.PubKey, newKey);
                keyset.SignWith(original.Key);
                keyset.Save(keysPath);

                using var clone = DeviceKey.FromPrivateKey(original.Key.ExportPrivateKey());
                using var recovered = VaultSession.UnlockWithKey(home, clone);
                Assert(recovered.Vault.Entries.Any(e => e.Title == "recover-me"), "запись потеряна при восстановлении");
                Assert(recovered.VaultKey.SequenceEqual(newKey), "хранилище не перешифровано новым ключом");
                Assert(!File.Exists(keysPath + ".prev") && !File.Exists(storePath + ".prev"), "резервная копия осталась");
            });

            Step("passphrase включает, меняет и снимает защиту", () =>
            {
                using var session = VaultSession.Init(Path.Combine(root, "pp"), "PP", "old");
                var secrets = new MemorySecretStore();
                VaultSecurity.Enable(session.Home, secrets, session.Key, "pass1");
                Assert(VaultSecurity.IsEnabled(session.Home, secrets));
                Assert(VaultSecurity.Verify(session.Home, secrets, "pass1"));
                Assert(!VaultSecurity.Verify(session.Home, secrets, "wrong"));

                var locked = VaultOpener.Open(session.Home, "PP", secrets);
                Assert(locked.State == VaultOpenState.NeedsPassphrase);
                var opened = VaultOpener.Open(session.Home, "PP", secrets, passphrase: "pass1");
                using (var unlocked = opened.Session!) Assert(unlocked.Vault.Entries.Count == 0);

                VaultSecurity.Change(session.Home, secrets, session.Key, "pass2");
                Assert(VaultSecurity.Verify(session.Home, secrets, "pass2"));
                VaultSecurity.Disable(session.Home, secrets, session.Key);
                Assert(!VaultSecurity.IsEnabled(session.Home, secrets));
            });

            Step("битый passphrase-блоб не удаляет старый ключ", () =>
            {
                using var session = VaultSession.Init(Path.Combine(root, "pp-corrupt"), "PC", "old");
                var secrets = new CorruptingSecretStore();
                var keyId = SecretKeys.ForHome(session.Home);
                secrets.Set(keyId, session.Key.ExportPrivateKey());

                Expect<Exception>(() => VaultSecurity.Enable(session.Home, secrets, session.Key, "pass1"));

                Assert(secrets.Get(keyId) is not null, "старый ключ удалён");
                Assert(!VaultSecurity.IsEnabled(session.Home, secrets), "битый passphrase остался");
            });

            Step("миграция старого device.key переносит ключ", () =>
            {
                var home = Path.Combine(root, "legacy");
                using (var old = VaultSession.Init(home, "L", "old-pass"))
                    old.AddEntry("legacy", "u", "p", "", "");

                var result = VaultOpener.Open(home, "L", new MemorySecretStore(), legacyPassword: "old-pass");
                using var session = result.Session!;
                Assert(result.Migrated);
                Assert(!File.Exists(Path.Combine(home, "device.key")));
                Assert(File.Exists(Path.Combine(home, "device.key.migrated")));
                Assert(session.Vault.Entries.Any(e => e.Title == "legacy"));
            });

            Step("запись из другого процесса", () =>
            {
                var home = Path.Combine(root, "process");
                using (var seed = VaultSession.Init(home, "Process", "pass"))
                    seed.AddEntry("seed", "u", "p", "", "");

                var host = Environment.ProcessPath ?? "dotnet";
                var dll = Path.Combine(AppContext.BaseDirectory,
                    typeof(SelfCheck).Assembly.GetName().Name + ".dll");
                var arguments = Path.GetFileNameWithoutExtension(host)
                    .Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                    ? $"\"{dll}\" selfcheck-lock-child \"{home}\" pass"
                    : $"selfcheck-lock-child \"{home}\" pass";
                var info = new ProcessStartInfo(host, arguments)
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                };

                using var child = Process.Start(info) ?? throw new Exception("не удалось запустить процесс");
                if (!child.WaitForExit(10000))
                {
                    child.Kill();
                    throw new Exception("дочерний процесс завис");
                }

                Assert(child.ExitCode == 0, "дочерний процесс: " + child.StandardError.ReadToEnd());
                using var check = VaultSession.Unlock(home, "pass");
                Assert(check.Vault.Entries.Any(e => e.Title.StartsWith("child-")),
                    "запись из другого процесса потеряна");
            });

            Console.WriteLine("сеть");

            Step("после пяти ошибок билет сопряжения закрыт", () =>
            {
                var ticket = new PairingTicket();
                for (var i = 0; i < 5; i++) ticket.Fail();
                Assert(!ticket.IsValid);
                Assert(!ticket.TryConsume());
            });

            var ticket = new PairingTicket();
            await using var server = await LanServer.StartAsync(a, "127.0.0.1", 0, ticket);

            await StepAsync("сопряжение по коду", async () =>
            {
                b.Config.Peers.Add(new Peer { Id = "stale-peer", Label = "stale", Address = "127.0.0.1:1" });
                b.SaveConfig();

                var peer = await PairAsync(b, server, ticket, 47821);
                Assert(peer.DeviceId == a.Config.DeviceId, "B не узнал A");
                Assert(b.Config.Peers.All(p => p.Id != "stale-peer"), "старый пир остался после сопряжения");
                var notified = await ticket.Completed.Task;
                Assert(notified.DeviceId == b.Config.DeviceId, "A не узнал B");
                Assert(b.Keyset.Find(b.Config.DeviceId) is not null, "B не получил обёртку ключа");
            });

            Step("нельзя сопрячь собственный идентификатор", () =>
                Expect<InvalidOperationException>(() => a.AddDevice(a.Config.DeviceId, "self", b.PublicKey)));

            Step("нельзя подменить ключ существующего устройства", () =>
                Expect<InvalidOperationException>(() => a.AddDevice(b.Config.DeviceId, "B", c.PublicKey)));

            await StepAsync("неверный код сопряжения отвергается", async () =>
            {
                using var c = VaultSession.Init(Path.Combine(root, "c"), "C", "pass-c");
                var t = new PairingTicket();
                await using var s = await LanServer.StartAsync(a, "127.0.0.1", 0, t);
                var wrong = t.Code == "000000" ? "000001" : "000000";
                await ExpectAsync<InvalidOperationException>(() =>
                    SyncClient.PairAsync(c, s.Address, wrong, 47821));
            });

            await StepAsync("третье устройство для проверки ротации", async () =>
            {
                var t = new PairingTicket();
                await using var s = await LanServer.StartAsync(a, "127.0.0.1", 0, t);
                await PairAsync(c, s, t, 47821);
                Assert(c.Keyset.Find(c.Config.DeviceId) is not null, "C не получил обёртку ключа");
            });

            var entry = a.AddEntry("github", "me", "one", "https://github.com", "");

            await StepAsync("запись доходит до второго устройства", async () =>
            {
                await SyncClient.SyncAsync(b, server.Address);
                Assert(b.Vault.Entries.Any(e => e.Id == entry.Id), "запись не пришла");
            });

            await StepAsync("правка с B возвращается на A", async () =>
            {
                var fromB = b.Vault.Entries.First(e => e.Id == entry.Id).Clone();
                fromB.Password = "two";
                b.UpdateEntry(fromB);
                await SyncClient.SyncAsync(b, server.Address);
                Assert(a.Vault.Entries.First(e => e.Id == entry.Id).Password == "two", "A не увидел правку");
            });

            await StepAsync("сервер видит записи из другой сессии", async () =>
            {
                using var other = VaultSession.Unlock(a.Home, "pass-a");
                other.AddEntry("OtherProcess", "x", "y", "", "");

                await SyncClient.SyncAsync(b, server.Address);

                using var check = VaultSession.Unlock(a.Home, "pass-a");
                Assert(check.Vault.Entries.Any(e => e.Title == "OtherProcess"), "запись из другого процесса потеряна");
                Assert(check.Vault.Entries.Any(e => e.Id == entry.Id), "запись github потеряна");
            });

            await StepAsync("конфликт сохраняет обе версии на обоих устройствах", async () =>
            {
                var ea = a.Vault.Entries.First(e => e.Id == entry.Id).Clone();
                var eb = b.Vault.Entries.First(e => e.Id == entry.Id).Clone();
                ea.Password = "from-a";
                eb.Password = "from-b";
                var va = a.UpdateEntry(ea);
                var vb = b.UpdateEntry(eb);
                Assert(va.Version == vb.Version, "версии должны совпадать для конфликта");

                await SyncClient.SyncAsync(b, server.Address);

                var idsA = a.Vault.Entries.Select(e => e.Id).OrderBy(x => x).ToList();
                var idsB = b.Vault.Entries.Select(e => e.Id).OrderBy(x => x).ToList();
                Assert(idsA.SequenceEqual(idsB), "устройства разошлись");
                Assert(a.Vault.Entries.Count(e => e.Title.Contains("конфликт")) == 1, "конфликтная копия не создана");
                var passwordsA = a.Vault.Entries.Select(e => e.Password).ToList();
                Assert(passwordsA.Contains("from-a") && passwordsA.Contains("from-b"), "потеряна одна из версий");
                Assert(b.Vault.Entries.Count(e => e.Title.Contains("конфликт")) == 1, "на B нет конфликтной копии");
                var passwordsB = b.Vault.Entries.Select(e => e.Password).ToList();
                Assert(passwordsB.Contains("from-a") && passwordsB.Contains("from-b"), "B потерял одну из версий");
            });

            await StepAsync("устаревшая правка не затирает новую", async () =>
            {
                var fresh = a.AddEntry("stale-check", "u", "v1", "", "");
                await SyncClient.SyncAsync(b, server.Address);

                var stale = b.Vault.Entries.First(e => e.Id == fresh.Id).Clone();
                var newer = stale.Clone();
                newer.Password = "v2";
                a.UpdateEntry(newer);

                await SyncClient.SyncAsync(b, server.Address);

                stale.Password = "stale";
                Expect<InvalidOperationException>(() => b.UpdateEntry(stale));
            });

            await StepAsync("новый keys.bin уходит от клиента, равные rev сливаются", async () =>
            {
                using var p = VaultSession.Init(Path.Combine(root, "push-p"), "P", "pass-p");
                using var q = VaultSession.Init(Path.Combine(root, "push-q"), "Q", "pass-q");
                using var r = VaultSession.Init(Path.Combine(root, "push-r"), "R", "pass-r");

                var t1 = new PairingTicket();
                await using var ps = await LanServer.StartAsync(p, "127.0.0.1", 0, t1);
                await PairAsync(q, ps, t1, 47821);

                var t2 = new PairingTicket();
                ps.Ticket = t2;
                await PairAsync(r, ps, t2, 47821);

                await SyncClient.SyncAsync(q, ps.Address);
                Assert(q.Keyset.Find(r.Config.DeviceId) is not null, "Q не узнал R");

                Assert(q.RemoveDevice(r.Config.DeviceId), "Q не отозвал R");
                var (conflicts, live) = await SyncClient.SyncAsync(q, ps.Address);
                Assert(conflicts == 0 && live == 0, "синхронизация после отзыва не прошла");
                Assert(p.Keyset.Find(r.Config.DeviceId) is null, "P не получил отзыв");
                Assert(p.VaultKey.SequenceEqual(q.VaultKey), "P и Q остались на разных ключах");

                // Both peers add a device without seeing the other addition: equal rev, same key.
                using var s = VaultSession.Init(Path.Combine(root, "union-s"), "S", "pass-s");
                using var t = VaultSession.Init(Path.Combine(root, "union-t"), "T", "pass-t");

                var ts = new PairingTicket();
                await using var qs = await LanServer.StartAsync(q, "127.0.0.1", 0, ts);
                await PairAsync(s, qs, ts, 47821);

                var tt = new PairingTicket();
                ps.Ticket = tt;
                await PairAsync(t, ps, tt, 47821);

                await SyncClient.SyncAsync(q, ps.Address);
                Assert(p.Keyset.Find(s.Config.DeviceId) is not null, "P потерял S при равных rev");
                Assert(q.Keyset.Find(t.Config.DeviceId) is not null, "Q потерял T при равных rev");
                Assert(p.Keyset.Rev == q.Keyset.Rev, "keyset rev разошлись");
            });

            await StepAsync("восстановление .prev принимает отозванного подписанта", async () =>
            {
                using var r = VaultSession.Init(Path.Combine(root, "recover-r"), "R", "pass-r");
                using var s = VaultSession.Init(Path.Combine(root, "recover-s"), "S", "pass-s");
                using var t = VaultSession.Init(Path.Combine(root, "recover-t"), "T", "pass-t");

                var tr = new PairingTicket();
                await using var rs = await LanServer.StartAsync(r, "127.0.0.1", 0, tr);
                await PairAsync(s, rs, tr, 47821);

                var ts = new PairingTicket();
                await using var ss = await LanServer.StartAsync(s, "127.0.0.1", 0, ts);
                await PairAsync(t, ss, ts, 47821);

                // R adopts S's newer keyset, which S signed when it added T.
                await SyncClient.SyncAsync(r, ss.Address);

                var keysPath = Path.Combine(r.Home, "keys.bin");
                var storePath = Path.Combine(r.Home, "store.bin");
                var prevKeys = File.ReadAllBytes(keysPath);
                var prevStore = File.ReadAllBytes(storePath);

                Assert(r.RemoveDevice(s.Config.DeviceId), "R не отозвал S");
                // Simulate a crash after the new keyset was written but before the new store was.
                File.WriteAllBytes(storePath, prevStore);
                File.WriteAllBytes(keysPath + ".prev", prevKeys);
                File.WriteAllBytes(storePath + ".prev", prevStore);

                using var recovered = VaultSession.Unlock(r.Home, "pass-r");
                Assert(recovered.Vault.Entries.Count == 0, "хранилище не восстановлено");
            });

            await StepAsync("отзыв не воскресает из устаревшей ветки", async () =>
            {
                using var h = VaultSession.Init(Path.Combine(root, "revoke-h"), "H", "pass-h");
                using var j = VaultSession.Init(Path.Combine(root, "revoke-j"), "J", "pass-j");
                using var k = VaultSession.Init(Path.Combine(root, "revoke-k"), "K", "pass-k");
                using var u = VaultSession.Init(Path.Combine(root, "revoke-u"), "U", "pass-u");
                using var v = VaultSession.Init(Path.Combine(root, "revoke-v"), "V", "pass-v");
                using var w = VaultSession.Init(Path.Combine(root, "revoke-w"), "W", "pass-w");

                var th = new PairingTicket();
                await using var hs = await LanServer.StartAsync(h, "127.0.0.1", 0, th);
                await PairAsync(j, hs, th, 47821);

                th = new PairingTicket();
                hs.Ticket = th;
                await PairAsync(k, hs, th, 47821);

                th = new PairingTicket();
                hs.Ticket = th;
                await PairAsync(u, hs, th, 47821);

                Assert(h.RemoveDevice(k.Config.DeviceId), "H не отозвал K");

                // U missed the revocation and bumps its stale keyset past H by pairing V and W.
                var tu = new PairingTicket();
                await using var us = await LanServer.StartAsync(u, "127.0.0.1", 0, tu);
                await PairAsync(v, us, tu, 47821);

                var tw = new PairingTicket();
                us.Ticket = tw;
                await PairAsync(w, us, tw, 47821);

                Assert(u.Keyset.Rev > h.Keyset.Rev, "ветка U не новее");
                Assert(u.Keyset.Find(k.Config.DeviceId) is not null, "у U уже нет K, ветка не устаревшая");

                await SyncClient.SyncAsync(u, hs.Address);

                Assert(h.Keyset.Find(k.Config.DeviceId) is null, "K воскрес на H");
                Assert(u.Keyset.Find(k.Config.DeviceId) is null, "K воскрес на U");
                Assert(h.Keyset.Find(v.Config.DeviceId) is not null, "V потерялся при слиянии веток");
                Assert(h.Keyset.Find(w.Config.DeviceId) is not null, "W потерялся при слиянии веток");
                Assert(u.Keyset.Find(v.Config.DeviceId) is not null, "U потерял V");
                Assert(u.Keyset.Find(w.Config.DeviceId) is not null, "U потерял W");
                Assert(h.VaultKey.SequenceEqual(u.VaultKey), "H и U остались на разных ключах");
            });

            await StepAsync("отзыв меняет ключ, оставшееся устройство синхронизируется", async () =>
            {
                var oldKey = c.VaultKey.ToArray();
                Assert(a.RemoveDevice(b.Config.DeviceId), "устройство не найдено");
                Assert(a.Keyset.Find(b.Config.DeviceId) is null, "устройство осталось в keys.bin");
                Assert(!a.VaultKey.SequenceEqual(oldKey), "ключ хранилища не сменился");

                await ExpectAsync<HttpRequestException>(() => SyncClient.SyncAsync(b, server.Address));
                await SyncClient.SyncAsync(c, server.Address);

                Assert(c.VaultKey.SequenceEqual(a.VaultKey), "C не получил новый ключ");
                Assert(c.Keyset.Find(b.Config.DeviceId) is null, "C сохранил отозванное устройство");
            });

            await StepAsync("ошибка пира не выдаётся за offline", async () =>
            {
                await ExpectAsync<Exception>(() => SyncClient.SyncAllAsync(b));
            });

            await StepAsync("чужой формат протокола отвергается", async () =>
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var request = new SyncRequest
                {
                    Format = Protocol.Format + 1,
                    DeviceId = c.Config.DeviceId,
                    SentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Body = new byte[] { 1 },
                    Signature = new byte[256],
                };
                var response = await http.PostAsJsonAsync($"http://{server.Address}/sync", request, Json.Options);
                Assert((int)response.StatusCode == 426, $"ожидался 426, получен {(int)response.StatusCode}");
            });

            await StepAsync("рассинхрон часов отличается от битого запроса", async () =>
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var request = new SyncRequest
                {
                    Format = Protocol.Format,
                    DeviceId = c.Config.DeviceId,
                    SentAt = 0,
                    Body = new byte[] { 1 },
                    Signature = new byte[256],
                };
                var response = await http.PostAsJsonAsync($"http://{server.Address}/sync", request, Json.Options);
                Assert((int)response.StatusCode == 400, $"ожидался 400, получен {(int)response.StatusCode}");
                Assert(response.Headers.TryGetValues(Protocol.ErrorHeader, out var values) &&
                       values.Contains(Protocol.ClockSkew), "нет заголовка clock-skew");
            });

            await StepAsync("битый store отвергается по сети", async () =>
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var body = new byte[] { 1, 2, 3 };
                var sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var request = new SyncRequest
                {
                    Format = Protocol.Format,
                    DeviceId = c.Config.DeviceId,
                    SentAt = sentAt,
                    Body = body,
                    Signature = SyncAuth.Sign(c.Key, c.Config.DeviceId, sentAt, body),
                    Keyset = c.Keyset,
                    KeysetSignature = Crypto.Sign(c.Key.Rsa, c.Keyset.BodyBytes()),
                };
                var response = await http.PostAsJsonAsync($"http://{server.Address}/sync", request, Json.Options);
                Assert((int)response.StatusCode == 400,
                    $"ожидался 400, получен {(int)response.StatusCode}");
            });

            await StepAsync("sync без известного устройства отвергается", async () =>
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var request = new SyncRequest
                {
                    Format = Protocol.Format,
                    DeviceId = "unknown",
                    SentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Body = new byte[] { 1, 2, 3 },
                    Signature = new byte[256],
                };
                var response = await http.PostAsJsonAsync($"http://{server.Address}/sync", request, Json.Options);
                Assert((int)response.StatusCode == 403, "ожидался 403");
            });
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine($"FAIL  подготовка: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine(failed == 0 ? "selfcheck: ok" : $"selfcheck: ошибок {failed}");
        return failed == 0 ? 0 : 1;
    }

    private static void Assert(bool condition, string? message = null)
    {
        if (!condition) throw new Exception(message ?? "проверка не прошла");
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new Exception($"ожидалось исключение {typeof(T).Name}");
    }

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }

        throw new Exception($"ожидалось исключение {typeof(T).Name}");
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, byte[]> _values = new();

        public string Name => "memory";
        public bool Secure => true;

        public byte[]? Get(string key) =>
            _values.TryGetValue(key, out var value) ? value.ToArray() : null;

        public void Set(string key, byte[] value) => _values[key] = value.ToArray();

        public void Delete(string key) => _values.Remove(key);
    }

    private sealed class CorruptingSecretStore : ISecretStore
    {
        private readonly Dictionary<string, byte[]> _values = new();

        public string Name => "corrupt";
        public bool Secure => true;

        public byte[]? Get(string key) =>
            _values.TryGetValue(key, out var value) ? value.ToArray() : null;

        public void Set(string key, byte[] value) =>
            _values[key] = key.EndsWith("-pass", StringComparison.Ordinal) && value.Length > 1
                ? value[..(value.Length / 2)].ToArray()
                : value.ToArray();

        public void Delete(string key) => _values.Remove(key);
    }
}