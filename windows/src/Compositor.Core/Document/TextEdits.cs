using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>
/// Laying out and drawing a text layer's pixels: the style is the truth and the PNG is what it looks like,
/// so an edit re-rasterises from the style rather than painting over the last picture.
/// <para>
/// Each text element is placed by hand, preserving grapheme clusters and UTF-16 style offsets. System
/// font fallback covers missing glyphs when a suitable installed face is available. Contextual shaping,
/// bidirectional layout and joined emoji still need a shaping engine; their rendering can differ from
/// Core Text in the Mac build.
/// </para>
/// </summary>
public static class TextEdits
{
    /// <summary>The room left around the text inside its layer, as the Mac build leaves it.</summary>
    public const double Padding = 12;

    /// <summary>The smallest a text layer may be, so an empty line still has somewhere to put a caret.</summary>
    public const int LeastSide = 16;

    private static readonly ConcurrentDictionary<string, SKTypeface> Faces = new();
    private static readonly ConcurrentDictionary<(SKTypeface Face, int Codepoint), SKTypeface> FallbackFaces = new();

    /// <summary>A text layer's name: its first words on one line, so a paragraph does not make the row tall.</summary>
    public static string LayerName(string content)
    {
        var flattened = string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flattened.Length == 0) return "Text";
        if (flattened.Length <= 40) return flattened;
        var end = StringInfo.ParseCombiningCharacters(flattened).Last(index => index <= 40);
        return flattened[..end];
    }

    /// <summary>How big a text layer's pixels are: the paragraph's own box, or what point text measures.</summary>
    public static (int Width, int Height) BoxSize(LayerTextStyle style)
    {
        if (style.BoxSize is { } box)
        {
            return (Math.Max(LeastSide, (int)Math.Ceiling(box.Width)), Math.Max(LeastSide, (int)Math.Ceiling(box.Height)));
        }
        Layout(style, out var measuredWidth, out var measuredHeight);
        var width = Math.Max(LeastSide, (int)Math.Ceiling(measuredWidth + Padding * 2 + style.FontSize * 0.1));
        var height = Math.Max(LeastSide, (int)Math.Ceiling(Math.Max(measuredHeight, Math.Ceiling(style.LineHeight)) + Padding * 2));
        return (width, height);
    }

    /// <summary>The text drawn: straight alpha sRGB, the format every layer's pixels are held in.</summary>
    public static SKBitmap? Image(LayerTextStyle style)
    {
        if (!style.IsValid) return null;
        var (width, height) = BoxSize(style);
        if (width > DocumentLimits.MaxSide || height > DocumentLimits.MaxSide
            || (long)width * height > DocumentLimits.MaxSurfacePixels)
        {
            return null;
        }
        var pieces = Layout(style, out _, out _);
        var image = new SKBitmap(Bitmaps.ColorInfo(width, height));
        image.Erase(SKColors.Transparent);
        using var canvas = new SKCanvas(image);
        using var paint = new SKPaint { IsAntialias = true };
        foreach (var piece in pieces)
        {
            paint.Color = piece.Colour;
            using var font = new SKFont(piece.Typeface, (float)style.FontSize);
            canvas.DrawText(piece.Text, piece.X, piece.Y, SKTextAlign.Left, font, paint);
        }
        return image;
    }

    /// <summary>
    /// A new text layer at <paramref name="origin"/>, holding the text and the style that drew it. The
    /// pixels and the style share one bitmap, which is what makes the layer still a live text layer.
    /// </summary>
    public static Guid? Add(CanvasDocument document, LayerTextStyle style, SKPoint origin)
    {
        if (document.Layers.Count >= LayerPlacement.MaxLayers) return null;
        if (Image(style) is not { } image) return null;
        var name = LayerName(style.Content);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(image, name),
            new Model.LayerTransform(origin.X, origin.Y, image.Width, image.Height), name)
        {
            ParentID = null,
            Text = new LayerText(style, image),
        };
        document.Layers.Add(layer);
        return layer.ID;
    }

    /// <summary>Changes a text layer's style and draws it again, which is how editing live text works.</summary>
    public static bool SetStyle(CanvasDocument document, Guid layerID, LayerTextStyle style)
    {
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { LiveText: not null } layer) return false;
        if (Image(style) is not { } image) return false;
        // Repainting changes the raster size, while the layer's scale, rotation and center stay in place.
        var width = image.Width * (layer.Transform.Width / layer.Asset!.Width);
        var height = image.Height * (layer.Transform.Height / layer.Asset.Height);
        var transform = layer.Transform with
        {
            Width = width,
            Height = height,
            X = layer.Transform.CenterX - width / 2,
            Y = layer.Transform.CenterY - height / 2,
        };
        if (!transform.IsValid)
        {
            image.Dispose();
            return false;
        }
        var name = LayerName(style.Content);
        layer.Asset = ImportedImage.Create(image, name);
        layer.Text = new LayerText(style, image);
        layer.Name = name;
        layer.Transform = transform;
        return true;
    }

    /// <summary>The frontmost visible text under the point, including its folders' visibility and opacity.</summary>
    public static ImageLayer? HitTest(CanvasDocument document, SKPoint point)
    {
        var opacities = document.EffectiveOpacities();
        return document.RenderLayers().LastOrDefault(layer => layer.LiveText is not null
            && opacities[layer.ID] > 0 && layer.Transform.Contains(point));
    }

    /// <summary>
    /// Where the caret sits when the text ends: after the last character of the last line, and on the first
    /// line's baseline when there is no text yet. Relative to the layer's own top left, in layer pixels.
    /// </summary>
    public static SKPoint Caret(LayerTextStyle style) => Caret(style, (style.Content ?? "").Length);

    /// <summary>
    /// Where the caret sits with <paramref name="index"/> characters in front of it: at the place the text
    /// before it ends, on the line that falls on. An index past the end is the end, and one on a character
    /// that a line break left out — the space a wrapped line breaks at — sits at the end of the line before.
    /// </summary>
    public static SKPoint Caret(LayerTextStyle style, int index)
    {
        var stops = new List<Stop>();
        Layout(style, out _, out _, stops);
        if (stops.Count == 0) return new SKPoint((float)Padding, (float)(Padding + Baseline(style)));
        var at = Math.Clamp(index, 0, (style.Content ?? "").Length);
        foreach (var stop in stops)
        {
            if (stop.Index >= at) return new SKPoint(stop.X, stop.Y);
        }
        var last = stops[^1];
        return new SKPoint(last.X, last.Y);
    }

    /// <summary>Find an insertion point using one layout pass, with the click in the text's own pixels.</summary>
    public static int CaretAt(LayerTextStyle style, SKPoint point)
    {
        var stops = new List<Stop>();
        Layout(style, out _, out _, stops);
        if (stops.Count == 0) return 0;
        var baseline = stops.MinBy(stop => Math.Abs(stop.Y - point.Y)).Y;
        return stops.Where(stop => Math.Abs(stop.Y - baseline) < 0.01)
            .MinBy(stop => Math.Abs(stop.X - point.X)).Index;
    }

    /// <summary>A place the caret can sit: how far into the words, and where that is drawn.</summary>
    private readonly record struct Stop(int Index, float X, float Y);

    /// <summary>How far below the top of a line its writing sits.</summary>
    private static double Baseline(LayerTextStyle style)
    {
        var metrics = Metrics(style.FontName, style.FontSize);
        var ascent = -metrics.Ascent;
        var descent = metrics.Descent;
        return (style.LineHeight - (ascent + descent)) / 2 + ascent;
    }

    /// <summary>What a text layer needs to be drawn: its face, its size and the leading between lines.</summary>
    public static SKFontMetrics Metrics(string fontName, double size)
    {
        using var font = new SKFont(Typeface(fontName), (float)size);
        return font.Metrics;
    }

    /// <summary>The face to set text in, falling back to the system face as the Mac build falls back.</summary>
    public static SKTypeface Typeface(string? fontName) =>
        Faces.GetOrAdd(fontName ?? "", name => name.Length > 0
            ? SKTypeface.FromFamilyName(name) ?? SKTypeface.Default
            : SKTypeface.Default);

    /// <summary>One character to draw, with the face and colour it is set in and where it goes.</summary>
    private readonly record struct Piece(string Text, SKTypeface Typeface, SKColor Colour, float X, float Y);

    /// <summary>
    /// The text laid out: every character with its place, and how big the whole paragraph came out. Lines
    /// break where the content says so, and, in a paragraph box, wherever the next word would not fit.
    /// </summary>
    private static List<Piece> Layout(LayerTextStyle style, out double measuredWidth, out double measuredHeight,
        List<Stop>? stops = null)
    {
        var pieces = new List<Piece>();
        var content = style.Content ?? "";
        var colours = Colours(style, content.Length);
        var fonts = Fonts(style, content);
        var lineHeight = style.LineHeight;
        // Where the lines have to fit: the paragraph box less its padding, or no limit at all for point text.
        var limit = style.BoxSize is { } box ? Math.Max(1, box.Width - Padding * 2) : double.PositiveInfinity;
        var baseline = Baseline(style);

        measuredWidth = 0;
        var line = 0;
        foreach (var (start, length) in Lines(content))
        {
            foreach (var (from, to) in Wrap(content, start, length, style, fonts, limit))
            {
                var width = 0.0;
                var index = from;
                var first = pieces.Count;
                var firstStop = stops?.Count ?? 0;
                var y = (float)(Padding + line * lineHeight + baseline);
                while (index < to)
                {
                    var text = NextElement(content, index, to, out var taken);
                    var typeface = fonts[index];
                    pieces.Add(new Piece(text, typeface, colours[index], (float)width, y));
                    stops?.Add(new Stop(index, (float)width, y));
                    width += Advance(text, typeface, style);
                    index += taken;
                }
                stops?.Add(new Stop(to, (float)width, y));
                // The line is aligned inside the paragraph's box, which point text does not have: its box
                // is what it measures, so there is nothing to shift within.
                var offset = AlignmentOffset(style.Alignment, limit, width);
                var shift = (float)(Padding + offset);
                for (var at = first; at < pieces.Count; at++)
                {
                    pieces[at] = pieces[at] with { X = pieces[at].X + shift };
                }
                if (stops is not null)
                {
                    for (var at = firstStop; at < stops.Count; at++)
                    {
                        stops[at] = stops[at] with { X = stops[at].X + shift };
                    }
                }
                measuredWidth = Math.Max(measuredWidth, width);
                line++;
            }
        }
        measuredHeight = line * lineHeight;
        return pieces;
    }

    /// <summary>How far a line starts from the left of its box: nothing, or what centring or righting leaves.</summary>
    private static double AlignmentOffset(TextAlignment alignment, double container, double width)
    {
        if (double.IsPositiveInfinity(container)) return 0;
        return alignment switch
        {
            TextAlignment.Center => (container - width) / 2,
            TextAlignment.Right => container - width,
            _ => 0,
        };
    }

    /// <summary>The advance of one character: what it measures, plus the tracking, as Core Text adds it.</summary>
    private static double Advance(string text, SKTypeface typeface, LayerTextStyle style)
    {
        using var font = new SKFont(typeface, (float)style.FontSize);
        return font.MeasureText(text) + style.Tracking;
    }

    /// <summary>The lines of the content, as (start, length) into it. A break is a newline, in any of its forms.</summary>
    internal static IEnumerable<(int Start, int Length)> Lines(string content)
    {
        var start = 0;
        for (var index = 0; index < content.Length; index++)
        {
            var character = content[index];
            if (character is not ('\n' or '\r' or '\u000b' or '\u000c' or '\u0085' or '\u2028' or '\u2029')) continue;
            yield return (start, index - start);
            // A carriage return followed by a line feed is one break, not two.
            if (character == '\r' && index + 1 < content.Length && content[index + 1] == '\n') index++;
            start = index + 1;
        }
        yield return (start, content.Length - start);
    }

    /// <summary>
    /// Greedy word wrapping for Latin text, with additional breaks between CJK text elements. Opening
    /// punctuation stays with what follows and closing punctuation stays with what precedes it. A word
    /// or punctuation group wider than the box remains intact rather than splitting a grapheme cluster.
    /// </summary>
    private static IEnumerable<(int From, int To)> Wrap(string content, int start, int length,
        LayerTextStyle style, List<SKTypeface> fonts, double limit)
    {
        if (double.IsPositiveInfinity(limit) || length == 0)
        {
            yield return (start, start + length);
            yield break;
        }
        var end = start + length;
        var from = start;
        var at = start;
        var width = 0.0;
        var hasWord = false;
        while (at < end)
        {
            var unitEnd = at;
            do
            {
                var previous = NextElement(content, unitEnd, end, out var taken);
                unitEnd += taken;
                if (unitEnd == end || CanBreak(previous, NextElement(content, unitEnd, end, out _))) break;
            } while (unitEnd < end);

            var unitWidth = Width(content, at, unitEnd, style, fonts);
            var space = IsBreakableSpace(content[at]);
            if (!space && hasWord && width + unitWidth > limit)
            {
                // Spaces at a wrap are omitted; their original UTF-16 offsets remain in the style.
                var trimmed = at;
                while (trimmed > from && IsBreakableSpace(content[trimmed - 1])) trimmed--;
                yield return (from, trimmed);
                from = at;
                width = 0;
                hasWord = false;
            }
            width += unitWidth;
            hasWord |= !space;
            at = unitEnd;
        }
        yield return (from, end);
    }

    private static bool CanBreak(string previous, string next)
    {
        var left = Rune.TryGetRuneAt(previous, 0, out var previousRune) ? previousRune : Rune.ReplacementChar;
        var right = Rune.TryGetRuneAt(next, 0, out var nextRune) ? nextRune : Rune.ReplacementChar;
        if (IsBreakableSpace(previous[0]) || IsBreakableSpace(next[0])) return true;
        if (IsNonbreaking(left) || IsNonbreaking(right)) return false;
        if (IsOpening(left) || IsClosing(right)) return false;
        return IsCjkOrEmoji(left) || IsCjkOrEmoji(right);
    }

    private static bool IsBreakableSpace(char value) =>
        char.IsWhiteSpace(value) && value is not ('\u00a0' or '\u202f');

    private static bool IsNonbreaking(Rune value) => value.Value is 0x00a0 or 0x202f or 0x2060 or 0xfeff;

    private static bool IsOpening(Rune value) =>
        Rune.GetUnicodeCategory(value) is UnicodeCategory.OpenPunctuation or UnicodeCategory.InitialQuotePunctuation;

    private static bool IsClosing(Rune value) =>
        Rune.GetUnicodeCategory(value) is UnicodeCategory.ClosePunctuation or UnicodeCategory.FinalQuotePunctuation
        || value.Value is ',' or '.' or '!' or '?' or ':' or ';'
            or 0x3001 or 0x3002 or 0xff0c or 0xff0e or 0xff01 or 0xff1f or 0xff1a or 0xff1b or 0x2026;

    private static bool IsCjkOrEmoji(Rune value) => value.Value is
        >= 0x2e80 and <= 0xa4cf or >= 0xac00 and <= 0xd7af or >= 0xf900 and <= 0xfaff
        or >= 0xfe30 and <= 0xfe4f or >= 0xff00 and <= 0xffef or >= 0x20000 and <= 0x3ffff
        or >= 0x1f000 and <= 0x1faff;

    private static double Width(string content, int from, int to, LayerTextStyle style, List<SKTypeface> fonts)
    {
        var width = 0.0;
        var index = from;
        while (index < to)
        {
            var text = NextElement(content, index, to, out var taken);
            width += Advance(text, fonts[index], style);
            index += taken;
        }
        return width;
    }

    /// <summary>A complete grapheme cluster, whose starting offset still indexes the UTF-16 style runs.</summary>
    private static string NextElement(string content, int index, int limit, out int taken)
    {
        taken = Math.Min(StringInfo.GetNextTextElementLength(content, index), limit - index);
        return content.Substring(index, taken);
    }

    /// <summary>Each character's colour: the style's, unless a colour run covers it.</summary>
    private static List<SKColor> Colours(LayerTextStyle style, int length)
    {
        var colour = ToColour(style.Red, style.Green, style.Blue);
        var colours = new List<SKColor>(length);
        for (var index = 0; index < length; index++) colours.Add(colour);
        foreach (var run in style.ColorRuns ?? [])
        {
            if (!Covers(run.Location, run.Length, length)) continue;
            var set = ToColour(run.Red, run.Green, run.Blue);
            for (var index = run.Location; index < run.Location + run.Length; index++) colours[index] = set;
        }
        return colours;
    }

    /// <summary>Each character's face: the style's, unless a font run covers it.</summary>
    private static List<SKTypeface> Fonts(LayerTextStyle style, string content)
    {
        var length = content.Length;
        var font = Typeface(style.FontName);
        var fonts = new List<SKTypeface>(length);
        for (var index = 0; index < length; index++) fonts.Add(font);
        foreach (var run in style.FontRuns ?? [])
        {
            if (!Covers(run.Location, run.Length, length)) continue;
            var set = Typeface(run.FontName);
            for (var index = run.Location; index < run.Location + run.Length; index++) fonts[index] = set;
        }
        for (var index = 0; index < length;)
        {
            var element = NextElement(content, index, length, out var taken);
            var face = FaceForElement(element, fonts[index]);
            for (var part = index; part < index + taken; part++) fonts[part] = face;
            index += taken;
        }
        return fonts;
    }

    private static SKTypeface FaceForElement(string element, SKTypeface preferred)
    {
        using var font = new SKFont(preferred);
        foreach (var rune in element.EnumerateRunes())
        {
            if (IsFontControl(rune) || font.ContainsGlyph(rune.Value)) continue;
            var fallback = FallbackFaces.GetOrAdd((preferred, rune.Value), key =>
                SKFontManager.Default.MatchCharacter(key.Face.FamilyName, key.Face.FontStyle, null, key.Codepoint)
                ?? key.Face);
            // Keep a cluster in one face. Falling back only half of it could detach a combining mark.
            using var candidate = new SKFont(fallback);
            if (element.EnumerateRunes().All(part => IsFontControl(part) || candidate.ContainsGlyph(part.Value)))
                return fallback;
        }
        return preferred;
    }

    private static bool IsFontControl(Rune value) =>
        Rune.GetUnicodeCategory(value) is UnicodeCategory.Control or UnicodeCategory.Format
        || value.Value is >= 0xfe00 and <= 0xfe0f or >= 0xe0100 and <= 0xe01ef;

    private static bool Covers(int location, int length, int total) =>
        length > 0 && location >= 0 && location <= total - length;

    private static SKColor ToColour(double red, double green, double blue) => new(
        (byte)Math.Clamp(Math.Round(red * 255), 0, 255),
        (byte)Math.Clamp(Math.Round(green * 255), 0, 255),
        (byte)Math.Clamp(Math.Round(blue * 255), 0, 255));
}
