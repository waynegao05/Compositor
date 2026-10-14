using Compositor.Core.Document;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Tests;

public class SelectionResizeRegressionTests
{
    [Theory]
    [InlineData("image", 24, 24, 16, 16)]
    [InlineData("canvas", 28, 28, 8, 8)]
    [InlineData("crop", 4, 4, 8, 8)]
    public void SelectionFollowsArtworkAndHistoryKeepsTheOriginalOutline(string operation,
        int x, int y, int width, int height)
    {
        using var document = LayerPlacement.NewDocument(32, 32)!;
        var layer = document.Layers[0];
        Assert.True(FillEdits.Fill(document, layer.ID, SKColors.Red));
        document.Selection = DocumentSelection.Rectangular(SKRectI.Create(12, 12, 8, 8), 32, 32);
        var original = document.Selection;
        var expected = SKRect.Create(x, y, width, height);
        var history = new DocumentHistory();
        history.Begin("Resize", document, layer.ID);
        Assert.True(operation switch
        {
            "image" => ImageEdits.Resize(document, 64, 64, 72),
            "canvas" => CanvasEdits.Resize(document, 64, 64),
            _ => CanvasEdits.Crop(document, SKRectI.Create(8, 8, 24, 24)),
        });
        history.End(document, layer.ID);
        Assert.NotSame(original.Path, document.Selection.Path);
        Assert.Equal(SKRect.Create(12, 12, 8, 8), original.Path!.Bounds);
        Assert.Equal(expected, document.Selection.Path!.Bounds);
        Assert.True(FillEdits.Fill(document, layer.ID, SKColors.Blue));
        using var rendered = DocumentRenderer.Render(document);
        Assert.Equal(SKColors.Blue, rendered.GetPixel((int)expected.MidX, (int)expected.MidY));

        var undo = history.Undo();
        Assert.NotNull(undo);
        Assert.Same(original, undo.Value.Document!.Selection);
        Assert.Equal(SKRect.Create(12, 12, 8, 8), undo.Value.Document.Selection.Path!.Bounds);
        var redo = history.Redo();
        Assert.NotNull(redo);
        Assert.Equal(expected, redo.Value.Document!.Selection.Path!.Bounds);
    }

    [Fact]
    public void NonuniformResizeScalesTheOutlineAndKeepsFeatherInDocumentPixels()
    {
        using var document = LayerPlacement.NewDocument(32, 32)!;
        document.Selection = DocumentSelection.Rectangular(SKRectI.Create(4, 8, 8, 16), 32, 32).WithFeather(3);
        Assert.True(ImageEdits.Resize(document, 64, 16, 72));
        Assert.Equal(SKRect.Create(8, 4, 16, 8), document.Selection.Path!.Bounds);
        Assert.Equal(3, document.Selection.Feather);
        Assert.True(document.Selection.Antialiased);
    }

    [Fact]
    public void NoSelectionAndResolutionOnlyChangesKeepTheSameSelection()
    {
        using var document = LayerPlacement.NewDocument(32, 32)!;
        Assert.True(ImageEdits.Resize(document, 64, 64, 72));
        Assert.Same(DocumentSelection.All, document.Selection);
        Assert.True(CanvasEdits.Crop(document, SKRectI.Create(4, 4, 32, 32)));
        Assert.Same(DocumentSelection.All, document.Selection);
        var selected = DocumentSelection.Rectangular(SKRectI.Create(4, 4, 8, 8), 32, 32);
        document.Selection = selected;
        Assert.True(ImageEdits.Resize(document, 32, 32, 300));
        Assert.Same(selected, document.Selection);
        Assert.False(ImageEdits.Resize(document, 0, 32, 300));
        Assert.Same(selected, document.Selection);
    }
}
