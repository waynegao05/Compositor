using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;
using LayerMask = Compositor.Core.Model.LayerMask;

namespace Compositor.Core.Tests;

public class MaskTransformRegressionTests
{
    private static CanvasDocument Document()
    {
        var image = new SKBitmap(Bitmaps.ColorInfo(16, 16));
        image.Erase(SKColors.Red);
        var mask = new SKBitmap(Bitmaps.MaskInfo(16, 16));
        mask.Erase(SKColors.Black);
        for (var y = 0; y < 16; y++)
            for (var x = 0; x < 8; x++) mask.SetPixel(x, y, SKColors.White);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(image, "Red"),
            new LayerTransform(8, 8, 16, 16), "Red") { Mask = LayerMask.AssetFrom(mask) };
        var document = new CanvasDocument(Guid.NewGuid(), 48, 32);
        document.Layers.Add(layer);
        return document;
    }

    [Fact]
    public void AnUnlinkedMaskStaysPutWhenTheLayerMovesAndHistoryRestoresIt()
    {
        using var document = Document();
        var layer = document.Layers[0];
        LayerMaskEdits.SetLinked(document, layer.ID, false);
        var original = layer.Transform;
        var history = new DocumentHistory();
        history.Begin("Move", document, layer.ID);
        Assert.True(LayerEdits.Move(document, layer.ID, 8, 0));
        history.End(document, layer.ID);
        Assert.Equal(original, layer.MaskTransform);
        using var moved = DocumentRenderer.Render(document);
        Assert.Equal(0, moved.GetPixel(18, 10).Alpha);
        document.Adopt(history.Undo()!.Value.Document!);
        Assert.Equal(original, document.Layers[0].Transform);
        Assert.Null(document.Layers[0].Mask!.Placement);
        document.Adopt(history.Redo()!.Value.Document!);
        Assert.Equal(original, document.Layers[0].MaskTransform);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReturningADragToItsStartRestoresTheExactMaskPlacement(bool linked)
    {
        using var document = Document();
        var layer = document.Layers[0];
        layer.Mask!.IsLinked = linked;
        layer.Mask.Placement = layer.Transform with { X = 5, Rotation = 15 };
        var originalMask = layer.Mask.Placement;
        var box = TransformEdits.GroupBox(document, [layer.ID])!.Value;
        var originals = new Dictionary<Guid, LayerTransform> { [layer.ID] = layer.Transform };
        var masks = new Dictionary<Guid, LayerTransform?> { [layer.ID] = originalMask };
        var history = new DocumentHistory();
        history.Begin("Transform", document, layer.ID);
        Assert.True(TransformEdits.Carry(document, originals, box,
            box with { X = 15, Width = 24, Height = 20, Rotation = 35 }, masks));
        Assert.True(TransformEdits.Carry(document, originals, box, box, masks));
        history.End(document, layer.ID);
        Assert.Equal(originalMask, layer.Mask.Placement);
        Assert.False(history.IsModified);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void AnIndependentLinkedMaskMovesByTheSameTranslationAsItsLayer()
    {
        using var document = Document();
        var layer = document.Layers[0];
        layer.Mask!.Placement = layer.Transform with { X = 4 };
        Assert.True(LayerEdits.Move(document, layer.ID, 5, 3));
        Assert.Equal(9, layer.MaskTransform.X);
        Assert.Equal(11, layer.MaskTransform.Y);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FlippingALinkedMaskMirrorsTheWholeVisibleImage(bool horizontal)
    {
        using var document = Document();
        var layer = document.Layers[0];
        // A diagonal split distinguishes both horizontal and vertical reflection.
        var mask = layer.Mask!.Asset.Image;
        for (var y = 0; y < 16; y++)
            for (var x = 0; x < 16; x++) mask.SetPixel(x, y, x < y ? SKColors.White : SKColors.Black);
        using var before = DocumentRenderer.Render(document);
        Assert.True(LayerEdits.Flip(document, [layer.ID], horizontal));
        using var after = DocumentRenderer.Render(document);
        for (var y = 8; y < 24; y++)
        {
            for (var x = 8; x < 24; x++)
                Assert.Equal(before.GetPixel(horizontal ? 31 - x : x, horizontal ? y : 31 - y), after.GetPixel(x, y));
        }
    }

    [Fact]
    public void AQuarterTurnRotatesTheMaskCoverageAndTilesMatchTheWholeRender()
    {
        using var document = Document();
        var layer = document.Layers[0];
        var box = layer.Transform;
        TransformEdits.Carry(document, new Dictionary<Guid, LayerTransform> { [layer.ID] = box }, box,
            box with { Rotation = 90 });
        using var whole = DocumentRenderer.Render(document);
        Assert.Equal(255, whole.GetPixel(10, 10).Alpha);
        Assert.Equal(0, whole.GetPixel(10, 20).Alpha);
        using var tile = DocumentRenderer.RenderRegion(document, SKRectI.Create(10, 10, 12, 12));
        for (var y = 0; y < tile.Height; y++)
            for (var x = 0; x < tile.Width; x++) Assert.Equal(whole.GetPixel(x + 10, y + 10), tile.GetPixel(x, y));
    }

    [Fact]
    public void AddingASameColorOverlayDoesNotReattachAnIndependentMask()
    {
        using var document = Document();
        var layer = document.Layers[0];
        LayerMaskEdits.SetLinked(document, layer.ID, false);
        LayerEdits.Flip(document, [layer.ID], true);
        using var before = DocumentRenderer.Render(document);
        Assert.True(LayerEdits.SetEffects(document, layer.ID, new LayerEffects
        {
            ColorOverlay = new ColorOverlayEffect { Red = 1, Opacity = 1 },
        }));
        using var after = DocumentRenderer.Render(document);
        Assert.Equal(before.GetPixelSpan().ToArray(), after.GetPixelSpan().ToArray());
    }
}
