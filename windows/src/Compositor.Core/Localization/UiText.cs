using System.Globalization;
using System.Text.Json;
using Compositor.Core.IO;

namespace Compositor.Core.Localization;

/// <summary>Display text only. Document values, command IDs and user text never pass through this catalog.</summary>
public static class UiText
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Chinese = new(ReadCatalog);
    public const string SystemLanguage = "system";
    public const string English = "en";
    public const string SimplifiedChinese = "zh-Hans";

    public static string CurrentLanguage { get; private set; } = Resolve(SystemLanguage, CultureInfo.CurrentUICulture);
    public static string SelectedLanguage { get; private set; } = SystemLanguage;
    public static string PreferencePath => Path.Combine(AppDataPaths.Root, "ui-language.json");

    public static void Initialize(string? language = null)
    {
        SelectedLanguage = ReadPreference(PreferencePath);
        var requested = language ?? Environment.GetEnvironmentVariable("COMPOSITOR_LANGUAGE") ?? SelectedLanguage;
        CurrentLanguage = Resolve(requested, CultureInfo.CurrentUICulture);
    }

    public static string T(string source) => Translate(source, CurrentLanguage);

    public static string F(string source, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, T(source), arguments);

    public static string Translate(string source, string language)
    {
        if (Resolve(language, CultureInfo.CurrentUICulture) != SimplifiedChinese) return source;
        return Chinese.Value.TryGetValue(source, out var translated) ? translated : source;
    }

    public static string Resolve(string? language, CultureInfo systemCulture)
    {
        var requested = string.IsNullOrWhiteSpace(language) || language == SystemLanguage ? systemCulture.Name : language;
        return requested.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? SimplifiedChinese : English;
    }

    public static string ReadPreference(string path)
    {
        try
        {
            var preference = JsonSerializer.Deserialize<LanguagePreference>(File.ReadAllText(path));
            return ValidPreference(preference?.Language) ? preference!.Language : SystemLanguage;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return SystemLanguage;
        }
    }

    /// <summary>The next launch takes this choice. Open dialogs keep one consistent language meanwhile.</summary>
    public static bool SavePreference(string language, string? path = null)
    {
        if (!ValidPreference(language)) return false;
        path ??= PreferencePath;
        string? temporary = null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new LanguagePreference(language)));
            File.Move(temporary, fullPath, overwrite: true);
            SelectedLanguage = language;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public static IReadOnlyDictionary<string, string> ChineseCatalog => Chinese.Value;

    private static bool ValidPreference(string? language) => language is SystemLanguage or English or SimplifiedChinese;
    private sealed record LanguagePreference(string Language);

    private static IReadOnlyDictionary<string, string> ReadCatalog()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var assembly = typeof(UiText).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".zh-Hans.json", StringComparison.Ordinal)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
            foreach (var (key, value) in entries) result.TryAdd(key, value);
        }
        return result;
    }
}
