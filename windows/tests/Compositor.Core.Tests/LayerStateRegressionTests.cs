using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.Model;
using SkiaSharp;
using LayerMask = Compositor.Core.Model.LayerMask;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

public sealed class LayerStateRegressionTests
{
    private static ImageLayer Layer(double x = 10, double y = 20)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(4, 6));
        bitmap.Erase(SKColors.Red);
        return new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Pixels"), new(x, y, 4, 6), "Pixels");
    }

    private static ImageLayer Folder(Guid? parent = null) =>
        new(Guid.NewGuid(), null, new(0, 0, 100, 100), "Folder") { IsGroup = true, ParentID = parent };

    private static CanvasDocument Document(params ImageLayer[] layers)
    {
        var document = new CanvasDocument(Guid.NewGuid(), 100, 100);
        document.Layers.AddRange(layers);
        return document;
    }

    [Theory]
    [InlineData("enabled")]
    [InlineData("linked")]
    [InlineData("placement")]
    public void MaskStateChangesCanBeUndoneAndRedone(string property)
    {
        var layer = Layer();
        layer.Mask = LayerMask.Solid(true)!;
        using var document = Document(layer);
        var history = new DocumentHistory();
        history.Begin("Mask state", document, layer.ID);
        switch (property)
        {
            case "enabled": layer.Mask.IsEnabled = false; break;
            case "linked": layer.Mask.IsLinked = false; break;
            case "placement": layer.Mask.Placement = new LayerTransform(5, 8, 4, 6); break;
        }
        history.End(document, layer.ID);
        Assert.True(history.IsModified);
        var undone = history.Undo();
        Assert.NotNull(undone);
        var before = Assert.Single(undone.Value.Document!.Layers).Mask;
        Assert.NotNull(before);
        Assert.True(before.IsEnabled);
        Assert.True(before.IsLinked);
        Assert.Null(before.Placement);
        Assert.NotSame(layer.Mask, before);
        Assert.Same(layer.Mask.Asset.Image, before.Asset.Image);
        var redone = history.Redo();
        Assert.NotNull(redone);
        Assert.True(layer.SameAs(Assert.Single(redone.Value.Document!.Layers)));
    }

    [Fact]
    public void DuplicateAndSnapshotMaskWrappersAreIndependent()
    {
        var layer = Layer();
        layer.Mask = LayerMask.Solid(true)!;
        using var document = Document(layer);
        using var snapshot = document.Clone();
        var duplicateID = LayerEdits.Duplicate(document, layer.ID);
        Assert.NotNull(duplicateID);
        var duplicate = document.Layers.Single(item => item.ID == duplicateID);
        Assert.NotNull(duplicate.Mask);
        duplicate.Mask.IsEnabled = false;
        duplicate.Mask.IsLinked = false;
        duplicate.Mask.Placement = new LayerTransform(7, 9, 4, 6);
        Assert.True(layer.Mask.IsEnabled);
        Assert.True(layer.Mask.IsLinked);
        Assert.Null(layer.Mask.Placement);
        Assert.True(layer.SameAs(Assert.Single(snapshot.Layers)));
        Assert.Same(layer.Mask.Asset.Image, duplicate.Mask.Asset.Image);
    }

    [Theory]
    [InlineData(LayerBlendMode.Multiply)]
    [InlineData(LayerBlendMode.Screen)]
    [InlineData(LayerBlendMode.Color)]
    public void FoldersRejectBlendModesThatCannotBeSaved(LayerBlendMode mode)
    {
        var folder = Folder();
        using var document = Document(folder);
        Assert.False(LayerEdits.SetBlendMode(document, folder.ID, mode));
        Assert.Equal(LayerBlendMode.Normal, folder.BlendMode);
        ProjectStore.Validate(ProjectSnapshot.FromDocument(document).Manifest);
    }

    [Fact]
    public void NudgeMovesNestedContentsExactlyOnceAndLeavesFolderRectangles()
    {
        var outer = Folder();
        var inner = Folder(outer.ID);
        var child = Layer();
        child.ParentID = inner.ID;
        var sibling = Layer(30, 40);
        sibling.ParentID = outer.ID;
        var outside = Layer(60, 70);
        using var document = Document(child, outside, inner, sibling, outer);
        Assert.True(LayerEdits.Move(document, [outer.ID, inner.ID, child.ID], 3, -2));
        Assert.Equal(new LayerTransform(13, 18, 4, 6), child.Transform);
        Assert.Equal(new LayerTransform(33, 38, 4, 6), sibling.Transform);
        Assert.Equal(new LayerTransform(60, 70, 4, 6), outside.Transform);
        Assert.Equal(new LayerTransform(0, 0, 100, 100), inner.Transform);
        Assert.Equal(new LayerTransform(0, 0, 100, 100), outer.Transform);
    }

    [Fact]
    public void NudgeUsesTheSameVisibilityAndEmptyGroupRulesAsDrag()
    {
        var folder = Folder();
        var hidden = Layer();
        hidden.ParentID = folder.ID;
        hidden.IsVisible = false;
        using var document = Document(hidden, folder);
        Assert.False(LayerEdits.Move(document, folder.ID, 3, -2));
        Assert.Equal(new LayerTransform(10, 20, 4, 6), hidden.Transform);
        hidden.IsVisible = true;
        folder.IsVisible = false;
        Assert.False(LayerEdits.Move(document, [folder.ID, hidden.ID], 3, -2));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.PositiveInfinity)]
    [InlineData(2_000_000, 0)]
    public void InvalidOrEmptyNudgesDoNotChangeTheDocument(double dx, double dy)
    {
        var layer = Layer();
        using var document = Document(layer);
        using var before = document.Clone();
        Assert.False(LayerEdits.Move(document, layer.ID, dx, dy));
        Assert.True(before.SameAs(document));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlippingAFolderIncludesEveryDescendantRegardlessOfArrayOrder(bool horizontal)
    {
        var outer = Folder();
        var inner = Folder(outer.ID);
        var deep = Folder(inner.ID);
        var child = Layer();
        child.ParentID = deep.ID;
        var sibling = Layer(30, 40);
        sibling.ParentID = outer.ID;
        using var document = Document(child, deep, sibling, inner, outer);
        Assert.True(LayerEdits.Flip(document, [outer.ID, deep.ID, child.ID], horizontal));
        Assert.Equal(horizontal ? 30 : 10, child.Transform.X);
        Assert.Equal(horizontal ? 20 : 40, child.Transform.Y);
        Assert.Equal(horizontal, child.Transform.FlipX);
        Assert.Equal(!horizontal, child.Transform.FlipY);
        Assert.Equal(horizontal ? 10 : 30, sibling.Transform.X);
        Assert.Equal(horizontal ? 40 : 20, sibling.Transform.Y);
        Assert.Equal(new LayerTransform(0, 0, 100, 100), deep.Transform);
    }

    [Fact]
    public void DraggingBackToTheOriginalGroupBoxRestoresTheLayersAndLeavesNoUndoStep()
    {
        var layer = Layer();
        using var document = Document(layer);
        var original = layer.Transform;
        var originals = new Dictionary<Guid, LayerTransform> { [layer.ID] = original };
        var startingBox = TransformEdits.GroupBox(document, [layer.ID])!.Value;
        var history = new DocumentHistory();
        history.Begin("Drag", document, layer.ID);
        Assert.True(TransformEdits.Carry(document, originals, startingBox, startingBox with { X = startingBox.X + 20 }));
        Assert.True(TransformEdits.Carry(document, originals, startingBox, startingBox));
        Assert.Equal(original, layer.Transform);
        Assert.False(TransformEdits.Carry(document, originals, startingBox, startingBox));
        history.End(document, layer.ID);
        Assert.False(history.IsModified);
        Assert.False(history.CanUndo);
    }
}
