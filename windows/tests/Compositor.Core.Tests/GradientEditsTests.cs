using Compositor.Core.Document;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// The Gradient tool: how far along a point is, the colour it takes, and filling a layer's pixels or its
/// mask with it.
/// </summary>
public class GradientEditsTests
{
    /// <summary>A pixel is a shade off the exact end colour, because a gradient is read at pixel middles.</summary>
    private static void Close(SKColor actual, int red, int green, int blue, int alpha, int tolerance = 3)
    {
        Assert.True(Math.Abs(actual.Red - red) <= tolerance && Math.Abs(actual.Green - green) <= tolerance
            && Math.Abs(actual.Blue - blue) <= tolerance && Math.Abs(actual.Alpha - alpha) <= tolerance,
            $"{actual} is not ({red},{green},{blue},{alpha})");
    }

    /// <summary>A layer of one flat colour, its pixel grid the same size as the document it fills.</summary>
    private static (CanvasDocument Document, ImageLayer Layer) Flat(int width, int height, SKColor colour)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(colour);
        var document = new CanvasDocument(Guid.NewGuid(), width, height);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Flat"),
            new LayerTransform(0, 0, width, height), "Flat");
        document.Layers.Add(layer);
        return (document, layer);
    }

    [Fact]
    public void APointIsSoFarAlongTheRun()
    {
        var start = new SKPoint(0, 0);
        var end = new SKPoint(100, 0);
        Assert.Equal(0, GradientEdits.Parameter(start, end, new SKPoint(0, 50), GradientShape.Linear), 3);
        Assert.Equal(0.25, GradientEdits.Parameter(start, end, new SKPoint(25, 50), GradientShape.Linear), 3);
        Assert.Equal(1, GradientEdits.Parameter(start, end, new SKPoint(100, 50), GradientShape.Linear), 3);
        // Past the ends is held to them, as drawing past the ends of a gradient is.
        Assert.Equal(0, GradientEdits.Parameter(start, end, new SKPoint(-40, 0), GradientShape.Linear), 3);
        Assert.Equal(1, GradientEdits.Parameter(start, end, new SKPoint(400, 0), GradientShape.Linear), 3);
    }

    [Fact]
    public void ARadialGradientRunsOutFromWhereItBegan()
    {
        var start = new SKPoint(100, 100);
        var end = new SKPoint(150, 100);
        Assert.Equal(0, GradientEdits.Parameter(start, end, start, GradientShape.Radial), 3);
        // Halfway out to the rim, whatever direction.
        Assert.Equal(0.5, GradientEdits.Parameter(start, end, new SKPoint(125, 100), GradientShape.Radial), 3);
        Assert.Equal(0.5, GradientEdits.Parameter(start, end, new SKPoint(100, 125), GradientShape.Radial), 3);
        Assert.Equal(1, GradientEdits.Parameter(start, end, new SKPoint(150, 100), GradientShape.Radial), 3);
    }

    [Fact]
    public void TheColourIsTakenPartOfTheWayBetweenTheTwoEnds()
    {
        var from = new SKColor(255, 0, 0, 255);
        var to = new SKColor(0, 0, 255, 255);
        Assert.Equal(from, GradientEdits.Blend(from, to, 0));
        Assert.Equal(to, GradientEdits.Blend(from, to, 1));
        var middle = GradientEdits.Blend(from, to, 0.5);
        Assert.Equal(128, middle.Red);
        Assert.Equal(128, middle.Blue);
    }

    [Fact]
    public void FadingToTransparentFadesTheAlphaOut()
    {
        var from = new SKColor(255, 0, 0, 255);
        var to = new SKColor(255, 0, 0, 0);
        Assert.Equal(255, GradientEdits.Blend(from, to, 0).Alpha);
        Assert.Equal(0, GradientEdits.Blend(from, to, 1).Alpha);
        Assert.Equal(128, GradientEdits.Blend(from, to, 0.5).Alpha);
    }

    [Fact]
    public void AFullGradientReplacesWhatItCovers()
    {
        var (document, layer) = Flat(100, 20, SKColors.White);
        using var _ = document;
        Assert.True(GradientEdits.Fill(document, layer.ID, mask: false, new SKPoint(0, 10), new SKPoint(100, 10),
            new SKColor(255, 0, 0), new SKColor(0, 0, 255), 1, GradientShape.Linear));

        // The left edge is the first colour, the right edge the second, and halfway is between them.
        Close(layer.Asset!.Image.GetPixel(0, 10), 255, 0, 0, 255);
        Close(layer.Asset.Image.GetPixel(99, 10), 0, 0, 255, 255);
        var middle = layer.Asset.Image.GetPixel(50, 10);
        Assert.True(middle.Red is > 100 and < 160, $"halfway is {middle}");
        Assert.True(middle.Blue is > 100 and < 160, $"halfway is {middle}");
        // It runs across the whole layer, not just along the drag line.
        Close(layer.Asset.Image.GetPixel(0, 1), 255, 0, 0, 255);
        Close(layer.Asset.Image.GetPixel(99, 18), 0, 0, 255, 255);
    }

    [Fact]
    public void AnOpacityLeavesSomeOfWhatWasThere()
    {
        var (document, layer) = Flat(100, 20, SKColors.White);
        using var _ = document;
        Assert.True(GradientEdits.Fill(document, layer.ID, mask: false, new SKPoint(0, 10), new SKPoint(100, 10),
            new SKColor(0, 0, 0), new SKColor(0, 0, 0), 0.5, GradientShape.Linear));

        // Half the paint over white leaves grey.
        var painted = layer.Asset!.Image.GetPixel(50, 10);
        Assert.True(Math.Abs(painted.Red - 128) <= 2, $"the colour is {painted}");
    }

    [Fact]
    public void AGradientThatFadesToTransparentPaintsLessAndLess()
    {
        // A gradient is painted onto a layer, so a fade shows where the layer is clear.
        var (document, layer) = Flat(100, 20, SKColors.Transparent);
        using var _ = document;
        Assert.True(GradientEdits.Fill(document, layer.ID, mask: false, new SKPoint(0, 10), new SKPoint(100, 10),
            new SKColor(0, 255, 0, 255), new SKColor(0, 255, 0, 0), 1, GradientShape.Linear));

        Close(layer.Asset!.Image.GetPixel(0, 10), 0, 255, 0, 255);
        var middle = layer.Asset.Image.GetPixel(50, 10);
        Assert.True(middle.Alpha is > 100 and < 160, $"halfway is {middle}");
        Assert.True(layer.Asset.Image.GetPixel(99, 10).Alpha <= 3, $"the far end is {layer.Asset.Image.GetPixel(99, 10)}");
    }

    [Fact]
    public void AGradientOverAnOpaqueLayerLeavesItWhereTheGradientIsClear()
    {
        var (document, layer) = Flat(100, 20, new SKColor(255, 0, 0));
        using var _ = document;
        Assert.True(GradientEdits.Fill(document, layer.ID, mask: false, new SKPoint(0, 10), new SKPoint(100, 10),
            new SKColor(0, 255, 0, 255), new SKColor(0, 255, 0, 0), 1, GradientShape.Linear));

        // The near end is the gradient's colour, and where the gradient has faded out the layer shows through
        // exactly as it was.
        Close(layer.Asset!.Image.GetPixel(0, 10), 0, 255, 0, 255);
        Close(layer.Asset.Image.GetPixel(99, 10), 255, 0, 0, 255);
    }

    [Fact]
    public void ARadialGradientRunsOutFromItsMiddle()
    {
        var (document, layer) = Flat(100, 100, SKColors.White);
        using var _ = document;
        Assert.True(GradientEdits.Fill(document, layer.ID, mask: false, new SKPoint(50, 50), new SKPoint(90, 50),
            SKColors.White, SKColors.Black, 1, GradientShape.Radial));

        // The middle is the first colour (a shade off it, since a gradient is read at pixel middles), and it
        // darkens as it runs out, the same distance out in any direction.
        Close(layer.Asset!.Image.GetPixel(50, 50), 255, 255, 255, 255, tolerance: 8);
        var across = layer.Asset.Image.GetPixel(70, 50);
        var down = layer.Asset.Image.GetPixel(50, 70);
        Assert.Equal(across.Red, down.Red);
        Assert.True(across.Red is > 0 and < 255, $"partway out is {across}");
        // Past the rim it is the last colour.
        Assert.True(layer.Asset.Image.GetPixel(99, 50).Red <= 3, $"past the rim is {layer.Asset.Image.GetPixel(99, 50)}");
    }

    [Fact]
    public void AClickWithoutADragPaintsNothing()
    {
        var (document, layer) = Flat(20, 20, SKColors.White);
        using var _ = document;
        Assert.False(GradientEdits.Fill(document, layer.ID, mask: false, new SKPoint(10, 10), new SKPoint(10, 10),
            SKColors.Black, SKColors.Black, 1, GradientShape.Linear));
        Assert.False(GradientEdits.HasLine(new SKPoint(10, 10), new SKPoint(10.2f, 10.2f)));
        Assert.True(GradientEdits.HasLine(new SKPoint(10, 10), new SKPoint(20, 10)));
    }

    [Fact]
    public void AGradientFollowsTheLayerItFills()
    {
        var (document, layer) = Flat(20, 20, SKColors.White);
        using var _ = document;
        // A layer twice the size of its grid, so its pixels are drawn at two document pixels each.
        layer.Transform = new LayerTransform(10, 10, 40, 40);
        Assert.True(GradientEdits.Fill(document, layer.ID, mask: false, new SKPoint(10, 30), new SKPoint(50, 30),
            new SKColor(255, 0, 0), new SKColor(0, 0, 255), 1, GradientShape.Linear));

        // Document x 10 is the layer's own first column, so it is nearly the first colour; the last column
        // is nearly the second. The tolerance is wider here because each layer pixel covers two of the
        // gradient's, so the run is sampled more coarsely.
        Close(layer.Asset!.Image.GetPixel(0, 10), 255, 0, 0, 255, tolerance: 8);
        Close(layer.Asset.Image.GetPixel(19, 10), 0, 0, 255, 255, tolerance: 8);
    }

    [Fact]
    public void AGradientAppliesToAMaskAsCoverage()
    {
        var (document, layer) = Flat(100, 20, SKColors.Red);
        using var _ = document;
        Assert.True(LayerMaskEdits.Add(document, layer.ID, revealing: false));
        Assert.True(GradientEdits.Fill(document, layer.ID, mask: true, new SKPoint(0, 10), new SKPoint(100, 10),
            SKColors.White, SKColors.Black, 1, GradientShape.Linear));

        Assert.True(Bitmaps.IsValidMask(layer.Mask!.Asset.Image));
        // White at the near end reveals, black at the far end hides, and the middle is part way.
        Assert.True(layer.Mask.Asset.Image.GetPixel(0, 10).Red >= 250);
        Assert.True(layer.Mask.Asset.Image.GetPixel(99, 10).Red <= 5);
        var middle = layer.Mask.Asset.Image.GetPixel(50, 10).Red;
        Assert.True(middle is > 100 and < 160, $"halfway is {middle}");
        // And the layer shows through where the mask reveals it.
        using var rendered = Rendering.DocumentRenderer.Render(document);
        Assert.True(rendered.GetPixel(1, 10).Alpha >= 250);
        Assert.True(rendered.GetPixel(98, 10).Alpha <= 5);
    }

    [Fact]
    public void AGradientStopsAtTheSelection()
    {
        var (document, layer) = Flat(100, 20, SKColors.White);
        using var _ = document;
        SelectionEdits.Select(document, SKRectI.Create(20, 0, 40, 20));
        Assert.True(GradientEdits.Fill(document, layer.ID, mask: false, new SKPoint(0, 10), new SKPoint(100, 10),
            new SKColor(0, 0, 0), new SKColor(0, 0, 0), 1, GradientShape.Linear));

        // Inside the selection the layer is now black; outside it is still white.
        Assert.Equal(new SKColor(0, 0, 0, 255), layer.Asset!.Image.GetPixel(40, 10));
        Assert.Equal(new SKColor(255, 255, 255, 255), layer.Asset.Image.GetPixel(10, 10));
        Assert.Equal(new SKColor(255, 255, 255, 255), layer.Asset.Image.GetPixel(90, 10));
    }

    [Fact]
    public void ABlankLayerGetsPixelsOnItsFirstGradient()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 20, 20);
        var blank = new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 20, 20), "Empty");
        document.Layers.Add(blank);
        Assert.True(GradientEdits.Fill(document, blank.ID, mask: false, new SKPoint(0, 10), new SKPoint(20, 10),
            SKColors.Black, SKColors.Black, 1, GradientShape.Linear));
        Assert.NotNull(blank.Asset);
        Assert.Equal(20, blank.Asset.Width);
        Assert.Equal(20, blank.Asset.Height);
        Assert.Equal(SKColors.Black, blank.Asset.Image.GetPixel(0, 0));
        Assert.Equal(SKColors.Black, blank.Asset.Image.GetPixel(19, 19));
    }
}
