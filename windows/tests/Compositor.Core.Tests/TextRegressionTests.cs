using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Tests;

public sealed class TextRegressionTests
{
    private static LayerTextStyle Style(string content) => new() { Content = content, FontSize = 24 };

    [Fact]
    public void RasterEditedTextCannotOverwriteItsNewPixelsWithOldMetadata()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 200, 100);
        var id = TextEdits.Add(document, Style("Original"), new SKPoint(10, 10))!.Value;
        var layer = document.Layers.Single();
        var changed = new SKBitmap(Bitmaps.ColorInfo(4, 4));
        changed.Erase(SKColors.Red);
        layer.Asset = ImportedImage.Create(changed, "Painted");
        Assert.Null(layer.LiveText);
        Assert.Throws<ArgumentException>(() => TextSession.Editing(layer));
        Assert.False(TextEdits.SetStyle(document, id, Style("Replacement")));
        Assert.Same(changed, layer.Asset.Image);
    }

    [Fact]
    public void CancelRestoresTheExactOriginalAndPreservesRedo()
    {
        var document = new CanvasDocument(Guid.NewGuid(), 200, 100);
        var id = TextEdits.Add(document, Style("Original"), new SKPoint(10, 10))!.Value;
        var history = new DocumentHistory();
        history.Begin("Rename", document, id);
        document.Layers[0].Name = "Changed";
        history.End(document, id);
        document = history.Undo()!.Value.Document!;
        try
        {
            var layer = document.Layers.Single();
            var before = layer.Clone();
            history.Begin("Text", document, id);
            var session = TextSession.Editing(layer);
            Assert.True(session.Type(document, " added"));
            Assert.True(session.Cancel(document));
            history.End(document, id);
            Assert.Same(before.Asset, layer.Asset);
            Assert.Same(before.Text, layer.Text);
            Assert.Equal(before.Transform, layer.Transform);
            Assert.Equal(before.Name, layer.Name);
            Assert.False(history.IsModified);
            Assert.True(history.CanRedo);
        }
        finally { document.Dispose(); }
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    [InlineData("\u000b")]
    [InlineData("\u000c")]
    [InlineData("\u0085")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public void NavigationUsesEverySupportedNewline(string newline)
    {
        var session = TextSession.New(Style("ab" + newline + "cd"), default);
        Assert.True(session.MoveCaret(TextSession.TextMove.Home));
        Assert.Equal(2 + newline.Length, session.CaretIndex);
        Assert.True(session.MoveCaret(TextSession.TextMove.Up));
        Assert.Equal(0, session.CaretIndex);
        Assert.True(session.MoveCaret(TextSession.TextMove.End));
        Assert.Equal(2, session.CaretIndex);
        Assert.True(session.MoveCaret(TextSession.TextMove.Down));
        Assert.Equal(session.Content.Length, session.CaretIndex);
    }

    [Fact]
    public void ClickingTheSecondRowNeverChoosesAnEquallyCloseFirstRow()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 200, 100);
        var session = TextSession.New(Style("same\nsame"), new SKPoint(10, 10));
        var at = TextEdits.Caret(session.Style, 6);
        Assert.True(session.PlaceCaret(document, new SKPoint(10 + at.X, 10 + at.Y)));
        Assert.Equal(6, session.CaretIndex);
    }

    [Fact]
    public void SpliceShiftsUtf16RunsAndInheritsReplacementStyle()
    {
        var original = Style("A😀BC");
        original.ColorRuns = [new LayerTextColorRun { Location = 1, Length = 3, Red = 1 }];
        original.FontRuns = [new LayerTextFontRun { Location = 1, Length = 3, FontName = "Arial" }];
        var inserted = TextStyleEdits.Splice(original, 3, 0, "XY");
        Assert.Equal("A😀XYBC", inserted.Content);
        Assert.NotNull(inserted.ColorRuns);
        Assert.Equal(5, Assert.Single(inserted.ColorRuns).Length);
        Assert.NotNull(inserted.FontRuns);
        Assert.Equal(5, Assert.Single(inserted.FontRuns).Length);
        var deleted = TextStyleEdits.Splice(inserted, 1, 5, "Q");
        Assert.Equal("AQC", deleted.Content);
        Assert.NotNull(deleted.ColorRuns);
        Assert.Equal(1, Assert.Single(deleted.ColorRuns).Length);
        Assert.NotNull(deleted.FontRuns);
        Assert.Equal(1, Assert.Single(deleted.FontRuns).Location);
        Assert.True(deleted.IsValid);
        Assert.Equal("A😀BC", original.Content);
        Assert.Equal(3, original.ColorRuns[0].Length);
    }

    [Fact]
    public void DeletingEverythingRemovesEmptyRunsAndAdjacentRunsMerge()
    {
        var style = Style("ABA");
        style.ColorRuns = [new LayerTextColorRun { Location = 0, Length = 1, Red = 1 },
            new LayerTextColorRun { Location = 2, Length = 1, Red = 1 }];
        var joined = TextStyleEdits.Splice(style, 1, 1, "");
        Assert.NotNull(joined.ColorRuns);
        Assert.Equal(2, Assert.Single(joined.ColorRuns).Length);
        var empty = TextStyleEdits.Splice(joined, 0, 2, "");
        Assert.Null(empty.ColorRuns);
        Assert.True(empty.IsValid);
    }
}
