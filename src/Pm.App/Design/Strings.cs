using System.Globalization;

namespace Pm.App.Design;

public sealed class Strings
{
    private static Strings? _current;
    public static Strings Current => _current ??= new Strings(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

    public static void Reload() => _current = null;

    private readonly bool _ru;

    public Strings(string language) => _ru = language.StartsWith("ru", StringComparison.OrdinalIgnoreCase);

    private string P(string ru, string en) => _ru ? ru : en;

    public string AppTitle => P("Pm — пароли в локальной сети", "Pm — passwords on your network");
    public string Greeting => P("Привет", "Hello");
    public string GreetingSub => P("Пароли остаются в вашей сети", "Passwords stay on your network");
    public string GreetingHint => P("Ни облака, ни аккаунта", "No cloud, no account");

    public string Search => P("Поиск", "Search");
    public string Add => P("Добавить", "Add");
    public string Sync => P("Синхронизировать", "Sync");
    public string Pair => P("Сопряжение", "Pair");
    public string Tour => P("Обучение", "Tour");
    public string Settings => P("Настройки", "Settings");
    public string Keychain => P("Ключница", "Keychain");
    public string Home => P("Папка", "Home");
    public string Theme => P("Тема", "Theme");
    public string ThemeSystem => P("Системная", "System");
    public string ThemeLight => P("Светлая", "Light");
    public string ThemeDark => P("Тёмная", "Dark");

    public string Language => P("Язык", "Language");
    public string LanguageSystem => P("Системный", "System");
    public string LanguageRussian => P("Русский", "Russian");
    public string LanguageEnglish => P("English", "English");

    public string Lock => P("Заблокировать", "Lock");
    public string Unlock => P("Разблокировать", "Unlock");
    public string LockedTitle => P("Pm заблокирован", "Pm is locked");
    public string LockedBody => P("Введите пароль, чтобы открыть хранилище.", "Enter the passphrase to open the vault.");

    public string PassphraseTitle => P("Защита паролем", "Passphrase");
    public string PassphraseOn => P("Включена", "On");
    public string PassphraseOff => P("Выключена", "Off");
    public string SetPassphrase => P("Установить пароль", "Set passphrase");
    public string ChangePassphrase => P("Сменить", "Change");
    public string RemovePassphrase => P("Убрать", "Remove");
    public string Passphrase => P("Пароль", "Passphrase");
    public string PassphraseConfirm => P("Повторите пароль", "Confirm passphrase");
    public string PassphraseCurrent => P("Текущий пароль", "Current passphrase");
    public string PassphraseWarning => P("Забыли пароль — доступ не восстановить.", "If you forget it, access cannot be recovered.");
    public string PassphraseMismatch => P("Пароли не совпадают", "Passphrases do not match");
    public string PassphraseTooShort => P("От 4 до 30 символов", "4 to 30 characters");
    public string PassphraseWrong => P("Неверный пароль", "Wrong passphrase");
    public string PassphraseVaultError(string message) =>
        P($"Пароль верный, но хранилище не открылось: {message}",
          $"The passphrase is correct, but the vault did not open: {message}");
    public string PassphraseSet => P("Пароль установлен", "Passphrase set");
    public string PassphraseRemoved => P("Пароль убран", "Passphrase removed");
    public string PassphrasePromptTitle => P("Защитить хранилище паролем?", "Protect the vault with a passphrase?");
    public string PassphrasePromptBody => P("Пароль будет запрашиваться при запуске. Восстановления нет.", "Asked at startup. There is no recovery.");
    public string Set => P("Установить", "Set");
    public string Later => P("Позже", "Later");

    public string Name => P("Название", "Name");
    public string Login => P("Логин", "Login");
    public string Password => P("Пароль", "Password");
    public string Address => P("Адрес", "Address");
    public string Notes => P("Заметки", "Notes");
    public string Save => P("Сохранить", "Save");
    public string Edit => P("Редактировать", "Edit");
    public string Editing => P("Редактирование", "Editing");
    public string Delete => P("Удалить", "Delete");
    public string Actions => P("Действия", "Actions");
    public string Reveal => P("Показать", "Show");
    public string Generate => P("Сгенерировать", "Generate");
    public string Copy => P("Копировать", "Copy");

    public string Listening(string address) => P($"приём синхронизации: {address}", $"listening: {address}");
    public string SyncOnOpen => P("синхронизация при открытии приложения", "syncs while the app is open");
    public string NoPeers => P("нет сопряжённых устройств", "no paired devices");
    public string Syncing => P("синхронизация...", "syncing...");
    public string Synced(int peers, int conflicts) =>
        P($"синхронизировано: {peers}, конфликтов: {conflicts}", $"synced: {peers}, conflicts: {conflicts}");
    public string PeersOffline => P("сопряжённые устройства не отвечают", "paired devices are not reachable");
    public string Saved => P("сохранено", "saved");
    public string Deleted => P("удалено", "deleted");
    public string Copied => P("пароль скопирован, буфер очистится через 30 секунд", "password copied, clipboard clears in 30 seconds");
    public string Zoom(int percent) => P($"Масштаб: {percent}%", $"Zoom: {percent}%");
    public string NewEntry => P("Новая запись", "New entry");
    public string EmptyVault => P("Пока нет записей. Нажмите «Добавить».", "No entries yet. Press Add.");
    public string NoMatches => P("Ничего не найдено.", "Nothing found.");
    public string DeleteConfirm => P("Удалить запись?", "Delete entry?");
    public string Cancel => P("Отмена", "Cancel");
    public string HidePassword => P("Скрыть", "Hide");
    public string NeedTitle => P("Нужно название", "A name is required");
    public string SyncError(string message) => P($"ошибка синхронизации: {message}", $"sync error: {message}");
    public string Meta(string label, int entries, int devices, string address) =>
        P($"{label} · {address} · записей {entries} · устройств {devices}",
          $"{label} · {address} · entries {entries} · devices {devices}");

    public string PairTitle => P("Сопряжение устройств", "Pair devices");
    public string PairShow => P("Показать адрес и код", "Show address and code");
    public string PairWaiting => P("Введите этот код на втором устройстве", "Enter this code on the other device");
    public string PairFind => P("Найти устройства", "Find devices");
    public string PairNone => P("Никого не нашли. Убедитесь, что оба устройства в одной сети.", "Nothing found. Make sure both devices share the network.");
    public string PairAddress => P("адрес второго устройства", "the other device address");
    public string PairCode => P("6 цифр", "6 digits");
    public string PairConnect => P("Подключиться", "Connect");
    public string PairDone => P("Сопряжено", "Paired");
    public string PairFingerprint(string own, string peer) =>
        P($"Ваш отпечаток: {own}\nОтпечаток собеседника: {peer}\nСверьте отпечаток собеседника с отпечатком на втором устройстве.",
          $"Your fingerprint: {own}\nPeer fingerprint: {peer}\nCompare the peer fingerprint with the one shown on the other device.");
    public string PairCheckTitle => P("Сверьте отпечатки", "Compare fingerprints");
    public string PairCheckWarning =>
        P("Нажмите «Совпадают», только если отпечатки на обоих экранах одинаковые. Иначе отмените сопряжение.",
          "Press Match only if the fingerprints on both screens are the same. Otherwise cancel the pairing.");
    public string PairMatch => P("Совпадают", "Fingerprints match");
    public string PairWaitingConfirm => P("Собеседник ввёл код. Сверьте отпечатки.", "The other device entered the code. Compare fingerprints.");
    public string PairWaitingOther => P("Ожидание подтверждения на втором устройстве...", "Waiting for the other device to confirm...");
    public string PairCancelled => P("Сопряжение отменено. Если отпечатки не совпали, отзовите это устройство в настройках.",
        "Pairing cancelled. If the fingerprints did not match, revoke this device in settings.");
    public string PairExpired => P("Код истёк. Покажите новый.", "The code expired. Show a new one.");
    public string PairIosHint => P("Откройте «Сопряжение» на компьютере и введите показанный код.", "Open Pairing on the computer and enter the code shown there.");
    public string Close => P("Закрыть", "Close");

    public string Devices => P("Устройства", "Devices");
    public string ThisDevice => P("это устройство", "this device");
    public string Refresh => P("Обновить", "Refresh");
    public string Revoke => P("Отозвать", "Revoke");
    public string ConfirmRevoke => P("Точно?", "Confirm?");
    public string Revoked => P("Устройство отозвано; остальные получат новый ключ при синхронизации.", "Device revoked; the others will get the new key on their next sync.");
    public string NoOtherDevices => P("Пока нет других устройств.", "No other devices yet.");
    public string RevokeWarning => P("Отзыв меняет ключ хранилища. Всё, что устройство уже скачало, останется у него.", "Revoke rotates the vault key. Anything the device already downloaded stays on it.");
    public string DeviceNotFound => P("Устройство не найдено.", "Device not found.");

    public string Tour1Title => P("Первая запись", "Your first entry");
    public string Tour1Body => P("Нажмите «Добавить» и сохраните первый пароль.", "Press Add and save your first password.");
    public string Tour2Title => P("Второе устройство", "A second device");
    public string Tour2Body => P("Нажмите «Сопряжение», покажите код на одном устройстве и введите его на другом.", "Press Pair, show the code on one device and enter it on the other.");
    public string Tour3Title => P("Готово", "Done");
    public string Tour3Body => P("Здесь видно, когда прошла синхронизация. Пароли не покидают вашу сеть.", "Sync status lives here. Passwords never leave your network.");
    public string Next => P("Далее", "Next");
    public string Done => P("Готово", "Done");
    public string Skip => P("Пропустить", "Skip");

    public string MigrateTitle => P("Обновление хранилища", "Vault update");
    public string MigrateBody => P("Введите старый пароль устройства один раз. Дальше приложение будет открываться само.",
        "Enter the old device password once. After that the app opens by itself.");
    public string Continue => P("Продолжить", "Continue");

    public string Fatal => P("Не удалось открыть хранилище", "Could not open the vault");
    public string Retry => P("Повторить", "Retry");
}
