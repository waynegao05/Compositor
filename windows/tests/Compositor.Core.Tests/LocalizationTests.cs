using System.Globalization;
using System.Text;
using System.Text.Json;
using Compositor.Core.IO;
using Compositor.Core.Localization;

namespace Compositor.Core.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData("system", "zh-CN", "zh-Hans")]
    [InlineData("system", "en-US", "en")]
    [InlineData("en", "zh-CN", "en")]
    [InlineData("zh-Hans", "en-US", "zh-Hans")]
    [InlineData("unknown", "zh-CN", "en")]
    public void ExplicitLanguageWinsOverSystemLanguage(string choice, string system, string expected) =>
        Assert.Equal(expected, UiText.Resolve(choice, CultureInfo.GetCultureInfo(system)));

    [Fact]
    public void MissingTranslationFallsBackToItsSource()
    {
        Assert.Equal("A future command", UiText.Translate("A future command", "zh-Hans"));
        Assert.Equal("Cancel", UiText.Translate("Cancel", "en"));
        Assert.Equal("取消", UiText.Translate("Cancel", "zh-Hans"));
    }

    [Fact]
    public void TranslatedShortcutNamesKeepPersistedIdentityAndOverrides()
    {
        var definition = Shortcuts.Definitions.First(d => d.ID == "Menus:Save");
        var rebind = new ShortcutChord("F8");
        var overrides = new Dictionary<string, ShortcutChord> { [definition.ID] = rebind };
        var serialized = JsonSerializer.Serialize(overrides);
        Assert.NotEqual(UiText.Translate(definition.Title, "en"), UiText.Translate(definition.Title, "zh-Hans"));
        Assert.Equal("Menus:Save", definition.ID);
        var restored = JsonSerializer.Deserialize<Dictionary<string, ShortcutChord>>(serialized)!;
        Assert.Equal(rebind, Shortcuts.Effective(restored)[definition.ID]);
        Assert.Equal(serialized, JsonSerializer.Serialize(overrides));
    }

    [Fact]
    public void CatalogsHaveConsistentTranslationsAndFormatArguments()
    {
        var assembly = typeof(UiText).Assembly;
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".zh-Hans.json")))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var json = JsonDocument.Parse(stream);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in json.RootElement.EnumerateObject())
            {
                Assert.True(keys.Add(entry.Name), $"Duplicate key in {name}: {entry.Name}");
                var translated = entry.Value.GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(translated), $"Empty translation: {entry.Name}");
                if (all.TryGetValue(entry.Name, out var existing)) Assert.Equal(existing, translated);
                else all.Add(entry.Name, translated);
                Assert.Equal(CompositeFormat.Parse(entry.Name).MinimumArgumentCount,
                    CompositeFormat.Parse(translated).MinimumArgumentCount);
            }
        }
        Assert.True(all.Count > 100, "The embedded Chinese catalog is missing.");
    }

    [Fact]
    public void LanguagePreferenceSurvivesRestartAndCorruptionFallsBack()
    {
        var directory = Path.Combine(Path.GetTempPath(), "compositor-language-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "ui-language.json");
        try
        {
            Assert.Equal("system", UiText.ReadPreference(path));
            Assert.True(UiText.SavePreference("zh-Hans", path));
            Assert.Equal("zh-Hans", UiText.ReadPreference(path));
            var saved = File.ReadAllText(path);
            Assert.False(UiText.SavePreference("unsupported", path));
            Assert.Equal(saved, File.ReadAllText(path));
            File.WriteAllText(path, "{broken");
            Assert.Equal("system", UiText.ReadPreference(path));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
