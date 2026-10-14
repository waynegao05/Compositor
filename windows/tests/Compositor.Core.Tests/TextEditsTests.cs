using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>
/// The text layer: how big it comes out, what it draws, and that a text layer stays editable — the pixels
/// and the style are the same bitmap, so the style can be changed and the layer drawn again.
/// </summary>
public class TextEditsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CompositorText-" + Guid.NewGuid().ToString("N"));

    public TextEditsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static LayerTextStyle Style(string content, double size = 72) => new()
    {
        Content = content,
        FontName = "Arial",
        FontSize = size,
        Red = 0,
        Green = 0,
        Blue = 0,
    };

    /// <summary>How many pixels hold ink, and where the ink starts, so a glyph's shape need not be known.</summary>
    private static (int Count, int Left, int Right, int Top, int Bottom) Ink(SKBitmap image, SKColor? colour = null)
    {
        var count = 0;
        var left = image.Width;
        var right = -1;
        var top = image.Height;
        var bottom = -1;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var pixel = image.GetPixel(x, y);
                if (pixel.Alpha == 0) continue;
                if (colour is { } wanted && pixel.Red < 200 && wanted.Red >= 200) continue;
                count++;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }
        return (count, left, right, top, bottom);
    }

    [Fact]
    public void ATextLayerIsNamedAfterItsFirstWords()
    {
        Assert.Equal("Hello there", TextEdits.LayerName("Hello   there"));
        Assert.Equal("Hello there world", TextEdits.LayerName("Hello\nthere\r\nworld"));
        Assert.Equal("Text", TextEdits.LayerName("   \n  "));
        Assert.Equal(new string('x', 40), TextEdits.LayerName(new string('x', 60)));
    }

    [Fact]
    public void PointTextIsAsBigAsItMeasures()
    {
        var small = TextEdits.BoxSize(Style("Hello", 36));
        var large = TextEdits.BoxSize(Style("Hello", 72));
        Assert.True(large.Width > small.Width, $"{large.Width} is not wider than {small.Width}");
        Assert.True(large.Height > small.Height, $"{large.Height} is not taller than {small.Height}");

        // The room around the text is part of the box, so even an empty line has the padding.
        var empty = TextEdits.BoxSize(Style("", 72));
        Assert.True(empty.Width >= TextEdits.LeastSide, $"an empty box is {empty.Width} wide");
        Assert.True(empty.Height >= TextEdits.LeastSide, $"an empty box is {empty.Height} tall");
    }

    [Fact]
    public void ASecondLineMakesTheLayerTaller()
    {
        var one = TextEdits.BoxSize(Style("Hello"));
        var two = TextEdits.BoxSize(Style("Hello\nHello"));
        Assert.True(two.Height > one.Height, $"{two.Height} is not taller than {one.Height}");
        Assert.Equal(one.Width, two.Width);
    }

    [Fact]
    public void AParagraphBoxIsTheBoxItWasGiven()
    {
        var style = Style("Hello");
        style.BoxSize = new JsonSize { Width = 300, Height = 200 };
        Assert.Equal((300, 200), TextEdits.BoxSize(style));

        // A box too small to hold a caret is held to the least side.
        style.BoxSize = new JsonSize { Width = 4, Height = 4 };
        Assert.Equal((TextEdits.LeastSide, TextEdits.LeastSide), TextEdits.BoxSize(style));
    }

    [Fact]
    public void TheTextIsDrawnInItsOwnColourWithRoomAroundIt()
    {
        using var image = TextEdits.Image(Style("Hello"))!;
        Assert.Equal(SKAlphaType.Unpremul, image.AlphaType);
        var ink = Ink(image);
        Assert.True(ink.Count > 0, "nothing was drawn");
        // The padding is left clear, and the ink is inside the box.
        Assert.True(ink.Left >= TextEdits.Padding - 1, $"ink starts at {ink.Left}");
        Assert.True(ink.Right < image.Width, $"ink reaches {ink.Right} of {image.Width}");
        Assert.True(ink.Top >= 1, $"ink starts at {ink.Top}");
        Assert.True(ink.Bottom < image.Height);
    }

    [Fact]
    public void AColourRunIsDrawnInItsOwnColourAndToTheRightOfWhatPrecedesIt()
    {
        using var image = TextEdits.Image(Style("XXXX"))!;
        var oneLine = Ink(image);

        var style = Style("XXXX");
        style.ColorRuns = [new LayerTextColorRun { Location = 2, Length = 2, Red = 1, Green = 0, Blue = 0 }];
        using var coloured = TextEdits.Image(style)!;
        var (count, left, _, _, _) = Ink(coloured, SKColors.Red);
        Assert.True(count > 0, "the red run was not drawn");
        // The red run is the right-hand half, so its ink starts past the middle of the whole line.
        Assert.True(left > oneLine.Left + (oneLine.Right - oneLine.Left) / 2, $"the red run starts at {left}");
    }

    [Fact]
    public void TrackingWidensTheLine()
    {
        var plain = TextEdits.BoxSize(Style("Hello"));
        var style = Style("Hello");
        style.Tracking = 12;
        Assert.True(TextEdits.BoxSize(style).Width > plain.Width);
    }

    [Fact]
    public void CentreingMovesTheTextInwards()
    {
        var left = Style("Hi");
        left.BoxSize = new JsonSize { Width = 400, Height = 120 };
        left.Alignment = TextAlignment.Left;
        using var leftImage = TextEdits.Image(left)!;

        var centred = Style("Hi");
        centred.BoxSize = new JsonSize { Width = 400, Height = 120 };
        centred.Alignment = TextAlignment.Center;
        using var centredImage = TextEdits.Image(centred)!;

        var right = Style("Hi");
        right.BoxSize = new JsonSize { Width = 400, Height = 120 };
        right.Alignment = TextAlignment.Right;
        using var rightImage = TextEdits.Image(right)!;

        Assert.True(Ink(centredImage).Left > Ink(leftImage).Left);
        Assert.True(Ink(rightImage).Left > Ink(centredImage).Left);
    }

    [Fact]
    public void AParagraphWrapsAtItsBox()
    {
        var style = Style("one two three four five six", 24);
        style.BoxSize = new JsonSize { Width = 120, Height = 400 };
        using var image = TextEdits.Image(style)!;
        var ink = Ink(image);
        // Six words in a 96-pixel measure take more than one line, so the ink is taller than one line.
        Assert.True(ink.Bottom - ink.Top > style.LineHeight, $"the ink is {ink.Bottom - ink.Top} tall");
        Assert.True(ink.Right < 120, $"the ink reaches {ink.Right}, past the box");
    }

    [Fact]
    public void AChineseParagraphWrapsWithoutSpaces()
    {
        var style = Style("中文排版需要自动换行", 24);
        var twoCharacters = TextEdits.Caret(Style("中文", 24)).X - TextEdits.Padding;
        style.BoxSize = new JsonSize { Width = twoCharacters + TextEdits.Padding * 2 + 0.1, Height = 400 };

        var start = TextEdits.Caret(style, 0);
        var end = TextEdits.Caret(style);
        Assert.True(end.Y >= start.Y + style.LineHeight * 3, "the continuous Chinese paragraph did not wrap");
        Assert.True(end.X <= style.BoxSize.Value.Width - TextEdits.Padding + 0.1, "the last line exceeds its box");
    }

    [Theory]
    [InlineData("甲乙，丙", "乙，")]
    [InlineData("甲（乙丙", "（乙")]
    [InlineData("甲乙。丙", "乙。")]
    [InlineData("甲“乙丙", "“乙")]
    public void ChinesePunctuationStaysWithItsNeighbor(string content, string group)
    {
        var style = Style(content, 24);
        var groupWidth = TextEdits.Caret(Style(group, 24)).X - TextEdits.Padding;
        style.BoxSize = new JsonSize { Width = groupWidth + TextEdits.Padding * 2 + 0.1, Height = 400 };

        Assert.True(TextEdits.Caret(style, 2).Y > TextEdits.Caret(style, 1).Y);
        Assert.Equal(TextEdits.Caret(style, 2).Y, TextEdits.Caret(style, 3).Y, 2);
    }

    [Fact]
    public void EnglishWordsUseTheAvailableWidthBeforeWrapping()
    {
        var style = Style("one two three", 24);
        var firstTwoWidth = TextEdits.Caret(Style("one two", 24)).X - TextEdits.Padding;
        style.BoxSize = new JsonSize { Width = firstTwoWidth + TextEdits.Padding * 2 + 0.1, Height = 400 };

        Assert.Equal(TextEdits.Caret(style, 1).Y, TextEdits.Caret(style, 7).Y, 2);
        Assert.True(TextEdits.Caret(style).Y > TextEdits.Caret(style, 7).Y);
    }

    [Fact]
    public void AnUnbrokenEnglishWordIsKeptTogetherInANarrowBox()
    {
        var style = Style("unbroken", 24);
        style.BoxSize = new JsonSize { Width = 30, Height = 400 };
        Assert.Equal(TextEdits.Caret(style, 0).Y, TextEdits.Caret(style).Y, 2);
    }

    [Theory]
    [InlineData("e\u0301")]
    [InlineData("😀")]
    [InlineData("👩‍🔬")]
    [InlineData("🇨🇳")]
    public void TrackingAndCaretStopsKeepGraphemeClustersTogether(string cluster)
    {
        var plain = Style(cluster, 24);
        var tracked = Style(cluster, 24);
        tracked.Tracking = 10;
        Assert.Equal(10, TextEdits.Caret(tracked).X - TextEdits.Caret(plain).X, 2);
        for (var index = 1; index <= cluster.Length; index++)
            Assert.Equal(TextEdits.Caret(plain), TextEdits.Caret(plain, index));
    }

    [Fact]
    public void AColorRunAfterAnEmojiStillUsesUtf16Offsets()
    {
        const string emoji = "👩‍🔬";
        var style = Style(emoji + "X", 48);
        style.ColorRuns = [new LayerTextColorRun { Location = emoji.Length, Length = 1, Red = 1, Green = 0, Blue = 0 }];
        using var image = TextEdits.Image(style)!;
        var red = Ink(image, SKColors.Red);
        Assert.True(red.Count > 0, "the UTF-16 color run after the emoji was not drawn");
        Assert.True(red.Left >= TextEdits.Caret(style, emoji.Length).X - 1);
    }

    [Fact]
    public void ALayerNameDoesNotEndWithPartOfAGraphemeCluster()
    {
        Assert.Equal(new string('x', 39), TextEdits.LayerName(new string('x', 39) + "😀tail"));
        Assert.Equal(new string('x', 39), TextEdits.LayerName(new string('x', 39) + "e\u0301tail"));
    }

    [Fact]
    public void ChineseMeasurementUsesTheSystemFallbackWhenOneIsAvailable()
    {
        var style = Style("中", 48);
        var preferred = TextEdits.Typeface(style.FontName);
        using var original = new SKFont(preferred, (float)style.FontSize);
        using var fallback = original.ContainsGlyph('中') ? null
            : SKFontManager.Default.MatchCharacter(preferred.FamilyName, preferred.FontStyle, null, '中');
        using var expected = new SKFont(fallback ?? preferred, (float)style.FontSize);

        Assert.Equal(expected.MeasureText("中"), TextEdits.Caret(style).X - TextEdits.Padding, 2);
        using var image = TextEdits.Image(style);
        Assert.NotNull(image);
    }

    [Fact]
    public void AnUnknownFaceFallsBackToTheOneTheSystemHas()
    {
        Assert.NotNull(TextEdits.Typeface("No such face at all"));
        Assert.NotNull(TextEdits.Typeface(null));
    }

    [Fact]
    public void ATextLayerIsStillAnEditableTextLayer()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var id = TextEdits.Add(document, Style("Hello"), new SKPoint(20, 30));
        Assert.NotNull(id);
        var layer = document.Layers.Single();
        Assert.Equal("Hello", layer.Name);
        // The pixels and the style share one bitmap, which is what makes it live text.
        Assert.NotNull(layer.LiveText);
        Assert.Same(layer.Asset!.Image, layer.Text!.Image);
        // The style that drew it comes back with it.
        Assert.Equal("Hello", layer.LiveText.Content);
        Assert.Equal("Arial", layer.LiveText.FontName);
        Assert.Equal(0, layer.LiveText.Red);
        // It is placed where it was asked for, and its pixels are the size of the box.
        Assert.Equal(20, layer.Transform.X);
        Assert.Equal(30, layer.Transform.Y);
        Assert.Equal(TextEdits.BoxSize(layer.LiveText).Width, layer.Asset.Width);
    }

    [Fact]
    public void EditingTheStyleDrawsTheLayerAgainAboutItsMiddle()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 600, 300);
        var id = TextEdits.Add(document, Style("Hello", 36), new SKPoint(20, 30));
        Assert.NotNull(id);
        var layer = document.Layers.Single();
        var centreX = layer.Transform.CenterX;
        var centreY = layer.Transform.CenterY;
        var wasSmall = layer.Asset!.Width;

        var bigger = Style("Hello", 72);
        Assert.True(TextEdits.SetStyle(document, id!.Value, bigger));
        Assert.True(layer.Asset!.Width > wasSmall);
        Assert.Equal(centreX, layer.Transform.CenterX, 6);
        Assert.Equal(centreY, layer.Transform.CenterY, 6);
        Assert.Equal(72, layer.LiveText!.FontSize);
    }

    [Fact]
    public void ATextLayerSavesAndLoadsWithItsStyle()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 400, 200);
        var style = Style("Save me");
        style.ColorRuns = [new LayerTextColorRun { Location = 0, Length = 4, Red = 1, Green = 0, Blue = 0 }];
        Assert.NotNull(TextEdits.Add(document, style, new SKPoint(10, 10)));

        var package = Path.Combine(_root, "Text.comp");
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), package);
        using var loaded = ProjectStore.Load(package);
        var record = loaded.Manifest.Layers.Single();
        var saved = record.Text;
        Assert.NotNull(saved);
        Assert.Equal("Save me", saved!.Content);
        Assert.Equal("Arial", saved.FontName);
        Assert.Single(saved.ColorRuns!);
        Assert.Equal(1, saved.ColorRuns![0].Red);

        using var after = loaded.ToDocument();
        Assert.NotNull(after.Layers.Single().LiveText);
    }

    [Fact]
    public void TextTooBigForOneSurfaceIsRefused()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 100, 100);
        var style = Style("Hello");
        style.BoxSize = new JsonSize { Width = 20_000, Height = 20_000 };
        Assert.Null(TextEdits.Image(style));
        Assert.Null(TextEdits.Add(document, style, new SKPoint(0, 0)));
        Assert.Empty(document.Layers);
    }

    [Fact]
    public void TheCaretSitsAfterTheLastCharacterOfTheLastLine()
    {
        // An empty layer has its caret on the first line, inside the padding.
        var empty = TextEdits.Caret(Style(""));
        Assert.Equal(TextEdits.Padding, empty.X, 2);
        Assert.True(empty.Y > TextEdits.Padding, $"the caret is at {empty.Y}");

        var one = TextEdits.Caret(Style("Hello"));
        var two = TextEdits.Caret(Style("Hello" + Environment.NewLine + "Hi"));
        // Each is on its own baseline, so the second line is a line lower.
        Assert.True(two.Y > one.Y, $"{two.Y} is not below {one.Y}");
        Assert.Equal(one.Y + Style("x").LineHeight, two.Y, 2);
        // And on the second line the caret is after fewer characters, so it is nearer the left.
        Assert.True(two.X < one.X, $"{two.X} is not left of {one.X}");
        // A longer line puts the caret further right.
        Assert.True(TextEdits.Caret(Style("Hello there")).X > one.X);
    }

    [Fact]
    public void TheCaretSitsWhereTheCharactersInFrontOfItEnd()
    {
        var style = Style("Hello");
        // The very start is at the left of the text, and each character moves it right by that character.
        var start = TextEdits.Caret(style, 0);
        var after = TextEdits.Caret(style, style.Content.Length);
        Assert.True(start.X < after.X, $"{start.X} is not left of {after.X}");
        for (var index = 1; index <= style.Content.Length; index++)
        {
            Assert.True(TextEdits.Caret(style, index).X > TextEdits.Caret(style, index - 1).X,
                $"the caret did not move at {index}");
        }
        // Asking for the end is the same as asking for the words themselves.
        Assert.Equal(TextEdits.Caret(style), after);
        // And past the end is the end.
        Assert.Equal(after, TextEdits.Caret(style, 99));
        Assert.Equal(start, TextEdits.Caret(style, -3));
    }

    [Fact]
    public void TheCaretFollowsTheLineTheIndexFallsOn()
    {
        var style = Style("Hello" + Environment.NewLine + "Hi");
        var endOfFirst = TextEdits.Caret(style, 5);
        var startOfSecond = TextEdits.Caret(style, 5 + Environment.NewLine.Length);
        // A line lower, and back at the left of the box: both ends of a newline.
        Assert.True(startOfSecond.Y > endOfFirst.Y, $"{startOfSecond.Y} is not below {endOfFirst.Y}");
        Assert.Equal(style.LineHeight, startOfSecond.Y - endOfFirst.Y, 2);
        Assert.True(startOfSecond.X < endOfFirst.X, $"{startOfSecond.X} is not left of {endOfFirst.X}");
        Assert.Equal(TextEdits.Padding, startOfSecond.X, 2);
    }

    [Fact]
    public void TheCaretOnAWrappedLineSitsAtTheEndOfTheLineBefore()
    {
        // A paragraph box narrow enough that "Hello there" wraps: the space it breaks at is left out of the
        // drawing, so a caret on it belongs at the end of the first line.
        var style = Style("Hello there");
        style.BoxSize = new JsonSize { Width = 120, Height = 0 };
        var atSpace = TextEdits.Caret(style, 5);
        var firstLine = TextEdits.Caret(style, 4);
        Assert.Equal(firstLine.Y, atSpace.Y, 2);
        Assert.True(atSpace.X > firstLine.X, "the caret at the space is not at the end of the line");
        // The character after it starts the next line.
        var nextLine = TextEdits.Caret(style, 6);
        Assert.True(nextLine.Y > atSpace.Y, $"{nextLine.Y} is not below {atSpace.Y}");
    }

    [Fact]
    public void AnEmptyTextHasItsCaretInsideThePadding()
    {
        var caret = TextEdits.Caret(Style(""), 0);
        Assert.Equal(TextEdits.Padding, caret.X, 2);
        Assert.True(caret.Y > TextEdits.Padding, $"the caret is at {caret.Y}");
    }

    [Fact]
    public void TrackingMovesTheCaretWithTheLetters()
    {
        var plain = TextEdits.Caret(Style("Hello"));
        var spaced = Style("Hello");
        spaced.Tracking = 10;
        Assert.True(TextEdits.Caret(spaced).X > plain.X);
    }

    [Fact]
    public void AParagraphPutsTheCaretAfterTheWordOnItsLastLine()
    {
        var style = Style("one two three four", 24);
        style.BoxSize = new JsonSize { Width = 90, Height = 400 };
        var caret = TextEdits.Caret(style);
        // The text wraps, so the caret is on the last of the lines and inside the box.
        Assert.True(caret.Y > Style("x").LineHeight, $"the caret is at {caret.Y}");
        Assert.True(caret.X < 90, $"the caret is at {caret.X}");
    }
}
