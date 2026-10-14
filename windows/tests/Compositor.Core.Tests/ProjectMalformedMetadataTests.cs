using System.Text.Json.Nodes;
using Compositor.Core.Format;
using Compositor.Core.IO;

namespace Compositor.Core.Tests;

public sealed class ProjectMalformedMetadataTests : ProjectTestBase
{
    [Theory]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("1e999")]
    public void ANonIntegerOrOutOfRangeVersionIsAnInvalidProject(string version)
    {
        WriteProject("BadHeader.comp", $$"""{"format":"com.compositor.project","version":{{version}}}""");
        Assert.Equal(ProjectError.Invalid,
            Assert.Throws<ProjectException>(() => ProjectStore.Load(PathIn("BadHeader.comp"))).Error);
    }

    [Theory]
    [InlineData("layer")]
    [InlineData("guide")]
    [InlineData("color run")]
    [InlineData("font run")]
    [InlineData("curve channel")]
    [InlineData("curve point")]
    [InlineData("levels range")]
    [InlineData("transform")]
    public void NullMetadataEntriesProduceAProjectError(string field)
    {
        using var fixture = KitchenSink();
        var json = JsonNode.Parse(ManifestJson.Serialize(fixture.Manifest))!.AsObject();
        var layers = json["layers"]!.AsArray();
        switch (field)
        {
            case "layer": layers[0] = null; break;
            case "guide": json["guides"] = new JsonArray((JsonNode?)null); break;
            case "color run": layers[1]!["text"]!["colorRuns"]!.AsArray()[0] = null; break;
            case "font run": layers[1]!["text"]!["fontRuns"]!.AsArray()[0] = null; break;
            case "curve channel": layers[4]!["adjustment"]!["curves"]!["channels"]!.AsArray()[0] = null; break;
            case "curve point": layers[4]!["adjustment"]!["curves"]!["channels"]![0]!.AsArray()[0] = null; break;
            case "levels range": layers[4]!["adjustment"]!["levels"]!["ranges"]!.AsArray()[0] = null; break;
            case "transform": layers[0]!["transform"] = null; break;
        }
        WriteProject("NullEntry.comp", json.ToJsonString());
        Assert.Equal(ProjectError.Invalid,
            Assert.Throws<ProjectException>(() => ProjectStore.Load(PathIn("NullEntry.comp"))).Error);
    }

    [Fact]
    public void DirectValidationRejectsANullRequiredTransform()
    {
        using var fixture = KitchenSink();
        fixture.Manifest.Layers[0].Transform = null!;
        Assert.Equal(ProjectError.Invalid,
            Assert.Throws<ProjectException>(() => ProjectStore.Validate(fixture.Manifest)).Error);
    }
}
