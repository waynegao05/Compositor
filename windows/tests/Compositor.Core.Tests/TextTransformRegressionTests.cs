using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

public sealed class TextTransformRegressionTests
{
    private static LayerTextStyle Style(string content = "ABCDEFGHIJ") => new()
    { Content = content, FontName = "Arial", FontSize = 24 };

    private static ImageLayer Add(CanvasDocument document, string content = "ABCDEFGHIJ")
    {
        var id = TextEdits.Add(document, Style(content), new SKPoint(100, 100));
        Assert.NotNull(id);
        return document.Layers.Single(layer => layer.ID == id);
    }

    [Fact]
    public void TextHitTestingUsesTheFrontmostVisibleLayerAndItsAncestors()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 500, 500);
        var bottom = Add(document, "BOTTOM");
        var top = Add(document, "TOPTOP");
        var point = new SKPoint(120, 120);
        Assert.Same(top, TextEdits.HitTest(document, point));
        top.IsVisible = false;
        Assert.Same(bottom, TextEdits.HitTest(document, point));
        bottom.IsVisible = false;
        Assert.Null(TextEdits.HitTest(document, point));
        top.IsVisible = true;
        var group = new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 500, 500), "Folder")
        { IsGroup = true, IsVisible = false };
        document.Layers.Add(group);
        top.ParentID = group.ID;
        Assert.Null(TextEdits.HitTest(document, point));
        group.IsVisible = true;
        group.Opacity = 0;
        Assert.Null(TextEdits.HitTest(document, point));
        group.Opacity = 0.5;
        Assert.Same(top, TextEdits.HitTest(document, point));
        top.Opacity = 0;
        Assert.Null(TextEdits.HitTest(document, point));
    }

    [Fact]
    public void TextHitTestingFollowsTheRenderedOrderAcrossFolders()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 500, 500);
        var back = new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 500, 500), "Back") { IsGroup = true };
        var front = new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 500, 500), "Front") { IsGroup = true };
        document.Layers.Add(back);
        document.Layers.Add(front);
        var frontText = Add(document, "FRONT"); frontText.ParentID = front.ID;
        var backText = Add(document, "BACK"); backText.ParentID = back.ID;
        Assert.Same(frontText, TextEdits.HitTest(document, new SKPoint(120, 120)));
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(0.5, 1.5)]
    public void EditingPreservesScaleRotationFlipsAndUndo(double scaleX, double scaleY)
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 500, 500);
        var layer = Add(document);
        layer.Transform = layer.Transform with
        { Width = layer.Asset!.Width * scaleX, Height = layer.Asset.Height * scaleY, Rotation = 37, FlipX = true, FlipY = true };
        var original = layer.Clone();
        var history = new DocumentHistory();
        history.Begin("Edit Text", document, layer.ID);
        var session = TextSession.Editing(layer);
        Assert.True(session.Type(document, "中文"));
        session.Commit(document);
        history.End(document, layer.ID);
        Assert.Equal(scaleX, layer.Transform.Width / layer.Asset!.Width, 6);
        Assert.Equal(scaleY, layer.Transform.Height / layer.Asset.Height, 6);
        Assert.Equal(original.Transform.CenterX, layer.Transform.CenterX, 6);
        Assert.Equal(original.Transform.CenterY, layer.Transform.CenterY, 6);
        Assert.Equal(37, layer.Transform.Rotation);
        Assert.True(layer.Transform.FlipX && layer.Transform.FlipY);
        var edited = layer.Clone();
        document.Adopt(history.Undo()!.Value.Document!);
        Assert.True(original.SameAs(document.Layers.Single()));
        document.Adopt(history.Redo()!.Value.Document!);
        Assert.True(edited.SameAs(document.Layers.Single()));
    }

    [Fact]
    public void RejectedScaledSizeLeavesTheSessionAndItsPixelsUnchanged()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 500, 500);
        var layer = Add(document, "A");
        layer.Transform = layer.Transform with { Width = 300_000 };
        var original = layer.Clone();
        var session = TextSession.Editing(layer);
        Assert.False(session.Type(document, "BCDEFG"));
        Assert.Equal("A", session.Content);
        Assert.True(original.SameAs(layer));
    }

    [Theory]
    [InlineData(0, false, false, 1, 1)]
    [InlineData(90, false, false, 1, 1)]
    [InlineData(45, true, false, 1.5, 2)]
    [InlineData(180, false, true, 2, 0.5)]
    [InlineData(270, true, true, 0.7, 1.3)]
    public void ClickingTransformedTextAndItsCaretUseTheSameMapping(double angle, bool flipX, bool flipY,
        double scaleX, double scaleY)
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 500, 500);
        var layer = Add(document);
        layer.Transform = layer.Transform with
        { Width = layer.Asset!.Width * scaleX, Height = layer.Asset.Height * scaleY, Rotation = angle, FlipX = flipX, FlipY = flipY };
        var session = TextSession.Editing(layer);
        var local = TextEdits.Caret(session.Style, 3);
        var click = Map(layer, local);
        Assert.True(session.Contains(document, click));
        Assert.True(session.PlaceCaret(document, click));
        Assert.Equal(3, session.CaretIndex);
        var (from, to) = session.CaretLine(document);
        var top = Map(layer, new SKPoint(local.X, local.Y - 24 * 0.8f));
        var bottom = Map(layer, new SKPoint(local.X, local.Y + 24 * 0.2f));
        Assert.Equal(top.X, from.X, 3); Assert.Equal(top.Y, from.Y, 3);
        Assert.Equal(bottom.X, to.X, 3); Assert.Equal(bottom.Y, to.Y, 3);
        Assert.True(session.Caret(document).Width >= 1 && session.Caret(document).Height >= 1);
        Assert.True(session.Type(document, "中"));
        Assert.Equal("ABC中DEFGHIJ", session.Content);
    }

    [Fact]
    public void OnePassHitTestingPreservesUnicodeBoundariesAndMultilineRows()
    {
        var style = Style("A😀e\u0301中\r\nBC中文");
        foreach (var at in new[] { 0, 1, 3, 5, 6, 8, 9, 10, 11, 12 })
            Assert.Equal(at, TextEdits.CaretAt(style, TextEdits.Caret(style, at)));
        var paragraph = Style(string.Concat(Enumerable.Repeat("测试 AB 文字 ", 200)));
        paragraph.BoxSize = new JsonSize { Width = 800, Height = 5000 };
        Assert.Equal(1, TextEdits.CaretAt(paragraph, TextEdits.Caret(paragraph, 1)));
    }

    private static SKPoint Map(ImageLayer layer, SKPoint local) => layer.Transform.Point(
        layer.Transform.FlipX ? 1 - local.X / layer.Asset!.Width : local.X / layer.Asset!.Width,
        layer.Transform.FlipY ? 1 - local.Y / layer.Asset.Height : local.Y / layer.Asset.Height);
}
