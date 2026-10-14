using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Tests;

public class BlankFillRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstFillCreatesPixelsInsideTheSelectionAndUndoesBackToBlank(bool gradient)
    {
        using var document = LayerPlacement.NewDocument(32, 32)!;
        var layer = document.Layers[0];
        document.Selection = DocumentSelection.Rectangular(SKRectI.Create(8, 8, 16, 16), 32, 32);
        var history = new DocumentHistory();
        history.Begin("Fill", document, layer.ID);
        Assert.True(Fill(document, layer.ID, gradient));
        history.End(document, layer.ID);

        Assert.NotNull(layer.Asset);
        Assert.Equal(SKColors.Blue, layer.Asset.Image.GetPixel(16, 16));
        Assert.Equal(0, layer.Asset.Image.GetPixel(2, 2).Alpha);
        Assert.Equal(1, history.UndoCount);
        var before = history.Undo();
        Assert.NotNull(before);
        Assert.Null(before.Value.Document!.Layers[0].Asset);
        var after = history.Redo();
        Assert.NotNull(after);
        Assert.Equal(SKColors.Blue, after.Value.Document!.Layers[0].Asset!.Image.GetPixel(16, 16));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ASelectionThatTouchesNothingDoesNotAllocateBlankLayerPixels(bool gradient, bool outside)
    {
        using var document = LayerPlacement.NewDocument(32, 32)!;
        var layer = document.Layers[0];
        document.Selection = outside
            ? DocumentSelection.Rectangular(SKRectI.Create(8, 8, 8, 8), 32, 32).Translated(100, 100)
            : DocumentSelection.FromPath(new SKPath());
        var history = new DocumentHistory();
        history.Begin("Fill", document, layer.ID);
        Assert.False(Fill(document, layer.ID, gradient));
        history.End(document, layer.ID);
        Assert.Null(layer.Asset);
        Assert.False(history.IsModified);
        Assert.False(history.CanUndo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FolderAndOversizedBlankLayerAreNotAllocated(bool gradient)
    {
        using var document = LayerPlacement.NewDocument(32, 32)!;
        var layer = document.Layers[0];
        layer.IsGroup = true;
        Assert.False(Fill(document, layer.ID, gradient));
        Assert.Null(layer.Asset);
        layer.IsGroup = false;
        layer.Transform = layer.Transform with { Width = DocumentLimits.MaxSide + 1 };
        Assert.False(Fill(document, layer.ID, gradient));
        Assert.Null(layer.Asset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FillingAnAdjustmentLayerLeavesTheDocumentAndHistoryUnchanged(bool gradient)
    {
        using var document = LayerPlacement.NewDocument(32, 32)!;
        var layer = document.Layers[0];
        layer.Adjustment = new LayerAdjustment { Kind = AdjustmentKind.Invert };
        using var before = document.Clone();
        var history = new DocumentHistory();
        history.Begin("Fill", document, layer.ID);
        Assert.False(Fill(document, layer.ID, gradient));
        history.End(document, layer.ID);
        Assert.Null(layer.Asset);
        Assert.True(document.SameAs(before));
        Assert.False(history.IsModified);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void FirstGradientHasDifferentEndpointColors()
    {
        using var document = LayerPlacement.NewDocument(32, 32)!;
        var layer = document.Layers[0];
        Assert.True(GradientEdits.Fill(document, layer.ID, false, new SKPoint(0, 0), new SKPoint(32, 0),
            SKColors.Red, SKColors.Blue, 1, GradientShape.Linear));
        Assert.NotNull(layer.Asset);
        var left = layer.Asset.Image.GetPixel(0, 16);
        var right = layer.Asset.Image.GetPixel(31, 16);
        Assert.True(left.Red > left.Blue);
        Assert.True(right.Blue > right.Red);
        Assert.Equal(255, left.Alpha);
        Assert.Equal(255, right.Alpha);
    }

    private static bool Fill(CanvasDocument document, Guid id, bool gradient) => gradient
        ? GradientEdits.Fill(document, id, false, new SKPoint(0, 0), new SKPoint(32, 0),
            SKColors.Blue, SKColors.Blue, 1, GradientShape.Linear)
        : FillEdits.Fill(document, id, SKColors.Blue);
}
