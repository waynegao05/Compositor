using System.Text;
using System.Text.Json;
using Compositor.Core.Format;
using Compositor.Core.IO;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>The manifest as the format spec describes it: names, spellings, order and stability.</summary>
public class ManifestFormatTests : ProjectTestBase
{
    /// <summary>The minimal manifest from docs/writing-comp-files.md, verbatim.</summary>
    private const string DocumentedManifest = """
        {
          "format": "com.compositor.project",
          "version": 11,
          "colorSpace": "sRGB",
          "documentID": "0C5E7A91-3B2D-4F6A-8E1C-9D0B7A6F5E4D",
          "width": 1920,
          "height": 1080,
          "resolution": 72,
          "activeLayerID": "6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F",
          "layers": [
            {
              "id": "6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F",
              "name": "Background",
              "imageFile": "6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F.png",
              "isVisible": true,
              "isGroup": false,
              "opacity": 1,
              "blendMode": "Normal",
              "transform": {
                "origin": [0, 0],
                "size": [1920, 1080],
                "rotation": 0,
                "flipX": false,
                "flipY": false,
                "sampling": "High quality"
              }
            }
          ]
        }
        """;

    private const string DocumentedLayerID = "6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F";
    private const string DocumentedDocumentID = "0C5E7A91-3B2D-4F6A-8E1C-9D0B7A6F5E4D";
    private const string DocumentedLayerFile = DocumentedLayerID + ".png";

    private const string ExpectedMinimalManifest = """
        {
          "activeLayerID": "6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F",
          "colorSpace": "sRGB",
          "documentID": "0C5E7A91-3B2D-4F6A-8E1C-9D0B7A6F5E4D",
          "format": "com.compositor.project",
          "height": 1080,
          "layers": [
            {
              "blendMode": "Normal",
              "id": "6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F",
              "imageFile": "6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F.png",
              "isGroup": false,
              "isVisible": true,
              "name": "Background",
              "opacity": 1,
              "transform": {
                "flipX": false,
                "flipY": false,
                "origin": [
                  0,
                  0
                ],
                "rotation": 0,
                "sampling": "High quality",
                "size": [
                  1920,
                  1080
                ]
              }
            }
          ],
          "resolution": 72,
          "version": 11,
          "width": 1920
        }
        """;

    [Fact]
    public void TheMinimalManifestFromTheGuideLoads()
    {
        WriteProject("Guide.comp", DocumentedManifest, (DocumentedLayerFile, Picture(8, 4, new SKColor(255, 0, 0))));

        using var snapshot = ProjectStore.Load(PathIn("Guide.comp"));
        var manifest = snapshot.Manifest;

        Assert.Equal("com.compositor.project", manifest.Format);
        Assert.Equal(11, manifest.Version);
        Assert.Equal("sRGB", manifest.ColorSpace);
        Assert.Equal(Guid.Parse(DocumentedDocumentID), manifest.DocumentID);
        Assert.Equal(1920, manifest.Width);
        Assert.Equal(1080, manifest.Height);
        Assert.Equal(72, manifest.Resolution);
        Assert.Equal(Guid.Parse(DocumentedLayerID), manifest.ActiveLayerID);

        var layer = Assert.Single(manifest.Layers);
        Assert.Equal(Guid.Parse(DocumentedLayerID), layer.ID);
        Assert.Equal("Background", layer.Name);
        Assert.Equal(DocumentedLayerFile, layer.ImageFile);
        Assert.True(layer.IsVisible);
        Assert.False(layer.IsGroupValue);
        Assert.Equal(1, layer.OpacityValue);
        Assert.Equal(LayerBlendMode.Normal, layer.BlendModeValue);
        Assert.Null(layer.ParentID);
        Assert.Null(layer.MaskFile);
        Assert.Null(manifest.Guides);

        var transform = layer.Transform;
        Assert.Equal(0, transform.Origin.X);
        Assert.Equal(0, transform.Origin.Y);
        Assert.Equal(1920, transform.Size.Width);
        Assert.Equal(1080, transform.Size.Height);
        Assert.Equal(0, transform.Rotation);
        Assert.False(transform.FlipX);
        Assert.False(transform.FlipY);
        Assert.Equal(LayerSampling.HighQuality, transform.Sampling);

        var image = Assert.Single(snapshot.Images).Value;
        Assert.Equal(8, image.Width);
        Assert.Equal(4, image.Height);
        Assert.Equal(255, image.Image.GetPixel(0, 0).Red);
        Assert.Equal(0, image.Image.GetPixel(0, 0).Green);
    }

    /// <summary>
    /// A save writes sorted, pretty-printed keys, so a re-save of what was just read is a stable diff.
    /// The document's own file keeps the layout a person wrote; only what this writer produces is fixed.
    /// </summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void SavingWritesSortedKeysAndReSavingIsStable(string inputNewLine)
    {
        WriteProject("Guide.comp", DocumentedManifest.ReplaceLineEndings(inputNewLine),
            (DocumentedLayerFile, Picture(4, 4, SKColors.Blue)));

        using var snapshot = ProjectStore.Load(PathIn("Guide.comp"));
        var writtenBytes = ManifestJson.Serialize(snapshot.Manifest);
        // Raw string literals inherit the source checkout's line endings. Normalize only the fixture:
        // the writer must still produce exactly these UTF-8 bytes, with LF on every platform.
        Assert.Equal(Encoding.UTF8.GetBytes(ExpectedMinimalManifest.ReplaceLineEndings("\n")), writtenBytes);
        var written = Encoding.UTF8.GetString(writtenBytes);

        var rewrittenBytes = ManifestJson.Serialize(ManifestJson.Deserialize(writtenBytes));
        Assert.Equal(writtenBytes, rewrittenBytes);

        foreach (var names in ObjectKeySets(written))
        {
            Assert.Equal(names.OrderBy(name => name, StringComparer.Ordinal), names);
        }
        // Ordinal: a culture-aware search treats U+FEFF as a zero-weight character and matches anywhere.
        Assert.DoesNotContain("\uFEFF", written, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", written, StringComparison.Ordinal);
        Assert.False(written.EndsWith("\n", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownKeysAreIgnored()
    {
        var withExtras = DocumentedManifest.Replace("\"version\": 11,",
            "\"version\": 11, \"somethingNew\": {\"nested\": [1, 2]}, \"future\": true,");
        WriteProject("Extras.comp", withExtras, (DocumentedLayerFile, Picture(4, 4, SKColors.Blue)));

        using var snapshot = ProjectStore.Load(PathIn("Extras.comp"));
        Assert.Single(snapshot.Manifest.Layers);
    }

    private static List<List<string>> ObjectKeySets(string json)
    {
        var sets = new List<List<string>>();
        using var document = JsonDocument.Parse(json);
        Walk(document.RootElement);
        return sets;

        void Walk(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    sets.Add(element.EnumerateObject().Select(property => property.Name).ToList());
                    foreach (var property in element.EnumerateObject()) Walk(property.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray()) Walk(item);
                    break;
            }
        }
    }

    [Fact]
    public void PropertyNamesAndUuidCaseMatchTheFormatExactly()
    {
        using var snapshot = KitchenSink();
        var json = Serialized(snapshot.Manifest);
        var keys = KeyNames(json).ToHashSet();

        foreach (var expected in new[]
        {
            "documentID", "activeLayerID", "colorSpace", "format", "version", "width", "height", "resolution", "layers", "guides",
            "parentID", "maskSourceID", "imageFile", "maskFile", "maskEnabled", "maskPlacement", "maskLinked",
            "isGroup", "isVisible", "blendMode", "opacity", "name", "id", "transform",
            "adjustment", "effects", "text", "shape",
            "kind", "hue", "saturation", "lightness", "colorize", "hsvSettings", "levels", "curves",
            "channel", "ranges", "black", "gamma", "white", "outputBlack", "outputWhite", "channels", "x", "y",
            "falloffStart", "rangeStart", "rangeEnd", "falloffEnd", "adjustments", "bands", "invertRange", "range",
            "stroke", "shadow", "colorOverlay", "innerShadow", "outerGlow", "innerGlow",
            "enabled", "size", "red", "green", "blue", "inside", "angle", "distance", "blur",
            "exposureSettings", "gradientMapSettings", "grainSettings", "blackWhiteSettings", "colorBalanceSettings",
            "exposure", "offset", "shadows", "highlights", "reversed", "amount", "roughness", "seed",
            "reds", "yellows", "greens", "cyans", "blues", "magentas", "tint", "tintHue", "tintSaturation",
            "shadowCyanRed", "midMagentaGreen", "highlightYellowBlue", "preserveLuminosity",
            "blurRadius", "motionAngle", "motionDistance", "noiseAmount", "noiseGaussian", "noiseMonochromatic", "noiseSeed",
            "content", "fontName", "fontSize", "alignment", "tracking", "leading", "boxSize", "colorRuns", "fontRuns",
            "location", "length", "lineWidth", "start", "end", "cornerRadius",
            "axis", "position", "sampling", "flipX", "flipY", "origin", "rotation",
        })
        {
            Assert.Contains(expected, keys);
        }

        Assert.DoesNotContain("ID", keys);
        Assert.DoesNotContain("images", keys);
        var background = BackgroundID.ToString("D").ToUpperInvariant();
        Assert.Contains($"\"id\": \"{background}\"", json);
        Assert.Contains($"\"imageFile\": \"{background}.png\"", json);
        Assert.Contains($"\"maskSourceID\": \"{background}\"", json);
        Assert.DoesNotContain(BackgroundID.ToString("D"), json);
        Assert.Contains("\"axis\": \"horizontal\"", json);
        Assert.Contains("\"axis\": \"vertical\"", json);
        Assert.Contains("\"sampling\": \"High quality\"", json);
        Assert.Contains("\"enabled\": false", json);
        Assert.Contains("\"noiseSeed\": 4000000000", json);
    }

    [Fact]
    public void EveryBlendModeAndAdjustmentKindKeepsItsSpelling()
    {
        string[] blends =
        [
            "Normal", "Darken", "Multiply", "Color Burn", "Linear Burn", "Lighten", "Screen", "Color Dodge",
            "Linear Dodge (Add)", "Overlay", "Soft Light", "Hard Light", "Vivid Light", "Linear Light", "Pin Light",
            "Hard Mix", "Difference", "Exclusion", "Subtract", "Divide", "Hue", "Saturation", "Color", "Luminosity",
        ];
        var values = Enum.GetValues<LayerBlendMode>();
        Assert.Equal(24, values.Length);
        Assert.Equal(blends, values.Select(RawEnum));

        string[] kinds =
        [
            "Hue/Saturation", "Levels", "Curves", "Exposure", "Gradient Map", "Grain", "Add Noise", "Gaussian Blur",
            "Motion Blur", "Invert", "Black & White", "Color Balance",
        ];
        var adjustmentKinds = Enum.GetValues<AdjustmentKind>();
        Assert.Equal(12, adjustmentKinds.Length);
        Assert.Equal(kinds, adjustmentKinds.Select(RawEnum));

        Assert.Equal(["Nearest", "Smooth", "High quality"], Enum.GetValues<LayerSampling>().Select(RawEnum));
        Assert.Equal(["RGB", "Red", "Green", "Blue"], Enum.GetValues<LevelsChannel>().Select(RawEnum));
        Assert.Equal(["Master", "Reds", "Yellows", "Greens", "Cyans", "Blues", "Magentas"], Enum.GetValues<ColorRange>().Select(RawEnum));
        Assert.Equal(["Left", "Center", "Right"], Enum.GetValues<TextAlignment>().Select(RawEnum));
        Assert.Equal(["Rectangle", "Ellipse", "Line"], Enum.GetValues<ShapeKind>().Select(RawEnum));
        Assert.Equal(["horizontal", "vertical"], Enum.GetValues<GuideAxis>().Select(RawEnum));
    }

    private static string RawEnum<T>(T value) where T : struct, Enum =>
        JsonSerializer.Serialize(value, ManifestJson.Options).Trim('"');

    /// <summary>
    /// Hue/Saturation carries two `[ColorRange: …]` dictionaries. Swift's ColorRange does not conform to
    /// CodingKeyRepresentable, so its encoder writes them as alternating keys and values in an array.
    /// </summary>
    [Fact]
    public void HueSaturationSettingsWriteEnumDictionariesAsAlternatingArrays()
    {
        var settings = new HueSaturationSettings();
        settings.Set(12, -30, 4);
        settings.Bands.Set(ColorRange.Reds, new HueBand { FalloffStart = 300, RangeStart = 340, RangeEnd = 20, FalloffEnd = 60 });

        var adjustment = new LayerAdjustment { Kind = AdjustmentKind.HueSaturation, HsvSettings = settings };
        var manifest = Manifest(11, new ProjectLayerRecord
        {
            ID = Guid.NewGuid(),
            Name = "Hue",
            IsVisible = true,
            Transform = Concrete(),
            Adjustment = adjustment,
        });

        var json = Serialized(manifest);
        Assert.Contains("\"hsvSettings\": {", json);

        using var document = JsonDocument.Parse(json);
        var hsv = document.RootElement.GetProperty("layers")[0].GetProperty("adjustment").GetProperty("hsvSettings");
        var adjustments = hsv.GetProperty("adjustments");
        Assert.Equal(JsonValueKind.Array, adjustments.ValueKind);
        Assert.Equal(2, adjustments.GetArrayLength());
        Assert.Equal("Master", adjustments[0].GetString());
        Assert.Equal(12, adjustments[1].GetProperty("hue").GetDouble());
        Assert.Equal(-30, adjustments[1].GetProperty("saturation").GetDouble());
        Assert.Equal(4, adjustments[1].GetProperty("lightness").GetDouble());
        Assert.Equal(14, hsv.GetProperty("bands").GetArrayLength());

        var decoded = ManifestJson.Deserialize(Encoding.UTF8.GetBytes(json));
        var restored = decoded.Layers[0].Adjustment!.HsvSettings!;
        Assert.Equal(12, restored.Current.Hue);
        Assert.Equal(-30, restored.Current.Saturation);
        Assert.Equal(4, restored.Current.Lightness);
        Assert.Equal(20, restored.Bands.Find(ColorRange.Reds)!.RangeEnd);
        Assert.Equal(360, restored.Bands.Find(ColorRange.Master)!.FalloffEnd);
        Assert.True(decoded.Layers[0].Adjustment!.IsValid);
    }

    /// <summary>A value of the wrong type, a missing required field, or an unknown spelling is a rejection.</summary>
    [Theory]
    [InlineData("{\"format\":\"com.compositor.project\",\"version\":\"11\",\"colorSpace\":\"sRGB\",\"documentID\":\"0C5E7A91-3B2D-4F6A-8E1C-9D0B7A6F5E4D\",\"width\":1,\"height\":1,\"layers\":[]}")]
    [InlineData("{\"format\":\"com.compositor.project\",\"version\":11,\"colorSpace\":\"sRGB\",\"documentID\":\"0C5E7A91-3B2D-4F6A-8E1C-9D0B7A6F5E4D\",\"width\":1,\"height\":1}")]
    [InlineData("{\"format\":\"com.compositor.project\",\"version\":11,\"colorSpace\":\"sRGB\",\"documentID\":\"0C5E7A91-3B2D-4F6A-8E1C-9D0B7A6F5E4D\",\"width\":1.5,\"height\":1,\"layers\":[]}")]
    [InlineData("{\"format\":\"com.compositor.project\",\"version\":11,\"colorSpace\":\"sRGB\",\"documentID\":\"0C5E7A91-3B2D-4F6A-8E1C-9D0B7A6F5E4D\",\"width\":1,\"height\":1,\"layers\":[{\"id\":\"6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F\",\"name\":null,\"isVisible\":true,\"transform\":{\"origin\":[0,0],\"size\":[1,1],\"rotation\":0,\"flipX\":false,\"flipY\":false,\"sampling\":\"High quality\"}}]}")]
    [InlineData("{\"format\":\"com.compositor.project\",\"version\":11,\"colorSpace\":\"sRGB\",\"documentID\":\"0C5E7A91-3B2D-4F6A-8E1C-9D0B7A6F5E4D\",\"width\":1,\"height\":1,\"layers\":[{\"id\":\"6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F\",\"name\":\"a\",\"isVisible\":true,\"transform\":{\"origin\":[0,0],\"size\":[1,1],\"flipX\":false,\"flipY\":false,\"sampling\":\"High quality\"}}]}")]
    [InlineData("{\"format\":\"com.compositor.project\",\"version\":11,\"colorSpace\":\"sRGB\",\"documentID\":\"0C5E7A91-3B2D-4F6A-8E1C-9D0B7A6F5E4D\",\"width\":1,\"height\":1,\"layers\":[{\"id\":\"6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F\",\"name\":\"a\",\"isVisible\":true,\"transform\":{\"origin\":[0,0],\"size\":[1,1],\"rotation\":0,\"flipX\":false,\"flipY\":false,\"sampling\":\"Bilinear\"}}]}")]
    [InlineData("{\"format\":\"com.compositor.project\",\"version\":11,\"colorSpace\":\"sRGB\",\"documentID\":\"0C5E7A91-3B2D-4F6A-8E1C-9D0B7A6F5E4D\",\"width\":1,\"height\":1,\"layers\":[{\"id\":\"6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F\",\"name\":\"a\",\"isVisible\":true,\"transform\":{\"origin\":[0,0],\"size\":[1,1],\"rotation\":0,\"flipX\":false,\"flipY\":false,\"sampling\":\"High quality\"},\"blendMode\":\"Multiply Blend\"}]}")]
    [InlineData("null")]
    public void AManifestThatDoesNotDecodeIsRejected(string manifest)
    {
        WriteProject("Broken.comp", manifest);
        var error = Assert.Throws<ProjectException>(() => ProjectStore.Load(PathIn("Broken.comp")));
        Assert.Equal(ProjectError.Invalid, error.Error);
    }

    [Fact]
    public void AnOlderVersionStillLoadsAndKeepsItsDefaults()
    {
        const string manifest = """
            {
              "format": "com.compositor.project",
              "version": 1,
              "colorSpace": "sRGB",
              "documentID": "0C5E7A91-3B2D-4F6A-8E1C-9D0B7A6F5E4D",
              "width": 4,
              "height": 4,
              "layers": [
                {
                  "id": "6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F",
                  "name": "Background",
                  "imageFile": "6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F.png",
                  "isVisible": true,
                  "transform": { "origin": [0, 0], "size": [4, 4], "rotation": 0, "flipX": false, "flipY": false, "sampling": "High quality" }
                }
              ]
            }
            """;
        WriteProject("Old.comp", manifest, (DocumentedLayerFile, Picture(4, 4, SKColors.Green)));

        using var snapshot = ProjectStore.Load(PathIn("Old.comp"));
        Assert.Null(snapshot.Manifest.Resolution);
        Assert.Null(snapshot.Manifest.ActiveLayerID);
        Assert.Null(snapshot.Manifest.Guides);
        var layer = Assert.Single(snapshot.Manifest.Layers);
        Assert.Null(layer.Opacity);
        Assert.Null(layer.BlendMode);
        Assert.Null(layer.IsGroup);

        var document = snapshot.ToDocument();
        Assert.Equal(72, document.Resolution);
        Assert.Equal(1, document.Layers[0].Opacity);
        Assert.Equal(LayerBlendMode.Normal, document.Layers[0].BlendMode);
        Assert.False(document.Layers[0].IsGroup);
        Assert.Null(document.Layers[0].Mask);
    }
}
