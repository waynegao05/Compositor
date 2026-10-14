using System.Globalization;
using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Document;

/// <summary>
/// Text being typed on the canvas: the words so far, the layer they are drawn on, and what that layer was
/// before. The layer is made when the first character arrives and drawn again on every letter after it, so
/// what is on the canvas is always what will be saved; the whole session is one change to the document, and
/// letting it go puts the layer back the way it was.
/// </summary>
public sealed class TextSession
{
    private readonly ImageLayer? _original;
    private readonly SKPoint _origin;
    private LayerTextStyle _style;

    private TextSession(LayerTextStyle style, ImageLayer? original, Guid? layer, SKPoint origin)
    {
        _style = style;
        _original = original;
        _origin = origin;
        LayerID = layer;
        // The caret starts after whatever is already there, so a session that is typed into rather than
        // clicked into adds to the end — which is where it has always gone.
        CaretIndex = style.Content?.Length ?? 0;
    }

    /// <summary>The layer the words are on, once they have one: a session with no characters has no layer.</summary>
    public Guid? LayerID { get; private set; }

    /// <summary>What has been typed so far.</summary>
    public string Content => _style.Content;

    /// <summary>How many characters are in front of the caret, from 0 to the length of the words.</summary>
    public int CaretIndex { get; private set; }

    /// <summary>The style the words are being drawn with, as they stand.</summary>
    public LayerTextStyle Style => _style;

    /// <summary>Text that starts where it is clicked. Nothing is added to the document until a character is.</summary>
    public static TextSession New(LayerTextStyle style, SKPoint origin) =>
        new(Copy(style), null, null, origin);

    /// <summary>
    /// More words on a layer that is already text, from where the layer is. A copy of its style is taken, so
    /// that letting the session go can put the original back.
    /// </summary>
    public static TextSession Editing(ImageLayer layer)
    {
        if (layer.LiveText is not { } live) throw new ArgumentException("That layer is not editable text.", nameof(layer));
        var style = Copy(live);
        return new TextSession(style, layer.Clone(), layer.ID,
            new SKPoint((float)layer.Transform.X, (float)layer.Transform.Y));
    }

    /// <summary>
    /// Adds what was typed — a character, or a newline — where the caret is, and draws the layer again.
    /// False when the text cannot be drawn at all, which leaves the session as it was.
    /// </summary>
    public bool Type(CanvasDocument document, string typed)
    {
        if (typed.Length == 0) return false;
        return Splice(document, CaretIndex, 0, typed);
    }

    /// <summary>
    /// The backspace key removes one text element, including combining marks or joined emoji.
    /// Nothing happens at the start of the words.
    /// </summary>
    public bool Backspace(CanvasDocument document)
    {
        if (CaretIndex == 0) return false;
        var previous = PreviousBoundary(_style.Content, CaretIndex);
        return Splice(document, previous, CaretIndex - previous, "");
    }

    /// <summary>The delete key: the character after the caret goes. Nothing happens at the end.</summary>
    public bool Delete(CanvasDocument document)
    {
        if (CaretIndex >= _style.Content.Length) return false;
        return Splice(document, CaretIndex, NextBoundary(_style.Content, CaretIndex) - CaretIndex, "");
    }

    /// <summary>
    /// Moves the caret through the words, and answers whether it went anywhere. Up and down move it a line,
    /// keeping its place along the line as far as that line reaches — counted by text elements rather than by
    /// what is drawn, so on a wrapped line it can land a character or two from where the eye expects.
    /// </summary>
    public bool MoveCaret(TextMove move)
    {
        var content = _style.Content;
        var wanted = move switch
        {
            TextMove.Left => PreviousBoundary(content, CaretIndex),
            TextMove.Right => NextBoundary(content, CaretIndex),
            TextMove.Home => StartOfLine(content, CaretIndex),
            TextMove.End => EndOfLine(content, CaretIndex),
            TextMove.Up => NeighbouringLine(content, CaretIndex, -1),
            _ => NeighbouringLine(content, CaretIndex, 1),
        };
        if (wanted == CaretIndex) return false;
        CaretIndex = Math.Clamp(wanted, 0, content.Length);
        return true;
    }

    /// <summary>Where a click puts the caret: at the nearest place on the line it was clicked on.</summary>
    public bool PlaceCaret(CanvasDocument document, SKPoint point)
    {
        var (transform, width, height) = Placement(document);
        if (transform.InBox(point) is not { } local) return false;
        var best = TextEdits.CaretAt(_style, new SKPoint((float)(local.X * width / transform.Width),
            (float)(local.Y * height / transform.Height)));
        if (best == CaretIndex) return false;
        CaretIndex = best;
        return true;
    }

    /// <summary>Takes characters out and puts others in their place, and draws the layer again.</summary>
    private bool Splice(CanvasDocument document, int at, int remove, string insert)
    {
        var next = TextStyleEdits.Splice(_style, at, remove, insert);
        var before = _style;
        var caret = CaretIndex;
        _style = next;
        CaretIndex = BoundaryAtOrAfter(next.Content, at + insert.Length);
        if (Draw(document)) return true;
        _style = before;
        CaretIndex = caret;
        return false;
    }

    private static int PreviousBoundary(string content, int index) =>
        StringInfo.ParseCombiningCharacters(content).LastOrDefault(at => at < index);

    private static int NextBoundary(string content, int index) =>
        StringInfo.ParseCombiningCharacters(content).FirstOrDefault(at => at > index, content.Length);

    private static int BoundaryAtOrAfter(string content, int index) =>
        StringInfo.ParseCombiningCharacters(content).FirstOrDefault(at => at >= index, content.Length);

    private static (int Start, int Length)[] Lines(string content) => TextEdits.Lines(content).ToArray();

    private static int LineIndex((int Start, int Length)[] lines, int index)
    {
        var line = 0;
        while (line + 1 < lines.Length && lines[line + 1].Start <= index) line++;
        return line;
    }

    private static int StartOfLine(string content, int index)
    {
        var lines = Lines(content);
        return lines[LineIndex(lines, index)].Start;
    }

    private static int EndOfLine(string content, int index)
    {
        var lines = Lines(content);
        var line = lines[LineIndex(lines, index)];
        return line.Start + line.Length;
    }

    private static int NeighbouringLine(string content, int index, int direction)
    {
        var lines = Lines(content);
        var row = LineIndex(lines, index);
        var next = row + direction;
        if (next < 0 || next >= lines.Length) return index;
        var column = StringInfo.ParseCombiningCharacters(content[lines[row].Start..index]).Length;
        return AtColumn(content, lines[next].Start, column);
    }

    private static int AtColumn(string content, int line, int column)
    {
        var end = EndOfLine(content, line);
        var elements = StringInfo.ParseCombiningCharacters(content[line..end]);
        return column < elements.Length ? line + elements[column] : end;
    }

    /// <summary>
    /// Keeps the words and answers with the layer that stayed: null when there was nothing worth keeping, in
    /// which case the session lets go of what it started.
    /// </summary>
    public Guid? Commit(CanvasDocument document)
    {
        if (LayerID is { } id && !string.IsNullOrWhiteSpace(_style.Content)) return id;
        Cancel(document);
        return null;
    }

    /// <summary>
    /// Lets the words go: a layer that was only being started is taken away, and one that was already there
    /// gets its old words back. False when there is nothing to put back.
    /// </summary>
    public bool Cancel(CanvasDocument document)
    {
        if (LayerID is not { } id) return false;
        if (_original is null) return LayerEdits.Delete(document, id);
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { } layer) return false;
        layer.Asset = _original.Asset;
        layer.Text = _original.Text;
        layer.Transform = _original.Transform;
        layer.Name = _original.Name;
        return true;
    }

    /// <summary>The rectangle the text occupies on the document, whether it has a layer yet or not.</summary>
    public SKRectI Box(CanvasDocument document)
    {
        if (LayerID is { } id && document.Layers.FirstOrDefault(layer => layer.ID == id) is { } layer)
        {
            return SKRectI.Create((int)layer.Transform.X, (int)layer.Transform.Y,
                Math.Max(1, (int)layer.Transform.Width), Math.Max(1, (int)layer.Transform.Height));
        }
        var (width, height) = TextEdits.BoxSize(_style);
        return SKRectI.Create((int)_origin.X, (int)_origin.Y, width, height);
    }

    /// <summary>Whether a document point is inside the text, so a click there stays in the session.</summary>
    public bool Contains(CanvasDocument document, SKPoint point) =>
        Placement(document).Transform.Contains(point);

    /// <summary>The caret's document bounds, used to place native input method UI.</summary>
    public SKRect Caret(CanvasDocument document)
    {
        var (from, to) = CaretLine(document);
        return SKRect.Create(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y),
            Math.Max(2, Math.Abs(to.X - from.X)), Math.Max(1, Math.Abs(to.Y - from.Y)));
    }

    /// <summary>The insertion line follows the same rotation, scale and flips as the text.</summary>
    public (SKPoint From, SKPoint To) CaretLine(CanvasDocument document)
    {
        var (transform, width, height) = Placement(document);
        var caret = TextEdits.Caret(_style, CaretIndex);
        var size = (float)_style.FontSize;
        SKPoint Map(float x, float y) => transform.Point(transform.FlipX ? 1 - x / width : x / width,
            transform.FlipY ? 1 - y / height : y / height);
        return (Map(caret.X, caret.Y - size * 0.8f), Map(caret.X, caret.Y + size * 0.2f));
    }

    private (LayerTransform Transform, int Width, int Height) Placement(CanvasDocument document)
    {
        if (LayerID is { } id && document.Layers.FirstOrDefault(layer => layer.ID == id) is { Asset: { } asset } layer)
            return (layer.Transform, asset.Width, asset.Height);
        var (width, height) = TextEdits.BoxSize(_style);
        return (new LayerTransform(_origin.X, _origin.Y, width, height), width, height);
    }

    /// <summary>Draws the words: a new layer on the first character, and the same layer drawn again after.</summary>
    private bool Draw(CanvasDocument document)
    {
        if (LayerID is { } id && document.Layers.Any(layer => layer.ID == id))
        {
            return TextEdits.SetStyle(document, id, _style);
        }
        if (_style.Content.Length == 0) return true;
        LayerID = TextEdits.Add(document, _style, _origin);
        return LayerID is not null;
    }

    /// <summary>Which way an editing key sends the caret.</summary>
    public enum TextMove
    {
        Left,
        Right,
        Up,
        Down,
        Home,
        End,
    }

    /// <summary>A copy, so that editing a session does not change the layer it came from.</summary>
    private static LayerTextStyle Copy(LayerTextStyle style) => TextStyleEdits.Copy(style);
}
