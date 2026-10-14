using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.Input.TextInput;
using Compositor.Core.Document;
using Compositor.Core.Model;
using Format = Compositor.Core.Format;
using Compositor.Core.Rendering;
using LayerShapeStyle = Compositor.Core.Format.LayerShapeStyle;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;
using SelectionMode = Compositor.Core.Document.SelectionMode;

namespace Compositor.Desktop;

/// <summary>The selection tools the pointer can hold: one shape each, plus the wand's single click.</summary>
public enum SelectionTool
{
    None,
    Rectangle,
    Ellipse,
    Lasso,
    Polygon,
    Wand,
}

/// <summary>
/// The document on screen. It composites only the part of the canvas it is showing, through
/// <see cref="DocumentRenderer.RenderRegion"/>, which is what lets a canvas far bigger than one buffer
/// still be looked at.
/// </summary>
public sealed class CanvasView : Control
{
    /// <summary>
    /// How many document pixels one screenful may composite. Zooming out past this cannot be done at 1:1,
    /// so the view stops there rather than asking for a buffer the machine will not give it.
    /// </summary>
    private const long ViewportPixelLimit = 32L * 1024 * 1024;

    // Every colour the canvas draws with comes from Skin, which takes them from the Mac's own canvas.
    private static readonly IBrush Backdrop = Skin.PasteboardBrush;
    /// <summary>What shows through a transparent picture: the checkerboard, as the Mac's canvas draws it.</summary>
    private static readonly IBrush Paper = Skin.Checker;

    /// <summary>The caret: white, over whatever it is on.</summary>
    private static readonly Pen CaretPen = new() { Brush = Brushes.White, Thickness = 1 };

    /// <summary>What a crop is about to take away.</summary>
    private static readonly IBrush DimBrush = Skin.CropDimBrush;

    /// <summary>The Camera Raw panel's own lines: not the document's guides, so not drawn like them.</summary>
    private static readonly Pen UprightPen = new()
    {
        Brush = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
        Thickness = 1.5,
    };

    private CanvasDocument? _document;
    private SKPoint _origin;
    private double _zoom = 1;
    private Point? _dragging;
    private IPointer? _interactionPointer;
    private Guid? _guideDrag;
    private SKPoint[]? _distortCorners;
    private int _distortHandle;

    /// <summary>The stroke being drawn, in document pixels, until the pointer comes back up.</summary>
    private readonly List<SKPoint> _stroke = [];
    private bool _painting;

    /// <summary>When set, dragging paints instead of panning.</summary>
    public bool PaintEnabled { get; set; }

    /// <summary>Which selection tool the pointer is holding; None leaves it panning.</summary>
    public SelectionTool Selection { get; set; }

    /// <summary>Handed the box a marquee drag ended on, in document pixels, and how it meets the selection.</summary>
    public Action<SKRectI, SelectionMode>? MarqueeFinished { get; set; }

    /// <summary>Handed the outline a lasso or a polygonal lasso closed, in document pixels.</summary>
    public Action<IReadOnlyList<SKPoint>, SelectionMode>? LassoFinished { get; set; }

    /// <summary>Handed a click of the wand, in document pixels.</summary>
    public Action<SKPoint, SelectionMode>? WandClicked { get; set; }

    /// <summary>
    /// When set, dragging on the canvas draws a line for the Camera Raw panel to straighten the picture by,
    /// whatever tool is in hand: the panel asks for this while its Draw Guides button is on.
    /// </summary>
    public bool UprightDrawing { get; set; }

    /// <summary>The lines already drawn for the panel, in document pixels, which the canvas draws for it.</summary>
    public IReadOnlyList<(SKPoint Start, SKPoint End)> UprightGuides
    {
        get => _uprightGuides;
        set { _uprightGuides = value; InvalidateVisual(); }
    }

    private IReadOnlyList<(SKPoint Start, SKPoint End)> _uprightGuides = [];

    /// <summary>The line being drawn, in document pixels, until the pointer comes back up.</summary>
    private (SKPoint Start, SKPoint End)? _uprightDraft;

    /// <summary>Handed the line a guide drag finished on, in document pixels.</summary>
    public Action<SKPoint, SKPoint>? UprightDrawn;

    /// <summary>
    /// Handed where the pointer is over the canvas, in document pixels, on every move over it — a panel that
    /// reads a colour out of the picture follows the pointer with this.
    /// </summary>
    public Action<SKPoint>? PointerMovedAt { get; set; }

    /// <summary>The pointer left the canvas, so a readout that was following it can be cleared.</summary>
    public Action? PointerLeftCanvas { get; set; }

    /// <summary>When set, Alt-clicking reports where a Clone Stamp stroke should copy from.</summary>
    public bool SampleSourceOnClick { get; set; }

    /// <summary>Handed the point an Alt-click landed on, in document pixels.</summary>
    public Action<SKPoint>? CloneSourceClicked { get; set; }

    /// <summary>When set, clicking reports the colour under the pointer instead of painting.</summary>
    public bool EyedropperOnClick { get; set; }

    /// <summary>Handed the point the eyedropper was clicked at, in document pixels, and the keys held:
    /// a colour-range pick adds the colour with Shift and takes it away with Alt, as the Mac build's does.</summary>
    public Action<SKPoint, KeyModifiers>? EyedropperClicked { get; set; }

    /// <summary>
    /// Whether the ring that follows an eyedropper drag is drawn — the colour under the pointer across its top
    /// half and the colour the pick is replacing across its bottom, which is the Mac build's own Sample Ring.
    /// </summary>
    public bool ShowsSampleRing { get; set; } = true;

    /// <summary>Whether the sampling ring is up, and the two colours on it, which is what the checks read.</summary>
    internal bool SampleRingShowing => _sampleRing is not null;

    internal (SKColor Original, SKColor Sampled)? SampleRing => _sampleRing;

    /// <summary>The ring while the eyedropper's pointer is down: where it sits, and what it compares.</summary>
    private (SKColor Original, SKColor Sampled)? _sampleRing;
    private Point _sampleAt;
    private SKColor _sampleOriginal;
    private bool _sampling;

    /// <summary>The document point a selection's pixels were taken hold of at, while the drag carries them.</summary>
    private SKPoint? _pixelsFrom;

    private (FloatingPixels Pixels, int Dx, int Dy)? _floating;
    private WriteableBitmap? _floatingImage;

    /// <summary>Whether a press inside the selection takes hold of the pixels under it, which is the window's call.</summary>
    public Func<SKPoint, bool>? PixelsGrabbed { get; set; }

    /// <summary>How far the pixels being carried have come, in whole document pixels.</summary>
    public Action<int, int>? PixelsMoved { get; set; }

    /// <summary>The drag has let the pixels go, wherever they are.</summary>
    public Action? PixelsDropped { get; set; }

    /// <summary>
    /// The pixels a drag is carrying and how far they have come, which the canvas draws where the pointer has them
    /// so a selection's contents follow it — the Mac build's floating selection. The cut is turned into an image
    /// once, when the pixels are taken hold of, because the offset changes on every move and the pixels do not.
    /// </summary>
    public (FloatingPixels Pixels, int Dx, int Dy)? Floating
    {
        get => _floating;
        set
        {
            if (!ReferenceEquals(_floating?.Pixels, value?.Pixels))
            {
                _floatingImage?.Dispose();
                _floatingImage = value is { } carrying ? ToImage(carrying.Pixels.Cut) : null;
            }
            _floating = value;
            InvalidateVisual();
        }
    }

    /// <summary>Whether there are pixels to draw where the pointer is carrying them, for the checks to read.</summary>
    internal bool FloatingShowing => _floatingImage is not null;

    /// <summary>When set, the crop frame below is drawn and can be dragged about.</summary>
    public bool CropEnabled { get; set; }

    /// <summary>The crop frame, in document pixels; null leaves the whole canvas as the frame.</summary>
    public SKRectI? CropBox { get; set; }

    /// <summary>The frame a crop drag has worked out, and where the pointer is, for the app to snap.</summary>
    public Action<SKRectI, SKPoint>? CropChanged { get; set; }

    /// <summary>A double-click inside the frame asks for the crop to be made.</summary>
    public Action? CropCommitted { get; set; }

    private enum CropDrag
    {
        None,
        Create,
        Move,
        Resize,
    }

    private CropDrag _cropDragging;
    private TransformHandle? _cropHandle;
    private SKRectI _cropOriginal;
    private SKPoint _cropStart;

    /// <summary>When set, dragging draws a shape out and reports the box it made.</summary>
    public bool ShapeEnabled { get; set; }

    /// <summary>Which shape the drag draws, for the outline the canvas shows while it is drawn.</summary>
    public Format.ShapeKind ShapeKind { get; set; }

    /// <summary>Handed the drag's start, the box it made and where a line ended, in document pixels.</summary>
    public Action<SKPoint, SKRectI, SKPoint>? ShapeFinished { get; set; }

    /// <summary>When set, dragging paints a gradient between where it began and where it ended.</summary>
    public bool GradientEnabled { get; set; }

    /// <summary>Handed the two ends of a gradient drag, in document pixels.</summary>
    public Action<SKPoint, SKPoint>? GradientFinished { get; set; }

    /// <summary>The line the gradient runs along is being dragged: where it is now.</summary>
    public Action<SKPoint, SKPoint>? GradientChanged { get; set; }

    /// <summary>The gradient's line has been taken hold of: the app starts showing what it would do.</summary>
    public Action? GradientStarted { get; set; }

    private bool _gradientDrag;
    private SKPoint _gradientStart;
    private SKPoint _gradientEnd;

    private bool _shaping;
    private ShapePreviewPlan? _shapePlan;
    private SKBitmap? _shapePreview;
    private SKRectI _shapePreviewBox;
    private SKRectI _shapePreviewAt;
    private SKPoint _shapeAnchor;
    private SKRectI _shapeBox;
    private SKPoint _shapeEnd;

    /// <summary>When set, clicking reports where a new text layer should go.</summary>
    public bool TypeOnClick { get; set; }

    /// <summary>Handed the point the Type tool was clicked at, in document pixels.</summary>
    public Action<SKPoint>? TextClicked { get; set; }

    /// <summary>Whether text is being typed on the canvas, so the keys come here and a caret is drawn.</summary>
    public bool TextEditing { get; private set; }

    /// <summary>The caret to draw, in document pixels, while text is being typed.</summary>
    public SKRect? TextCaret
    {
        get => _textCaret;
        set
        {
            _textCaret = value;
            _textInput.CursorMoved();
            InvalidateVisual();
        }
    }

    private SKRect? _textCaret;
    internal (SKPoint From, SKPoint To)? TextCaretLine { get; set; }
    private readonly CanvasTextInputClient _textInput;

    /// <summary>Uncommitted input belongs to the input method, including its navigation keys.</summary>
    public bool TextComposing => TextEditing && _textInput.IsComposing;

    /// <summary>The document caret in view coordinates; Avalonia applies the window's DPI scaling.</summary>
    internal Rect TextCursorRectangle => TextCaret is { } caret
        ? new Rect(ToScreen(new SKPoint(caret.Left, caret.Top)),
            new Size(Math.Max(1, caret.Width * _zoom), Math.Max(1, caret.Height * _zoom)))
        : new Rect(0, 0, 1, 16);

    /// <summary>Text that was typed, a newline included.</summary>
    public Action<string>? TextTyped { get; set; }

    /// <summary>The delete key.</summary>
    public Action? TextBackspaced { get; set; }

    /// <summary>The delete key while text is being typed: the character after the caret goes.</summary>
    public Action? TextDeleted { get; set; }

    /// <summary>An arrow or Home or End: which way the caret goes through the words.</summary>
    public Action<TextSession.TextMove>? TextMoved { get; set; }

    /// <summary>Ctrl and Enter, or a click outside the text.</summary>
    public Action? TextCommitted { get; set; }

    /// <summary>Escape.</summary>
    public Action? TextCancelled { get; set; }

    private readonly DispatcherTimer _caretBlink = new() { Interval = TimeSpan.FromMilliseconds(530) };
    private bool _caretOn = true;

    /// <summary>When set, the box below is drawn with its handles and can be dragged about.</summary>
    public bool TransformEnabled { get; set; }

    /// <summary>Whether a guide can be taken hold of and dragged, which the Move tool allows.</summary>
    public bool GuidesDraggable { get; set; }

    /// <summary>Whether a guide may be taken hold of: it has to be shown, and not locked.</summary>
    private bool GuidesMovable => GuidesDraggable && ShowsGuides && !LocksGuides;

    /// <summary>A guide has been taken hold of: the window begins one undo step here.</summary>
    public Action? GuideDragStarted { get; set; }

    /// <summary>
    /// Whether a corner can be dragged on its own. The window allows it for one layer at a time, since a
    /// distortion resamples that layer's pixels and a box around several is not one layer's shape.
    /// </summary>
    public bool DistortEnabled { get; set; }

    /// <summary>A distortion has been taken hold of: the window begins one undo step here.</summary>
    public Action? DistortStarted { get; set; }

    /// <summary>The distortion is something else now; nothing is resampled until it is let go.</summary>
    public Action<IReadOnlyList<SKPoint>>? DistortChanged { get; set; }

    /// <summary>The distortion has been let go: the window resamples the pixels into that shape.</summary>
    public Action<IReadOnlyList<SKPoint>>? DistortFinished { get; set; }

    /// <summary>The guide being dragged has been put at a document position.</summary>
    public Action<Guid, double>? GuideMoved { get; set; }

    /// <summary>The drag has ended; the window decides whether a guide left off the canvas is taken away.</summary>
    public Action? GuideDragFinished { get; set; }

    /// <summary>The box the transform handles sit around, in document pixels.</summary>
    public LayerTransform? TransformBox { get; set; }

    /// <summary>Whether a scale drag keeps the sides in proportion unless Shift says otherwise.</summary>
    public bool TransformLockRatio { get; set; }

    /// <summary>The pointer took hold of the box: the whole drag is one undo step.</summary>
    public Action? TransformStarted { get; set; }

    /// <summary>The box a drag has worked out, for the app to put on the layer.</summary>
    public Action<LayerTransform>? TransformChanged { get; set; }

    public Action? TransformFinished { get; set; }

    /// <summary>Lines to draw along while a drag is snapped to something.</summary>
    public (double? X, double? Y) SnapLines { get; set; }

    private TransformHandle? _handle;
    private LayerTransform _dragOriginal;
    private SKPoint _dragStart;

    private bool _transformDragging;
    private bool _selecting;
    private SKPoint _selectionAnchor;
    private SKRectI? _selectionBox;

    /// <summary>The outline being dragged or clicked out right now, in document pixels.</summary>
    private readonly List<SKPoint> _lasso = [];

    /// <summary>Where the pointer is, so an open polygonal lasso can show the line it would add.</summary>
    private SKPoint? _lassoPointer;
    private SelectionMode _draftMode = SelectionMode.Replace;

    public BrushSettings Brush { get; set; } = new();

    /// <summary>Handed the finished stroke, in document pixels.</summary>
    public Action<IReadOnlyList<SKPoint>>? StrokeFinished { get; set; }

    public CanvasView()
    {
        _textInput = new CanvasTextInputClient(this);
        ClipToBounds = true;
        // The canvas takes the keys while text is being typed, as the Mac build's canvas does.
        Focusable = true;
        InputMethod.SetIsInputMethodEnabled(this, false);
        AddHandler(TextInputMethodClientRequestedEvent, (_, e) =>
        {
            if (!TextEditing) return;
            e.Client = _textInput;
            e.Handled = true;
        });
        LostFocus += (_, _) => _textInput.Clear();
        _caretBlink.Tick += (_, _) =>
        {
            _caretOn = !_caretOn;
            if (TextEditing) InvalidateVisual();
        };
    }

    /// <summary>Starts typing on the canvas: the keys come here and a caret blinks where the text ends.</summary>
    public void BeginText()
    {
        TextEditing = true;
        InputMethod.SetIsInputMethodEnabled(this, true);
        _caretOn = true;
        _caretBlink.Start();
        Focus();
        InvalidateVisual();
    }

    /// <summary>Stops typing: the keys go back to the rest of the window.</summary>
    public void EndText()
    {
        TextEditing = false;
        InputMethod.SetIsInputMethodEnabled(this, false);
        _textInput.Clear();
        TextCaretLine = null;
        TextCaret = null;
        _caretBlink.Stop();
        InvalidateVisual();
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (TextEditing && e.Text is { Length: > 0 } typed)
        {
            // A preedit is only a preview. The committed TextInput event inserts it exactly once.
            _textInput.Clear();
            // Whatever the keyboard layout or the input method produced, as it produced it.
            TextTyped?.Invoke(typed);
            e.Handled = true;
            return;
        }
        base.OnTextInput(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (TextEditing)
        {
            // Windows processes these keys before routing them here. Do not also cancel the text layer,
            // insert a newline, or move its caret while the IME is choosing a candidate.
            if (TextComposing || e.Key == Key.ImeProcessed)
            {
                e.Handled = true;
                return;
            }
            switch (e.Key)
            {
                case Key.Escape:
                    TextCancelled?.Invoke();
                    e.Handled = true;
                    return;
                case Key.Enter or Key.Return when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                    TextCommitted?.Invoke();
                    e.Handled = true;
                    return;
                case Key.Enter or Key.Return:
                    TextTyped?.Invoke("\n");
                    e.Handled = true;
                    return;
                case Key.Back:
                    TextBackspaced?.Invoke();
                    e.Handled = true;
                    return;
                case Key.Delete:
                    TextDeleted?.Invoke();
                    e.Handled = true;
                    return;
                case Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End:
                    // With Shift held these would take a selection; there is none inside the text yet.
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) break;
                    TextMoved?.Invoke(e.Key switch
                    {
                        Key.Left => TextSession.TextMove.Left,
                        Key.Right => TextSession.TextMove.Right,
                        Key.Up => TextSession.TextMove.Up,
                        Key.Down => TextSession.TextMove.Down,
                        Key.Home => TextSession.TextMove.Home,
                        _ => TextSession.TextMove.End,
                    });
                    e.Handled = true;
                    return;
            }
        }
        base.OnKeyDown(e);
    }

    public CanvasDocument? Document
    {
        get => _document;
        set
        {
            _document = value;
            Fit();
        }
    }

    /// <summary>
    /// A document to draw instead of the one being edited, while a filter panel is open. It is the same size
    /// and shares everything but the layer being filtered, so the canvas shows the filter without the document
    /// being touched. The owner must clear this before disposing it.
    /// </summary>
    public CanvasDocument? PreviewDocument { get; set; }

    public double Zoom => _zoom;

    /// <summary>The document place at the view's top left corner, which the ruler strips are numbered from.</summary>
    public double OriginX => _origin.X;

    /// <summary>The same down the side.</summary>
    public double OriginY => _origin.Y;

    /// <summary>
    /// Called whenever the view moves over the document or changes zoom, so what is drawn around the canvas —
    /// the rulers — can follow it.
    /// </summary>
    public Action? ViewportChanged { get; set; }

    /// <summary>Where the view sits over the document now, which a reload puts back afterwards.</summary>
    public (double Zoom, double OriginX, double OriginY) Viewport => (_zoom, _origin.X, _origin.Y);

    /// <summary>Puts the view back where it was, for a document that has been swapped underneath it.</summary>
    public void RestoreViewport((double Zoom, double OriginX, double OriginY) viewport)
    {
        _zoom = Math.Clamp(viewport.Zoom, 0.01, 32);
        _origin = new SKPoint((float)viewport.OriginX, (float)viewport.OriginY);
        Moved();
    }

    /// <summary>The layout grid drawn under the guides and everything else, or null when it is off.</summary>
    public LayoutGrid? Grid { get; set; }

    /// <summary>
    /// A line around every document pixel once the view is in far enough to see them, which is what tells one
    /// pixel from the next when working close up. Photoshop shows it from 800% up.
    /// </summary>
    public bool PixelGrid { get; set; }

    /// <summary>Whether the guides are drawn at all: hiding them is a view choice, not a change to them.</summary>
    public bool ShowsGuides { get; set; } = true;

    /// <summary>Whether the guides may be dragged. Locked, they are drawn but a click passes them by.</summary>
    public bool LocksGuides { get; set; }

    /// <summary>Whether the Move tool's transform handles are drawn around the selected layer.</summary>
    public bool ShowsTransformControls { get; set; } = true;

    /// <summary>
    /// Whether the Move tool's handles are there for a click at this point: the handles are off entirely when
    /// they are hidden, so what they draw and what they catch cannot come apart.
    /// </summary>
    private bool TransformHandles => TransformEnabled && ShowsTransformControls;

    /// <summary>The layout grid's own zoom of 8: one document pixel covers eight view points or more.</summary>
    private const double PixelGridZoom = 8;

    /// <summary>Whether the view had to stop zooming out because one screenful would be too big to draw.</summary>
    public bool ZoomedOutAsFarAsItGoes { get; private set; }

    /// <summary>Shows the whole canvas, as far out as one screenful may composite.</summary>
    public void Fit()
    {
        if (_document is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var across = Bounds.Width / _document.Width;
        var down = Bounds.Height / _document.Height;
        SetZoom(Math.Min(across, down));
        _origin = new SKPoint(
            (float)((_document.Width - Bounds.Width / _zoom) / 2),
            (float)((_document.Height - Bounds.Height / _zoom) / 2));
        Moved();
    }

    public void ActualSize()
    {
        var centre = _document is null
            ? new SKPoint()
            : new SKPoint(_origin.X + (float)(Bounds.Width / _zoom / 2), _origin.Y + (float)(Bounds.Height / _zoom / 2));
        SetZoom(1);
        _origin = new SKPoint((float)(centre.X - Bounds.Width / 2), (float)(centre.Y - Bounds.Height / 2));
        Moved();
    }

    public void ZoomBy(double factor)
    {
        var anchor = new SKPoint(_origin.X + (float)(Bounds.Width / _zoom / 2), _origin.Y + (float)(Bounds.Height / _zoom / 2));
        var before = anchor;
        SetZoom(_zoom * factor);
        // Keep the same document point under the middle of the view.
        _origin = new SKPoint((float)(before.X - Bounds.Width / _zoom / 2), (float)(before.Y - Bounds.Height / _zoom / 2));
        Moved();
    }

    /// <summary>The view has moved or changed zoom: it is redrawn, and whatever follows it is told.</summary>
    private void Moved()
    {
        _textInput.CursorMoved();
        InvalidateVisual();
        ViewportChanged?.Invoke();
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.01, 32);
        if (_document is null) return;
        // One screenful must not ask for more than the limit; stop zooming out when it would.
        var needed = (long)(Bounds.Width / _zoom) * (long)(Bounds.Height / _zoom);
        ZoomedOutAsFarAsItGoes = false;
        if (needed > ViewportPixelLimit)
        {
            var allowed = Math.Sqrt(Bounds.Width * (double)Bounds.Height / ViewportPixelLimit);
            _zoom = Math.Max(_zoom, allowed);
            ZoomedOutAsFarAsItGoes = true;
        }
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        context.DrawRectangle(Backdrop, null, new Rect(0, 0, size.Width, size.Height));
        // What is drawn is the preview while a panel is showing one, and the document itself otherwise.
        if ((PreviewDocument ?? _document) is not { } document) return;

        var left = (int)Math.Floor(_origin.X);
        var top = (int)Math.Floor(_origin.Y);
        var right = (int)Math.Ceiling(_origin.X + size.Width / _zoom);
        var bottom = (int)Math.Ceiling(_origin.Y + size.Height / _zoom);
        var region = SKRectI.Intersect(SKRectI.Create(left, top, right - left, bottom - top),
            SKRectI.Create(0, 0, document.Width, document.Height));
        if (region.Width <= 0 || region.Height <= 0) return;

        using var rendered = DocumentRenderer.RenderRegion(document, region);
        using var image = ToImage(rendered);
        var destination = new Rect(
            (region.Left - _origin.X) * _zoom,
            (region.Top - _origin.Y) * _zoom,
            region.Width * _zoom,
            region.Height * _zoom);
        DrawPaperShadow(context, destination);
        // The checkerboard under the picture, so transparent pixels show through it as they do on the Mac.
        context.DrawRectangle(Paper, null, destination);
        context.DrawImage(image, destination);
        context.DrawRectangle(null, Skin.PictureEdgePen, destination);
        DrawGrid(context, document);
        DrawPixelGrid(context, document);
        if (ShowsGuides) DrawGuides(context, document);
        DrawSelection(context);
        DrawUprightGuides(context);
        DrawStroke(context);
        DrawSampleRing(context);
        DrawFloating(context);
    }

    /// <summary>The pixels a drag is carrying, drawn where the pointer has put them.</summary>
    private void DrawFloating(DrawingContext context)
    {
        if (_floating is not { } carrying || _floatingImage is not { } image) return;
        var at = ToScreen(new SKPoint(carrying.Pixels.CutAt.X + carrying.Dx, carrying.Pixels.CutAt.Y + carrying.Dy));
        context.DrawImage(image,
            new Rect(at.X, at.Y, carrying.Pixels.Cut.Width * _zoom, carrying.Pixels.Cut.Height * _zoom));
    }

    /// <summary>
    /// Reads the colour under the pointer as the canvas shows it and puts it on the ring. The read is of one
    /// pixel through the region renderer rather than of the whole picture, so a drag can sample as fast as the
    /// pointer moves; it is the same route the canvas's own drawing takes, so what the ring names is drawn.
    /// </summary>
    private void Sample()
    {
        var point = ToDocument(_sampleAt);
        var x = (int)Math.Floor(point.X);
        var y = (int)Math.Floor(point.Y);
        var sampled = _sampleOriginal;
        if ((PreviewDocument ?? _document) is { } document
            && x >= 0 && y >= 0 && x < document.Width && y < document.Height)
        {
            using var one = DocumentRenderer.RenderRegion(document, SKRectI.Create(x, y, 1, 1));
            sampled = one.GetPixel(0, 0);
        }
        _sampleRing = ShowsSampleRing ? (_sampleOriginal, sampled) : null;
        InvalidateVisual();
    }

    /// <summary>
    /// The ring an eyedropper drag carries, as the Mac build's Sample Ring draws it: a band of grey with the
    /// colour being sampled stroked across its top half and the colour being replaced across its bottom, so the
    /// two can be told apart at a glance while the pointer is moving.
    /// </summary>
    private void DrawSampleRing(DrawingContext context)
    {
        if (_sampleRing is not { } ring) return;
        const double side = 116, inset = 15, band = 24, weight = 16;
        var box = new Rect(_sampleAt.X - side / 2, _sampleAt.Y - side / 2, side, side);
        var oval = new EllipseGeometry(new Rect(box.X + inset, box.Y + inset,
            box.Width - inset * 2, box.Height - inset * 2));
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(0x73, 0x73, 0x73)), band), oval);
        foreach (var (colour, top) in new[] { (ring.Sampled, true), (ring.Original, false) })
        {
            var half = new Rect(box.X, top ? box.Y : box.Y + box.Height / 2, box.Width, box.Height / 2);
            using (context.PushClip(half))
            {
                context.DrawGeometry(null,
                    new Pen(new SolidColorBrush(Color.FromArgb(colour.Alpha, colour.Red, colour.Green, colour.Blue)),
                        weight),
                    oval);
            }
        }
    }

    /// <summary>
    /// The lines the Camera Raw panel is straightening by, and the one being drawn, in a colour of their own so
    /// they read as the panel's rather than as the document's own guides.
    /// </summary>
    private void DrawUprightGuides(DrawingContext context)
    {
        foreach (var (start, end) in _uprightGuides) context.DrawLine(UprightPen, ToScreen(start), ToScreen(end));
        if (_uprightDraft is { } draft) context.DrawLine(UprightPen, ToScreen(draft.Start), ToScreen(draft.End));
    }

    /// <summary>
    /// The shadow the paper casts on the pasteboard, as the Mac's canvas draws it: black at 0.35, blurred 14
    /// points, three points down. A drawing context has no blur, so it goes down as bands of falling strength,
    /// each drawn over the last so they accumulate against the picture and fade away from it.
    /// </summary>
    private static void DrawPaperShadow(DrawingContext context, Rect paper)
    {
        const int bands = 7;
        const double blur = 14;
        for (var band = bands; band >= 1; band--)
        {
            var spread = blur * band / bands;
            var brush = new SolidColorBrush(Color.FromArgb(0x0D, 0, 0, 0));
            context.DrawRectangle(brush, null, new Rect(paper.X - spread, paper.Y - spread + 3,
                paper.Width + spread * 2, paper.Height + spread * 2));
        }
    }

    /// <summary>The layout grid, drawn over the picture as Photoshop draws it and under everything else.</summary>
    /// <summary>
    /// The guide within reach of a click, if any: a guide is a line, so what is near it is a click whose
    /// crossways coordinate is within a handle's grab of it.
    /// </summary>
    private Guid? GuideAt(SKPoint point)
    {
        if (_document is not { } document || document.Guides.Count == 0) return null;
        var tolerance = TransformEdits.Grab / _zoom;
        return GuideEdits.At(document, Format.GuideAxis.Vertical, point.X, tolerance)
            ?? GuideEdits.At(document, Format.GuideAxis.Horizontal, point.Y, tolerance);
    }

    private void DrawGrid(DrawingContext context, CanvasDocument document)
    {
        if (Grid is not { } grid) return;
        var lines = grid.Lines(document.Width, document.Height, _zoom, _origin.X, _origin.Y);
        foreach (var x in lines.VerticalFine) context.DrawLine(Skin.GridFinePen, new Point(x, 0), new Point(x, Bounds.Height));
        foreach (var y in lines.HorizontalFine) context.DrawLine(Skin.GridFinePen, new Point(0, y), new Point(Bounds.Width, y));
        foreach (var x in lines.VerticalMajor) context.DrawLine(Skin.GridPen, new Point(x, 0), new Point(x, Bounds.Height));
        foreach (var y in lines.HorizontalMajor) context.DrawLine(Skin.GridPen, new Point(0, y), new Point(Bounds.Width, y));
    }

    /// <summary>
    /// A line around every document pixel, drawn only when one pixel is at least eight points across: any
    /// closer and the lines would be the picture rather than a guide to it.
    /// </summary>
    private void DrawPixelGrid(DrawingContext context, CanvasDocument document)
    {
        if (!PixelGrid || _zoom < PixelGridZoom) return;
        var left = (int)Math.Floor(_origin.X);
        var top = (int)Math.Floor(_origin.Y);
        var right = (int)Math.Ceiling(_origin.X + Bounds.Width / _zoom);
        var bottom = (int)Math.Ceiling(_origin.Y + Bounds.Height / _zoom);
        for (var x = Math.Max(0, left); x <= Math.Min(document.Width, right); x++)
        {
            var at = (x - _origin.X) * _zoom;
            context.DrawLine(Skin.PixelGridPen, new Point(at, 0), new Point(at, Bounds.Height));
        }
        for (var y = Math.Max(0, top); y <= Math.Min(document.Height, bottom); y++)
        {
            var at = (y - _origin.Y) * _zoom;
            context.DrawLine(Skin.PixelGridPen, new Point(0, at), new Point(Bounds.Width, at));
        }
    }

    /// <summary>The alignment guides, across the whole canvas at the place each one sits.</summary>
    private void DrawGuides(DrawingContext context, CanvasDocument document)
    {
        foreach (var guide in document.Guides)
        {
            var (x1, y1, x2, y2) = GuideEdits.ScreenLine(guide, document.Width, document.Height,
                _zoom, _origin.X, _origin.Y);
            context.DrawLine(Skin.GuidePen, new Point(x1, y1), new Point(x2, y2));
        }
    }

    /// <summary>The caret, drawn over everything else while text is being typed.</summary>
    private void DrawCaret(DrawingContext context)
    {
        if (!TextEditing) return;
        if (TextComposing)
        {
            _textInput.Draw(context);
            return;
        }
        if (!_caretOn || TextCaret is not { } caret) return;
        var top = ToScreen(TextCaretLine?.From ?? new SKPoint(caret.Left, caret.Top));
        var bottom = ToScreen(TextCaretLine?.To ?? new SKPoint(caret.Right, caret.Bottom));
        context.DrawLine(CaretPen, top, bottom);
    }

    /// <summary>
    /// The gradient line being dragged, with a cross at each end as the Mac draws its ends. It goes down as
    /// the Mac's does — a black line under a white one — so it reads over a dark picture and a light one alike.
    /// </summary>
    private void DrawGradient(DrawingContext context)
    {
        if (!_gradientDrag) return;
        var under = new Pen(new SolidColorBrush(Color.FromArgb(0xB3, 0, 0, 0)), 3);
        var over = new Pen(Brushes.White, 1);
        var from = ToScreen(_gradientStart);
        var to = ToScreen(_gradientEnd);
        foreach (var pen in new[] { under, over })
        {
            context.DrawLine(pen, from, to);
            foreach (var end in new[] { from, to })
            {
                context.DrawLine(pen, new Point(end.X - 6, end.Y), new Point(end.X + 6, end.Y));
                context.DrawLine(pen, new Point(end.X, end.Y - 6), new Point(end.X, end.Y + 6));
            }
        }
    }

    /// <summary>
    /// The shape being dragged, shown as the shape it will be — the app hands over the style, so what is drawn
    /// while the drag is under way is the pixels the shape will be made of — with its outline over the top, so
    /// the bounds can be seen against what is under them.
    /// </summary>
    private void DrawShape(DrawingContext context)
    {
        if (!_shaping) return;
        var pen = new Pen { Brush = Brushes.White, Thickness = 1, DashStyle = new DashStyle([4.0, 4.0], 0) };
        if (ShapeKind == Format.ShapeKind.Line) context.DrawLine(pen, ToScreen(_shapeAnchor), ToScreen(_shapeEnd));
        DrawShapePreview(context);
        if (ShapeKind == Format.ShapeKind.Line) return;
        var corner = ToScreen(new SKPoint(_shapeBox.Left, _shapeBox.Top));
        var box = new Rect(corner.X, corner.Y, _shapeBox.Width * _zoom, _shapeBox.Height * _zoom);
        if (ShapeKind == Format.ShapeKind.Ellipse) context.DrawEllipse(null, pen, box.Center, box.Width / 2, box.Height / 2);
        else context.DrawRectangle(null, pen, box);
    }

    /// <summary>
    /// The pixels the shape being dragged is going to be made of, drawn where they will land. The app is asked
    /// what the shape is — its style and the box that will hold it — since only the app knows the colour, the
    /// corner radius and where a line's ends are. Without it, a drag shows its outline alone.
    /// </summary>
    private void DrawShapePreview(DrawingContext context)
    {
        if (ShapePreviewFor is not { } ask || _shapeBox.Width <= 0 || _shapeBox.Height <= 0) return;
        if (_shapePreviewBox != _shapeBox)
        {
            // Only when the box has changed: a redraw that moves nothing should not build it again.
            var (style, at) = ask(_shapeBox);
            _shapePreview?.Dispose();
            _shapePreview = ShapeEdits.Image(style, Math.Max(1, at.Width), Math.Max(1, at.Height));
            _shapePreviewBox = _shapeBox;
            _shapePreviewAt = at;
        }
        if (_shapePreview is not { } preview) return;
        var corner = ToScreen(new SKPoint(_shapePreviewAt.Left, _shapePreviewAt.Top));
        using var image = ToImage(preview);
        context.DrawImage(image, new Rect(corner.X, corner.Y,
            _shapePreviewAt.Width * _zoom, _shapePreviewAt.Height * _zoom));
    }

    /// <summary>What a shape being dragged will be made of: the style, and the box its pixels will cover.</summary>
    public delegate (LayerShapeStyle Style, SKRectI Box) ShapePreviewPlan(SKRectI dragged);

    /// <summary>The app's answer to what the shape being dragged will be made of, or null when the tool that
    /// is not the shape tool is in use.</summary>
    public ShapePreviewPlan? ShapePreviewFor
    {
        get => _shapePlan;
        set
        {
            _shapePlan = value;
            ForgetShapePreview();
        }
    }

    /// <summary>
    /// Draws a gradient line as if it were being dragged, so the self check can look at a drawing that needs
    /// a pointer to make. Both ends are in document pixels.
    /// </summary>
    public void PreviewGradient(SKPoint from, SKPoint to)
    {
        _gradientStart = from;
        _gradientEnd = to;
        _gradientDrag = true;
        InvalidateVisual();
    }

    /// <summary>Drops the shape preview's own pixels, which are the canvas' to free.</summary>
    private void ForgetShapePreview()
    {
        _shapePreview?.Dispose();
        _shapePreview = null;
        _shapePreviewBox = default;
        _shapePreviewAt = default;
    }

    /// <summary>
    /// Draws a shape as if it were being dragged, so the self check can look at a preview that needs a pointer
    /// to make. The box is in document pixels.
    /// </summary>
    public void PreviewShape(SKRectI box)
    {
        _shapeBox = box;
        _shapeAnchor = new SKPoint(box.Left, box.Top);
        _shapeEnd = new SKPoint(box.Right, box.Bottom);
        _shaping = true;
        InvalidateVisual();
    }

    /// <summary>Whether a crop drag is under way, so the tool is not reset under it.</summary>
    public bool CropDragging => _cropDragging is not CropDrag.None;

    /// <summary>Whether the crop frame is being moved whole rather than by an edge, which snaps differently.</summary>
    public bool CropMoving => _cropDragging == CropDrag.Move;

    /// <summary>The ratio a crop drag is held to, or null for a free frame; the app sets it.</summary>
    public double? CropRatio { get; set; }

    private SKRectI WholeCanvas() => _document is { } document
        ? SKRectI.Create(0, 0, document.Width, document.Height)
        : SKRectI.Create(0, 0, 1, 1);

    /// <summary>Whether a document point is inside a frame, which is what tells a move from a new frame.</summary>
    private static bool Inside(SKRectI frame, SKPoint point) =>
        point.X >= frame.Left && point.X < frame.Right && point.Y >= frame.Top && point.Y < frame.Bottom;

    /// <summary>Lets go of an outline that is being drawn, as Escape does in the Mac build.</summary>
    public void CancelDraft()
    {
        _selecting = false;
        ClearDraft();
        InvalidateVisual();
    }

    /// <summary>
    /// Takes hold of a handle, or of the box itself, if the click landed on one. The drag is measured from
    /// where it began rather than compounded, so the app can hold the box it started with.
    /// </summary>
    private bool beginTransformDrag(LayerTransform box, SKPoint point, KeyModifiers modifiers)
    {
        var handle = TransformEdits.HandleAt(box, point, TransformEdits.Grab / _zoom, TransformEdits.RotateGrip / _zoom);
        // Ctrl on a corner takes hold of that corner on its own, which is a distortion: the shape it is
        // dragged into is not a rectangle with an angle, so the pixels are resampled into it on release.
        if (handle is { } corner && Corner(corner) is { } index && DistortEnabled
            && modifiers.HasFlag(KeyModifiers.Control))
        {
            _distortCorners = TransformEdits.Corners(box);
            _distortHandle = index;
            DistortStarted?.Invoke();
            InvalidateVisual();
            return true;
        }
        if (handle is null && !box.Contains(point)) return false;
        _transformDragging = true;
        _handle = handle;
        _dragOriginal = box;
        _dragStart = point;
        TransformStarted?.Invoke();
        return true;
    }

    /// <summary>Which of the four corners a handle is, or null for the edges, the middle and the turn grip.</summary>
    private static int? Corner(TransformHandle handle) => handle switch
    {
        TransformHandle.TopLeft => 0,
        TransformHandle.TopRight => 1,
        TransformHandle.BottomRight => 2,
        TransformHandle.BottomLeft => 3,
        _ => null,
    };

    /// <summary>Closes an open polygonal lasso and hands it to the app.</summary>
    private void CompleteSelection()
    {
        var points = _lasso.ToList();
        var mode = _draftMode;
        ClearDraft();
        InvalidateVisual();
        LassoFinished?.Invoke(points, mode);
    }

    private void ClearDraft()
    {
        _lasso.Clear();
        _lassoPointer = null;
        _selectionBox = null;
        _draftMode = SelectionMode.Replace;
    }

    /// <summary>Whether a new sample is far enough from the last to be worth keeping, in document pixels.</summary>
    private bool Moved(SKPoint point) =>
        _lasso.Count == 0 || Math.Abs(point.X - _lasso[^1].X) + Math.Abs(point.Y - _lasso[^1].Y) >= 0.5f;

    /// <summary>Whether a click is on the point a polygon started from, within eight screen pixels.</summary>
    private bool Near(Point screen, SKPoint document)
    {
        var start = ToScreen(document);
        return Math.Abs(screen.X - start.X) <= 8 && Math.Abs(screen.Y - start.Y) <= 8;
    }

    /// <summary>
    /// How a new shape meets the selection already there: Option takes away, Shift adds, otherwise it
    /// replaces, as the Mac build reads the modifiers.
    /// </summary>
    private static SelectionMode ModeOf(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Alt) ? SelectionMode.Subtract
        : modifiers.HasFlag(KeyModifiers.Shift) ? SelectionMode.Add
        : SelectionMode.Replace;

    /// <summary>The outline of what is selected, and of the shape being dragged or clicked out.</summary>
    private void DrawSelection(DrawingContext context)
    {
        DrawCaret(context);
        DrawGradient(context);
        DrawShape(context);
        DrawDraft(context);
        DrawCrop(context);
        DrawTransform(context);
        if (_document?.Selection.Path is not { } path || path.IsEmpty) return;
        // A white line with a black dashed one over it, as the Mac's overlay draws the marching ants.
        var outline = new Pen(Brushes.White, 1);
        var ants = new Pen(Brushes.Black, 1) { DashStyle = new DashStyle([4.0, 4.0], 0) };
        foreach (var contour in Contours(path))
        {
            for (var index = 1; index < contour.Count; index++)
            {
                var from = ToScreen(contour[index - 1]);
                var to = ToScreen(contour[index]);
                context.DrawLine(outline, from, to);
                context.DrawLine(ants, from, to);
            }
        }
    }

    /// <summary>
    /// The crop frame: what will be kept is left clear, what will go is dimmed, and the eight handles say
    /// where it can be dragged. The frame carries the thirds the Mac's does, so a picture can be laid out on it.
    /// </summary>
    private void DrawCrop(DrawingContext context)
    {
        if (!CropEnabled || _document is not { } document) return;
        var frame = CropBox ?? WholeCanvas();
        var pen = new Pen(Brushes.White, 1);
        var corner = ToScreen(new SKPoint(frame.Left, frame.Top));
        var right = corner.X + frame.Width * _zoom;
        var bottom = corner.Y + frame.Height * _zoom;
        // What goes is dimmed, in four bands around what stays.
        var canvas = new Rect(0, 0, Bounds.Width, Bounds.Height);
        foreach (var band in new[]
                 {
                     new Rect(canvas.X, canvas.Y, canvas.Width, Math.Max(0, corner.Y - canvas.Y)),
                     new Rect(canvas.X, bottom, canvas.Width, Math.Max(0, canvas.Bottom - bottom)),
                     new Rect(canvas.X, corner.Y, Math.Max(0, corner.X - canvas.X), Math.Max(0, bottom - corner.Y)),
                     new Rect(right, corner.Y, Math.Max(0, canvas.Right - right), Math.Max(0, bottom - corner.Y)),
                 })
        {
            context.FillRectangle(DimBrush, band);
        }
        var box = new Rect(corner.X, corner.Y, frame.Width * _zoom, frame.Height * _zoom);
        context.DrawRectangle(null, pen, box);
        var thirds = new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), 1);
        for (var index = 1; index <= 2; index++)
        {
            var across = box.X + box.Width * index / 3;
            var down = box.Y + box.Height * index / 3;
            context.DrawLine(thirds, new Point(across, box.Y), new Point(across, box.Bottom));
            context.DrawLine(thirds, new Point(box.X, down), new Point(box.Right, down));
        }
        foreach (var handle in new[]
                 {
                     TransformHandle.TopLeft, TransformHandle.Top, TransformHandle.TopRight, TransformHandle.Right,
                     TransformHandle.BottomRight, TransformHandle.Bottom, TransformHandle.BottomLeft, TransformHandle.Left,
                 })
        {
            var at = ToScreen(TransformEdits.Position(CropEdits.Box(frame), handle));
            var knobs = new Rect(at.X - 4, at.Y - 4, 8, 8);
            context.FillRectangle(Brushes.White, knobs);
            context.DrawRectangle(null, new Pen(Brushes.Black, 1), knobs);
        }
    }

    /// <summary>The transform box: its outline, its eight handles, the grip that turns it, and the lines a
    /// drag has snapped to. The box is the accent's line with white handles, as the Mac's overlay draws it.</summary>
    private void DrawTransform(DrawingContext context)
    {
        if (SnapLines is ({ } lineX, _))
        {
            var top = ToScreen(new SKPoint((float)lineX, 0));
            context.DrawLine(Skin.SnapPen, top, new Point(top.X, Bounds.Height));
        }
        if (SnapLines is (_, { } lineY))
        {
            var left = ToScreen(new SKPoint(0, (float)lineY));
            context.DrawLine(Skin.SnapPen, left, new Point(Bounds.Width, left.Y));
        }
        if (!TransformEnabled) return;
        // A distortion in progress is its own shape: the box is what it is being dragged away from.
        if (_distortCorners is { } shape)
        {
            for (var index = 0; index < shape.Length; index++)
            {
                context.DrawLine(Skin.TransformPen, ToScreen(shape[index]), ToScreen(shape[(index + 1) % shape.Length]));
                Handle(context, ToScreen(shape[index]), 7);
            }
            return;
        }
        if (TransformBox is not { } box) return;
        var corners = TransformEdits.Corners(box);
        for (var index = 0; index < corners.Length; index++)
        {
            context.DrawLine(Skin.TransformPen, ToScreen(corners[index]),
                ToScreen(corners[(index + 1) % corners.Length]));
        }
        foreach (var handle in Enum.GetValues<TransformHandle>())
        {
            var at = handle == TransformHandle.Rotate
                ? TransformEdits.RotatePosition(box, TransformEdits.RotateGrip / _zoom)
                : TransformEdits.Position(box, handle);
            var centre = ToScreen(at);
            // The grip that turns the box is round, so it is not mistaken for one that resizes it.
            Handle(context, centre, handle == TransformHandle.Rotate ? 8 : 7, round: handle == TransformHandle.Rotate);
        }
    }

    /// <summary>One handle of the transform box: a white square or circle the accent outlines.</summary>
    private static void Handle(DrawingContext context, Point centre, double size, bool round = false)
    {
        var rect = new Rect(centre.X - size / 2, centre.Y - size / 2, size, size);
        if (round)
        {
            var circle = new EllipseGeometry(rect);
            context.DrawGeometry(Skin.HandleFill, Skin.HandlePen, circle);
            return;
        }
        context.FillRectangle(Skin.HandleFill, rect);
        context.DrawRectangle(null, Skin.HandlePen, rect);
    }

    /// <summary>
    /// The shape being drawn right now, before it becomes a selection. It goes down as the Mac's does — a
    /// dark line under a light one — so it reads over the picture and over the pasteboard alike.
    /// </summary>
    private void DrawDraft(DrawingContext context)
    {
        var under = new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0)), 2);
        var over = new Pen(Brushes.White, 1);
        if (Selection == SelectionTool.Polygon)
        {
            if (_lasso.Count > 0) DrawPolyline(context, under, over, _lasso, _lassoPointer);
            return;
        }
        if (!_selecting) return;
        if (Selection == SelectionTool.Lasso)
        {
            DrawPolyline(context, under, over, _lasso, null);
            return;
        }
        if (_selectionBox is not { } box || box.Width <= 0 || box.Height <= 0) return;
        var corner = ToScreen(new SKPoint(box.Left, box.Top));
        var width = box.Width * _zoom;
        var height = box.Height * _zoom;
        foreach (var pen in new[] { under, over })
        {
            if (Selection == SelectionTool.Ellipse)
            {
                context.DrawEllipse(null, pen, new Point(corner.X + width / 2, corner.Y + height / 2),
                    width / 2, height / 2);
                continue;
            }
            context.DrawRectangle(null, pen, new Rect(corner.X, corner.Y, width, height));
        }
    }

    private void DrawPolyline(DrawingContext context, Pen under, Pen over, List<SKPoint> points, SKPoint? to)
    {
        for (var index = 1; index < points.Count; index++)
        {
            var from = ToScreen(points[index - 1]);
            var to2 = ToScreen(points[index]);
            context.DrawLine(under, from, to2);
            context.DrawLine(over, from, to2);
        }
        if (to is { } last && points.Count > 0)
        {
            var from = ToScreen(points[^1]);
            var end = ToScreen(last);
            context.DrawLine(under, from, end);
            context.DrawLine(over, from, end);
        }
    }

    /// <summary>
    /// An outline as polylines: Skia measures the path and hands back points along it, about two screen
    /// pixels apart, so a lasso or an ellipse is drawn as closely as the screen can show it.
    /// </summary>
    private List<List<SKPoint>> Contours(SKPath path)
    {
        var step = (float)Math.Max(0.25, 2 / Math.Max(_zoom, 0.01));
        var contours = new List<List<SKPoint>>();
        using var measure = new SKPathMeasure();
        measure.SetPath(path, forceClosed: true);
        do
        {
            var length = measure.Length;
            if (length <= 0) continue;
            var points = new List<SKPoint> { measure.GetPosition(0) };
            for (var at = step; at < length && points.Count < 20_000; at += step)
            {
                points.Add(measure.GetPosition(at));
            }
            points.Add(measure.GetPosition(length));
            contours.Add(points);
        }
        while (measure.NextContour());
        return contours;
    }

    /// <summary>The whole pixels between two points, whichever way round they are.</summary>
    private static SKRectI Between(SKPoint from, SKPoint to)
    {
        var left = (int)Math.Floor(Math.Min(from.X, to.X));
        var top = (int)Math.Floor(Math.Min(from.Y, to.Y));
        var right = (int)Math.Ceiling(Math.Max(from.X, to.X));
        var bottom = (int)Math.Ceiling(Math.Max(from.Y, to.Y));
        return SKRectI.Create(left, top, right - left, bottom - top);
    }

    /// <summary>The stroke so far, drawn as a line of the brush's width while the pointer is down.</summary>
    private void DrawStroke(DrawingContext context)
    {
        if (!_painting || _stroke.Count < 2) return;
        var colour = Color.FromArgb(170, (byte)(Brush.Red * 255), (byte)(Brush.Green * 255), (byte)(Brush.Blue * 255));
        var pen = new Pen(new SolidColorBrush(colour), Math.Max(1, Brush.Diameter * _zoom), lineCap: PenLineCap.Round);
        for (var index = 1; index < _stroke.Count; index++)
        {
            context.DrawLine(pen, ToScreen(_stroke[index - 1]), ToScreen(_stroke[index]));
        }
    }

    private Point ToScreen(SKPoint document) =>
        new((document.X - _origin.X) * _zoom, (document.Y - _origin.Y) * _zoom);

    /// <summary>
    /// Where a document point is drawn in this control. The checks that drive a pointer at the canvas aim
    /// through this, so a check cannot aim at one place while the tool reads another.
    /// </summary>
    internal Point InView(SKPoint document) => ToScreen(document);

    /// <summary>The document point under a point of this control — the same mapping a pointer goes through, which
    /// is what the ruler strips use to turn a point on themselves into a guide's position.</summary>
    internal SKPoint InDocument(Point inView) => ToDocument(inView);

    private SKPoint ToDocument(Point screen) =>
        new((float)(_origin.X + screen.X / _zoom), (float)(_origin.Y + screen.Y / _zoom));

    /// <summary>A copy of a rendered piece in the order the screen wants: blue before red, premultiplied.</summary>
    private static WriteableBitmap ToImage(SKBitmap source)
    {
        var target = new WriteableBitmap(new PixelSize(source.Width, source.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var locked = target.Lock())
        {
            var pixels = source.GetPixelSpan();
            unsafe
            {
                var start = (byte*)locked.Address;
                for (var y = 0; y < source.Height; y++)
                {
                    var from = pixels.Slice(y * source.Width * 4, source.Width * 4);
                    var to = new Span<byte>(start + y * locked.RowBytes, source.Width * 4);
                    for (var x = 0; x < source.Width; x++)
                    {
                        to[x * 4] = from[x * 4 + 2];
                        to[x * 4 + 1] = from[x * 4 + 1];
                        to[x * 4 + 2] = from[x * 4];
                        to[x * 4 + 3] = from[x * 4 + 3];
                    }
                }
            }
        }
        return target;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        // A Camera Raw guide comes before everything: the panel has asked to draw lines on the picture, and
        // nothing else the canvas does should happen while the pointer is down.
        if (_document is not null && UprightDrawing && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var at = ToDocument(e.GetPosition(this));
            _uprightDraft = (at, at);
            CaptureInteraction(e.Pointer);
            e.Handled = true;
            InvalidateVisual();
            return;
        }
        // Sampling comes next, and before the tool: a panel that picks a colour off the picture (the eyedropper,
        // and Select ▸ Colour Range while its panel is up) takes the press whatever tool is in hand, as the
        // Mac build's canvas does.
        if (EyedropperOnClick && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _sampling = true;
            _sampleAt = e.GetPosition(this);
            // The colour the pick replaces, which the ring's lower half goes on showing while the drag does.
            _sampleOriginal = new SKColor((byte)Math.Round(Brush.Red * 255), (byte)Math.Round(Brush.Green * 255),
                (byte)Math.Round(Brush.Blue * 255));
            Sample();
            CaptureInteraction(e.Pointer);
            e.Handled = true;
            EyedropperClicked?.Invoke(ToDocument(_sampleAt), e.KeyModifiers);
            return;
        }
        if (GuidesMovable && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && GuideAt(ToDocument(e.GetPosition(this))) is { } guide)
        {
            _guideDrag = guide;
            GuideDragStarted?.Invoke();
            CaptureInteraction(e.Pointer);
            e.Handled = true;
            InvalidateVisual();
            return;
        }
        if (TransformEnabled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && TransformHandles && TransformBox is { } box
            && beginTransformDrag(box, ToDocument(e.GetPosition(this)), e.KeyModifiers))
        {
            CaptureInteraction(e.Pointer);
            e.Handled = true;
            return;
        }
        if (CropEnabled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var point = ToDocument(e.GetPosition(this));
            var frame = CropBox ?? WholeCanvas();
            if (e.ClickCount > 1 && Inside(frame, point))
            {
                e.Handled = true;
                CropCommitted?.Invoke();
                return;
            }
            _cropStart = point;
            _cropOriginal = frame;
            _cropHandle = null;
            if (TransformEdits.HandleAt(CropEdits.Box(frame), point, TransformEdits.Grab / _zoom) is { } handle)
            {
                _cropHandle = handle;
                _cropDragging = CropDrag.Resize;
            }
            else if (Inside(frame, point))
            {
                _cropDragging = CropDrag.Move;
            }
            else
            {
                _cropDragging = CropDrag.Create;
            }
            CaptureInteraction(e.Pointer);
            e.Handled = true;
            return;
        }
        if (GradientEnabled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _gradientDrag = true;
            GradientStarted?.Invoke();
            _gradientStart = ToDocument(e.GetPosition(this));
            _gradientEnd = _gradientStart;
            CaptureInteraction(e.Pointer);
            e.Handled = true;
            return;
        }
        if (ShapeEnabled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var point = ToDocument(e.GetPosition(this));
            _shaping = true;
            // The drag starts on a whole pixel, as the Mac build's does.
            _shapeAnchor = new SKPoint(MathF.Round(point.X), MathF.Round(point.Y));
            _shapeBox = SKRectI.Create((int)_shapeAnchor.X, (int)_shapeAnchor.Y, 1, 1);
            _shapeEnd = _shapeAnchor;
            CaptureInteraction(e.Pointer);
            e.Handled = true;
            return;
        }
        if (TypeOnClick && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
            TextClicked?.Invoke(ToDocument(e.GetPosition(this)));
            return;
        }
        if (SampleSourceOnClick && e.KeyModifiers.HasFlag(KeyModifiers.Alt)
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
            CloneSourceClicked?.Invoke(ToDocument(e.GetPosition(this)));
            return;
        }
        if (PaintEnabled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _painting = true;
            _stroke.Clear();
            _stroke.Add(ToDocument(e.GetPosition(this)));
            CaptureInteraction(e.Pointer);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (Selection != SelectionTool.None && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var point = ToDocument(e.GetPosition(this));
            // Control with the pointer down inside the selection takes hold of its pixels instead of drawing a
            // new outline: Photoshop's temporary Move tool, and the Mac build's own Command-drag.
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && PixelsGrabbed?.Invoke(point) == true)
            {
                _pixelsFrom = point;
                CaptureInteraction(e.Pointer);
                e.Handled = true;
                return;
            }
            if (Selection == SelectionTool.Wand)
            {
                e.Handled = true;
                WandClicked?.Invoke(point, ModeOf(e.KeyModifiers));
                return;
            }
            if (Selection == SelectionTool.Polygon)
            {
                // The mode is fixed when the first point goes down, as the Mac build fixes it.
                if (_lasso.Count == 0) _draftMode = ModeOf(e.KeyModifiers);
                var closes = e.ClickCount > 1
                    || (_lasso.Count >= 3 && Near(e.GetPosition(this), _lasso[0]));
                _lasso.Add(point);
                _lassoPointer = point;
                e.Handled = true;
                if (closes) CompleteSelection();
                else InvalidateVisual();
                return;
            }
            _selecting = true;
            _draftMode = ModeOf(e.KeyModifiers);
            _selectionAnchor = point;
            _selectionBox = null;
            _lasso.Clear();
            _lasso.Add(point);
            _lassoPointer = point;
            CaptureInteraction(e.Pointer);
            e.Handled = true;
            return;
        }
        _dragging = e.GetPosition(this);
        CaptureInteraction(e.Pointer);
        base.OnPointerPressed(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        PointerLeftCanvas?.Invoke();
        base.OnPointerExited(e);
    }

    /// <summary>
    /// Where the stroke is drawn to: the pointer's own place when nothing is asked for, and short of it by the
    /// smoothing in hand, so the tip trails the pointer. Each sample covers a share of what is left to it, which
    /// is what makes a high smoothing trail a long way behind without ever falling further than that behind.
    /// </summary>
    private SKPoint Smoothed(SKPoint point)
    {
        var smoothing = Math.Clamp(Brush.Smoothing, 0, 1);
        if (smoothing <= 0 || _stroke.Count == 0) return point;
        var last = _stroke[^1];
        var share = 1 - smoothing * 0.9;
        return new SKPoint((float)(last.X + (point.X - last.X) * share),
            (float)(last.Y + (point.Y - last.Y) * share));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var now = e.GetPosition(this);
        var buttons = e.GetCurrentPoint(this).Properties;
        if (HasCapturedInteraction && !buttons.IsLeftButtonPressed
            && (_dragging is null || (!buttons.IsMiddleButtonPressed && !buttons.IsRightButtonPressed)))
        {
            FinishCapturedInteraction();
        }
        // Every move is reported, whatever the drag in hand is, so a readout that follows the pointer does not
        // stop while a stroke is being painted.
        PointerMovedAt?.Invoke(ToDocument(now));
        // Carrying a selection's pixels: how far they have come is all the window needs to know.
        if (_pixelsFrom is { } cut)
        {
            var at = ToDocument(now);
            PixelsMoved?.Invoke((int)Math.Round(at.X - cut.X), (int)Math.Round(at.Y - cut.Y));
            e.Handled = true;
            return;
        }
        // Sampling with the eyedropper is its own drag: the ring follows the pointer and names what is under it.
        if (_sampling)
        {
            _sampleAt = now;
            Sample();
            e.Handled = true;
            return;
        }
        if (_uprightDraft is { } drawn)
        {
            _uprightDraft = (drawn.Start, ToDocument(now));
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (_distortCorners is { } corners)
        {
            corners[_distortHandle] = ToDocument(now);
            DistortChanged?.Invoke(corners);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (_guideDrag is { } moved && _document is { } document
            && document.Guides.FirstOrDefault(entry => entry.ID == moved) is { } guide)
        {
            var point = ToDocument(now);
            GuideMoved?.Invoke(moved, guide.Axis == Format.GuideAxis.Vertical ? point.X : point.Y);
            e.Handled = true;
            return;
        }
        if (_painting)
        {
            var point = Smoothed(ToDocument(now));
            // Samples arrive thick and fast; only a real move is worth another dab.
            if (_stroke.Count == 0 || Math.Abs(point.X - _stroke[^1].X) + Math.Abs(point.Y - _stroke[^1].Y) >= 0.5f)
            {
                _stroke.Add(point);
                InvalidateVisual();
            }
            base.OnPointerMoved(e);
            return;
        }
        if (_gradientDrag)
        {
            _gradientEnd = ToDocument(now);
            GradientChanged?.Invoke(_gradientStart, _gradientEnd);
            InvalidateVisual();
            base.OnPointerMoved(e);
            return;
        }
        if (_shaping)
        {
            var point = ToDocument(now);
            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var option = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            _shapeEnd = ShapeKind == Format.ShapeKind.Line && shift
                ? ShapeEdits.LineEnd(_shapeAnchor, point)
                : point;
            _shapeBox = ShapeEdits.Box(_shapeAnchor, _shapeEnd,
                square: shift && ShapeKind != Format.ShapeKind.Line, fromCentre: option);
            InvalidateVisual();
            base.OnPointerMoved(e);
            return;
        }
        if (_cropDragging is not CropDrag.None)
        {
            var point = ToDocument(now);
            var ratio = CropRatio;
            var symmetric = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            var frame = _cropDragging switch
            {
                CropDrag.Create => CropEdits.Create(_cropStart, point, ratio, symmetric),
                CropDrag.Move => CropEdits.Move(_cropOriginal, _cropStart, point),
                _ => _cropHandle is { } grabbed
                    ? CropEdits.Resize(_cropOriginal, grabbed, _cropStart, point, ratio, symmetric)
                    : _cropOriginal,
            };
            CropChanged?.Invoke(frame, point);
            base.OnPointerMoved(e);
            return;
        }
        if (_dragOriginal.IsValid && TransformBox is not null && _transformDragging)
        {
            var point = ToDocument(now);
            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var option = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            var draft = _handle switch
            {
                TransformHandle.Rotate => TransformEdits.Rotate(_dragOriginal, _dragStart, point, steps: shift),
                null => TransformEdits.Move(_dragOriginal, point.X - _dragStart.X, point.Y - _dragStart.Y, axisLock: shift),
                var handle => TransformEdits.Resize(_dragOriginal, handle.Value, _dragStart, point,
                    lockRatio: TransformLockRatio != shift, fromCentre: option),
            };
            TransformChanged?.Invoke(draft);
            base.OnPointerMoved(e);
            return;
        }
        if (_selecting)
        {
            var point = ToDocument(now);
            if (Selection == SelectionTool.Lasso && Moved(point)) _lasso.Add(point);
            if (Selection is SelectionTool.Rectangle or SelectionTool.Ellipse)
            {
                _selectionBox = Between(_selectionAnchor, point);
            }
            _lassoPointer = point;
            InvalidateVisual();
            base.OnPointerMoved(e);
            return;
        }
        if (Selection == SelectionTool.Polygon && _lasso.Count > 0)
        {
            _lassoPointer = ToDocument(now);
            InvalidateVisual();
            base.OnPointerMoved(e);
            return;
        }
        if (_dragging is { } last)
        {
            _origin = new SKPoint(
                (float)(_origin.X - (now.X - last.X) / _zoom),
                (float)(_origin.Y - (now.Y - last.Y) / _zoom));
            _dragging = now;
            Moved();
        }
        base.OnPointerMoved(e);
    }

    /// <summary>Whether an unfinished gesture still belongs to the current document.</summary>
    public bool HasPendingInteraction => HasCapturedInteraction || _lasso.Count > 0;

    private bool HasCapturedInteraction => _pixelsFrom is not null || _sampling || _uprightDraft is not null
        || _distortCorners is not null || _guideDrag is not null || _painting || _gradientDrag || _shaping
        || _cropDragging is not CropDrag.None || _transformDragging || _selecting || _dragging is not null;

    private void CaptureInteraction(IPointer pointer)
    {
        _interactionPointer = pointer;
        pointer.Capture(this);
    }

    /// <summary>Commits the gesture before saving, switching tools or changing documents.</summary>
    public void FinishInteraction()
    {
        FinishCapturedInteraction();
        if (_lasso.Count > 0)
        {
            if (_lasso.Count >= 3) CompleteSelection();
            else ClearDraft();
            InvalidateVisual();
        }
    }

    private void FinishCapturedInteraction()
    {
        if (!HasCapturedInteraction) return;
        var pixels = _pixelsFrom is not null;
        var upright = _uprightDraft;
        var distort = _distortCorners;
        var guide = _guideDrag is not null;
        var stroke = _painting ? _stroke.ToArray() : [];
        var gradient = _gradientDrag;
        var gradientStart = _gradientStart;
        var gradientEnd = _gradientEnd;
        var shape = _shaping;
        var shapeAnchor = _shapeAnchor;
        var shapeBox = _shapeBox;
        var shapeEnd = _shapeEnd;
        var transform = _transformDragging;
        var selecting = _selecting;
        var selection = Selection;
        var selectionBox = _selectionBox;
        var mode = _draftMode;
        var lasso = selecting ? _lasso.ToArray() : [];
        var pointer = _interactionPointer;

        // Clear every flag before releasing capture or calling the owner: either can reenter this method.
        _interactionPointer = null;
        _pixelsFrom = null;
        _sampling = false;
        _sampleRing = null;
        _uprightDraft = null;
        _distortCorners = null;
        _guideDrag = null;
        _painting = false;
        _stroke.Clear();
        _gradientDrag = false;
        _shaping = false;
        _cropDragging = CropDrag.None;
        _cropHandle = null;
        _transformDragging = false;
        _handle = null;
        _selecting = false;
        _dragging = null;
        SnapLines = (null, null);
        if (selecting) ClearDraft();
        if (pointer?.Captured == this) pointer.Capture(null);
        InvalidateVisual();

        if (pixels) PixelsDropped?.Invoke();
        if (upright is { } drawn) UprightDrawn?.Invoke(drawn.Start, drawn.End);
        if (distort is not null) DistortFinished?.Invoke(distort);
        if (guide) GuideDragFinished?.Invoke();
        if (stroke.Length > 0) StrokeFinished?.Invoke(stroke);
        if (gradient) GradientFinished?.Invoke(gradientStart, gradientEnd);
        if (shape) ShapeFinished?.Invoke(shapeAnchor, shapeBox, shapeEnd);
        if (transform) TransformFinished?.Invoke();
        if (selecting && selection == SelectionTool.Lasso) LassoFinished?.Invoke(lasso, mode);
        else if (selecting && selectionBox is { } box) MarqueeFinished?.Invoke(box, mode);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        FinishCapturedInteraction();
        base.OnPointerCaptureLost(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        FinishCapturedInteraction();
        base.OnPointerReleased(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        ZoomBy(e.Delta.Y > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
        base.OnPointerWheelChanged(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        InvalidateVisual();
    }
}
