using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// Typing text on the canvas: the layer is made on the first character and drawn again as the words grow,
/// the whole session is one change, and letting it go puts the layer back the way it was.
/// </summary>
public class TextSessionTests
{
    private static LayerTextStyle Style(string content = "") => new()
    {
        Content = content,
        FontName = "Arial",
        FontSize = 48,
        Red = 0,
        Green = 0,
        Blue = 0,
    };

    [Fact]
    public void TheCaretStartsAfterWhatIsAlreadyThere()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        // A session that is typed into rather than clicked into adds to the end, as it always has.
        var fresh = TextSession.New(Style(), new SKPoint(0, 0));
        Assert.Equal(0, fresh.CaretIndex);
        var layer = LayerWith(document, "Hello");
        var joined = TextSession.Editing(layer);
        Assert.Equal("Hello".Length, joined.CaretIndex);
    }

    [Fact]
    public void TypingInTheMiddleOfTheWordsPutsTheCharactersThere()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.Editing(LayerWith(document, "Hllo"));
        Assert.Equal(4, session.CaretIndex);
        // Back to just after the H, and type the e that was left out.
        Assert.True(session.MoveCaret(TextSession.TextMove.Left));
        Assert.True(session.MoveCaret(TextSession.TextMove.Left));
        Assert.True(session.MoveCaret(TextSession.TextMove.Left));
        Assert.Equal(1, session.CaretIndex);
        Assert.True(session.Type(document, "e"));
        Assert.Equal("Hello", session.Content);
        Assert.Equal(2, session.CaretIndex);
        Assert.Equal("Hello", document.Layers.Single().LiveText!.Content);
    }

    [Fact]
    public void BackspaceAndDeleteTakeTheCharactersEitherSideOfTheCaret()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.Editing(LayerWith(document, "Helo"));
        // The caret sits at the end: backspace takes the o.
        Assert.True(session.Backspace(document));
        Assert.Equal("Hel", session.Content);
        Assert.Equal(3, session.CaretIndex);
        // Delete has nothing after it to take.
        Assert.False(session.Delete(document));
        // Move back one and delete takes the l that is now in front of the caret.
        Assert.True(session.MoveCaret(TextSession.TextMove.Left));
        Assert.Equal(2, session.CaretIndex);
        Assert.True(session.Delete(document));
        Assert.Equal("He", session.Content);
        Assert.Equal(2, session.CaretIndex);
        // Nothing happens at the start of the words.
        Assert.True(session.MoveCaret(TextSession.TextMove.Home));
        Assert.False(session.Backspace(document));
        Assert.Equal("He", session.Content);
    }

    [Fact]
    public void TheCaretWalksTheLinesAndTheEndsOfThem()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.Editing(LayerWith(document, "one" + Environment.NewLine + "three"));
        // The first line is three characters, and the second starts after the newline whatever it is made of.
        var firstLineEnd = 3;
        var secondStart = firstLineEnd + Environment.NewLine.Length;
        // Up from the end of the second line lands at the end of the shorter first line.
        Assert.True(session.MoveCaret(TextSession.TextMove.Up));
        Assert.Equal(firstLineEnd, session.CaretIndex);
        Assert.True(session.MoveCaret(TextSession.TextMove.Home));
        Assert.Equal(0, session.CaretIndex);
        Assert.True(session.MoveCaret(TextSession.TextMove.Down));
        Assert.Equal(secondStart, session.CaretIndex);
        Assert.True(session.MoveCaret(TextSession.TextMove.End));
        Assert.Equal(session.Content.Length, session.CaretIndex);
        // The ends of the words are as far as each direction goes.
        Assert.False(session.MoveCaret(TextSession.TextMove.Right));
        // Home goes to the start of the line the caret is on, not to the start of everything.
        Assert.True(session.MoveCaret(TextSession.TextMove.Home));
        Assert.Equal(secondStart, session.CaretIndex);
        // From there, up is the same place along the line above — which is its start, and the very beginning.
        Assert.True(session.MoveCaret(TextSession.TextMove.Up));
        Assert.Equal(0, session.CaretIndex);
        Assert.False(session.MoveCaret(TextSession.TextMove.Left));
        Assert.False(session.MoveCaret(TextSession.TextMove.Up));
    }

    [Fact]
    public void AClickInTheMiddleOfTheWordsPutsTheCaretThere()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.Editing(LayerWith(document, "Hello"));
        var box = session.Box(document);
        // A click a little way in from the left of the text, on its own line.
        var caret = TextEdits.Caret(session.Style, 2);
        var point = new SKPoint(box.Left + caret.X + 1, box.Top + caret.Y);
        Assert.True(session.PlaceCaret(document, point));
        Assert.Equal(2, session.CaretIndex);
    }

    /// <summary>A text layer in the document, so a session can join it.</summary>
    private static ImageLayer LayerWith(CanvasDocument document, string content)
    {
        var style = Style(content);
        var id = TextEdits.Add(document, style, new SKPoint(30, 40))!.Value;
        var layer = document.Layers.First(entry => entry.ID == id);
        Assert.NotNull(layer.Text);
        return layer;
    }

    [Fact]
    public void NothingIsAddedUntilACharacterIsTyped()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.New(Style(), new SKPoint(20, 30));
        Assert.Null(session.LayerID);
        Assert.Empty(document.Layers);

        Assert.True(session.Type(document, "H"));
        Assert.NotNull(session.LayerID);
        Assert.Single(document.Layers);
        Assert.Equal("H", document.Layers.Single().LiveText!.Content);
        Assert.Equal(20, document.Layers.Single().Transform.X);
        Assert.Equal(30, document.Layers.Single().Transform.Y);
    }

    [Fact]
    public void EveryLetterIsDrawnAsItArrives()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.New(Style(), new SKPoint(0, 0));
        Assert.True(session.Type(document, "H"));
        var wasNarrow = document.Layers.Single().Asset!.Width;
        Assert.True(session.Type(document, "i"));
        Assert.Equal("Hi", session.Content);
        Assert.Equal("Hi", document.Layers.Single().LiveText!.Content);
        // A second letter makes the layer wider, so what is on the canvas follows what was typed.
        Assert.True(document.Layers.Single().Asset!.Width > wasNarrow);
    }

    [Fact]
    public void ANewlineIsTypedLikeAnyOtherCharacter()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.New(Style(), new SKPoint(0, 0));
        session.Type(document, "one");
        session.Type(document, "\n");
        session.Type(document, "two");
        Assert.Equal("one\ntwo", session.Content);
        // Two lines are taller than one.
        Assert.True(document.Layers.Single().Asset!.Height > TextEdits.BoxSize(Style("one")).Height);
    }

    [Fact]
    public void TheDeleteKeyTakesTheLastCharacterBack()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.New(Style(), new SKPoint(0, 0));
        session.Type(document, "Hi");
        Assert.True(session.Backspace(document));
        Assert.Equal("H", session.Content);
        Assert.Equal("H", document.Layers.Single().LiveText!.Content);
        Assert.True(session.Backspace(document));
        Assert.Equal("", session.Content);
        // Nothing left to take back.
        Assert.False(session.Backspace(document));
    }

    [Fact]
    public void TheDeleteKeyTakesAWholeSurrogatePair()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.New(Style(), new SKPoint(0, 0));
        session.Type(document, "\U0001F600");
        Assert.True(session.Backspace(document));
        Assert.Equal("", session.Content);
        Assert.False(session.Backspace(document));
    }

    [Theory]
    [InlineData("e\u0301")]
    [InlineData("\U0001F469\u200D\U0001F52C")]
    [InlineData("\U0001F44D\U0001F3FD")]
    [InlineData("\U00020000")]
    public void NavigationAndDeletionKeepWholeTextElements(string element)
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.New(Style("A" + element + "B"), new SKPoint(0, 0));
        Assert.True(session.MoveCaret(TextSession.TextMove.Left));
        Assert.Equal(1 + element.Length, session.CaretIndex);
        Assert.True(session.MoveCaret(TextSession.TextMove.Left));
        Assert.Equal(1, session.CaretIndex);
        Assert.True(session.MoveCaret(TextSession.TextMove.Right));
        Assert.Equal(1 + element.Length, session.CaretIndex);
        Assert.True(session.Backspace(document));
        Assert.Equal("AB", session.Content);
        Assert.Equal(1, session.CaretIndex);

        Assert.True(session.Type(document, element));
        Assert.True(session.MoveCaret(TextSession.TextMove.Left));
        Assert.True(session.Delete(document));
        Assert.Equal("AB", session.Content);
    }

    [Fact]
    public void VerticalNavigationCountsTextElementsWithoutSplittingThem()
    {
        var session = TextSession.New(Style("A\U0001F469\u200D\U0001F52CZ\nabe\u0301"), new SKPoint(0, 0));
        Assert.True(session.MoveCaret(TextSession.TextMove.Left));
        Assert.True(session.MoveCaret(TextSession.TextMove.Up));
        Assert.Equal("A\U0001F469\u200D\U0001F52C".Length, session.CaretIndex);
        Assert.True(session.MoveCaret(TextSession.TextMove.Down));
        Assert.Equal(session.Content.Length - "e\u0301".Length, session.CaretIndex);
    }

    [Fact]
    public void EmptyTextAndLeadingNewlinesHaveValidHomePositions()
    {
        var empty = TextSession.New(Style(), new SKPoint(0, 0));
        Assert.False(empty.MoveCaret(TextSession.TextMove.Home));
        Assert.False(empty.MoveCaret(TextSession.TextMove.Up));
        var leadingNewline = TextSession.New(Style("\nA"), new SKPoint(0, 0));
        Assert.True(leadingNewline.MoveCaret(TextSession.TextMove.Up));
        Assert.Equal(0, leadingNewline.CaretIndex);
        Assert.False(leadingNewline.MoveCaret(TextSession.TextMove.Home));
    }

    [Fact]
    public void CommittingKeepsTheWordsAndAnswersWithTheLayer()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.New(Style(), new SKPoint(10, 10));
        session.Type(document, "Kept");
        var kept = session.Commit(document);
        Assert.NotNull(kept);
        Assert.Single(document.Layers);
        Assert.Equal(kept, document.Layers.Single().ID);
        Assert.Equal("Kept", document.Layers.Single().Name);
    }

    [Fact]
    public void CommittingNothingTypedTakesAwayWhatWasStarted()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.New(Style(), new SKPoint(10, 10));
        session.Type(document, "   ");
        Assert.Single(document.Layers);
        // Spaces alone are not worth a layer.
        Assert.Null(session.Commit(document));
        Assert.Empty(document.Layers);
    }

    [Fact]
    public void LettingGoOfNewTextTakesAwayWhatWasStarted()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.New(Style(), new SKPoint(10, 10));
        session.Type(document, "Gone");
        Assert.True(session.Cancel(document));
        Assert.Empty(document.Layers);
    }

    [Fact]
    public void EditingALayerPutsItsOldWordsBackWhenLetGo()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        Assert.NotNull(TextEdits.Add(document, Style("Before"), new SKPoint(10, 10)));
        var layer = document.Layers.Single();

        var session = TextSession.Editing(layer);
        Assert.Equal("Before", session.Content);
        Assert.Equal(layer.ID, session.LayerID);
        session.Type(document, " and after");
        Assert.Equal("Before and after", layer.LiveText!.Content);

        Assert.True(session.Cancel(document));
        Assert.Equal("Before", layer.LiveText!.Content);
        Assert.Equal("Before", layer.Name);
    }

    [Fact]
    public void EditingALayerKeepsTheNewWordsWhenTheyAreFinished()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        Assert.NotNull(TextEdits.Add(document, Style("Before"), new SKPoint(10, 10)));
        var layer = document.Layers.Single();
        var session = TextSession.Editing(layer);
        session.Type(document, " more");
        Assert.Equal(layer.ID, session.Commit(document));
        Assert.Equal("Before more", layer.LiveText!.Content);
        Assert.Equal("Before more", layer.Name);
    }

    [Fact]
    public void EditingIsRefusedOnALayerThatIsNotText()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(20, 20));
        bitmap.Erase(SKColors.Red);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Plain"),
            new Model.LayerTransform(0, 0, 20, 20), "Plain");
        document.Layers.Add(layer);
        Assert.Throws<ArgumentException>(() => TextSession.Editing(layer));
    }

    [Fact]
    public void TheCaretSitsWhereTheTextEndsAndGrowsWithIt()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.New(Style(), new SKPoint(50, 60));
        // Before anything is typed the caret is inside the padding of where the text will go.
        var empty = session.Caret(document);
        Assert.True(empty.Left >= 50, $"the caret is at {empty.Left}");
        Assert.True(empty.Top < 60 + Style().FontSize, $"the caret is at {empty.Top}");
        Assert.True(empty.Height > 1);

        session.Type(document, "Hi");
        var typed = session.Caret(document);
        // It has moved right with the letters, and down with the type's own size.
        Assert.True(typed.Left > empty.Left, $"{typed.Left} is not right of {empty.Left}");
        Assert.True(typed.Height > empty.Height / 2);
    }

    [Fact]
    public void TheBoxOfASessionWithNoLayerIsWhereTheTextWouldGo()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.New(Style(), new SKPoint(30, 40));
        var box = session.Box(document);
        Assert.Equal(30, box.Left);
        Assert.Equal(40, box.Top);
        Assert.True(box.Width >= TextEdits.LeastSide);
        // A click inside it stays in the session; one outside it does not.
        Assert.True(session.Contains(document, new SKPoint(35, 45)));
        Assert.False(session.Contains(document, new SKPoint(5, 5)));
    }

    [Fact]
    public void TheBoxFollowsTheLayerOnceThereIsOne()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var session = TextSession.New(Style(), new SKPoint(30, 40));
        session.Type(document, "Hi");
        var layer = document.Layers.Single();
        layer.Transform = layer.Transform with { X = 100, Y = 120 };
        var box = session.Box(document);
        Assert.Equal(100, box.Left);
        Assert.Equal(120, box.Top);
        Assert.True(session.Contains(document, new SKPoint(105, 125)));
    }

    [Fact]
    public void TextTooBigToOneSurfaceIsRefusedAndTheSessionIsLeftAsItWas()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 100, 100);
        var style = Style();
        style.BoxSize = new JsonSize { Width = 20_000, Height = 20_000 };
        var session = TextSession.New(style, new SKPoint(0, 0));
        Assert.False(session.Type(document, "H"));
        Assert.Equal("", session.Content);
        Assert.Empty(document.Layers);
    }
}
