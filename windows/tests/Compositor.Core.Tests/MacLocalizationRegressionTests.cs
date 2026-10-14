using System.Text.Json;

namespace Compositor.Core.Tests;

/// <summary>Source/catalog consistency only; these checks do not execute the native Mac application.</summary>
public sealed class MacLocalizationRegressionTests
{
    [Fact]
    public void DynamicErrorsAndTheCurlyApostropheSaveButtonHaveCatalogEntries()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Compositor", "Localizable.xcstrings")))
            root = root.Parent;
        Assert.NotNull(root);
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "Compositor", "Localizable.xcstrings")));
        var strings = catalog.RootElement.GetProperty("strings");
        var keys = new[]
        {
            "Don’t Save",
            "This project uses format version %d. This app supports versions %d–%d.",
            "This project exceeds the supported canvas, layer, file-size, or %d-megapixel document limit.",
            "This import exceeds the current %d-megapixel document budget or %@-pixel side limit.",
            "Image export supports canvases up to %d megapixels and %@ pixels per side.",
        };
        foreach (var key in keys)
        {
            var text = strings.GetProperty(key).GetProperty("localizations").GetProperty("zh-Hans")
                .GetProperty("stringUnit").GetProperty("value").GetString();
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.NotEqual(key, text);
        }
        foreach (var file in new[] { "ProjectStore.swift", "ImageImporter.swift", "ImageExporter.swift" })
        {
            var source = File.ReadAllText(Path.Combine(root.FullName, "Compositor", "IO", file));
            Assert.Contains("case .tooLarge: L10n.format(", source, StringComparison.Ordinal);
        }
    }
}
