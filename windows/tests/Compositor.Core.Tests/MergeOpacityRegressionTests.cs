using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;
using LayerMask = Compositor.Core.Model.LayerMask;

namespace Compositor.Core.Tests;

public class MergeOpacityRegressionTests
{
    private static ImageLayer Pixel(string name, SKColor color, Guid parent, int x = 8)
    {
        var image = new SKBitmap(Bitmaps.ColorInfo(16, 16));
        image.Erase(color);
        return new ImageLayer(Guid.NewGuid(), ImportedImage.Create(image, name), new LayerTransform(x, 8, 16, 16), name)
        { ParentID = parent };
    }

    private static ImageLayer Folder(string name, double opacity, Guid? parent = null) =>
        new(Guid.NewGuid(), null, new LayerTransform(0, 0, 48, 32), name)
        { IsGroup = true, Opacity = opacity, ParentID = parent };

    private static void SamePixels(SKBitmap before, SKBitmap after)
    {
        var left = before.GetPixelSpan();
        var right = after.GetPixelSpan();
        Assert.Equal(left.Length, right.Length);
        for (var i = 0; i < left.Length; i++) Assert.InRange(Math.Abs(left[i] - right[i]), 0, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MergingInsideTranslucentFoldersPreservesThePictureAndUndo(bool nested)
    {
        var outer = Folder("Outer", .5);
        var inner = Folder("Inner", .5, nested ? outer.ID : null);
        var blue = Pixel("Blue", SKColors.Blue, inner.ID);
        var red = Pixel("Red", SKColors.Red, inner.ID);
        var sibling = Pixel("Sibling", SKColors.Green, inner.ID, 28);
        sibling.Opacity = .8;
        var hidden = Pixel("Hidden", SKColors.Yellow, inner.ID);
        hidden.IsVisible = false;
        hidden.Opacity = .6;
        using var document = new CanvasDocument(Guid.NewGuid(), 48, 32);
        document.Layers.AddRange(nested ? [outer, inner, blue, red, sibling, hidden] : [inner, blue, red, sibling, hidden]);
        using var original = document.Clone();
        using var before = DocumentRenderer.Render(document);
        var history = new DocumentHistory();
        history.Begin("Merge Down", document, red.ID);
        var id = LayerMerge.Merge(document, [red.ID], red.ID);
        Assert.NotNull(id);
        history.End(document, id);
        using var after = DocumentRenderer.Render(document);
        SamePixels(before, after);
        Assert.Equal(1, inner.Opacity);
        Assert.Equal(.8 * (nested ? .25 : .5), sibling.Opacity, 10);
        Assert.Equal(.6 * (nested ? .25 : .5), hidden.Opacity, 10);
        Assert.False(hidden.IsVisible);
        Assert.Equal(inner.ID, sibling.ParentID);
        document.Adopt(history.Undo()!.Value.Document!);
        Assert.True(original.SameAs(document));
        document.Adopt(history.Redo()!.Value.Document!);
        using var redone = DocumentRenderer.Render(document);
        SamePixels(before, redone);
    }

    [Fact]
    public void OpacityNormalizationPreservesUnmergedMasksAndEffects()
    {
        var folder = Folder("Folder", .5);
        var blue = Pixel("Blue", SKColors.Blue, folder.ID);
        var red = Pixel("Red", SKColors.Red, folder.ID);
        var sibling = Pixel("Sibling", SKColors.Green, folder.ID, 28);
        sibling.Mask = LayerMask.Solid(true);
        sibling.Effects = new LayerEffects { ColorOverlay = new ColorOverlayEffect { Blue = 1, Opacity = .5 } };
        var mask = sibling.Mask;
        var effects = sibling.Effects;
        using var document = new CanvasDocument(Guid.NewGuid(), 48, 32);
        document.Layers.AddRange([folder, blue, red, sibling]);
        using var before = DocumentRenderer.Render(document);
        Assert.NotNull(LayerMerge.Merge(document, [red.ID], red.ID));
        using var after = DocumentRenderer.Render(document);
        SamePixels(before, after);
        Assert.Same(mask, sibling.Mask);
        Assert.Same(effects, sibling.Effects);
    }

    [Fact]
    public void AnUnrepresentableSoftParentMaskRefusesTheMergeWithoutChangingAnyRecord()
    {
        var folder = Folder("Folder", .5);
        var maskPixels = new SKBitmap(Bitmaps.MaskInfo(4, 4));
        maskPixels.Erase(new SKColor(128, 128, 128));
        folder.Mask = LayerMask.AssetFrom(maskPixels);
        var blue = Pixel("Blue", SKColors.Blue, folder.ID);
        var red = Pixel("Red", SKColors.Red, folder.ID);
        using var document = new CanvasDocument(Guid.NewGuid(), 48, 32);
        document.Layers.AddRange([folder, blue, red]);
        using var original = document.Clone();
        var history = new DocumentHistory();
        history.Begin("Merge Down", document, red.ID);
        Assert.Null(LayerMerge.Merge(document, [red.ID], red.ID));
        history.End(document, red.ID);
        Assert.True(original.SameAs(document));
        Assert.False(history.IsModified);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void ARefusedHierarchyDoesNotLeaveNormalizedOpacitiesBehind()
    {
        var outer = Folder("Outer", .5);
        var inner = Folder("Inner", .5, outer.ID);
        var layer = Pixel("Child", SKColors.Red, inner.ID);
        using var document = new CanvasDocument(Guid.NewGuid(), 48, 32);
        document.Layers.AddRange([outer, inner, layer]);
        using var original = document.Clone();
        // The current merge planner places this result under the selected folder that it also removes.
        Assert.Null(LayerMerge.Merge(document, [inner.ID, layer.ID], layer.ID));
        Assert.True(original.SameAs(document));
    }
}
