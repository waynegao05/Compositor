using Compositor.Core.Document;
using Compositor.Core.Model;

namespace Compositor.Core.Tests;

public sealed class LayerDeleteSelectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeletingAClippedPairIsIndependentOfSelectionOrder(bool baseFirst)
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 64, 64);
        var source = Layer("Base");
        var clipped = Layer("Clipped");
        clipped.MaskSourceID = source.ID;
        document.Layers.AddRange([source, clipped]);
        Guid[] selected = baseFirst ? [source.ID, clipped.ID] : [clipped.ID, source.ID];

        Assert.True(LayerEdits.Delete(document, selected));
        Assert.Empty(document.Layers);
    }

    [Fact]
    public void AnUnselectedClippedDependentRefusesTheEntireDeletion()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 64, 64);
        var source = Layer("Base");
        var dependent = Layer("Keep");
        dependent.MaskSourceID = source.ID;
        var unrelated = Layer("Also selected");
        document.Layers.AddRange([source, dependent, unrelated]);
        using var before = document.Clone();

        Assert.False(LayerEdits.Delete(document, new[] { unrelated.ID, source.ID }));
        Assert.True(document.SameAs(before));
    }

    [Fact]
    public void NestedFoldersAndRepeatedSelectionsDeleteTheirCompleteUnion()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 64, 64);
        var folder = Layer("Folder");
        folder.IsGroup = true;
        var nested = Layer("Nested");
        nested.IsGroup = true;
        nested.ParentID = folder.ID;
        var source = Layer("Base");
        source.ParentID = nested.ID;
        var clipped = Layer("Clipped");
        clipped.MaskSourceID = source.ID;
        var keep = Layer("Keep");
        document.Layers.AddRange([folder, nested, source, clipped, keep]);

        Assert.True(LayerEdits.Delete(document, new[] { source.ID, folder.ID, nested.ID, folder.ID, clipped.ID }));
        Assert.Same(keep, Assert.Single(document.Layers));
    }

    [Fact]
    public void RejectedBatchPreservesRedoAndDoesNotLeaveHistoryOpen()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 64, 64);
        var source = Layer("Base");
        var clipped = Layer("Clipped");
        clipped.MaskSourceID = source.ID;
        document.Layers.AddRange([source, clipped]);
        var history = new DocumentHistory();
        history.Begin("Rename", document, source.ID);
        source.Name = "Changed";
        history.End(document, source.ID);
        document.Adopt(history.Undo()!.Value.Document!);
        var revision = history.CurrentRevision;

        history.Begin("Delete", document, source.ID);
        Assert.False(LayerEdits.Delete(document, new[] { source.ID }));
        history.End(document, source.ID);

        Assert.False(history.HasPendingEdit);
        Assert.True(history.CanRedo);
        Assert.Equal(revision, history.CurrentRevision);
        Assert.False(history.IsModified);
    }

    [Fact]
    public void UnknownSelectionsCannotPartiallyDeleteExistingLayers()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 64, 64);
        var layer = Layer("Keep");
        document.Layers.Add(layer);
        Assert.False(LayerEdits.Delete(document, new[] { layer.ID, Guid.NewGuid() }));
        Assert.False(LayerEdits.Delete(document, Array.Empty<Guid>()));
        Assert.Same(layer, Assert.Single(document.Layers));
    }

    private static ImageLayer Layer(string name) => new(Guid.NewGuid(), null, new(0, 0, 64, 64), name);
}
