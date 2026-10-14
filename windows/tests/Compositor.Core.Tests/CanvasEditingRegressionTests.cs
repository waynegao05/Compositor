using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using LayerMask = Compositor.Core.Model.LayerMask;
using Compositor.Core.Rendering;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

public class CanvasEditingRegressionTests
{
    [Fact]
    public void ResizingAnEmptyLayerKeepsItsDocumentExtentAndCanStillBePainted()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 40, 30);
        var layer = new ImageLayer(Guid.NewGuid(), null, new LayerTransform(5, 6, 30, 20), "Empty");
        document.Layers.Add(layer);
        Assert.True(ImageEdits.Resize(document, 80, 60, 72));
        Assert.Equal(new LayerTransform(10, 12, 60, 40), layer.Transform);
        Assert.Null(layer.Asset);
        Assert.True(BrushEdits.EnsurePixels(document, layer.ID));
        Assert.True(BrushEdits.Paint(document, layer.ID, [new SKPoint(40, 30)], new BrushSettings(Diameter: 10, Red: 1)));
        using var rendered = DocumentRenderer.Render(document);
        Assert.True(rendered.GetPixel(40, 30).Alpha > 0);
    }

    [Theory]
    [InlineData(45, false, false)]
    [InlineData(0, true, false)]
    [InlineData(0, false, true)]
    [InlineData(25, true, true)]
    public void TransformedShapesResizeLikeTheirVisibleRaster(double angle, bool flipX, bool flipY)
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 60, 60);
        var style = new LayerShapeStyle
        {
            Kind = ShapeKind.Line, Red = 1, LineWidth = 3,
            Start = new JsonPoint(0.1, 0.2), End = new JsonPoint(0.9, 0.7),
        };
        Assert.NotNull(ShapeEdits.Add(document, style, SKRectI.Create(20, 20, 20, 10), null));
        var layer = document.Layers.Single();
        layer.Transform = layer.Transform with { Rotation = angle, FlipX = flipX, FlipY = flipY };
        using var expected = document.Clone();
        expected.Layers.Single().Shape = null;
        Assert.True(ImageEdits.Resize(document, 120, 90, 72));
        Assert.True(ImageEdits.Resize(expected, 120, 90, 72));
        Assert.Null(layer.Shape);
        using var actualRaster = DocumentRenderer.Render(document);
        using var expectedRaster = DocumentRenderer.Render(expected);
        Assert.Equal(expectedRaster.GetPixelSpan().ToArray(), actualRaster.GetPixelSpan().ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(20)]
    public void PartialFillExpandsUniformMaskWithoutChangingUnselectedPixels(int left)
    {
        using var document = Split(out var layer);
        layer.Mask = LayerMask.Solid(true);
        document.Selection = DocumentSelection.Rectangular(SKRectI.Create(left, 10, 8, 8), 40, 40);
        Assert.True(FillEdits.FillMask(document, layer.ID, 0));
        Assert.NotNull(layer.Mask);
        Assert.Equal(40, layer.Mask.Asset.Width);
        Assert.Equal(40, layer.Mask.Asset.Height);
        Assert.Equal(0, layer.Mask.Asset.Image.GetPixel(left + 3, 13).Red);
        Assert.Equal(255, layer.Mask.Asset.Image.GetPixel(39, 39).Red);
    }

    [Fact]
    public void PartialMaskFillRetainsFeatherCoverage()
    {
        using var document = Split(out var layer);
        layer.Mask = LayerMask.Solid(true);
        document.Selection = DocumentSelection.Rectangular(SKRectI.Create(10, 10, 20, 20), 40, 40).WithFeather(6);
        Assert.True(FillEdits.FillMask(document, layer.ID, 0));
        Assert.NotNull(layer.Mask);
        Assert.Equal(255, layer.Mask.Asset.Image.GetPixel(0, 0).Red);
        Assert.Equal(0, layer.Mask.Asset.Image.GetPixel(20, 20).Red);
        Assert.InRange(layer.Mask.Asset.Image.GetPixel(10, 20).Red, (byte)1, (byte)254);
    }

    [Fact]
    public void EmptySelectionCannotFillOrClearPixelsOrExpandUniformMask()
    {
        using var document = Split(out var layer);
        layer.Mask = LayerMask.Solid(true);
        var original = layer.Asset;
        var mask = layer.Mask;
        document.Selection = DocumentSelection.FromPath(new SKPath());
        Assert.False(FillEdits.Fill(document, layer.ID, SKColors.White));
        Assert.False(FillEdits.Clear(document, layer.ID));
        Assert.False(FillEdits.FillMask(document, layer.ID, 0));
        Assert.Same(original, layer.Asset);
        Assert.Same(mask, layer.Mask);
    }

    [Fact]
    public void RefusingAnOversizedMaskExpansionKeepsTheUniformMask()
    {
        using var document = Split(out var layer);
        layer.Transform = new LayerTransform(0, 0, 20_000, 20_000);
        layer.Mask = LayerMask.Solid(true);
        var mask = layer.Mask;
        document.Selection = DocumentSelection.Rectangular(SKRectI.Create(0, 0, 20, 20), 40, 40);
        Assert.False(FillEdits.FillMask(document, layer.ID, 0));
        Assert.Same(mask, layer.Mask);
        Assert.Equal(255, layer.Mask!.Asset.Image.GetPixel(0, 0).Red);
    }

    [Fact]
    public void RefusingSelectionCoverageCannotTurnItIntoAnUnrestrictedFill()
    {
        using var document = Split(out var layer);
        document.Width = 30_000;
        document.Height = 30_000;
        document.Selection = DocumentSelection.Rectangular(SKRectI.Create(0, 0, 30_000, 30_000), 30_000, 30_000);
        var original = layer.Asset;
        Assert.False(FillEdits.Fill(document, layer.ID, SKColors.White));
        Assert.False(FillEdits.Clear(document, layer.ID));
        Assert.Same(original, layer.Asset);
    }

    [Theory]
    [InlineData(WarpMode.Liquify)]
    [InlineData(WarpMode.Smudge)]
    public void WarpOutsideSelectionLeavesTheLayerUntouched(WarpMode mode)
    {
        using var document = Split(out var layer);
        var original = layer.Asset;
        document.Selection = DocumentSelection.Rectangular(SKRectI.Create(0, 0, 5, 5), 40, 40);
        Assert.False(WarpEdits.Warp(document, layer.ID, Stroke, mode, Brush));
        Assert.Same(original, layer.Asset);
    }

    [Theory]
    [InlineData(WarpMode.Liquify)]
    [InlineData(WarpMode.Smudge)]
    public void EmptySelectionCannotWarpPixels(WarpMode mode)
    {
        using var document = Split(out var layer);
        var original = layer.Asset;
        document.Selection = DocumentSelection.FromPath(new SKPath());
        Assert.False(WarpEdits.Warp(document, layer.ID, Stroke, mode, Brush));
        Assert.Same(original, layer.Asset);
    }

    [Theory]
    [InlineData(WarpMode.Liquify)]
    [InlineData(WarpMode.Smudge)]
    public void WarpFeathersColorAndAlphaInPremultipliedSpace(WarpMode mode)
    {
        using var document = Split(out var layer, translucent: true);
        using var full = document.Clone();
        var original = layer.Asset!.Image;
        Assert.True(WarpEdits.Warp(full, layer.ID, Stroke, mode, Brush));
        document.Selection = DocumentSelection.Rectangular(SKRectI.Create(20, 10, 12, 20), 40, 40).WithFeather(8);
        var region = document.Selection.CoverageRect(40, 40);
        using var coverage = document.Selection.Coverage(region);
        Assert.NotNull(coverage);
        Assert.True(WarpEdits.Warp(document, layer.ID, Stroke, mode, Brush));
        var partial = 0;
        for (var y = 0; y < 40; y++)
        for (var x = 0; x < 40; x++)
        {
            var amount = x >= region.Left && x < region.Right && y >= region.Top && y < region.Bottom
                ? coverage.GetPixel(x - region.Left, y - region.Top).Red / 255.0 : 0;
            var before = original.GetPixel(x, y);
            var after = layer.Asset!.Image.GetPixel(x, y);
            var warped = full.Layers.Single().Asset!.Image.GetPixel(x, y);
            if (amount == 0) Assert.Equal(before, after);
            if (amount is <= 0 or >= 1 || before == warped) continue;
            var alpha = before.Alpha * (1 - amount) + warped.Alpha * amount;
            Assert.InRange(Math.Abs(after.Alpha - alpha), 0, 1);
            var red = alpha == 0 ? 0 : (before.Red * before.Alpha * (1 - amount) + warped.Red * warped.Alpha * amount) / alpha;
            Assert.InRange(Math.Abs(after.Red - red), 0, 2);
            partial++;
        }
        Assert.True(partial > 10, $"Only {partial} partially selected changed pixels were checked.");
    }

    [Theory]
    [InlineData(200, 100)]
    [InlineData(100, 200)]
    [InlineData(100, 100)]
    public void UprightMeasuresAnglesInImagePixels(int width, int height)
    {
        const double angle = 10;
        var guide = new CameraRawGeometryGuide(0.2, 0.2, 0.8,
            0.2 + 0.6 * width * Math.Tan(angle * Math.PI / 180) / height);
        Assert.Equal(-angle, GuidedUpright.Corrections([guide], width, height).Rotate, 8);
        var settings = new CameraRawGeometrySettings { Upright = CameraRawUprightMode.Guided };
        settings.Guides.Add(guide);
        var expected = new CameraRawGeometrySettings { Rotate = -angle }.Corners(width, height);
        var actual = settings.Corners(width, height);
        for (var index = 0; index < 4; index++)
        {
            Assert.Equal(expected[index].X, actual[index].X, 4);
            Assert.Equal(expected[index].Y, actual[index].Y, 4);
        }
    }

    private static readonly SKPoint[] Stroke = [new(10, 20), new(32, 20)];
    private static readonly BrushSettings Brush = new(Diameter: 14, Hardness: 0.8, Opacity: 1);

    private static CanvasDocument Split(out ImageLayer layer, bool translucent = false)
    {
        var document = new CanvasDocument(Guid.NewGuid(), 40, 40);
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(40, 40));
        for (var y = 0; y < 40; y++)
        for (var x = 0; x < 40; x++)
            bitmap.SetPixel(x, y, x < 20 ? new SKColor(220, 40, 40, translucent ? (byte)90 : (byte)255)
                : new SKColor(40, 60, 220, translucent ? (byte)180 : (byte)255));
        layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Split"), new LayerTransform(0, 0, 40, 40), "Split");
        document.Layers.Add(layer);
        return document;
    }
}
