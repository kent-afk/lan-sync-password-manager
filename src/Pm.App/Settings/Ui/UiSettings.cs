using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pm.App.Settings.Ui;

public sealed class UiSettings
{
    private static readonly string SystemLanguage = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    public string Theme { get; set; } = "system";
    public string Language { get; set; } = "system";
    public bool PassphraseAsked { get; set; }

    public static UiSettings Current { get; private set; } = new();

    public static UiSettings Load(string home)
    {
        try
        {
            var path = Path.Combine(home, "ui.json");
            if (File.Exists(path))
                Current = JsonSerializer.Deserialize(File.ReadAllText(path), UiJsonContext.Default.UiSettings) ?? new UiSettings();
        }
        catch
        {
            Current = new UiSettings();
        }

        if (Current.Theme is not ("light" or "dark" or "system"))
            Current.Theme = "system";

        if (Current.Language is not ("ru" or "en" or "system"))
            Current.Language = "system";

        return Current;
    }

    public static void Save(string home, string theme)
    {
        Current.Theme = theme;
        try
        {
            Files.WriteAtomic(Path.Combine(home, "ui.json"), JsonSerializer.SerializeToUtf8Bytes(Current, UiJsonContext.Default.UiSettings));
        }
        catch
        {
            // best effort: the selected theme still applies for this session
        }
    }

    public static ThemeVariant Variant(string theme) => theme switch
    {
        "light" => ThemeVariant.Light,
        "dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    public static void ApplyLanguage()
    {
        var code = Current.Language switch
        {
            "ru" => "ru",
            "en" => "en",
            _ => SystemLanguage,
        };

        var culture = CultureInfo.GetCultureInfo(code);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    public static void SaveLanguage(string home, string language)
    {
        Current.Language = language;
        Save(home, Current.Theme);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UiSettings))]
internal partial class UiJsonContext : JsonSerializerContext
{
}
