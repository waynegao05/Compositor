using Compositor.Core.Document;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// Guided Upright: lines drawn on the picture, read into a turn and a keystone. What is checked here is what
/// the lines ask for — the first levels itself, a steep second one keystones — that it is added to whatever
/// the sliders say rather than replacing it, and that a Guided choice with no usable line warps nothing.
/// </summary>
public class GuidedUprightTests
{
    /// <summary>A line of the given length leaving a point at the given angle below the horizontal.</summary>
    private static CameraRawGeometryGuide At(double startX, double startY, double degrees, double length = 0.8) =>
        new(startX, startY,
            startX + length * Math.Cos(degrees * Math.PI / 180),
            startY + length * Math.Sin(degrees * Math.PI / 180));

    [Fact]
    public void NoLineAsksForNothing()
    {
        Assert.Equal((0, 0, 0), GuidedUpright.Corrections([]));
        // A Guided choice with nothing drawn must not warp the picture: there is nothing to read.
        var settings = new CameraRawGeometrySettings { Upright = CameraRawUprightMode.Guided };
        Assert.False(settings.Adjusts);
        // Nor is a stub a line.
        settings.Guides.Add(new CameraRawGeometryGuide(0.5, 0.5, 0.505, 0.5));
        Assert.False(settings.Adjusts);
        Assert.Empty(settings.Normalized().Guides);
    }

    [Fact]
    public void OneLineLevelsItselfByItsOwnAngle()
    {
        var (_, _, down) = GuidedUpright.Corrections([At(0.1, 0.2, 10)]);
        Assert.Equal(-10, down, 3);
        var (_, _, up) = GuidedUpright.Corrections([At(0.1, 0.8, -10)]);
        Assert.Equal(10, up, 3);
    }

    [Fact]
    public void ALinePastAQuarterTurnIsReadTheOtherWayRound()
    {
        // Fifty degrees below the horizontal is the same line as forty above it read backwards, so the turn is
        // brought back inside the forty-five a turn is allowed.
        var (_, _, rotate) = GuidedUpright.Corrections([At(0.1, 0.1, 50)]);
        Assert.Equal(40, rotate, 3);
        Assert.True(Math.Abs(rotate) <= 45);
    }

    [Fact]
    public void ASteepSecondLineAsksForTheKeystoneAndAShallowOneForTheSides()
    {
        var level = At(0.1, 0.5, 0);
        var steep = GuidedUpright.Corrections([level, At(0.5, 0.1, 70)]);
        Assert.Equal(25, steep.Vertical);
        Assert.Equal(0, steep.Horizontal);
        var shallow = GuidedUpright.Corrections([level, At(0.1, 0.2, 20)]);
        Assert.Equal(0, shallow.Vertical);
        Assert.Equal(25, shallow.Horizontal);
        // The lean the other way asks for the keystone the other way.
        var leaning = GuidedUpright.Corrections([level, At(0.5, 0.9, -70)]);
        Assert.Equal(-25, leaning.Vertical);
        // Only the first two lines are read, however many are drawn.
        Assert.Equal(steep, GuidedUpright.Corrections([level, At(0.5, 0.1, 70), At(0.1, 0.9, -70)]));
    }

    [Fact]
    public void WhatTheLinesAskForIsAddedToTheSliders()
    {
        // The guide's pixel-space angle is added to the slider. Its normalized ten-degree angle is
        // shallower on an 80 by 60 image, so the aspect ratio is included in the expected correction.
        var guided = new CameraRawGeometrySettings { Upright = CameraRawUprightMode.Guided, Rotate = 5 };
        guided.Guides.Add(At(0.1, 0.2, 10));
        var plain = new CameraRawGeometrySettings { Rotate = 5 };
        var pixelAngle = Math.Atan(Math.Tan(10 * Math.PI / 180) * 60 / 80) * 180 / Math.PI;
        var equivalent = new CameraRawGeometrySettings { Rotate = 5 - pixelAngle };
        var turned = guided.Corners(80, 60);
        var sliders = plain.Corners(80, 60);
        var same = equivalent.Corners(80, 60);
        for (var index = 0; index < 4; index++)
        {
            Assert.Equal(same[index], turned[index]);
            Assert.NotEqual(sliders[index], turned[index]);
        }
    }

    [Fact]
    public void TurningUprightOffPutsThePictureBackToTheSlidersOwnAmounts()
    {
        var settings = new CameraRawGeometrySettings { Upright = CameraRawUprightMode.Guided, Rotate = 7 };
        settings.Guides.Add(At(0.1, 0.2, 10));
        var drawn = settings.Corners(80, 60);
        settings.Upright = CameraRawUprightMode.Off;
        var plain = settings.Corners(80, 60);
        var sliders = new CameraRawGeometrySettings { Rotate = 7 }.Corners(80, 60);
        for (var index = 0; index < 4; index++)
        {
            Assert.Equal(sliders[index], plain[index]);
            Assert.NotEqual(sliders[index], drawn[index]);
        }
    }

    [Fact]
    public void TheGuidesAndTheChoiceSurviveNormalizing()
    {
        // Corners works from what Normalized returns, so a field left out of it is a field the geometry
        // silently ignores — which is what would make Guided Upright do nothing at all.
        var settings = new CameraRawGeometrySettings { Upright = CameraRawUprightMode.Guided, Vertical = 300 };
        settings.Guides.Add(At(0.1, 0.2, 30));
        var normalized = settings.Normalized();
        Assert.Equal(CameraRawUprightMode.Guided, normalized.Upright);
        Assert.Single(normalized.Guides);
        Assert.Equal(100, normalized.Vertical);
    }

    [Fact]
    public void AGuideOffThePictureIsNotValid()
    {
        var settings = new CameraRawGeometrySettings { Upright = CameraRawUprightMode.Guided };
        settings.Guides.Add(new CameraRawGeometryGuide(0.1, 0.2, 1.4, 0.8));
        Assert.False(settings.IsValid);
        settings.Guides.Clear();
        settings.Guides.Add(At(0.1, 0.2, 30));
        Assert.True(settings.IsValid);
    }

    [Fact]
    public void TheGuidedTurnReachesTheLayersOwnPixels()
    {
        // The whole path: a line drawn on a layer turns it, and the layer's pixels come out moved. A turned
        // rectangle does not fill its own grid, so its corner is left as nothing.
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(40, 40));
        bitmap.Erase(new SKColor(200, 60, 40));
        using var document = new CanvasDocument(Guid.NewGuid(), 40, 40);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Flat"),
            new LayerTransform(0, 0, 40, 40), "Flat");
        document.Layers.Add(layer);
        var settings = new CameraRawSettings();
        settings.Geometry.Upright = CameraRawUprightMode.Guided;
        settings.Geometry.Guides.Add(At(0.1, 0.2, 10));
        Assert.True(settings.AdjustsGeometry);
        Assert.True(CameraRawEdits.Apply(document, layer.ID, settings));
        var corner = layer.Asset!.Image.GetPixel(0, 0);
        Assert.True(corner.Alpha < 250,
            $"the turn left the corner filled, so the picture did not move: {corner}");
        // The same layer with Upright off is not an adjustment at all, so nothing would have moved it.
        settings.Geometry.Upright = CameraRawUprightMode.Off;
        Assert.False(settings.AdjustsGeometry);
    }
}
