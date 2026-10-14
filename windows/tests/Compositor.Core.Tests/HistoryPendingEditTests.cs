using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;

namespace Compositor.Core.Tests;

public sealed class HistoryPendingEditTests
{
    [Fact]
    public void NestedEditsStayPendingUntilTheOuterEndWithoutPrematurelyMarkingDirty()
    {
        var history = new DocumentHistory();
        using var document = new CanvasDocument(Guid.NewGuid(), 20, 20);
        var generation = history.EditGeneration;
        history.Begin("Outer", document, null);
        Assert.True(history.HasPendingEdit);
        Assert.False(history.IsModified);
        Assert.Equal(generation + 1, history.EditGeneration);
        history.Begin("Inner", document, null);
        document.Width = 30;
        history.End(document, null);
        Assert.True(history.HasPendingEdit);
        Assert.False(history.IsModified);
        Assert.Equal(generation + 1, history.EditGeneration);
        history.End(document, null);
        Assert.False(history.HasPendingEdit);
        Assert.True(history.IsModified);
        Assert.Equal(1, history.UndoCount);
    }

    [Fact]
    public void ANoOpEditStillInvalidatesEarlierAsyncWorkAndKeepsRedo()
    {
        var history = new DocumentHistory();
        using var document = new CanvasDocument(Guid.NewGuid(), 20, 20);
        history.Begin("Resize", document, null);
        document.Width = 30;
        history.End(document, null);
        var before = history.Undo();
        Assert.NotNull(before);
        document.Adopt(before.Value.Document!);
        var generation = history.EditGeneration;
        history.Begin("No change", document, null);
        history.End(document, null);
        Assert.Equal(generation + 1, history.EditGeneration);
        Assert.False(history.HasPendingEdit);
        Assert.False(history.IsModified);
        Assert.True(history.CanRedo);
    }

    [Fact]
    public void OnlyEffectiveHistoryNavigationAdvancesTheGeneration()
    {
        var history = new DocumentHistory();
        using var document = new CanvasDocument(Guid.NewGuid(), 20, 20);
        var initial = history.EditGeneration;
        Assert.Null(history.Undo());
        Assert.Null(history.Redo());
        Assert.Equal(initial, history.EditGeneration);
        history.Begin("Resize", document, null);
        document.Width++;
        Assert.Null(history.Undo());
        history.End(document, null);
        var generation = history.EditGeneration;
        Assert.NotNull(history.Undo());
        Assert.Equal(generation + 1, history.EditGeneration);
        Assert.NotNull(history.Redo());
        Assert.Equal(generation + 2, history.EditGeneration);
    }

    [Fact]
    public void ResetClearsPendingEditsAndAdvancesGeneration()
    {
        var history = new DocumentHistory();
        history.Begin("Pending", null, null);
        var generation = history.EditGeneration;
        history.Reset();
        Assert.False(history.HasPendingEdit);
        Assert.Equal(generation + 1, history.EditGeneration);
        Assert.False(history.IsModified);
        history.End(null, null);
        Assert.False(history.CanUndo);
    }

    [Theory]
    [InlineData(GuideAxis.Horizontal)]
    [InlineData(GuideAxis.Vertical)]
    public void EditingAfterRedoCannotChangeGuidePositionsInEarlierHistory(GuideAxis axis)
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 100, 100);
        var id = GuideEdits.Add(document, axis, 10)!.Value;
        var history = new DocumentHistory();
        Move(20);
        Move(30);
        Restore(redo: false, 20);
        Restore(redo: true, 30);
        // Redo adopted an After snapshot still retained in the past. The next move must not alter it.
        Move(40);
        Restore(redo: false, 30);
        Restore(redo: false, 20);
        Restore(redo: false, 10);
        Restore(redo: true, 20);
        Restore(redo: true, 30);
        Restore(redo: true, 40);
        Assert.Equal(3, history.UndoCount);

        void Move(double position)
        {
            history.Begin("Move Guide", document, null);
            Assert.True(GuideEdits.Move(document, id, position));
            history.End(document, null);
        }

        void Restore(bool redo, double expected)
        {
            var snapshot = redo ? history.Redo() : history.Undo();
            Assert.NotNull(snapshot);
            document.Adopt(snapshot.Value.Document!);
            var guide = Assert.Single(document.Guides);
            Assert.Equal(id, guide.ID);
            Assert.Equal(axis, guide.Axis);
            Assert.Equal(expected, guide.Position);
        }
    }
}
