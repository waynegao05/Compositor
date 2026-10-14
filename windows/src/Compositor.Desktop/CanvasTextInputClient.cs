using System.Globalization;
using Avalonia;
using Avalonia.Input.TextInput;
using Avalonia.Media;

namespace Compositor.Desktop;

/// <summary>
/// Connects the canvas to Avalonia's native input method. Preedit text stays outside the document until a
/// TextInput event commits it, so choosing or cancelling a Chinese candidate never adds history entries.
/// </summary>
internal sealed class CanvasTextInputClient(CanvasView canvas) : TextInputMethodClient
{
    private string _preedit = "";
    private int _cursor;
    private static readonly IBrush PreeditBackground = new SolidColorBrush(Color.FromRgb(32, 32, 32));
    private static readonly Pen PreeditPen = new(Brushes.White, 1);

    internal bool IsComposing => _preedit.Length > 0;

    public override Visual TextViewVisual => canvas;
    public override bool SupportsPreedit => true;

    // TextSession has no selection/replacement API yet. Do not advertise surrounding-text editing until
    // reconversion and ranged replacement can actually be performed without losing document text.
    public override bool SupportsSurroundingText => false;
    public override string SurroundingText => "";
    public override TextSelection Selection { get => default; set { } }

    public override Rect CursorRectangle
    {
        get
        {
            var caret = canvas.TextCursorRectangle;
            if (!IsComposing) return caret;
            var text = Format(_preedit);
            return new Rect(caret.X + PrefixWidth(), caret.Y, 1, text.Height);
        }
    }

    public override void SetPreeditText(string? preeditText) => SetPreeditText(preeditText, null);

    public override void SetPreeditText(string? preeditText, int? cursorPos)
    {
        _preedit = canvas.TextEditing ? preeditText ?? "" : "";
        _cursor = Math.Clamp(cursorPos ?? _preedit.Length, 0, _preedit.Length);
        // Never split a UTF-16 surrogate pair when measuring a cursor supplied by an input method.
        if (_cursor > 0 && _cursor < _preedit.Length
            && char.IsHighSurrogate(_preedit[_cursor - 1]) && char.IsLowSurrogate(_preedit[_cursor]))
        {
            _cursor--;
        }
        CursorMoved();
        canvas.InvalidateVisual();
    }

    internal void Clear() => SetPreeditText(null, null);
    internal void CursorMoved() => RaiseCursorRectangleChanged();

    /// <summary>A legible, underlined composition preview at the insertion point, above the image.</summary>
    internal void Draw(DrawingContext context)
    {
        if (!IsComposing) return;
        var caret = canvas.TextCursorRectangle;
        var text = Format(_preedit);
        var width = Math.Max(1, text.WidthIncludingTrailingWhitespace);
        context.DrawRectangle(PreeditBackground, null, new Rect(caret.X - 2, caret.Y, width + 4, text.Height + 2));
        context.DrawText(text, caret.TopLeft);
        context.DrawLine(PreeditPen, new Point(caret.X, caret.Y + text.Height),
            new Point(caret.X + width, caret.Y + text.Height));
        var cursor = CursorRectangle;
        context.DrawLine(PreeditPen, cursor.TopLeft, cursor.BottomLeft);
    }

    private double PrefixWidth() => _cursor == 0 ? 0 : Format(_preedit[.._cursor]).WidthIncludingTrailingWhitespace;

    private FormattedText Format(string text) => new(text, CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight, Typeface.Default, Math.Clamp(canvas.TextCursorRectangle.Height, 14, 36), Brushes.White);
}
