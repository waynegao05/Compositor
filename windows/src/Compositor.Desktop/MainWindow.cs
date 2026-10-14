using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;
using SelectionMode = Compositor.Core.Document.SelectionMode;

namespace Compositor.Desktop;

/// <summary>
/// The editor window: the canvas, the layers panel and a status line. Projects are opened as folders,
/// because on Windows a `.comp` is an ordinary directory rather than the file-like package the Mac sees.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>The panels, the strip and the bar under the canvas: the Mac's editor background.</summary>
    private static readonly IBrush Panel = Skin.ChromeBrush;
    /// <summary>The tab in front, marked: a lighter panel than the strip it sits on.</summary>
    private static readonly IBrush Accent = Skin.TabFront;
    private static readonly IBrush Ink = Skin.LabelBrush;

    private readonly CanvasView _canvas = new();
    /// <summary>The layers panel: several rows may be selected, and the current row is the one an edit acts on.</summary>
    private readonly ListBox _layers = new() { SelectionMode = Avalonia.Controls.SelectionMode.Multiple };
    private readonly TextBlock _status = new()
    {
        Margin = new Thickness(10, 3, 10, 3),
        Foreground = Skin.SecondaryBrush,
        FontSize = 11,
        TextAlignment = Avalonia.Media.TextAlignment.Right,
    };
    /// <summary>What the picture is — its zoom, its size and its colour space — as the Mac's own line has.</summary>
    private readonly TextBlock _statusInfo = new()
    {
        Margin = new Thickness(10, 3, 10, 3),
        Foreground = Skin.SecondaryBrush,
        FontSize = 11,
    };

    /// <summary>The tool options above the canvas, which is a face on the same settings the Tools menu moves.</summary>
    private readonly ToolOptionsBar _optionsBar;

    /// <summary>The rail of tools down the left of the canvas, as the Mac keeps its own.</summary>
    private readonly ToolRail _rail = new();

    /// <summary>The Layers panel's own column, which steps aside while the Camera Raw panel has the right edge.</summary>
    private readonly Border _layersSide = new() { Width = 280, Background = Panel };
    /// <summary>Where the Camera Raw panel sits: the right edge at the window's full height, as the Mac docks it.</summary>
    private readonly Border _cameraRawHost = new() { Width = 440, Background = Panel, IsVisible = false };
    /// <summary>The Camera Raw panel while it is up — the one panel here that is not a dialog.</summary>
    private CameraRawPanel? _cameraRaw;
    /// <summary>The layer the Camera Raw panel was opened on, so what Apply writes to does not follow the panel selection.</summary>
    private Guid? _cameraRawLayer;
    /// <summary>The scope the last Camera Raw preview counted, for the panel to draw when that picture is shown.</summary>
    private CameraRawScope? _cameraRawScope;

    /// <summary>The colour picker while it is up, of which there is at most one: a swatch clicked again brings
    /// the one that is open to the front rather than opening a second.</summary>
    private ColorPickerDialog? _picker;

    /// <summary>Select ▸ Colour Range while its panel is up: the colours picked and the selection they make.</summary>
    private ColorRangeSession? _colorRange;
    private ColorRangePanel? _colorRangePanel;
    /// <summary>Finishes the panel against its original document and history, including tab switches.</summary>
    private Action<bool>? _finishColorRange;

    /// <summary>The picker while the self check has it up, so the caller can photograph that window too — the
    /// picture of the editor does not hold a window of its own.</summary>
    internal ColorPickerDialog? Picker => _picker;

    /// <summary>
    /// What the filter panels were last used with, which is where they open again: the Mac build keeps one set
    /// of filter settings for the session and its sheets read them. A panel edits a copy of these, so only
    /// Apply changes them, and a Cancel leaves every filter's amounts as they were.
    /// </summary>
    private FilterSettings _filterAmounts = new();
    private DitherStyle _ditherLook = DitherStyle.Atkinson;
    private DitherSettings _ditherAmounts = new();
    private CameraRawSettings _cameraRawAmounts = new();

    /// <summary>One row, because what ⌘E does depends on the panel selection: it is named for it here.</summary>
    private readonly MenuItem _merge = new();

    /// <summary>The clipping, mask and visibility rows, whose names and availability follow the selection.</summary>
    private readonly MenuItem _visibility = new();
    private readonly MenuItem _showGrid = new();
    private readonly MenuItem _recentMenu = new() { Header = L10n.T("Open _Recent") };
    private readonly RecentProjects _recent = new(RecentProjects.DefaultPath);
    private readonly MenuItem _snapToCanvas = new();
    private readonly MenuItem _snapToGuides = new();
    private readonly MenuItem _snapToLayers = new();
    private readonly MenuItem _snapToGrid = new();
    /// <summary>How thick the ruler strips are, in points.</summary>
    private const double RulerThickness = 18;

    private readonly RulerStrip _rulerAcross = new() { Axis = GuideAxis.Horizontal, Height = RulerThickness };
    private readonly RulerCorner _rulerCorner = new();
    private readonly RulerStrip _rulerDown = new() { Axis = GuideAxis.Vertical, Width = RulerThickness };
    private readonly MenuItem _showRulers = new();
    private bool _rulersVisible;
    private readonly MenuItem _showGuides = new();
    private readonly MenuItem _lockGuides = new();
    private readonly MenuItem _showTransform = new();
    private readonly MenuItem _pixelGrid = new();
    private readonly MenuItem _snapping = new();
    private bool _guidesVisible = true;
    private bool _guidesLocked;
    private bool _transformShown = true;
    private bool _pixelGridShown;
    private bool _snappingOn = true;

    private SnapTo _snapTo = SnapTo.All;
    private LayoutGrid _grid = new();
    private bool _gridVisible;
    /// <summary>The view's switches as they were left last time, which the View menu opens with.</summary>
    private readonly ToolDefaults _tools = ToolDefaults.Load(ToolDefaults.DefaultPath);
    private readonly ComboBox _blend = new();
    /// <summary>What each item of the blend list stands for: the mode, or nothing for a rule between groups.</summary>
    private readonly List<LayerBlendMode?> _blendRows = [];
    private readonly Slider _opacity = new() { Minimum = 0, Maximum = 100, Width = 130 };
    private readonly TextBlock _opacityReadout = new() { Width = 40, VerticalAlignment = VerticalAlignment.Center };
    private bool _showingAppearance;
    private bool _opacityDragging;
    private ClipboardImage? _clipboard;
    private FilterPreview? _preview;
    private DispatcherTimer? _previewTimer;
    private Func<CanvasDocument, bool>? _previewApply;
    /// <summary>The one layer a single-layer preview stands for, so a panel that thinks in layers can be shown.</summary>
    private Guid? _previewLayer;
    /// <summary>The box and the layers a distortion drag began with, so every step is measured from it.</summary>
    private LayerTransform? _distortBox;
    private List<Guid>? _distortLayers;

    /// <summary>The blend modes in the order the menu lists them, which is the order the enum declares.</summary>
    private static readonly LayerBlendMode[] BlendModes = Enum.GetValues<LayerBlendMode>();
    private readonly MenuItem _adjustmentMenu = new() { Header = L10n.T("New _Adjustment Layer") };
    private readonly MenuItem _effectsMenu = new() { Header = L10n.T("Layer _Effects") };
    private MenuItem _adjustmentSettings = new();
    private MenuItem _clearEffects = new();
    private readonly MenuItem _clipping = new();
    private readonly MenuItem _addMask = new() { Header = L10n.T("Add _Mask") };
    private readonly MenuItem _maskToggle = new();
    private readonly MenuItem _maskLink = new();

    /// <summary>The rest of the Layer menu, so all of it can go dead together when nothing is selected.</summary>
    private readonly List<MenuItem> _layerItems = [];
    private readonly List<MenuItem> _documentItems = [];

    /// <summary>
    /// The text being typed on the canvas, if any: it keeps the whole of the typing as one undo step, and
    /// letting it go puts the layer back the way it was.
    /// </summary>
    private TextSession? _text;

    /// <summary>The Gradient tool's own rows in the Tools menu, which the options bar also shows.</summary>
    private readonly MenuItem _gradientMenu = new() { Header = L10n.T("Gradient _options") };

    /// <summary>The Shape tool's own rows in the Tools menu, which the options bar also shows.</summary>
    private readonly MenuItem _shapeKinds = new() { Header = L10n.T("Shape _kind") };

    /// <summary>The crop frame's shape: the canvas's own, or one of the fixed ratios.</summary>
    private readonly MenuItem _cropRatios = new() { Header = L10n.T("Crop _ratio") };

    /// <summary>The crop frame while the Crop tool is in hand; null is the whole canvas.</summary>
    private SKRectI? _cropFrame;

    /// <summary>The guide being pulled off a ruler, so the drag that follows moves that one rather than making a
    /// new one for every step of it.</summary>
    private Guid? _pulledGuide;
    private Guid? _draggedGuide;

    /// <summary>Whether a brush stroke goes on the active layer's mask instead of its pixels.</summary>
    private readonly MenuItem _paintOnMask = new()
    {
        Header = L10n.T("Paint on the layer _mask"),
        ToggleType = MenuItemToggleType.CheckBox,
    };

    /// <summary>Whether the brush paints or erases; on a mask, that is white or black.</summary>
    private readonly MenuItem _eraseToggle = new()
    {
        Header = L10n.T("Brush _erases"),
        ToggleType = MenuItemToggleType.CheckBox,
    };

    private readonly List<(MenuItem Item, Func<CanvasDocument, ImageLayer, bool> Ready)> _layerRows = [];

    /// <summary>
    /// One open project: everything that belongs to a document rather than to the window. There is always a
    /// tab, even before anything is open — the empty one is where the next project goes — so the five names
    /// below always have something to answer for.
    /// </summary>
    private sealed class Tab
    {
        /// <summary>Owns the pixels: the document's layers reference the snapshot's images, so the document
        /// disposes them and the snapshot is dropped rather than disposed.</summary>
        public CanvasDocument? Document { get; set; }

        public DocumentHistory History { get; } = new();

        /// <summary>Where this project was opened from, so Save writes back to it.</summary>
        public string? Path { get; set; }

        /// <summary>The project's folder watched for someone else writing it.</summary>
        public ProjectWatch? Watch { get; set; }

        /// <summary>The layer behind each row of the panel, so a selection can be turned back into an id.</summary>
        public List<Guid> Rows { get; } = [];

        /// <summary>Which row the panel had selected, so a tab comes back the way it was left.</summary>
        public int SelectedRow { get; set; }

        /// <summary>What the tab is called: the project's name, or what it is until it is saved.</summary>
        public string Name => Path is { } path ? System.IO.Path.GetFileName(path) : L10n.T("Untitled");
    }

    private readonly List<Tab> _tabs = [];
    private Tab _open = new();
    private DispatcherTimer? _watchTimer;
    private readonly StackPanel _tabStrip = new() { Orientation = Orientation.Horizontal, Spacing = 4 };

    private CanvasDocument? _document
    {
        get => _open.Document;
        set => _open.Document = value;
    }

    private DocumentHistory _history => _open.History;

    private List<Guid> _rows => _open.Rows;

    private string? _projectPath
    {
        get => _open.Path;
        set => _open.Path = value;
    }

    private ProjectWatch? _watch
    {
        get => _open.Watch;
        set => _open.Watch = value;
    }

    /// <summary>Which pointer tool is in hand, and the menu rows that show it.</summary>
    private readonly Dictionary<Tool, MenuItem> _toolItems = [];
    private Tool _tool = Tool.Pan;

    /// <summary>The layer a transform drag is editing, while the pointer is down.</summary>
    private Guid? _transforming;

    /// <summary>What the box was when the drag began, and where every layer it moves was.</summary>
    private LayerTransform? _transformBox;
    private Dictionary<Guid, LayerTransform> _transformOriginals = [];
    private Dictionary<Guid, LayerTransform?> _transformMaskOriginals = [];

    /// <summary>What the tool in hand reads, which the options bar is a face on: the brush's settings, the
    /// selection's and the rest. One object holds them all so the bar can be built once.</summary>
    private readonly ToolOptions _options = new();

    /// <summary>Where the Clone Stamp copies from, and the offset a stroke is copying through.</summary>
    private SKPoint? _cloneSource;
    private SKPointI? _cloneOffset;

    /// <summary>The shortcut keys as they were left last time, which the table in force is built from.</summary>
    private readonly ShortcutDefaults _shortcutSettings = ShortcutDefaults.Load(ShortcutDefaults.DefaultPath);
    /// <summary>Every row's key as it stands, the rows on their original key included.</summary>
    private Dictionary<string, ShortcutChord> _keys = [];
    /// <summary>The same table the other way about, so a key pressed finds the row it belongs to.</summary>
    private Dictionary<ShortcutChord, string> _byKey = [];
    /// <summary>What each row does. A verb answers false for a key that does not apply, which leaves it alone.</summary>
    private readonly Dictionary<string, Func<ShortcutChord, bool>> _verbs = [];
    /// <summary>The menu rows that show a key, and the shortcut row each of them is the face of.</summary>
    private readonly List<(MenuItem Item, string ID)> _keyRows = [];
    /// <summary>The rows that are a digit of the brush's opacity, so two typed together make one exact amount.</summary>
    private readonly HashSet<string> _opacityRows = [];
    /// <summary>The opacity digits typed so far, and the tool the held Hand is standing in for.</summary>
    private string _opacityTyped = "";
    private Tool? _toolBeforeHand;

    public MainWindow()
    {
        Title = "Compositor";
        Width = 1280;
        Height = 820;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // The two calls a screen has to be asked for are made once the window is open, which is when there is a
        // screen to ask.
        Opened += (_, _) => FitToScreen();
        Opened += (_, _) => AppDiagnostics.Record($"Editor window opened: scaling {RenderScaling:0.##}, size {Bounds.Width:0} x {Bounds.Height:0}.");
        // The picker is a window of its own and the app is left running until the last window closes, so it
        // goes with the editor rather than being left behind to hold the session open.
        Closing += OnWindowClosing;
        Background = Skin.ChromeBrush;
        // The window's plain labels (a heading, a readout) take their colour from here, as the Mac's do from
        // the appearance; controls that name their own text keep it.
        TextElement.SetForeground(this, Skin.LabelBrush);
        _canvas.StrokeFinished = Painted;
        _canvas.MarqueeFinished = (box, mode) => MarqueeFinished(box, mode, _tool == Tool.Ellipse);
        _canvas.LassoFinished = (points, mode) => LassoFinished(points, mode, _tool == Tool.Polygon);
        _canvas.WandClicked = WandClicked;
        _canvas.CloneSourceClicked = CloneSourceChosen;
        _canvas.EyedropperClicked = Picked;
        _canvas.ShapeFinished = ShapeFinished;
        _canvas.GradientFinished = GradientFinished;
        _canvas.GradientStarted = GradientStarted;
        _canvas.GradientChanged = GradientChanged;
        _canvas.DistortStarted = DistortStarted;
        _canvas.DistortChanged = DistortChanged;
        _canvas.DistortFinished = DistortFinished;
        _canvas.GuideDragStarted = GuideDragStarted;
        _canvas.GuideMoved = GuideMoved;
        _canvas.GuideDragFinished = GuideDragFinished;
        // A press on a ruler pulls a new guide onto the canvas, as Photoshop's rulers do.
        _rulerAcross.Grabbed = (axis, at) => GuideGrabbed(_rulerAcross, axis, at);
        _rulerAcross.Dragged = at => GuidePulled(_rulerAcross, at);
        _rulerAcross.LetGo = GuidePulledOff;
        _rulerAcross.PointerCaptureLost += (_, _) => GuidePulledOff();
        _rulerDown.Grabbed = (axis, at) => GuideGrabbed(_rulerDown, axis, at);
        _rulerDown.Dragged = at => GuidePulled(_rulerDown, at);
        _rulerDown.LetGo = GuidePulledOff;
        _rulerDown.PointerCaptureLost += (_, _) => GuidePulledOff();
        // The pixels inside a selection can be taken hold of and dragged, which is the Mac build's other way of
        // moving them besides Command with an arrow key.
        _canvas.PixelsGrabbed = PixelsTaken;
        _canvas.PixelsMoved = PixelsDragged;
        _canvas.PixelsDropped = PixelsLetGo;
        _canvas.CropChanged = CropChanged;
        _canvas.CropCommitted = ApplyCrop;
        BuildCropRatios();
        BuildShapeKinds();
        BuildGradientMenu();
        BuildAdjustmentMenu();
        RefreshRecent();
        _canvas.TextClicked = TypeHere;
        _canvas.TextTyped = TypedText;
        _canvas.TextBackspaced = BackspacedText;
        _canvas.TextDeleted = DeletedText;
        _canvas.TextMoved = MovedTextCaret;
        _canvas.TextCommitted = CommitText;
        _canvas.TextCancelled = CancelText;
        _canvas.TransformStarted = TransformStarted;
        _canvas.TransformChanged = TransformChanged;
        _canvas.TransformFinished = TransformFinished;
        _paintOnMask.Click += (_, _) => SetPaintingMask(!_options.PaintOnMask);
        _eraseToggle.Click += (_, _) => SetErasing(!_options.Erase);
        // The rail is the same set of tools the Tools menu has: picking either marks both.
        _rail.Chosen += SetTool;
        _rail.ColoursSwapped += SwapColours;
        _rail.ColoursReset += ResetColours;
        _rail.ColourChosen += ChooseColour;
        _rail.ShowColours(BrushColour(), BackgroundColour());
        // The options bar is a face on the same settings the Tools menu moves, so either one marks the other.
        _optionsBar = new ToolOptionsBar(_options);
        _optionsBar.Changed += OptionsChanged;
        _optionsBar.BrushSettingAsked += which => _ = SetBrush(which);
        _optionsBar.WandSettingAsked += which => _ = SetWand(which);
        _optionsBar.ShapeSettingAsked += which => _ = SetShape(which);
        _optionsBar.ColourAsked += ChooseColour;
        _optionsBar.FlipAsked += horizontally => Flip(horizontally, canvas: false);
        _optionsBar.CropRatioChosen += index => SetCropRatio(CropRatios[index].Ratio);
        _optionsBar.CropApplied += ApplyCrop;
        _optionsBar.CropCancelled += CancelCrop;
        _optionsBar.TextAsked += () => _ = EditText();
        // Picking the marquee's shape or the lasso's kind is picking the tool that draws it, so the bar goes
        // through SetTool and the menu, the rail and the canvas all follow.
        _optionsBar.MarqueeShapeChosen += ellipse => SetTool(ellipse ? Tool.Ellipse : Tool.Marquee);
        _optionsBar.LassoKindChosen += polygonal => SetTool(polygonal ? Tool.Polygon : Tool.Lasso);
        _optionsBar.ShowCropRatios([.. CropRatios.Select(entry => entry.Label)]);
        _merge.Click += (_, _) => MergeLayers();
        _visibility.Click += (_, _) => ToggleVisibility();
        // The View switches open where they were left last time, as the Mac build's tool defaults keep them.
        _snapTo = _tools.SnapTo;
        _grid = _tools.Grid();
        _gridVisible = _tools.ShowGrid;
        _canvas.Grid = _gridVisible ? _grid : null;
        _showGrid.Header = L10n.T(_gridVisible ? "_Hide Grid" : "Show _Grid");
        _showGrid.Click += (_, _) => ShowGrid();
        _rulersVisible = _tools.ShowRulers;
        _showRulers.Header = L10n.T("R_ulers");
        _showRulers.ToggleType = MenuItemToggleType.CheckBox;
        _showRulers.IsChecked = _rulersVisible;
        _showRulers.Click += (_, _) => ShowRulers();
        _guidesVisible = _tools.ShowGuides;
        _guidesLocked = _tools.LockGuides;
        _transformShown = _tools.ShowTransformControls;
        _pixelGridShown = _tools.PixelGrid;
        _snappingOn = _tools.Snapping;
        Toggle("_Guides", _showGuides, _guidesVisible, () => ShowGuides());
        Toggle("_Lock Guides", _lockGuides, _guidesLocked, () => LockGuides());
        Toggle("Show _Transform Controls", _showTransform, _transformShown, () => ShowTransformControls());
        Toggle("_Pixel Grid (800% and above)", _pixelGrid, _pixelGridShown, () => ShowPixelGrid());
        Toggle("S_nap", _snapping, _snappingOn, () => ShowSnapping());
        PushViewSwitches();
        _canvas.ViewportChanged = UpdateRulers;
        foreach (var (item, flag, label) in SnapRows())
        {
            // A tick box, so the four read as switches rather than as commands. They open where they were left,
            // as the Mac build's tool defaults do.
            item.Header = L10n.T(label);
            item.ToggleType = MenuItemToggleType.CheckBox;
            item.IsChecked = _tools.SnapTo.HasFlag(flag);
            item.Click += (_, _) => ToggleSnapTo(flag, label);
        }
        _clipping.Click += (_, _) => ToggleClipping();
        _maskToggle.Click += (_, _) => ToggleMask();
        _maskLink.Click += (_, _) => ToggleMaskLink();
        _addMask.Items.Add(Command("_Reveal All (White)", () => AddMask(revealing: true)));
        _addMask.Items.Add(Command("_Hide All (Black)", () => AddMask(revealing: false)));
        _layers.SelectionChanged += (_, _) =>
        {
            InvalidatePendingClipboardPaste();
            UpdateLayerMenu();
        };
        _tabs.Add(_open);
        Content = Layout();
        RefreshTabs();
        UpdateLayerMenu();
        // The tool in hand at the start is the Pan, which the rail marks on its own — but the options bar is
        // only ever told what to show when a tool is picked, so without this it opens with every tool's rows at
        // once. Found by opening the real window and looking at it.
        RefreshOptionsBar();
        BuildVerbs();
        RegisterKeys();
        ShowKeys();
        // The shortcuts are delivered from here and nowhere else: no menu row carries a HotKey, because a
        // HotKey works through the window's own key bindings, which a routed handler cannot get in front of —
        // it would either fire twice or fire on a key the table no longer holds. One handler, one table.
        AddHandler(KeyDownEvent, KeyPressed, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, KeyLetGo, RoutingStrategies.Tunnel);
        InitializeExternalFiles();
        Say("File ▸ New Project… for a blank canvas, or File ▸ Open project folder… to load a .comp");
    }

    private Control Layout()
    {
        var menu = new Menu
        {
            Items =
            {
                new MenuItem
                {
                    Header = L10n.T("_File"),
                    Items =
                    {
                        Command("_New Project…", () => _ = NewProject(), "New Project"),
                        Command("_Open project folder…", OpenProject, "Open Project"),
                        _recentMenu,
                        Command("Recover interrupted saves…", () => _ = RecoverInterruptedSaveAsync()),
                        Command("_Import image…", () => _ = ImportImage()),
                        Command("_Save", Save, "Save"),
                        Command("Save _As…", SaveAs, "Save As"),
                        new Separator(),
                        Command("_Export PNG…", ExportPng, "Export PNG"),
                        Command("Export _JPEG…", () => _ = ExportJpeg(), "Export JPEG"),
                        new Separator(),
                        Command("_Close Tab", () => _ = CloseTab(_open), "Close Tab"),
                        Command("E_xit", Close),
                    },
                },
                new MenuItem
                {
                    Header = L10n.T("_Edit"),
                    Items =
                    {
                        Command("_Undo", Undo, "Undo"),
                        Command("_Redo", Redo, "Redo"),
                        new Separator(),
                        Command("Cu_t", Cut, "Cut"),
                        Command("_Copy", Copy, "Copy"),
                        Command("Copy _Merged", CopyMerged, "Copy Merged"),
                        Command("_Paste", Paste, "Paste"),
                        Command("Layer via Cop_y", LayerViaCopy, "Layer via Copy"),
                        new Separator(),
                        Command("Fill with _Foreground Color", () => FillPixels(BrushColour(), "Fill"),
                            "Fill with Foreground Color"),
                        Command("Fill with _Background Color", () => FillPixels(BackgroundColour(), "Fill"),
                            "Fill with Background Color"),
                        Command("_Clear Selection Pixels", ClearPixels),
                        new Separator(),
                        Command("Flip Layer _Horizontal", () => Flip(horizontal: true, canvas: false)),
                        Command("Flip Layer _Vertical", () => Flip(horizontal: false, canvas: false)),
                        Command("Flip _Canvas Horizontal", () => Flip(horizontal: true, canvas: true)),
                        Command("Flip Canvas _Vertical", () => Flip(horizontal: false, canvas: true)),
                        new Separator(),
                        Command("_Keyboard Shortcuts…", () => _ = KeyboardShortcuts()),
                        LanguageMenu(),
                    },
                },
                new MenuItem
                {
                    Header = L10n.T("_Layer"),
                    Items =
                    {
                        LayerCommand("_Duplicate Layer", DuplicateLayer, "Duplicate Layer"),
                        LayerCommand("_Rename Layer…", () => _ = RenameLayer(), "Rename Layer"),
                        LayerCommand("_Delete Layer", DeleteLayer, "Delete Layer"),
                        new Separator(),
                        LayerCommand("Move Layer _Up", () => MoveLayer(1), "Move Layer Up"),
                        LayerCommand("Move Layer _Down", () => MoveLayer(-1), "Move Layer Down"),
                        new Separator(),
                        _clipping,
                        LayerCommand("_Group Selected Layers", GroupSelected, "Group Layers",
                            (document, layer) => document.Layers.Count < LayerPlacement.MaxLayers),
                        LayerCommand("Move _Out of Folder", MoveOutOfFolder, null,
                            (_, layer) => layer.ParentID is not null),
                        _merge,
                        new Separator(),
                        _addMask,
                        _maskToggle,
                        LayerCommand("_Delete Mask", DeleteMask, null, (_, layer) => layer.Mask is not null),
                        LayerCommand("Edit _Text…", () => _ = EditText(), null, (_, layer) => layer.LiveText is not null),
                        _maskLink,
                        new Separator(),
                        DocumentCommand("New Blank Layer", NewBlankLayer, "New Blank Layer"),
                        DocumentCommand("New F_older", NewFolder),
                        _adjustmentMenu,
                        _adjustmentSettings,
                        _effectsMenu,
                        new Separator(),
                        _visibility,
                    },
                },
                new MenuItem
                {
                    Header = L10n.T("_Image"),
                    Items =
                    {
                        Command("_Hue/Saturation…", () => _ = ImageAdjustment(AdjustmentKind.HueSaturation),
                            "Hue/Saturation"),
                        Command("_Levels…", () => _ = ImageAdjustment(AdjustmentKind.Levels), "Levels"),
                        new MenuItem
                        {
                            Header = L10n.T("_Auto Levels"),
                            Items =
                            {
                                Command("Auto _Contrast", () => AutoLevels(LevelsAuto.Contrast)),
                                Command("Auto C_olour", () => AutoLevels(LevelsAuto.Color)),
                                Command("Auto Color + Neutral _Midtones", () => AutoLevels(LevelsAuto.Neutral)),
                            },
                        },
                        Command("C_urves…", () => _ = ImageAdjustment(AdjustmentKind.Curves), "Curves"),
                        Command("_Exposure…", () => _ = ImageAdjustment(AdjustmentKind.Exposure)),
                        Command("Black & _White…", () => _ = ImageAdjustment(AdjustmentKind.BlackWhite)),
                        Command("_Gradient Map…", () => _ = ImageAdjustment(AdjustmentKind.GradientMap)),
                        Command("C_olor Balance…", () => _ = ImageAdjustment(AdjustmentKind.ColorBalance)),
                        new Separator(),
                        Command("_Grain…", () => _ = ImageAdjustment(AdjustmentKind.Grain)),
                        Command("_Invert", () => _ = ImageAdjustment(AdjustmentKind.Invert), "Invert"),
                        new Separator(),
                        Command("_Canvas Size…", () => _ = CanvasSize(), "Canvas Size"),
                        Command("_Image Size…", () => _ = ImageSize(), "Image Size"),
                        Command("_Trim…", () => _ = Trim()),
                    },
                },
                new MenuItem
                {
                    Header = L10n.T("_Filter"),
                    Items =
                    {
                        Command("_Camera Raw Filter…", CameraRawFilter),
                        new Separator(),
                        Command("_Gaussian Blur…", () => _ = ApplyFilter(FilterKind.GaussianBlur)),
                        Command("_Motion Blur…", () => _ = ApplyFilter(FilterKind.MotionBlur)),
                        Command("Add _Noise…", () => _ = ApplyFilter(FilterKind.AddNoise)),
                        Command("_Bloom / Glow…", () => _ = ApplyFilter(FilterKind.BloomGlow)),
                        Command("_Dither…", () => _ = DitherFilter()),
                        new Separator(),
                        Command("_Content-Aware Fill", ContentAwareFill, "Content-Aware Fill"),
                        new Separator(),
                        Command("_Vignette…", () => _ = ApplyFilter(FilterKind.Vignette)),
                        Command("_Tonal Contrast…", () => _ = ApplyFilter(FilterKind.TonalContrast)),
                        Command("Lens _Correction…", () => _ = ApplyFilter(FilterKind.LensCorrection)),
                    },
                },
                new MenuItem
                {
                    Header = L10n.T("_Tools"),
                    Items =
                    {
                        ToolItem("_Pan (drag to scroll)", Tool.Pan, "Hand tool"),
                        ToolItem("_Move (drag the layer; Ctrl-drag a corner to distort it)", Tool.Move,
                            "Move / Transform tool"),
                        ToolItem("Marquee (_rectangular selection)", Tool.Marquee, "Marquee tool"),
                        ToolItem("_Elliptical marquee", Tool.Ellipse),
                        ToolItem("_Lasso (freehand)", Tool.Lasso, "Lasso tool"),
                        ToolItem("_Polygonal lasso (click each corner)", Tool.Polygon),
                        ToolItem("Magic _wand (click a color)", Tool.Wand, "Magic wand"),
                        ToolItem("_Brush", Tool.Brush, "Brush tool"),
                        ToolItem("_Clone stamp (Alt-click a source first)", Tool.Clone, "Clone Stamp"),
                        ToolItem("Blur brush", Tool.Blur, "Blur / Smudge / Liquify"),
                        ToolItem("_Liquify brush (push the pixels around)", Tool.Liquify),
                        ToolItem("S_mudge brush (drag the color along)", Tool.Smudge),
                        ToolItem("Spot _healing", Tool.Heal, "Spot Healing"),
                        ToolItem("_Eyedropper (click the canvas)", Tool.Eyedropper, "Eyedropper tool"),
                        ToolItem("_Type (click where the text goes)", Tool.Type, "Type tool"),
                        ToolItem("_Crop (drag a frame, then apply it)", Tool.Crop, "Crop tool"),
                        ToolItem("_Shape (drag out a rectangle, ellipse or line)", Tool.Shape, "Shape tool"),
                        ToolItem("_Gradient (drag the line it runs along)", Tool.Gradient, "Gradient tool"),
                        new Separator(),
                        _gradientMenu,
                        new Separator(),
                        _shapeKinds,
                        new Separator(),
                        _cropRatios,
                        new Separator(),
                        _paintOnMask,
                        _eraseToggle,
                        new Separator(),
                        new MenuItem
                        {
                            Header = L10n.T("_Brush settings"),
                            Items =
                            {
                                Command("_Size…", () => _ = SetBrush(BrushSetting.Size)),
                                Command("_Hardness…", () => _ = SetBrush(BrushSetting.Hardness)),
                                Command("_Opacity…", () => _ = SetBrush(BrushSetting.Opacity)),
                                Command("_Color…", () => _ = SetBrush(BrushSetting.Colour)),
                                new Separator(),
                                Command("Spot healing: _Content-Aware", () => Heal(HealingMode.ContentAware)),
                                Command("Spot healing: Create _Texture", () => Heal(HealingMode.CreateTexture)),
                                Command("Spot healing: Proximity _Match", () => Heal(HealingMode.ProximityMatch)),
                            },
                        },
                    },
                },
                new MenuItem
                {
                    Header = L10n.T("_Select"),
                    Items =
                    {
                        Command("Select _All", () => Change("Select All", SelectionEdits.SelectAll), "Select All"),
                        Command("_Deselect", Deselect, "Deselect"),
                        Command("_Inverse", () => Change("Inverse", SelectionEdits.Invert), "Inverse Selection"),
                        new Separator(),
                        Command("_Expand…", () => _ = ModifySelection(SelectionAmount.Expand)),
                        Command("_Contract…", () => _ = ModifySelection(SelectionAmount.Contract)),
                        Command("_Feather…", () => _ = ModifySelection(SelectionAmount.Feather)),
                        new Separator(),
                        Command("Layer's _Pixels", SelectLayerPixels),
                        Command("_Mask's Black Areas", SelectMaskBlack),
                        new Separator(),
                        Command("Color _Range…", ColorRange),
                    },
                },
                new MenuItem
                {
                    Header = L10n.T("_View"),
                    Items =
                    {
                        Command("Zoom _in", () => { _canvas.ZoomBy(1.25); Say(); }, "Zoom In"),
                        Command("Zoom _out", () => { _canvas.ZoomBy(1 / 1.25); Say(); }, "Zoom Out"),
                        Command("_Fit on screen", () => { _canvas.Fit(); Say(); }, "Fit Canvas"),
                        Command("Actual _pixels", () => { _canvas.ActualSize(); Say(); }, "Actual Pixels"),
                        new Separator(),
                        _showGrid,
                        _showRulers,
                        _showGuides,
                        _lockGuides,
                        _showTransform,
                        _pixelGrid,
                        _snapping,
                        Command("_Grid Settings…", () => _ = GridSettings()),
                        _snapToCanvas,
                        _snapToGuides,
                        _snapToLayers,
                        _snapToGrid,
                        new Separator(),
                        Command("New _Guide…", () => _ = NewGuide(), "New Guide"),
                        Command("_Clear Guides", ClearGuides),
                    },
                },
                new MenuItem
                {
                    Header = L10n.T("_Help"),
                    Items =
                    {
                        Command("Windows Version Info…", () => _ = CheckForUpdates()),
                        new Separator(),
                        Command("Export Diagnostics…", () => _ = ExportDiagnostics()),
                        Command("Open Diagnostics Folder", () => _ = OpenDiagnosticsFolder()),
                    },
                },
            },
        };

        var layers = new DockPanel();
        layers.Children.Add(new TextBlock
        {
            Text = L10n.T("Layers"),
            Margin = new Thickness(10, 8, 10, 6),
            Foreground = Ink,
            FontWeight = FontWeight.SemiBold,
        });
        DockPanel.SetDock(layers.Children[0], Dock.Top);
        layers.Children.Add(Appearance());
        DockPanel.SetDock(layers.Children[1], Dock.Top);
        layers.Children.Add(new ScrollViewer { Content = _layers });
        _layersSide.Child = layers;

        var statusBar = new Border
        {
            Height = 28,
            Background = Panel,
            Child = new DockPanel { Children = { _statusInfo, _status } },
        };
        DockPanel.SetDock(_statusInfo, Dock.Left);

        var root = new DockPanel();
        // The strip under the menu bar: the New canvas button, the project's tabs, and the view's own zoom
        // controls, which is what the Mac puts in its window toolbar.
        var tabs = new Border
        {
            Background = Panel,
            Padding = new Thickness(8, 4, 8, 4),
            Child = Toolbar(),
        };
        DockPanel.SetDock(menu, Dock.Top);
        DockPanel.SetDock(tabs, Dock.Top);
        // The options bar sits under the toolbar and over the canvas, as the Mac's tool header does.
        DockPanel.SetDock(_optionsBar, Dock.Top);
        // Docking order is what decides which panel is outermost: the Camera Raw panel takes the window's own
        // right edge and the Layers panel steps aside while it is up, as the Mac's docked panel covers it.
        DockPanel.SetDock(_cameraRawHost, Dock.Right);
        DockPanel.SetDock(_layersSide, Dock.Right);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(menu);
        root.Children.Add(tabs);
        root.Children.Add(_optionsBar);
        root.Children.Add(_cameraRawHost);
        root.Children.Add(_layersSide);
        root.Children.Add(statusBar);
        // The rail runs down the left of the canvas, as the Mac's does, and the canvas takes the rest.
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var views = Views();
        Grid.SetColumn(_rail, 0);
        Grid.SetColumn(views, 1);
        body.Children.Add(_rail);
        body.Children.Add(views);
        root.Children.Add(body);
        return root;
    }

    /// <summary>
    /// The strip under the menu bar: the New canvas button, the project's tabs, and the view's own controls —
    /// Fit, actual pixels and the two zoom steps — which are the same commands the View menu has.
    /// </summary>
    private Control Toolbar()
    {
        var add = new Button { Content = "＋", Padding = new Thickness(8, 0, 8, 0) };
        ToolTip.SetTip(add, L10n.T("New canvas"));
        add.Click += (_, _) => _ = NewProject();
        var zooms = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children =
            {
                ViewButton("Fit", "Fit the canvas in the window", () => _canvas.Fit()),
                ViewButton("100%", "Show the canvas at actual pixels", () => _canvas.ActualSize()),
                ViewButton("−", "Zoom out", () => _canvas.ZoomBy(1 / 1.25)),
                ViewButton("＋", "Zoom in", () => _canvas.ZoomBy(1.25)),
            },
        };
        var bar = new DockPanel();
        DockPanel.SetDock(add, Dock.Left);
        DockPanel.SetDock(zooms, Dock.Right);
        bar.Children.Add(add);
        bar.Children.Add(zooms);
        // The tab strip fills what is left. It is not put in a scroll view: a bitmap does not lay one out, and
        // the tab strip is one of the things --tabs draws.
        bar.Children.Add(_tabStrip);
        return bar;
    }

    /// <summary>The toolbar's own view controls, by their label, so the checks can press them as a pointer does.</summary>
    private readonly Dictionary<string, Button> _toolbar = [];

    /// <summary>A button of the toolbar: the same command a View menu row is, with the status line refreshed.</summary>
    private Button ViewButton(string text, string hint, Action act)
    {
        var button = new Button { Content = L10n.T(text), Padding = new Thickness(8, 2, 8, 2) };
        ToolTip.SetTip(button, L10n.T(hint));
        button.Click += (_, _) =>
        {
            act();
            Say();
        };
        _toolbar[text] = button;
        return button;
    }

    /// <summary>
    /// The canvas with its rulers: a strip along the top and down the side, and the little square between them
    /// where the two meet. The strips only draw, so the canvas keeps every pointer position it worked out
    /// before — they are outside it rather than over it.
    /// </summary>
    private Control Views()
    {
        var lined = new Grid
        {
            RowDefinitions = new RowDefinitions($"{RulerThickness},*"),
            ColumnDefinitions = new ColumnDefinitions($"{RulerThickness},*"),
        };
        Grid.SetRow(_rulerCorner, 0);
        Grid.SetColumn(_rulerCorner, 0);
        Grid.SetRow(_rulerAcross, 0);
        Grid.SetColumn(_rulerAcross, 1);
        Grid.SetRow(_rulerDown, 1);
        Grid.SetColumn(_rulerDown, 0);
        Grid.SetRow(_canvas, 1);
        Grid.SetColumn(_canvas, 1);
        _rulerAcross.IsVisible = _rulersVisible;
        _rulerDown.IsVisible = _rulersVisible;
        _rulerCorner.IsVisible = _rulersVisible;
        lined.Children.Add(_rulerCorner);
        lined.Children.Add(_rulerAcross);
        lined.Children.Add(_rulerDown);
        lined.Children.Add(_canvas);
        return lined;
    }

    /// <summary>
    /// A row of the menus. <paramref name="key"/> names the shortcut row this row is the face of, if it is one:
    /// both the key the row shows and the key it answers to are read from that one name, so a row cannot come
    /// to show a key it does not fire on.
    /// </summary>
    private MenuItem Command(string header, Action action, string? key = null, bool translate = true)
    {
        var item = new MenuItem { Header = translate ? L10n.T(header) : header };
        item.Click += (_, _) => action();
        ShowKey(item, key);
        return item;
    }

    private MenuItem LanguageMenu()
    {
        var menu = new MenuItem { Header = L10n.T("Language") };
        var choices = new[]
        {
            (L10n.SystemLanguage, "Follow system"),
            (L10n.SimplifiedChinese, "简体中文"),
            (L10n.English, "English"),
        };
        foreach (var (language, label) in choices)
        {
            var item = new MenuItem
            {
                Header = L10n.T(label),
                Tag = language,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = L10n.SelectedLanguage == language,
            };
            item.Click += async (_, _) =>
            {
                if (!L10n.SavePreference(language))
                {
                    await UpdateDialog.Ask(this, L10n.T("Language"), L10n.T("Could not save the language preference."));
                    return;
                }
                foreach (var choice in menu.Items.OfType<MenuItem>()) choice.IsChecked = Equals(choice.Tag, language);
                await UpdateDialog.Ask(this, L10n.T("Language"), L10n.T("Restart Compositor to apply the selected language."));
            };
            menu.Items.Add(item);
        }
        return menu;
    }

    /// <summary>A row of the Layer menu, remembered so it can be greyed out with the others.</summary>
    private MenuItem LayerCommand(string header, Action action, string? key = null,
        Func<CanvasDocument, ImageLayer, bool>? ready = null)
    {
        var item = Command(header, action, key);
        if (ready is null) _layerItems.Add(item);
        else _layerRows.Add((item, ready));
        return item;
    }

    private MenuItem DocumentCommand(string header, Action action, string? key = null)
    {
        var item = Command(header, action, key);
        _documentItems.Add(item);
        return item;
    }

    /// <summary>
    /// Opens no larger than the screen allows. The Mac build's own 1280 x 820 is a sensible window on a 1x
    /// display and taller than a 1080 one at 150%, where the bottom of this window — the status line — ends up
    /// under the taskbar and out of reach. Found by opening the real window on this machine.
    /// </summary>
    private void FitToScreen()
    {
        if (Screens.Primary is not { } screen || screen.Scaling <= 0) return;
        var room = screen.WorkingArea;
        Width = Math.Min(Width, room.Width / screen.Scaling - Room);
        Height = Math.Min(Height, room.Height / screen.Scaling - Room);
        // The window was placed from the size it was made with, so it is put back by hand: at 150% the shrinking
        // above is what would otherwise leave its title bar above the top of the screen.
        Position = new PixelPoint(
            room.X + (int)Math.Max(0, (room.Width - Width * screen.Scaling) / 2),
            room.Y + (int)Math.Max(0, (room.Height - Height * screen.Scaling) / 2));
    }

    /// <summary>How much of the screen a window leaves for the desktop around it.</summary>
    private const double Room = 40;

    /// <summary>How a shortcut row of the menus' group is named.</summary>
    private static string MenuKey(string title) => $"{Shortcuts.Menus}:{title}";

    /// <summary>The same for a row of the canvas's own group, which no menu shows.</summary>
    private static string CanvasKey(string title) => $"{Shortcuts.Canvas}:{title}";

    /// <summary>
    /// Remembers a menu row as the face of a shortcut row. Nothing is put on the row here: every row's key is
    /// put on it by <see cref="ShowKeys"/> once the whole menu has been built, so a fresh start and a rebind
    /// take the same path and cannot come out differently.
    /// </summary>
    private void ShowKey(MenuItem item, string? key, string group = Shortcuts.Menus)
    {
        if (key is not null) _keyRows.Add((item, $"{group}:{key}"));
    }

    /// <summary>The rows that are fields rather than built by <see cref="Command"/>, which are rows all the same.</summary>
    private void RegisterKeys()
    {
        foreach (var (item, key) in new (MenuItem, string)[]
                 {
                     (_showGrid, "Show Grid"),
                     (_showRulers, "Show Rulers"),
                     (_showGuides, "Show Guides"),
                     (_lockGuides, "Lock Guides"),
                     (_showTransform, "Show Transform Controls"),
                     (_snapping, "Snap"),
                     (_merge, "Merge Layers"),
                     (_clipping, "Toggle Clipping Mask"),
                 })
        {
            ShowKey(item, key);
        }
    }

    /// <summary>
    /// The table in force: every menu row shows the key its shortcut row holds, and a key pressed is looked up
    /// in the same table, so what a row shows and what it answers to are one thing. A row whose key this
    /// build's Avalonia has no name for loses it and is said so, rather than left looking as though it worked.
    /// </summary>
    private void ShowKeys()
    {
        _keys = Shortcuts.Effective(_shortcutSettings.Overrides);
        _byKey = [];
        var unknown = 0;
        foreach (var (id, chord) in _keys.ToList())
        {
            if (!chord.IsBound) continue;
            if (Known(chord) is not { } known)
            {
                _keys[id] = ShortcutChord.Unbound;
                unknown++;
                continue;
            }
            _byKey[known] = id;
        }
        foreach (var (item, id) in _keyRows)
        {
            item.InputGesture = _keys.TryGetValue(id, out var chord) ? ShortcutKeys.Gesture(chord) : null;
        }
        if (unknown > 0)
        {
            Say(L10n.F("{0} shortcut rows name a key this build does not know, so they are not in force", unknown));
        }
    }

    /// <summary>A chord as this build knows it: the key name put through Avalonia's own enum, so that the two
    /// spellings of one key — "OemOpenBrackets" and "Oem4" are the same key — come out the same and a key
    /// pressed finds its row however the row was written down. Null is a name this build has no key for.</summary>
    private static ShortcutChord? Known(ShortcutChord chord) => ShortcutKeys.Known(chord);

    /// <summary>The text field that has the keyboard, if one has: what keeps the keys it edits itself with.</summary>
    private TextBox? TypingIn() => FocusManager?.GetFocusedElement() as TextBox;

    /// <summary>
    /// Whether something is taking typing: the canvas while text is being typed on it, or a field with the
    /// keyboard. What is done about it is the Mac build's own rule — the unmodified keys are the text's, so that
    /// a letter typed into an amount never picks a tool.
    /// </summary>
    private bool Typing() => _canvas.TextEditing || TypingIn() is not null;

    /// <summary>Text fields retain editing, deletion and navigation chords, including Shift variants.</summary>
    private static bool FieldKeepsIt(KeyEventArgs e) =>
        e.KeyModifiers.HasFlag(KeyModifiers.Control)
        && e.Key is Key.A or Key.C or Key.V or Key.X or Key.Z or Key.Y
            or Key.Delete or Key.Back or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End;

    /// <summary>A key pressed anywhere in the window: the table says what it does, if anything.</summary>
    private void KeyPressed(object? sender, KeyEventArgs e)
    {
        if (_canvas.TextComposing && _canvas.IsFocused) return;
        // The Windows key is Windows', and a chord made with it is not one the table can hold.
        if (e.Handled || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return;
        if (ProtectCanvasTextShortcut(e)) return;
        var held = ShortcutKeys.Held(e.KeyModifiers);
        if (Typing() && (held is ShortcutModifiers.None or ShortcutModifiers.Shift
            || TypingIn() is not null && FieldKeepsIt(e)))
        {
            return;
        }
        var chord = new ShortcutChord(e.Key.ToString(), held);
        // A bare arrow belongs to whatever has the keyboard — the layer list walks its own rows with them — and
        // only the canvas takes it, which is where the Mac build's own nudges live.
        if (Arrows(chord) && !_canvas.IsFocused) return;
        if (!_byKey.TryGetValue(chord, out var id) || !_verbs.TryGetValue(id, out var verb)) return;
        if (TypingIn() is not null && id != MenuKey("Save") && id != MenuKey("Save As")) return;
        LastDelivered = id;
        // Two digits in a row make one exact opacity, and anything else starts the count again.
        if (!_opacityRows.Contains(id)) _opacityTyped = "";
        if (!verb(chord)) return;      // a row the key does not apply to leaves the key to whatever has it
        e.Handled = true;
    }

    /// <summary>A key let go: the Hand the space bar was holding gives the tool back.</summary>
    private void KeyLetGo(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || _toolBeforeHand is not { } held) return;
        _toolBeforeHand = null;
        if (_tool == Tool.Pan) SetTool(held);
    }

    /// <summary>Whether a chord is one of the four arrows, which the layer list walks its rows with.</summary>
    private static bool Arrows(ShortcutChord chord) => chord.Key is "Left" or "Right" or "Up" or "Down";

    /// <summary>
    /// Every shortcut row's own action. A verb answers whether the key applied at all: the crop's Enter with no
    /// frame to apply answers no, so the key carries on to whatever else would have had it.
    /// </summary>
    private void BuildVerbs()
    {
        void Does(string title, Action action, string group = Shortcuts.Menus) =>
            _verbs[$"{group}:{title}"] = _ => { action(); return true; };
        void When(string title, Func<bool> action, string group = Shortcuts.Menus) =>
            _verbs[$"{group}:{title}"] = _ => action();

        Does("Undo", Undo);
        Does("Redo", Redo);
        Does("New Project", () => _ = NewProject());
        Does("Open Project", OpenProject);
        Does("Save", Save);
        Does("Save As", SaveAs);
        Does("Export PNG", ExportPng);
        Does("Export JPEG", () => _ = ExportJpeg());
        Does("Close Tab", () => _ = CloseTab(_open));
        Does("Fit Canvas", () => { _canvas.Fit(); Say(); });
        Does("Actual Pixels", () => { _canvas.ActualSize(); Say(); });
        Does("Zoom In", () => { _canvas.ZoomBy(1.25); Say(); });
        Does("Zoom Out", () => { _canvas.ZoomBy(1 / 1.25); Say(); });
        Does("Show Transform Controls", ShowTransformControls);
        Does("Cut", Cut);
        Does("Copy", Copy);
        Does("Copy Merged", CopyMerged);
        Does("Paste", Paste);
        Does("Fill with Foreground Color", () => FillPixels(BrushColour(), "Fill"));
        Does("Fill with Background Color", () => FillPixels(BackgroundColour(), "Fill"));
        Does("Content-Aware Fill", ContentAwareFill);
        Does("Select All", () => Change("Select All", SelectionEdits.SelectAll));
        Does("Deselect", Deselect);
        Does("Inverse Selection", () => Change("Inverse", SelectionEdits.Invert));
        Does("Curves", () => _ = ImageAdjustment(AdjustmentKind.Curves));
        Does("Levels", () => _ = ImageAdjustment(AdjustmentKind.Levels));
        Does("Hue/Saturation", () => _ = ImageAdjustment(AdjustmentKind.HueSaturation));
        Does("Invert", () => _ = ImageAdjustment(AdjustmentKind.Invert));
        Does("Canvas Size", () => _ = CanvasSize());
        Does("Image Size", () => _ = ImageSize());
        Does("Layer via Copy", LayerViaCopy);
        Does("Duplicate Layer", DuplicateLayer);
        Does("Toggle Clipping Mask", ToggleClipping);
        Does("Group Layers", GroupSelected);
        Does("Merge Layers", MergeLayers);
        Does("New Blank Layer", NewBlankLayer);
        Does("Move Layer Up", () => MoveLayer(1));
        Does("Move Layer Down", () => MoveLayer(-1));
        Does("Rename Layer", () => _ = RenameLayer());
        Does("Delete Layer", DeleteLayer);
        Does("Show Grid", ShowGrid);
        Does("Show Rulers", ShowRulers);
        Does("Show Guides", ShowGuides);
        Does("Lock Guides", LockGuides);
        Does("Snap", ShowSnapping);
        Does("New Guide", () => _ = NewGuide());

        // The tools. A letter that stands for a family — the two marquees, the three brushes — walks the family
        // when it is pressed again, which is what the Mac build's own letters do.
        Does("Hand tool", () => SetTool(Tool.Pan), Shortcuts.Canvas);
        Does("Move / Transform tool", () => SetTool(Tool.Move), Shortcuts.Canvas);
        Does("Marquee tool", () => ChooseTool(Tool.Marquee, Tool.Ellipse), Shortcuts.Canvas);
        Does("Lasso tool", () => ChooseTool(Tool.Lasso, Tool.Polygon), Shortcuts.Canvas);
        Does("Magic wand", () => SetTool(Tool.Wand), Shortcuts.Canvas);
        Does("Brush tool", () => SetTool(Tool.Brush), Shortcuts.Canvas);
        Does("Clone Stamp", () => SetTool(Tool.Clone), Shortcuts.Canvas);
        Does("Blur / Smudge / Liquify", () => ChooseTool(Tool.Blur, Tool.Smudge, Tool.Liquify), Shortcuts.Canvas);
        Does("Spot Healing", () => SetTool(Tool.Heal), Shortcuts.Canvas);
        Does("Eyedropper tool", () => SetTool(Tool.Eyedropper), Shortcuts.Canvas);
        Does("Type tool", () => SetTool(Tool.Type), Shortcuts.Canvas);
        Does("Crop tool", () => SetTool(Tool.Crop), Shortcuts.Canvas);
        Does("Shape tool", () => SetTool(Tool.Shape), Shortcuts.Canvas);
        Does("Gradient tool", () => SetTool(Tool.Gradient), Shortcuts.Canvas);
        Does("Swap foreground/background", SwapColours, Shortcuts.Canvas);
        Does("Reset colors", ResetColours, Shortcuts.Canvas);
        When("Temporary Hand tool (hold)", TakeHand, Shortcuts.Canvas);
        When("Decrease brush size", () => StepBrushSize(false), Shortcuts.Canvas);
        When("Increase brush size", () => StepBrushSize(true), Shortcuts.Canvas);
        When("Decrease brush hardness", () => StepBrushHardness(false), Shortcuts.Canvas);
        When("Increase brush hardness", () => StepBrushHardness(true), Shortcuts.Canvas);
        When("Previous blend mode", () => StepBlend(-1), Shortcuts.Canvas);
        When("Next blend mode", () => StepBlend(1), Shortcuts.Canvas);
        When("Cycle shape kind", CycleShapeKind, Shortcuts.Canvas);
        for (var digit = 0; digit <= 9; digit++)
        {
            var value = digit;
            var id = CanvasKey($"Opacity digit {value} (type two for exact %)");
            _opacityRows.Add(id);
            _verbs[id] = _ => { OpacityDigit(value); return true; };
        }
        foreach (var (direction, dx, dy) in new[]
                 {
                     ("Left", -1.0, 0.0), ("Right", 1.0, 0.0), ("Up", 0.0, -1.0), ("Down", 0.0, 1.0),
                 })
        {
            When($"Nudge {direction} 1 px", () => Nudge(dx, dy), Shortcuts.Canvas);
            When($"Nudge {direction} 10 px", () => Nudge(dx * 10, dy * 10), Shortcuts.Canvas);
            When($"Move selected pixels {direction} 1 px", () => MovePixels(dx, dy), Shortcuts.Canvas);
            When($"Move selected pixels {direction} 10 px", () => MovePixels(dx * 10, dy * 10), Shortcuts.Canvas);
        }
        When("Apply Canvas Operation",
            () => { if (_cropFrame is null) return false; ApplyCrop(); return true; }, Shortcuts.Canvas);
        When("Cancel Canvas Operation",
            () => { if (_cropFrame is null) return false; CancelCrop(); return true; }, Shortcuts.Canvas);
    }

    /// <summary>
    /// Picks a tool from its letter, or the next in that letter's family when it is already in hand — the two
    /// marquees on M, the lasso and the polygon on L, the three brushes on R.
    /// </summary>
    private void ChooseTool(params Tool[] family)
    {
        var at = Array.IndexOf(family, _tool);
        SetTool(at < 0 ? family[0] : family[(at + 1) % family.Length]);
    }

    /// <summary>
    /// An arrow with the canvas in hand. Which of the two it moves is the Mac build's own choice: with a
    /// selection tool and something selected it moves the selection, and otherwise, with the Move tool in
    /// hand, it moves the layer.
    /// </summary>
    private bool Nudge(double dx, double dy)
    {
        if (_document is not { } document) return false;
        if (_canvas.Selection != SelectionTool.None && document.Selection.Path is not null)
        {
            return Edit("Move Selection", () => SelectionEdits.Move(document, dx, dy));
        }
        if (_tool != Tool.Move || Selected is not { } id) return false;
        return Edit("Move Layer", () => LayerEdits.Move(document, SelectedLayers, dx, dy));
    }

    /// <summary>The pixels a drag is carrying, and how far it has taken them.</summary>
    private FloatingPixels? _moving;
    private ImageLayer? _movingOriginal;
    private (int Dx, int Dy) _movedBy;

    /// <summary>
    /// A press inside the selection with Control held: the pixels under it come away from the layer and are
    /// carried by the drag. False — and nothing taken — when the press is outside the selection, so the drag goes
    /// on to draw a new outline as it would have.
    /// </summary>
    private bool PixelsTaken(SKPoint at)
    {
        if (_document is not { } document || Selected is not { } id) return false;
        if (document.Selection.Path is not { } path || !path.Contains(at.X, at.Y)) return false;
        if (!MayEditImage()) return false;
        _movingOriginal = document.Layers.FirstOrDefault(layer => layer.ID == id)?.Clone();
        _history.Begin("Move Pixels", document, id);
        if (SelectionEdits.LiftPixels(document, id) is not { } floating)
        {
            _movingOriginal = null;
            _history.End(document, id);
            Say("There is nothing inside the selection to move");
            return false;
        }
        _moving = floating;
        _movedBy = (0, 0);
        _canvas.Floating = (floating, 0, 0);
        Say("Dragging the pixels inside the selection");
        return true;
    }

    /// <summary>The pixels follow the pointer: where they are now is where they would land.</summary>
    private void PixelsDragged(int dx, int dy)
    {
        if (_document is not { } document || _moving is not { } moving) return;
        _movedBy = (dx, dy);
        _canvas.Floating = (moving, dx, dy);
        // The outline goes with them, so what is selected is what is being carried.
        document.Selection = moving.Origin.Translated(dx, dy);
        _canvas.InvalidateVisual();
        Say(L10n.F("Moving the pixels {0}, {1}", dx, dy));
    }

    /// <summary>
    /// The drag has let the pixels go: they are put down where they are, or back where they came from when the
    /// drag went nowhere. Either way it is one undo step, begun when they were taken hold of.
    /// </summary>
    private void PixelsLetGo()
    {
        if (_document is not { } document || _moving is not { } moving) return;
        _moving = null;
        _canvas.Floating = null;
        if (_movedBy is (0, 0))
        {
            if (_movingOriginal is { } original)
            {
                var index = document.Layers.FindIndex(layer => layer.ID == original.ID);
                if (index >= 0) document.Layers[index] = original;
                document.Selection = moving.Origin;
            }
            moving.Dispose();
        }
        else
        {
            SelectionEdits.SettlePixels(document, moving, _movedBy.Dx, _movedBy.Dy);
        }
        _movingOriginal = null;
        var moved = _movedBy;
        _movedBy = (0, 0);
        _history.End(document, Selected);
        Refresh();
        if (moved is not (0, 0)) Say(L10n.F("Pixels moved {0}, {1}", moved.Dx, moved.Dy));
    }

    /// <summary>
    /// The pixels inside the selection move by whole document pixels, one undo step a press, which is the Mac
    /// build's own Command-with-an-arrow: what the plain arrows do to a layer, this does to what is selected.
    /// </summary>
    private bool MovePixels(double dx, double dy)
    {
        if (_document is not { } document || Selected is not { } id) return false;
        return Edit("Move Pixels",
            () => SelectionEdits.MovePixels(document, id, (int)Math.Round(dx), (int)Math.Round(dy)));
    }

    /// <summary>Steps the selected layer's blend mode, as the Mac build's own Shift-minus and Shift-equals do.</summary>
    private bool StepBlend(int step)
    {
        if (_document is not { } document || Selected is not { } id) return false;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { } layer) return false;
        if (layer.IsGroup) return false;
        var at = Array.IndexOf(BlendModes, layer.BlendMode);
        var next = BlendModes[((at + step) % BlendModes.Length + BlendModes.Length) % BlendModes.Length];
        Edit("Blend Mode", () => LayerEdits.SetBlendMode(document, id, next));
        Say(L10n.F("Blend mode: {0}", L10n.T(Spell(next))));
        return true;
    }

    /// <summary>Steps to the next shape the Shape tool draws.</summary>
    private bool CycleShapeKind()
    {
        var kinds = Enum.GetValues<ShapeKind>();
        var at = Array.IndexOf(kinds, _options.Shape);
        SetShapeKind(kinds[(at + 1) % kinds.Length]);
        Say(L10n.F("Shape: {0}", L10n.T(_options.Shape.ToString())));
        return true;
    }

    /// <summary>
    /// The bracket keys, as the Mac build's own have them: a step of a fifth of the brush but never less than a
    /// pixel, so that the smallest brushes are not left out of reach.
    /// </summary>
    private bool StepBrushSize(bool increase)
    {
        if (!_canvas.PaintEnabled) return false;
        var current = _options.Brush.Diameter;
        var stepped = increase
            ? Math.Max(current + 1, Math.Round(current * 1.2))
            : Math.Min(current - 1, Math.Round(current / 1.2));
        _options.Brush = _options.Brush with { Diameter = Math.Clamp(stepped, 1, 2000) };
        OptionsChanged();
        Say(L10n.F("Brush: {0:0} pixels", _options.Brush.Diameter));
        return true;
    }

    /// <summary>Shift with the brackets: hardness in the Mac build's own quarter steps, 0 to 100%.</summary>
    private bool StepBrushHardness(bool increase)
    {
        if (!_canvas.PaintEnabled) return false;
        var quarter = _options.Brush.Hardness * 4;
        var step = increase ? Math.Floor(quarter + 0.001) + 1 : Math.Ceiling(quarter - 0.001) - 1;
        _options.Brush = _options.Brush with { Hardness = Math.Clamp(step, 0, 4) / 4 };
        OptionsChanged();
        Say(L10n.F("Brush: {0:0}% hard", _options.Brush.Hardness * 100));
        return true;
    }

    /// <summary>
    /// A digit with the canvas in hand: the brush's opacity as a percentage, with two digits typed together
    /// read as the whole number, so 5 alone is half and 5 then 3 is 53%.
    /// </summary>
    private void OpacityDigit(int digit)
    {
        _opacityTyped = _opacityTyped.Length >= 2 ? $"{digit}" : _opacityTyped + digit;
        var percent = int.Parse(_opacityTyped) * 10;
        if (_opacityTyped.Length == 2) percent = int.Parse(_opacityTyped);
        _options.Brush = _options.Brush with { Opacity = percent / 100.0 };
        OptionsChanged();
        Say(L10n.F("Brush opacity {0:0}%", _options.Brush.Opacity * 100));
    }

    /// <summary>
    /// The space bar held: the Hand comes to hand for as long as it is down, and the tool that was in hand
    /// comes back when it is let go. Only with the canvas in hand, so that space still works a button.
    /// </summary>
    private bool TakeHand()
    {
        if (!_canvas.IsFocused) return false;
        if (_toolBeforeHand is null)
        {
            _toolBeforeHand = _tool;
            SetTool(Tool.Pan);
        }
        return true;
    }

    /// <summary>Edit ▸ Keyboard Shortcuts…: the list and the recorder, and a table that passes the check.</summary>
    private async Task KeyboardShortcuts()
    {
        if (await ShortcutDialog.Show(this, _shortcutSettings.Overrides) is not { } chosen) return;
        var previous = _shortcutSettings.Overrides;
        _shortcutSettings.Overrides = chosen;
        if (!_shortcutSettings.Save(ShortcutDefaults.DefaultPath, out var error))
        {
            _shortcutSettings.Overrides = previous;
            await UpdateDialog.Ask(this, L10n.T("Keyboard Shortcuts"),
                L10n.F("Could not save keyboard shortcuts: {0}", error ?? ""));
            return;
        }
        ShowKeys();
        Say(chosen.Count == 0
            ? "Keyboard shortcuts back to their defaults"
            : L10n.F("{0} keyboard shortcuts changed", chosen.Count));
    }

    private async void OpenProject()
    {
        try
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = L10n.T("Open a Compositor project folder"),
                AllowMultiple = false,
            });
            if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return;
            await OpenProjectPathAsync(path);
        }
        catch (Exception error)
        {
            AppDiagnostics.Report("Open project", error);
            Say(L10n.F("Could not open: {0}", OperationErrors.Describe(error)));
        }
    }

    private void Open(string path)
    {
        var snapshot = ProjectStore.Load(path);
        // Into an empty tab when there is one and a tab of its own otherwise: an open project is not thrown
        // away by opening another, as the Mac build keeps one open per tab.
        _open = TabForNew();
        _document?.Dispose();
        // ToDocument takes the pixel references, so the document owns them from here on.
        _document = snapshot.ToDocument();
        _projectPath = path;
        // A fresh project starts with clean history, as opening a file does.
        _history.Reset();
        NoteRecent(path);
        Show(_open);
        Say(L10n.F("{0} — {1} by {2}, {3} layers, {4:0} pixels per inch", System.IO.Path.GetFileName(path), _document.Width, _document.Height, _document.Layers.Count, _document.Resolution));
    }

    /// <summary>
    /// The tab machinery driven without a pointer, for the self check: two projects opened into tabs, one
    /// brought back in front, one closed, and the last one closed as well. It answers with what it found, one
    /// line a step, and throws when a step is wrong \u2014 which is the only way the tab strip is checked, since
    /// nothing else can click it.
    /// </summary>
    internal string SelfCheck(string first, string second)
    {
        var report = new List<string>();
        // The window opens with one empty tab, which is what the first project goes into: that is why the tab
        // count does not change on the first open and does on the second.
        if (_tabs.Count != 1 || _document is not null) throw new InvalidOperationException("the window did not open with one empty tab");
        Open(first);
        report.Add($"open first: {_tabs.Count} tab(s), front {_open.Name}, " +
            $"{_document?.Width}x{_document?.Height}, {_rows.Count} row(s)");
        if (_tabs.Count != 1) throw new InvalidOperationException("the first project did not take the empty tab");
        if (_open.Name != "first.comp") throw new InvalidOperationException("the tab is not named for the project it holds");
        if (_rows.Count != _document!.Layers.Count) throw new InvalidOperationException("the panel does not hold the first project's layers");

        var firstTab = _open;
        Open(second);
        report.Add($"open second: {_tabs.Count} tab(s), front {_open.Name}, " +
            $"{_document?.Width}x{_document?.Height}, {_rows.Count} row(s)");
        if (_tabs.Count != 2) throw new InvalidOperationException("the second project did not get a tab of its own");
        if (_open == firstTab) throw new InvalidOperationException("the first project is still in front");

        Bring(firstTab);
        report.Add($"bring first: {_tabs.Count} tab(s), front {_open.Name}, {_document?.Width}x{_document?.Height}, " +
            $"{_rows.Count} row(s)");
        if (!ReferenceEquals(_open, firstTab)) throw new InvalidOperationException("the first tab did not come in front");
        // The first project is the demo document, 240 by 160, and the second is a blank 100 by 80 canvas: the
        // size is what tells which document the canvas and the panel are holding.
        if (_document?.Width != 240 || _document.Height != 160) throw new InvalidOperationException("the front document is not the first project's");
        if (_rows.Count != _document.Layers.Count) throw new InvalidOperationException("the panel did not come back to the first project's layers");

        _ = CloseTab(_tabs[^1]);
        report.Add($"close the tab behind: {_tabs.Count} tab(s), front {_open.Name}");
        if (_tabs.Count != 1) throw new InvalidOperationException("closing a background tab left the wrong number open");
        if (!ReferenceEquals(_open, firstTab)) throw new InvalidOperationException("closing the tab behind brought the wrong one in front");

        _ = CloseTab(_open);
        report.Add($"close the last: {_tabs.Count} tab(s), document {(_document is null ? "none" : "still open")}, " +
            $"{_rows.Count} row(s)");
        if (_tabs.Count != 1) throw new InvalidOperationException("closing the last tab left nothing to work in");
        if (_document is not null) throw new InvalidOperationException("the last tab still holds a document");
        if (_rows.Count != 0) throw new InvalidOperationException("the panel still holds rows with nothing open");
        if (_open.Name != L10n.T("Untitled")) throw new InvalidOperationException("the tab left behind is not a fresh one");
        return string.Join(Environment.NewLine, report);
    }

    /// <summary>
    /// The Camera Raw panel driven without a pointer, for the self check: it is opened on a layer that has
    /// pixels, two of its amounts are moved, the preview is run the way the window's timer runs it, and Apply
    /// is pressed and then Cancel. It answers with what it found, one line a step, and throws when a step is
    /// wrong — which is the only way a docked panel is checked, since nothing else can press its buttons.
    /// </summary>
    internal string CameraRawSelfCheck(string project)
    {
        var report = new List<string>();
        Open(project);
        if (_document is not { } document) throw new InvalidOperationException("the project did not open");
        var target = document.Layers.FirstOrDefault(layer => layer.Asset is not null && !layer.IsGroup)
            ?? throw new InvalidOperationException("the project has no layer with pixels of its own");
        Reselect(target.ID);
        report.Add($"open {System.IO.Path.GetFileName(project)}: {document.Width}x{document.Height}, " +
            $"{document.Layers.Count} layer(s), editing {target.Name}");

        CameraRawFilter();
        if (_cameraRaw is not { } panel) throw new InvalidOperationException("the Camera Raw panel did not open");
        if (!_cameraRawHost.IsVisible) throw new InvalidOperationException("the panel's column is not showing");
        if (_layersSide.IsVisible) throw new InvalidOperationException("the Layers panel did not step aside");
        report.Add($"panel up: {panel.AmountCount} amounts built, the right column {_cameraRawHost.Width} wide, "
            + "the Layers panel stepped aside");

        // An amount moved and the preview run the way the window's timer runs it: what the canvas would draw is
        // then what the panel asked for, while the layer's own pixels are not touched until Apply.
        var was = target.Asset!.Image.GetPixel(0, 0);
        panel.Move("Exposure, stops", 1.5);
        panel.Move("Contrast", 40);
        ShowPreviewOnce(null, EventArgs.Empty);
        if (_preview is null) throw new InvalidOperationException("moving an amount did not start a preview");
        if (_history.IsModified) throw new InvalidOperationException("the preview wrote into the document's history");
        if (target.Asset.Image.GetPixel(0, 0) != was)
        {
            throw new InvalidOperationException("the preview reached the layer's own pixels");
        }
        report.Add("amounts moved: exposure 1.5 and contrast 40 are previewed, and the layer is untouched");

        // The scope comes out of the same pass that made the picture, so it is there as soon as the preview is
        // and it has to have moved with the grade rather than describe the layer as it was.
        if (_cameraRawScope is not { } counted) throw new InvalidOperationException("the preview counted no scope");
        report.Add($"scope: peak {counted.Peak:0.#}, vectorscope peak {counted.ScopePeak:0.#}, "
            + $"busiest red bin {Array.IndexOf(counted.Red, counted.Red.Max())}");

        // The readout names the pixel under the pointer out of the picture the panel is showing, so what it
        // says is the grade and not the layer the grade came from.
        var middle = new SKPoint((float)target.Transform.CenterX, (float)target.Transform.CenterY);
        if (UnderCursor(middle) is not { } sampled) throw new InvalidOperationException("the readout found no pixel");
        report.Add($"readout at the middle of the layer: R {sampled.Red}   G {sampled.Green}   B {sampled.Blue}");
        if (sampled == (40, 70, 120)) throw new InvalidOperationException("the readout named the layer, not the grade");
        if (UnderCursor(new SKPoint(-50, -50)) is not null)
        {
            throw new InvalidOperationException("the readout answered for a point outside the picture");
        }

        // The clipping triangles in the scope are the same two switches the panel's own checkboxes are.
        if (panel.Clipping != (false, false)) throw new InvalidOperationException("a clipping view was already on");
        panel.PressClippingTriangle(shadows: true);
        if (panel.Clipping != (true, false)) throw new InvalidOperationException("the shadow triangle lit nothing");
        panel.PressClippingTriangle(shadows: true);
        if (panel.Clipping != (false, false)) throw new InvalidOperationException("the shadow triangle would not go out");
        panel.PressClippingTriangle(shadows: false);
        if (panel.Clipping != (false, true)) throw new InvalidOperationException("the highlight triangle lit nothing");
        panel.PressClippingTriangle(shadows: false);
        if (panel.Clipping != (false, false)) throw new InvalidOperationException("the triangles would not both clear");
        // And each is its own switch: the scope draws its triangle the colour the checkbox has it.
        panel.PressClippingTriangle(shadows: true);
        if (panel.Clipping != (true, false)) throw new InvalidOperationException("the two clipping views moved together");
        panel.PressClippingTriangle(shadows: true);
        report.Add("clipping: each triangle lights and clears its own view, and neither moves the other");

        // The right-click asks for the other of the histogram and the vectorscope.
        if (panel.ShowingVectorscope) throw new InvalidOperationException("the scope started on the vectorscope");
        panel.SwapScope();
        if (!panel.ShowingVectorscope) throw new InvalidOperationException("the scope did not swap over");
        panel.SwapScope();
        if (panel.ShowingVectorscope) throw new InvalidOperationException("the scope did not swap back");
        report.Add("scope: the histogram and the vectorscope swap over and back");

        // Guided upright: the panel asks for lines on the picture, a line drawn on the canvas is read into a
        // turn, and the picture moves by what it asks for.
        if (panel.DrawingGuides) throw new InvalidOperationException("the panel was already asking for guides");
        panel.PressDrawGuides();
        if (!panel.DrawingGuides) throw new InvalidOperationException("the Draw Guides button asked for nothing");
        if (!_canvas.UprightDrawing) throw new InvalidOperationException("the canvas was not armed to draw a line");
        // The middle of the layer, and a line ten degrees below the horizontal across it.
        var box = target.Transform;
        var from = box.Point(0.1, 0.2);
        var to = box.Point(0.9, 0.2 + 0.8 * Math.Tan(10 * Math.PI / 180));
        _canvas.UprightDrawn!(from, to);
        if (panel.Guides.Count != 1) throw new InvalidOperationException("the line was not taken");
        if (_canvas.UprightGuides.Count != 1) throw new InvalidOperationException("the line is not drawn for the canvas");
        var asked = panel.Current().Geometry;
        if (asked.Upright != CameraRawUprightMode.Guided) throw new InvalidOperationException("the line did not choose Guided");
        if (Math.Abs(GuidedUpright.Corrections(asked.Guides).Rotate + 10) > 0.01)
        {
            throw new InvalidOperationException("the line did not ask for its own angle back");
        }
        // The grade is asked for again, and what comes back is the picture with the line's turn in it.
        ShowPreviewOnce(null, EventArgs.Empty);
        report.Add($"upright: 1 line drawn, asking for a turn of {GuidedUpright.Corrections(asked.Guides).Rotate:0.##} degrees");

        // A second, steeper line adds the keystone; a third is not read at all.
        _canvas.UprightDrawn!(box.Point(0.5, 0.1), box.Point(0.5 + 0.4 * Math.Cos(70 * Math.PI / 180),
            0.1 + 0.4 * Math.Sin(70 * Math.PI / 180)));
        var two = GuidedUpright.Corrections(panel.Guides);
        if (two.Vertical != GuidedUpright.Keystone || two.Horizontal != 0)
        {
            throw new InvalidOperationException($"the steep second line asked for {two.Vertical}/{two.Horizontal}");
        }
        ShowPreviewOnce(null, EventArgs.Empty);
        report.Add($"upright: a steeper second line asks for a keystone of {two.Vertical:0}");

        // Stopping, and clearing, put the canvas and the picture back.
        panel.PressDrawGuides();
        if (panel.DrawingGuides || _canvas.UprightDrawing)
        {
            throw new InvalidOperationException("stopping left the panel or the canvas asking for lines");
        }
        panel.ClearGuides();
        if (panel.Guides.Count != 0 || _canvas.UprightGuides.Count != 0)
        {
            throw new InvalidOperationException("clearing left lines behind");
        }
        if (panel.Current().Geometry.Adjusts) throw new InvalidOperationException("the cleared lines still adjust");
        ShowPreviewOnce(null, EventArgs.Empty);
        report.Add("upright: stopping and clearing put the canvas and the picture back");

        // Apply writes them into the layer as one undo step, and puts the panel away with the Layers panel back.
        panel.Apply();
        if (_cameraRaw is not null) throw new InvalidOperationException("Apply did not put the panel away");
        if (_cameraRawHost.IsVisible) throw new InvalidOperationException("the panel's column is still showing");
        if (!_layersSide.IsVisible) throw new InvalidOperationException("the Layers panel did not come back");
        if (!_history.CanUndo || _history.UndoName != "Camera Raw Filter")
        {
            throw new InvalidOperationException("Apply did not leave one undo step named for the filter");
        }
        if (target.Asset.Image.GetPixel(0, 0) == was)
        {
            throw new InvalidOperationException("Apply changed nothing in the layer");
        }
        report.Add($"applied: the panel is away, the Layers panel is back, the history holds '{_history.UndoName}'");

        // Opened again and cancelled, the layer is left exactly as Apply left it. It opens where it was left:
        // the amounts the last Apply used, which is what the Mac's one set of filter settings keeps.
        var applied = target.Asset.Image.GetPixel(0, 0);
        CameraRawFilter();
        if (_cameraRaw is not { } again) throw new InvalidOperationException("the panel did not open a second time");
        if (again.SetTo("Exposure, stops") != 1.5 || again.SetTo("Contrast") != 40)
        {
            throw new InvalidOperationException(
                $"the panel opened with exposure {again.SetTo("Exposure, stops")} and contrast "
                + $"{again.SetTo("Contrast")} rather than what it was last used with");
        }
        report.Add($"opened again where it was left: exposure {again.SetTo("Exposure, stops")}, "
            + $"contrast {again.SetTo("Contrast")}");
        again.Move("Exposure, stops", -1);
        ShowPreviewOnce(null, EventArgs.Empty);
        again.Cancel();
        if (_cameraRaw is not null) throw new InvalidOperationException("Cancel did not put the panel away");
        if (target.Asset.Image.GetPixel(0, 0) != applied)
        {
            throw new InvalidOperationException("Cancel left the layer changed");
        }
        report.Add("cancelled: the panel is away and the layer is as Apply left it");

        // Left up with an amount moved and a line drawn: the caller draws the window, and the docked panel and
        // the line over the picture are what there is to look at in that drawing.
        CameraRawFilter();
        if (_cameraRaw is not { } shown) throw new InvalidOperationException("the panel did not stay open");
        // The amount the cancelled panel was moved to is not the one it is opened with: a Cancel keeps the
        // amounts that were last applied.
        if (shown.SetTo("Exposure, stops") != 1.5)
        {
            throw new InvalidOperationException("the amount a cancelled panel was moved to was kept");
        }
        shown.Move("Exposure, stops", 0.8);
        shown.Move("Clarity", 30);
        shown.PressDrawGuides();
        _canvas.UprightDrawn!(box.Point(0.1, 0.75), box.Point(0.9, 0.75 - 0.8 * Math.Tan(8 * Math.PI / 180)));
        ShowPreviewOnce(null, EventArgs.Empty);
        report.Add("left open with exposure 0.8, clarity 30 and one upright line, for the drawing");
        return string.Join(Environment.NewLine, report);
    }

    /// <summary>
    /// The tool rail and the window's toolbar driven without a pointer, for the self check: every tool is
    /// picked in turn and the rail, the Tools menu and the status line are read back, then the two colours are
    /// swapped and put back, then the toolbar's zoom is stepped. It answers with what it found, one line a
    /// step, and throws when a step is wrong.
    /// </summary>
    /// <summary>The row the last key delivered, which the check reads to watch the table doing its work.</summary>
    internal string? LastDelivered { get; private set; }

    /// <summary>A key pressed the way the keyboard presses it: through the window's own routed event, so what is
    /// exercised is the handler the real keyboard reaches and not a way around it.</summary>
    private string? Press(ShortcutChord chord)
    {
        LastDelivered = null;
        RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = KeyDownEvent,
            Key = Enum.Parse<Key>(chord.Key),
            KeyModifiers = ShortcutKeys.Modifiers(chord.Modifiers),
        });
        return LastDelivered;
    }

    /// <summary>
    /// The shortcut table driven without a keyboard: what every row is on, whether the menus show the same key,
    /// whether every row has something to do, and whether a key pressed delivers the row the table says —
    /// including a key that has just been rebound, which is the one thing a rebind could silently get wrong.
    /// </summary>
    internal string ShortcutsSelfCheck(string project)
    {
        var report = new List<string>();
        Open(project);
        if (_document is null) throw new InvalidOperationException("the project did not open");
        report.Add($"the table: {Shortcuts.Definitions.Count} rows, {_verbs.Count} with an action to call, "
            + $"{_byKey.Count} on a key and {_keyRows.Count} of them shown in a menu");
        report.Add($"groups: {string.Join(", ", Shortcuts.Groups)}");
        if (Shortcuts.Problem(_shortcutSettings.Overrides) is { } problem)
        {
            throw new InvalidOperationException($"the table in force does not pass its own check: {problem}");
        }

        // A row ported in and never wired looks exactly like a feature nothing reaches, so this is the guard.
        var orphans = Shortcuts.Definitions.Where(row => !_verbs.ContainsKey(row.ID)).Select(row => row.ID).ToList();
        if (orphans.Count > 0) throw new InvalidOperationException($"nothing to do for {string.Join(", ", orphans)}");
        report.Add("every row of the table has an action behind it");

        // Every menu row shows the key its own shortcut row holds, and no other.
        foreach (var (item, id) in _keyRows)
        {
            var chord = _keys[id];
            var wanted = ShortcutKeys.Gesture(chord);
            var shown = item.InputGesture;
            if (wanted is null)
            {
                if (shown is not null) throw new InvalidOperationException($"{id} shows {shown} holding nothing");
                continue;
            }
            if (shown is null || shown.Key != wanted.Key || shown.KeyModifiers != wanted.KeyModifiers)
            {
                throw new InvalidOperationException($"{id} shows {shown} but holds {chord.Label}");
            }
        }
        report.Add($"all {_keyRows.Count} menu rows show the key their own shortcut row holds");
        foreach (var group in Shortcuts.Groups)
        {
            foreach (var row in Shortcuts.Definitions.Where(row => row.Group == group))
            {
                report.Add($"  {row.ID} = {(_keys[row.ID].IsBound ? _keys[row.ID].Label : "nothing")}");
            }
        }

        // A few keys pressed the way the keyboard presses them, and the row each one reached.
        foreach (var (title, chord) in new (string, ShortcutChord)[]
                 {
                     ("Undo", new("Z", ShortcutModifiers.Control)),
                     ("Redo", new("Z", ShortcutModifiers.Control | ShortcutModifiers.Shift)),
                     ("Brush tool", new("B")),
                     ("Crop tool", new("C")),
                     ("Decrease brush size", new("OemOpenBrackets")),
                 })
        {
            var id = _verbs.ContainsKey(MenuKey(title)) ? MenuKey(title) : CanvasKey(title);
            var reached = Press(chord);
            report.Add($"  {chord.Label} → {reached ?? "nothing"}{(reached == id ? "" : $" rather than {id}!")}");
            if (reached != id) throw new InvalidOperationException($"{chord.Label} did not reach {id}");
        }

        // A key that is a menu row only with the modifiers its row asks for: the plain key is not the row.
        if (Press(new ShortcutChord("Z")) is { } plain && plain == MenuKey("Undo"))
        {
            throw new InvalidOperationException("plain Z reached Undo, which is Ctrl+Z's row");
        }
        report.Add("plain Z reaches nothing, as only Ctrl+Z is Undo's");

        // A rebind, made the way the sheet makes one: the new key delivers the row and the old key lets it go.
        var undo = MenuKey("Undo");
        _shortcutSettings.Overrides[undo] = new ShortcutChord("Y", ShortcutModifiers.Control);
        ShowKeys();
        if (Press(new ShortcutChord("Y", ShortcutModifiers.Control)) != undo)
        {
            throw new InvalidOperationException("the rebind did not take");
        }
        if (Press(new ShortcutChord("Z", ShortcutModifiers.Control)) == undo)
        {
            throw new InvalidOperationException("the key the row left still reached it");
        }
        report.Add("after rebinding Undo to Ctrl+Y: Ctrl+Y reaches it and Ctrl+Z does not");
        _shortcutSettings.Overrides.Remove(undo);
        ShowKeys();

        // The sheet: a clash is refused with both rows named, and a sound change is not.
        var sheet = new ShortcutDialog(new Dictionary<string, ShortcutChord>());
        sheet.Record(undo, Key.E, KeyModifiers.Control);
        report.Add($"the sheet with Ctrl+E recorded on Undo says \"{sheet.ComplaintText}\", "
            + $"save {(sheet.CanSave ? "on offer" : "held back")}");
        if (sheet.CanSave) throw new InvalidOperationException("a clash was offered for saving");
        sheet.Record(undo, Key.Y, KeyModifiers.Control);
        var changes = sheet.Changes();
        report.Add($"with Ctrl+Y recorded instead, it says \"{sheet.ComplaintText}\" and would save "
            + $"{changes.Count} row: {string.Join(", ", changes.Keys)}");
        if (changes.Count != 1 || !changes.ContainsKey(undo)) throw new InvalidOperationException("the wrong rows would be saved");
        // Backspace clears a row, which is how a key is taken off one.
        sheet.Record(undo, Key.Back, KeyModifiers.None);
        if (sheet.Draft[undo].IsBound) throw new InvalidOperationException("Backspace did not clear the row");
        report.Add("Backspace clears the row being recorded");
        return string.Join(Environment.NewLine, report);
    }

    /// <summary>
    /// How many pixels of the selection's own coverage come out part-way. An antialiased edge has them and a hard
    /// edge has none, which is the difference the Anti-alias tick makes and the only way to see it from outside.
    /// </summary>
    private static int PartCovered(CanvasDocument document)
    {
        using var coverage = document.Selection.Coverage(SKRectI.Create(0, 0, document.Width, document.Height));
        if (coverage is null) return 0;
        var partial = 0;
        for (var y = 0; y < coverage.Height; y++)
        {
            for (var x = 0; x < coverage.Width; x++)
            {
                if (coverage.GetPixel(x, y).Red is > 0 and < 255) partial++;
            }
        }
        return partial;
    }

    /// <summary>A drag with the left button down, from one window point to another, in the steps a hand makes.</summary>
    private void Drag(Point from, Point to, int steps = 8)
    {
        this.MouseDown(from, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        for (var step = 1; step <= steps; step++)
        {
            this.MouseMove(new Point(
                from.X + (to.X - from.X) * step / steps,
                from.Y + (to.Y - from.Y) * step / steps), RawInputModifiers.LeftMouseButton);
        }
        this.MouseUp(to, MouseButton.Left, RawInputModifiers.None);
    }

    /// <summary>
    /// A fingerprint of what a layer draws, so a check can tell that it changed. All four channels go in: a
    /// layer that is already opaque has no alpha left to change, which an alpha-only sum would read as "nothing
    /// happened" however much was painted on it.
    /// </summary>
    private static long InkOf(SKBitmap pixels)
    {
        var hash = 17L;
        for (var y = 0; y < pixels.Height; y++)
        {
            for (var x = 0; x < pixels.Width; x++)
            {
                var colour = pixels.GetPixel(x, y);
                var packed = (long)colour.Red << 24 | (long)colour.Green << 16 | (long)colour.Blue << 8 | colour.Alpha;
                hash = hash * 31 + packed;
            }
        }
        return hash;
    }

    /// <summary>
    /// The window driven with a pointer rather than only built: a stroke painted, a marquee dragged, the wand
    /// clicked and a guide pulled off the ruler, each aimed at a document point through the canvas's own
    /// mapping and each checked against the document and the history rather than against a picture. The caller
    /// shows the window on the headless platform first, which is what makes the hit-testing real.
    /// </summary>
    internal string PointerSelfCheck(string project)
    {
        var report = new List<string>();
        // Every step reports as it goes and a failure is reported rather than thrown away with the report: a
        // check that drives a pointer has many ways to fail and only one of them is worth a stack trace.
        try
        {
            Open(project);
        if (_document is not { } document) throw new InvalidOperationException("the project did not open");
        UpdateLayout();
        _canvas.Fit();
        report.Add($"open {System.IO.Path.GetFileName(project)}: {document.Width}x{document.Height} "
            + $"at {_canvas.Zoom * 100:0}%");

        // Where the canvas sits in the window, and how to aim at a document point: through the canvas's own
        // mapping, so a check cannot aim at one place and have the tool read another.
        var corner = _canvas.TranslatePoint(new Point(0, 0), this)
            ?? throw new InvalidOperationException("the canvas is not in the window");
        Point Aim(SKPoint at)
        {
            // Tools rebuild the options bar, and showing rulers moves the canvas. Complete that layout
            // before aiming, then use its current position rather than the one at the start of the check.
            UpdateLayout();
            return _canvas.TranslatePoint(_canvas.InView(at), this)
                ?? throw new InvalidOperationException("the canvas is not in the window");
        }
        report.Add($"the canvas sits at {corner.X:0},{corner.Y:0} in the window, "
            + $"{_canvas.Bounds.Width:0}x{_canvas.Bounds.Height:0} of it");
        // How many document pixels the selection holds: what the Colour Range panel is rebuilding as it is used.
        int Covered()
        {
            using var coverage = document.Selection.Coverage(SKRectI.Create(0, 0, document.Width, document.Height));
            if (coverage is null) return 0;
            var count = 0;
            for (var y = 0; y < coverage.Height; y++)
            {
                for (var x = 0; x < coverage.Width; x++)
                {
                    if (coverage.GetPixel(x, y).Red > 128) count++;
                }
            }
            return count;
        }
        var probe = Aim(new SKPoint(30, 30));
        report.Add($"aiming at document 30,30 lands on window {probe.X:0.#},{probe.Y:0.#}, "
            + $"{(corner.X <= probe.X && probe.X <= corner.X + _canvas.Bounds.Width && corner.Y <= probe.Y && probe.Y <= corner.Y + _canvas.Bounds.Height ? "on the canvas" : "off the canvas")}");

        // A brush stroke: a press, a drag and a release, which is the path every tool takes. The layer is chosen
        // for having pixels of its own and selected through the panel, the way a hand would select it.
        var target = document.Layers.FirstOrDefault(one => !one.IsGroup && one.Asset?.Image is not null);
        if (target?.Asset?.Image is not { } pixels) throw new InvalidOperationException("no layer to paint on");
        Reselect(target.ID);
        report.Add($"painting on \"{target.Name}\" ({pixels.Width}x{pixels.Height} of its own pixels)");
        SetTool(Tool.Brush);
        var before = InkOf(pixels);
        Drag(Aim(new SKPoint(30, 30)), Aim(new SKPoint(200, 130)));
        // Read the layer's pixels back: painting may hand the layer a new bitmap, so the one held before the
        // stroke is not necessarily the one the stroke went on.
        var after = InkOf(document.Layers.First(one => one.ID == target.ID).Asset!.Image);
        report.Add($"a brush stroke: {before} → {after} of ink, one \"{_history.UndoName}\" step");
        if (after == before) throw new InvalidOperationException("the stroke painted nothing");
        if (!_history.CanUndo) throw new InvalidOperationException("the stroke left nothing to undo");

        // A marquee: the selection it drags is what the document ends up holding.
        SetTool(Tool.Marquee);
        Drag(Aim(new SKPoint(20, 20)), Aim(new SKPoint(120, 90)));
        if (document.Selection.Path is not { } marquee) throw new InvalidOperationException("the marquee selected nothing");
        var box = marquee.Bounds;
        report.Add($"a marquee drag: {box.Width:0} x {box.Height:0} at {box.Left:0},{box.Top:0}");
        if (box.Width < 90 || box.Width > 110) throw new InvalidOperationException($"the marquee is {box.Width:0} wide, not 100");

        // The selection's own Anti-alias tick, from the options bar to the selection it makes. It is the ellipse
        // that shows it: a rectangle dragged out is whole pixels and comes out hard either way, where an oval's
        // edge always crosses pixels — which is why the Mac build's own tick is drawn with the lasso.
        SetTool(Tool.Ellipse);
        _optionsBar.PressAntialias(false);
        Drag(Aim(new SKPoint(20, 20)), Aim(new SKPoint(120, 90)));
        if (document.Selection.Antialiased) throw new InvalidOperationException("the tick did not reach the selection");
        report.Add($"the Anti-alias tick off: {PartCovered(document)} part-covered pixels round the ellipse");
        if (PartCovered(document) != 0) throw new InvalidOperationException("the edge came out soft with the tick off");
        _optionsBar.PressAntialias(true);
        Drag(Aim(new SKPoint(20, 20)), Aim(new SKPoint(120, 90)));
        report.Add($"the tick back on: {PartCovered(document)} part-covered pixels");
        if (!document.Selection.Antialiased) throw new InvalidOperationException("the tick back on did not take");
        if (PartCovered(document) < 20) throw new InvalidOperationException("the edge did not come out soft again");

        // The filter panel's Preview tick, from the window's side of it: with the tick on the canvas draws the
        // filter, and with it off it draws the document as it stands. (The tick itself is a checkbox whose
        // IsCheckedChanged does the asking, which is the same mechanism the selection's Anti-alias tick above
        // was driven through.)
        StartPreview(document, target.ID);
        RequestPreview((preview, layer) => FilterEdits.Apply(preview, layer, FilterKind.GaussianBlur,
            new FilterSettings { BlurRadius = 8 }));
        ShowPreviewOnce(null, EventArgs.Empty);
        var previewing = _canvas.PreviewDocument is not null;
        HidePreview();
        var hidden = _canvas.PreviewDocument is null;
        report.Add($"the filter preview: shown {previewing}, and with the Preview tick off {hidden}");
        if (!previewing || !hidden) throw new InvalidOperationException("the Preview tick's two states did not take");
        StopPreview();

        // Control-dragging inside the selection carries its pixels: Photoshop's temporary Move tool, and the Mac
        // build's other way of moving them besides Command with an arrow key. The selection here is the ellipse
        // the tick was just driven with, so the press is at its middle.
        var outlineWas = document.Selection.Path!.Bounds;
        var control = RawInputModifiers.LeftMouseButton | RawInputModifiers.Control;
        this.MouseDown(Aim(new SKPoint(70, 55)), MouseButton.Left, control);
        this.MouseMove(Aim(new SKPoint(90, 65)), control);
        if (_canvas.Floating is not { } floating)
        {
            throw new InvalidOperationException("the Control-drag did not take hold of the pixels");
        }
        report.Add($"a Control-drag inside the selection carries {floating.Pixels.Cut.Width}x"
            + $"{floating.Pixels.Cut.Height} of its pixels, {floating.Dx},{floating.Dy} so far"
            + $"{(_canvas.FloatingShowing ? ", drawn where the pointer is" : " and NOT drawn!")}");
        if (!_canvas.FloatingShowing) throw new InvalidOperationException("the pixels are carried but not drawn");
        this.MouseUp(Aim(new SKPoint(90, 65)), MouseButton.Left, RawInputModifiers.None);
        var outlineNow = document.Selection.Path!.Bounds;
        report.Add($"the outline went with them: {outlineWas.Left:0},{outlineWas.Top:0} → "
            + $"{outlineNow.Left:0},{outlineNow.Top:0}, one \"{_history.UndoName}\" step");
        if (_canvas.Floating is not null) throw new InvalidOperationException("the pixels were not put down");
        if (outlineNow.Left == outlineWas.Left) throw new InvalidOperationException("the outline stayed put");
        if (_history.UndoName != "Move Pixels")
        {
            throw new InvalidOperationException($"the drag made a \"{_history.UndoName}\" step");
        }

        // The wand: one click, on a colour the picture actually has.
        SetTool(Tool.Wand);
        Click(Aim(new SKPoint(60, 60)));
        if (document.Selection.Path is not { } wand) throw new InvalidOperationException("the wand selected nothing");
        report.Add($"a wand click: {wand.Bounds.Width:0} x {wand.Bounds.Height:0} of the picture taken");

        // A guide pulled off a ruler: a press on the strip, dragged onto the canvas. The ruler across the top
        // makes a horizontal guide, positioned by the Y it is let go at.
        SetTool(Tool.Pan);
        // Hidden guides cannot be grabbed, and a fresh profile hides them by default. Set the test's view
        // prerequisites explicitly without persisting a change to the person's preferences.
        _guidesVisible = true;
        _guidesLocked = false;
        _showGuides.IsChecked = true;
        _lockGuides.IsChecked = false;
        PushViewSwitches();
        if (!_rulersVisible)
        {
            // Turned on directly rather than through the View menu, so that a check does not write the view's
            // switches into the person's own settings.
            _rulersVisible = true;
            _showRulers.IsChecked = true;
            _rulerAcross.IsVisible = true;
            _rulerDown.IsVisible = true;
            _rulerCorner.IsVisible = true;
            UpdateRulers();
        }
        UpdateLayout();
        if (_rulerAcross.TranslatePoint(new Point(0, 0), this) is { } strip)
        {
            var grab = new Point(strip.X + _canvas.InView(new SKPoint(60, 0)).X, strip.Y + RulerThickness / 2);
            Drag(grab, Aim(new SKPoint(60, 110)));
            var pulled = document.Guides.LastOrDefault();
            report.Add("a guide pulled off the top ruler and dropped at y 110: "
                + (pulled is null ? "nothing" : $"a {pulled.Axis} guide at {pulled.Position:0.#}"));
            if (pulled is not { Axis: GuideAxis.Horizontal } || Math.Abs(pulled.Position - 110) > 3)
            {
                throw new InvalidOperationException("the top ruler did not make a horizontal guide at 110");
            }
            if (document.Guides.Count != 1) throw new InvalidOperationException($"{document.Guides.Count} guides, not one");
        }

        // The same guide dragged along the canvas with the Move tool, which is how an existing one is moved.
        SetTool(Tool.Move);
        var guideToMove = document.Guides.Single();
        var guideWas = guideToMove.Position;
        Drag(Aim(new SKPoint(60, (float)guideWas)), Aim(new SKPoint(60, 40)));
        var moved = document.Guides[0].Position;
        report.Add($"the guide dragged along the canvas: {guideWas:0.#} → {moved:0.#}");
        if (Math.Abs(moved - 40) > 3) throw new InvalidOperationException($"the guide ended at {moved:0.#}, not 40");
        if (document.Guides.Count != 1 || document.Guides[0].ID != guideToMove.ID || _history.UndoName != "Move Guide")
        {
            throw new InvalidOperationException("the drag did not move the same guide in one Move Guide step");
        }

        // The Type tool's caret: a click where the words go, then the words, and the layer carries them.
        SetTool(Tool.Type);
        var layers = document.Layers.Count;
        this.MouseDown(Aim(new SKPoint(40, 120)), MouseButton.Left, RawInputModifiers.LeftMouseButton);
        this.MouseUp(Aim(new SKPoint(40, 120)), MouseButton.Left, RawInputModifiers.None);
        this.KeyTextInput("Compositor");
        SetTool(Tool.Pan);      // leaving the tool is what commits what was typed
        var typed = document.Layers.LastOrDefault(one => one.Text is not null);
        report.Add($"the Type tool: a click and a word made {document.Layers.Count - layers} layer, holding "
            + $"\"{typed?.Text?.Style.Content}\"");
        if (typed?.Text is not { } words || words.Style.Content != "Compositor")
        {
            throw new InvalidOperationException("the words typed on the canvas did not reach a text layer");
        }

        // The Layers panel's own opacity slider, dragged: the layer under it dims.
        if (Selected is not { } dimming) throw new InvalidOperationException("no layer is selected to dim");
        if (_opacity.TranslatePoint(new Point(0, 0), this) is not { } track)
        {
            throw new InvalidOperationException("the opacity slider is not in the window");
        }
        var wasOpacity = document.Layers.First(one => one.ID == dimming).Opacity;
        var down = track.Y + _opacity.Bounds.Height / 2;
        Drag(new Point(track.X + 110, down), new Point(track.X + 30, down));
        var nowOpacity = document.Layers.First(one => one.ID == dimming).Opacity;
        report.Add($"the opacity slider dragged left: {wasOpacity:0.00} → {nowOpacity:0.00}, "
            + $"one \"{_history.UndoName}\" step");
        if (nowOpacity >= wasOpacity) throw new InvalidOperationException("the slider did not dim the layer");

        // The Move tool's corner taken on its own with Control held: a distortion, resampled on release. This is
        // before the crop so the layer being warped is still on the canvas for a handle to be pressed on.
        Reselect(target.ID);
        SetTool(Tool.Move);
        if (_canvas.TransformBox is not { } warpBox) throw new InvalidOperationException("no transform box to distort");
        var warpCorner = TransformEdits.Position(warpBox, TransformHandle.TopLeft);
        var inked = InkOf(document.Layers.First(one => one.ID == target.ID).Asset!.Image);
        var holding = RawInputModifiers.LeftMouseButton | RawInputModifiers.Control;
        this.MouseDown(Aim(warpCorner), MouseButton.Left, holding);
        this.MouseMove(Aim(new SKPoint(warpCorner.X + 40, warpCorner.Y + 30)), holding);
        this.MouseUp(Aim(new SKPoint(warpCorner.X + 40, warpCorner.Y + 30)), MouseButton.Left, RawInputModifiers.None);
        var inkedNow = InkOf(document.Layers.First(one => one.ID == target.ID).Asset!.Image);
        report.Add($"a Control-drag on the transform box's top-left corner: the layer's pixels "
            + $"{(inkedNow == inked ? "did NOT change" : "were resampled")}, one \"{_history.UndoName}\" step");
        if (inkedNow == inked) throw new InvalidOperationException("the corner drag did not distort the layer");
        if (_history.UndoName != "Distort")
        {
            throw new InvalidOperationException($"the distort made a \"{_history.UndoName}\" step");
        }

        // The Crop tool: picking it with something selected starts its frame at the selection, as the Mac build
        // does, and a corner handle drags it in from there.
        var selected = document.Selection.Path!.Bounds;
        SetTool(Tool.Crop);
        if (_cropFrame is not { } seeded) throw new InvalidOperationException("the crop did not start at the selection");
        report.Add($"picking Crop with a selection starts its frame at {seeded.Width} x {seeded.Height}, "
            + $"against the selection's {selected.Width:0} x {selected.Height:0}");
        if (Math.Abs(seeded.Width - (int)selected.Width) > 2)
        {
            throw new InvalidOperationException("the frame is not the selection it was seeded from");
        }
        var canvasWas = (document.Width, document.Height);
        Drag(Aim(new SKPoint(seeded.Left + 1, seeded.Top + 1)), Aim(new SKPoint(90, 70)));
        report.Add($"its corner handle dragged in: {_cropFrame?.Width ?? 0} x {_cropFrame?.Height ?? 0}");
        Press(new ShortcutChord("Enter"));
        report.Add($"the crop applied by Enter: {canvasWas.Width}x{canvasWas.Height} → "
            + $"{document.Width}x{document.Height}, one \"{_history.UndoName}\" step");
        if (document.Width >= canvasWas.Width || document.Height >= canvasWas.Height)
        {
            throw new InvalidOperationException($"the canvas is {document.Width}x{document.Height}, not cropped");
        }
        if (_history.UndoName != "Crop") throw new InvalidOperationException($"the crop made a \"{_history.UndoName}\" step");

        // The Layers panel's blend pop-up: a click opens it, and a click elsewhere closes it. A pop-up is a
        // window of its own rather than part of this one, which is why nothing else here touches it.
        Reselect(target.ID);
        if (_blend.TranslatePoint(new Point(_blend.Bounds.Width / 2, _blend.Bounds.Height / 2), this) is not { } popup)
        {
            throw new InvalidOperationException("the blend pop-up is not in the window");
        }
        Click(popup);
        var opened = _blend.IsDropDownOpen;
        Click(Aim(new SKPoint(200, 140)));      // anywhere else, which light-dismisses it
        report.Add($"the blend pop-up: opened {opened}, and closed {!_blend.IsDropDownOpen}");
        if (!opened) throw new InvalidOperationException("the blend pop-up did not open");
        if (_blend.IsDropDownOpen) throw new InvalidOperationException("the blend pop-up did not close");

        // The tool rail, clicked rather than invoked: the rail sits in a scroll view, which is the one place a
        // drawn control might be visible and still not reachable.
        SetTool(Tool.Pan);
        if (_rail.ButtonFor(Tool.Brush)?.TranslatePoint(
                new Point(_rail.ButtonFor(Tool.Brush)!.Bounds.Width / 2,
                    _rail.ButtonFor(Tool.Brush)!.Bounds.Height / 2), this) is not { } railBrush)
        {
            throw new InvalidOperationException("the rail has no button for the Brush");
        }
        Click(railBrush);
        report.Add($"the rail's Brush button clicked: the tool is {_tool} and the rail marks {_rail.Marked}");
        if (_tool != Tool.Brush || _rail.Marked != Tool.Brush)
        {
            throw new InvalidOperationException($"clicking the rail's Brush gave {_tool}");
        }

        // The toolbar's own zoom button, clicked: the strip under the menu bar is the Mac's furniture, and until
        // now it had only ever been invoked through the same methods the View menu calls.
        _canvas.Fit();
        var zoomWas = _canvas.Zoom;
        if (!_toolbar.TryGetValue("100%", out var actual))
        {
            throw new InvalidOperationException("the toolbar has no 100% button");
        }
        if (actual.TranslatePoint(new Point(actual.Bounds.Width / 2, actual.Bounds.Height / 2), this)
            is not { } onActual)
        {
            throw new InvalidOperationException("the toolbar's 100% button is not in the window");
        }
        Click(onActual);
        report.Add($"the toolbar's 100% button clicked: zoom {zoomWas * 100:0}% → {_canvas.Zoom * 100:0}%");
        if (Math.Abs(_canvas.Zoom - 1) > 0.001)
        {
            throw new InvalidOperationException($"the toolbar's 100% left the zoom at {_canvas.Zoom * 100:0}%");
        }

        // The Camera Raw panel's own amounts, clicked rather than driven through the panel's API: the panel is
        // docked in this window and its amounts are inside a scroll view, which is where a control can be drawn
        // and still not reachable.
        CameraRawFilter();
        if (_cameraRaw is not { } raw) throw new InvalidOperationException("the Camera Raw panel did not open");
        // The panel's amounts are inside a scroll view, whose content is only attached when a layout pass runs —
        // so without this the sliders are built, docked, and still nowhere a pointer can reach.
        UpdateLayout();
        if (raw.Amount("Exposure, stops") is not { } exposure)
        {
            throw new InvalidOperationException("the panel has no Exposure amount to click");
        }
        var exposureWas = raw.Current().Exposure;
        if (exposure.TranslatePoint(new Point(exposure.Bounds.Width * 0.8, exposure.Bounds.Height / 2), this)
            is not { } onSlider)
        {
            throw new InvalidOperationException("the panel's Exposure slider is not in the window");
        }
        Click(onSlider);
        var exposureNow = raw.Current().Exposure;
        report.Add($"the Camera Raw panel's Exposure slider clicked near its top: "
            + $"{exposureWas:0.00} → {exposureNow:0.00}");
        if (Math.Abs(exposureNow - exposureWas) < 0.1)
        {
            throw new InvalidOperationException("clicking the panel's own slider changed nothing");
        }
        CloseCameraRaw();

        // The filter panel's own widgets: the verb the Filter menu calls opens the panel and waits for it, and
        // the panel it opened is found among the windows this one owns. Its amount is clicked and its Apply is
        // pressed, and then the jobs a dispatcher loop would run are run — which is what lets the waiting verb
        // finish and put the filter on the layer. Nothing here reaches into the panel; it is aimed at where a
        // hand would click.
        Reselect(target.ID);
        var inkedForFilter = InkOf(document.Layers.First(one => one.ID == target.ID).Asset!.Image);
        var filtering = ApplyFilter(FilterKind.Vignette);
        var filterPanel = OwnedWindows.OfType<FilterDialog>().FirstOrDefault()
            ?? throw new InvalidOperationException("the Filter menu's verb opened no panel");
        // Its amounts are inside a scroll view, whose content is only attached when a layout pass runs.
        filterPanel.UpdateLayout();
        var vignetteAmount = AmountIn(filterPanel, "Amount");
        ClickIn(filterPanel, vignetteAmount, 0.85);
        var asked = filterPanel.Current().VignetteAmount;
        if (asked < 60) throw new InvalidOperationException($"the click left the amount at {asked}, near where it started");
        // Reset puts the panel back to what the filter is made with, the colour included — which is the one
        // place the two kinds of control share.
        PressIn(filterPanel, "Reset");
        var reset = filterPanel.Current();
        report.Add($"the panel's Reset: amount {reset.VignetteAmount:0.#}, colour "
            + $"{reset.VignetteRed:0.00},{reset.VignetteGreen:0.00},{reset.VignetteBlue:0.00}");
        if (reset.VignetteAmount != new FilterSettings().VignetteAmount
            || reset.VignetteRed != 0 || reset.VignetteGreen != 0 || reset.VignetteBlue != 0)
        {
            throw new InvalidOperationException("Reset did not put the amount and the colour back");
        }
        ClickIn(filterPanel, vignetteAmount, 0.85);
        asked = filterPanel.Current().VignetteAmount;

        // The panel's colour is a swatch, and the swatch opens the app's own picker — on the panel's colour,
        // owned by the panel, with every colour it is moved to reported back. That report is what the sheet
        // previews through, so a colour picked there has to arrive in the panel's own amounts.
        if (filterPanel.GetVisualDescendants().OfType<ColorSwatch>().FirstOrDefault() is not { } vignetteSwatch)
        {
            throw new InvalidOperationException("the filter panel's colour is not a swatch");
        }
        ClickIn(filterPanel, vignetteSwatch, 0.5);
        var colourPicker = filterPanel.OwnedWindows.OfType<ColorPickerDialog>().FirstOrDefault()
            ?? throw new InvalidOperationException("the panel's swatch opened no picker");
        colourPicker.UpdateLayout();
        // The field's top right corner: full saturation at full brightness, at whatever hue the strip is on.
        Press(colourPicker, At(colourPicker, colourPicker.Field, 0.85, 0.15));
        Press(colourPicker, At(colourPicker, colourPicker.Ok, 0.5));
        Dispatcher.UIThread.RunJobs();
        // The panel hands back the amounts it is editing, so what a step is compared against is read into
        // numbers here rather than held as an object that goes on changing.
        var withColour = filterPanel.Current();
        var (pickedRed, pickedGreen, pickedBlue) = (withColour.VignetteRed, withColour.VignetteGreen, withColour.VignetteBlue);
        report.Add($"the panel's colour swatch opened the picker: the colour is now "
            + $"{pickedRed:0.00},{pickedGreen:0.00},{pickedBlue:0.00}");
        if (pickedRed + pickedGreen + pickedBlue < 0.2)
        {
            throw new InvalidOperationException("the picker's colour never reached the panel");
        }
        if (withColour.VignetteAmount != asked)
        {
            throw new InvalidOperationException("picking a colour changed the amount beside it");
        }

        // And a Cancel puts it back: the picker reports the colour it opened on, which is what the swatch and the
        // preview behind it go back to.
        ClickIn(filterPanel, vignetteSwatch, 0.5);
        if (filterPanel.OwnedWindows.OfType<ColorPickerDialog>().LastOrDefault() is not { } backedOut)
        {
            throw new InvalidOperationException("the swatch opened no second picker");
        }
        backedOut.UpdateLayout();
        Press(backedOut, At(backedOut, backedOut.Field, 0.1, 0.9));
        var afterMove = filterPanel.Current();
        if (afterMove.VignetteRed == pickedRed && afterMove.VignetteGreen == pickedGreen)
        {
            throw new InvalidOperationException("the picker's move never reached the panel");
        }
        Press(backedOut, At(backedOut, backedOut.Cancel, 0.5));
        Dispatcher.UIThread.RunJobs();
        var restored = filterPanel.Current();
        report.Add($"the picker closed with Cancel: the colour is back at "
            + $"{restored.VignetteRed:0.00},{restored.VignetteGreen:0.00},{restored.VignetteBlue:0.00}");
        if (restored.VignetteRed != pickedRed || restored.VignetteGreen != pickedGreen
            || restored.VignetteBlue != pickedBlue)
        {
            throw new InvalidOperationException("Cancel did not put the panel's colour back");
        }
        // The panel's buttons are below the fold of its own scroll view, and a click outside the viewport does
        // not reach them: the view is scrolled the way a hand would scroll it first.
        ScrollToEnd(filterPanel);
        PressIn(filterPanel, "Apply");
        Dispatcher.UIThread.RunJobs();
        if (!filtering.IsCompletedSuccessfully) throw new InvalidOperationException("the filter verb never finished");
        var inkedAfterFilter = InkOf(document.Layers.First(one => one.ID == target.ID).Asset!.Image);
        report.Add($"the filter panel's own amount clicked and its Apply pressed: the amount went to {asked:0.#}, "
            + $"the layer's ink {inkedForFilter} → {inkedAfterFilter}, one \"{_history.UndoName}\" step");
        if (inkedAfterFilter == inkedForFilter) throw new InvalidOperationException("the filter changed nothing");
        if (_history.UndoName != "Vignette Filter") throw new InvalidOperationException($"the filter made a \"{_history.UndoName}\" step");
        // And the amounts the panel was left with are the ones the window remembers for the next time.
        if (_filterAmounts.VignetteAmount != asked)
        {
            throw new InvalidOperationException(
                $"the window remembers {_filterAmounts.VignetteAmount} rather than the {asked} the panel was left with");
        }

        // The curve editor, dragged with the pointer: it is the one control of this app that a hand reaches and
        // nothing else does. Image ▸ Curves runs it over the layer's own pixels, so the drag is read back out of
        // the document rather than out of the editor.
        Reselect(target.ID);
        var inkedForCurve = InkOf(document.Layers.First(one => one.ID == target.ID).Asset!.Image);
        var adjusting = ImageAdjustment(AdjustmentKind.Curves);
        var curvePanel = OwnedWindows.OfType<AdjustmentDialog>().FirstOrDefault()
            ?? throw new InvalidOperationException("the Image menu's Curves opened no panel");
        curvePanel.UpdateLayout();
        if (curvePanel.GetVisualDescendants().OfType<CurveEditor>().FirstOrDefault() is not { } curve)
        {
            throw new InvalidOperationException("the curves panel has no curve to drag");
        }
        // From the middle of the line upwards: a click there puts a handle on the line, and the drag lifts it, so
        // the midtones come up.
        DragIn(curvePanel, curve,
            new Point(curve.Bounds.Width / 2, curve.Bounds.Height / 2),
            new Point(curve.Bounds.Width / 2, curve.Bounds.Height / 4));
        ScrollToEnd(curvePanel);
        PressIn(curvePanel, "Apply");
        Dispatcher.UIThread.RunJobs();
        if (!adjusting.IsCompletedSuccessfully) throw new InvalidOperationException("the Curves verb never finished");
        var inkedAfterCurve = InkOf(document.Layers.First(one => one.ID == target.ID).Asset!.Image);
        report.Add($"the curve editor dragged and its Apply pressed: the layer's ink "
            + $"{inkedForCurve} → {inkedAfterCurve}, one \"{_history.UndoName}\" step");
        if (inkedAfterCurve == inkedForCurve) throw new InvalidOperationException("the curve changed nothing");
        if (_history.UndoName != "Curves") throw new InvalidOperationException($"the curve made a \"{_history.UndoName}\" step");

        // The Gradient Map's two ends are swatches over the bar they make, and each opens the picker: the same
        // path as the panels', inside a different dialog, so it is driven the same way.
        Reselect(target.ID);
        var inkedForMap = InkOf(document.Layers.First(one => one.ID == target.ID).Asset!.Image);
        var mapping = ImageAdjustment(AdjustmentKind.GradientMap);
        var mapPanel = OwnedWindows.OfType<AdjustmentDialog>().FirstOrDefault()
            ?? throw new InvalidOperationException("the Image menu's Gradient Map opened no panel");
        mapPanel.UpdateLayout();
        if (mapPanel.GetVisualDescendants().OfType<ColorSwatch>().FirstOrDefault() is not { } shadows)
        {
            throw new InvalidOperationException("the Gradient Map panel has no swatches");
        }
        ClickIn(mapPanel, shadows, 0.5);
        var mapPicker = mapPanel.OwnedWindows.OfType<ColorPickerDialog>().FirstOrDefault()
            ?? throw new InvalidOperationException("the Gradient Map panel's swatch opened no picker");
        mapPicker.UpdateLayout();
        Press(mapPicker, At(mapPicker, mapPicker.Field, 0.9, 0.15));
        Press(mapPicker, At(mapPicker, mapPicker.Ok, 0.5));
        Dispatcher.UIThread.RunJobs();
        ScrollToEnd(mapPanel);
        PressIn(mapPanel, "Apply");
        Dispatcher.UIThread.RunJobs();
        if (!mapping.IsCompletedSuccessfully) throw new InvalidOperationException("the Gradient Map verb never finished");
        var inkedAfterMap = InkOf(document.Layers.First(one => one.ID == target.ID).Asset!.Image);
        report.Add($"the Gradient Map's shadows swatch opened the picker and its Apply pressed: the layer's ink "
            + $"{inkedForMap} → {inkedAfterMap}, one \"{_history.UndoName}\" step");
        if (inkedAfterMap == inkedForMap) throw new InvalidOperationException("the gradient map changed nothing");
        if (_history.UndoName != "Gradient Map") throw new InvalidOperationException($"the map made a \"{_history.UndoName}\" step");

        // The Dither panel, driven the same way, and opened a second time to see that it opens where it was
        // left: the look and the amounts the last Apply used are the ones the Mac's one set of filter settings
        // keeps. The look is chosen from the pop-up and an amount is clicked, so what the check leaves is what
        // the panel was told, not what it started with.
        Reselect(target.ID);
        var inkedForDither = InkOf(document.Layers.First(one => one.ID == target.ID).Asset!.Image);
        var dithering = DitherFilter();
        var ditherPanel = OwnedWindows.OfType<DitherDialog>().FirstOrDefault()
            ?? throw new InvalidOperationException("the Dither verb opened no panel");
        ditherPanel.UpdateLayout();
        if (ditherPanel.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault() is not { } look)
        {
            throw new InvalidOperationException("the Dither panel has no list of looks");
        }
        var halftone = look.Items.Cast<object>().ToList().FindIndex(item => LabelOf(item) == L10n.T("Halftone Dots"));
        if (halftone < 0) throw new InvalidOperationException("the Dither panel has no Halftone Dots look");
        look.SelectedIndex = halftone;
        // Two Colors is the choice whose ink and paper are the panel's own, so it is the one that shows the
        // two swatches; the ink is then chosen through the picker, as the Mac's panel does it.
        var inkAndPaper = ditherPanel.GetVisualDescendants().OfType<ComboBox>()
            .FirstOrDefault(box => box.Items.Cast<object>().Any(item => LabelOf(item) == L10n.T("Two Colors")))
            ?? throw new InvalidOperationException("the Dither panel has no Ink and paper list");
        inkAndPaper.SelectedIndex = inkAndPaper.Items.Cast<object>().ToList().FindIndex(item => LabelOf(item) == L10n.T("Two Colors"));
        Dispatcher.UIThread.RunJobs();
        if (ditherPanel.GetVisualDescendants().OfType<ColorSwatch>().FirstOrDefault() is not { } ink)
        {
            throw new InvalidOperationException("the Dither panel's ink is not a swatch");
        }
        ClickIn(ditherPanel, ink, 0.5);
        var inkPicker = ditherPanel.OwnedWindows.OfType<ColorPickerDialog>().FirstOrDefault()
            ?? throw new InvalidOperationException("the Dither panel's swatch opened no picker");
        inkPicker.UpdateLayout();
        Press(inkPicker, At(inkPicker, inkPicker.Field, 0.9, 0.15));
        Press(inkPicker, At(inkPicker, inkPicker.Ok, 0.5));
        Dispatcher.UIThread.RunJobs();
        var inkNow = ditherPanel.Current();
        report.Add($"the Dither panel's ink swatch opened the picker: the ink is now "
            + $"{inkNow.DarkRed:0.00},{inkNow.DarkGreen:0.00},{inkNow.DarkBlue:0.00}");
        if (inkNow.DarkRed <= 0.5) throw new InvalidOperationException("the picker's colour never reached the ink");
        if (inkNow.Colors != DitherColors.TwoColors) throw new InvalidOperationException("the ink choice was lost");
        // Contrast is an amount every look shows, so it is the one to click.
        var contrast = AmountIn(ditherPanel, "Contrast");
        ClickIn(ditherPanel, contrast, 0.9);
        var wantedContrast = ditherPanel.Current().Contrast;
        if (Math.Abs(wantedContrast) < 20) throw new InvalidOperationException($"the click left contrast at {wantedContrast}");
        ScrollToEnd(ditherPanel);
        PressIn(ditherPanel, "Apply");
        Dispatcher.UIThread.RunJobs();
        if (!dithering.IsCompletedSuccessfully) throw new InvalidOperationException("the Dither verb never finished");
        var inkedAfterDither = InkOf(document.Layers.First(one => one.ID == target.ID).Asset!.Image);
        report.Add($"the Dither panel's look chosen and its Contrast clicked: the layer's ink "
            + $"{inkedForDither} → {inkedAfterDither}, one \"{_history.UndoName}\" step");
        if (inkedAfterDither == inkedForDither) throw new InvalidOperationException("the dither changed nothing");
        if (_history.UndoName != "Dither") throw new InvalidOperationException($"the dither made a \"{_history.UndoName}\" step");

        // Opened again, on the look and amounts it was left with rather than on the ones it opens with.
        var ditheringAgain = DitherFilter();
        var reopened = OwnedWindows.OfType<DitherDialog>().FirstOrDefault()
            ?? throw new InvalidOperationException("the Dither panel did not open a second time");
        reopened.UpdateLayout();
        var kept = reopened.Current();
        report.Add($"opened again: {reopened.Style()} with contrast {kept.Contrast:0.#}, "
            + $"against the {wantedContrast:0.#} it was left with");
        if (reopened.Style() != DitherStyle.Dots)
        {
            throw new InvalidOperationException($"the Dither panel opened on {reopened.Style()}, not the look it was left with");
        }
        if (Math.Abs(kept.Contrast - wantedContrast) > 1e-9)
        {
            throw new InvalidOperationException(
                $"the Dither panel opened with contrast {kept.Contrast} rather than {wantedContrast}");
        }
        ScrollToEnd(reopened);
        PressIn(reopened, "Cancel");
        Dispatcher.UIThread.RunJobs();
        if (!ditheringAgain.IsCompletedSuccessfully) throw new InvalidOperationException("the dismissed Dither panel never finished");

        // The colour picker, which is a window of this app's own: the rail's swatch opens it, a click in the
        // saturation and brightness field and one on the hue strip move the colour, a click on the canvas while
        // it is up samples into it, and OK takes the colour the swatch is left showing.
        SetTool(Tool.Brush);
        ResetColours();
        if (_rail.SwatchFor(foreground: true) is not { } frontSwatch)
        {
            throw new InvalidOperationException("the rail has no foreground swatch");
        }
        if (frontSwatch.TranslatePoint(new Point(frontSwatch.Bounds.Width / 2,
                frontSwatch.Bounds.Height / 2), this) is not { } onSwatch)
        {
            throw new InvalidOperationException("the rail's foreground swatch is not in the window");
        }
        Click(onSwatch);
        if (_picker is not { } picker) throw new InvalidOperationException("the swatch opened no picker");
        report.Add($"the rail's foreground swatch opened the picker, {picker.Width:0}x{picker.Height:0} of its own");

        // The hue strip first, a third of the way down, which is 240 degrees round the wheel: blue. The strip
        // takes the pointer where a drag would, and the colour is read back off the picker itself.
        picker.UpdateLayout();
        if (picker.Hue.TranslatePoint(new Point(picker.Hue.Bounds.Width / 2, picker.Hue.Bounds.Height / 3), picker)
            is not { } onHue)
        {
            throw new InvalidOperationException("the picker's hue strip is nowhere a pointer can reach");
        }
        picker.MouseDown(onHue, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        picker.MouseUp(onHue, MouseButton.Left, RawInputModifiers.None);

        // Then the field's top right corner, which is full saturation at full brightness: blue, if the strip
        // sent the pointer where it was sent. The brush's own colour is read back too, since nothing is to be
        // taken until the picker is put away.
        if (picker.Field.TranslatePoint(new Point(picker.Field.Bounds.Width - 2, 2), picker) is not { } onField)
        {
            throw new InvalidOperationException("the picker's field is nowhere a pointer can reach");
        }
        picker.MouseDown(onField, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        picker.MouseUp(onField, MouseButton.Left, RawInputModifiers.None);
        var chosen = picker.Colour;
        report.Add($"a third of the way down the hue strip, then the field's top right corner: "
            + $"{chosen.Red * 255:0},{chosen.Green * 255:0},{chosen.Blue * 255:0}");
        if (chosen.Blue < 0.95 || chosen.Red > 0.05 || chosen.Green > 0.05)
        {
            throw new InvalidOperationException(
                $"the corner gave {chosen.Red * 255:0},{chosen.Green * 255:0},{chosen.Blue * 255:0} rather than blue");
        }
        // Nothing has been taken yet: the brush is still the black the swatch reset it to, as the Mac build's
        // picker leaves the palette alone until it is put away with OK.
        if (BrushColour() != new SKColor(0, 0, 0)) throw new InvalidOperationException("the picker took the colour early");

        // A click on the canvas while the picker is up samples into it, which is what its own line says.
        SetTool(Tool.Eyedropper);
        Click(Aim(new SKPoint(30, 30)));
        var sampled = picker.Colour;
        report.Add($"the canvas clicked with the picker up: it is now "
            + $"{sampled.Red * 255:0},{sampled.Green * 255:0},{sampled.Blue * 255:0}");
        if (sampled == chosen) throw new InvalidOperationException("the canvas click did not sample into the picker");
        if (BrushColour() != new SKColor(0, 0, 0)) throw new InvalidOperationException("the sample went to the brush");

        // OK, pressed rather than invoked, and the colour the picker ended on is the brush's.
        if (picker.Ok.TranslatePoint(new Point(picker.Ok.Bounds.Width / 2, picker.Ok.Bounds.Height / 2), picker)
            is not { } onOk)
        {
            throw new InvalidOperationException("the picker's OK is nowhere a pointer can reach");
        }
        picker.MouseDown(onOk, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        picker.MouseUp(onOk, MouseButton.Left, RawInputModifiers.None);
        var taken = BrushColour();
        report.Add($"OK pressed: the picker is {(_picker is null ? "away" : "STILL UP")} and the brush is "
            + $"{Spell(taken)}, the colour the canvas was sampled at");
        if (_picker is not null) throw new InvalidOperationException("OK left the picker up");
        if (taken != new SKColor((byte)Math.Round(sampled.Red * 255), (byte)Math.Round(sampled.Green * 255),
                (byte)Math.Round(sampled.Blue * 255)))
        {
            throw new InvalidOperationException("the brush is not the colour the picker ended on");
        }

        // The eyedropper's Sample Ring: press and drag on the picture and the ring follows the pointer, naming
        // the colour under it across its top half and the colour being replaced across its bottom. This is the
        // last step and the pointer is left down, so the frame the caller photographs has the ring in it — a
        // pointer coming up takes the ring away, which is what the Mac build's does too.
        SetTool(Tool.Eyedropper);
        this.MouseDown(Aim(new SKPoint(30, 30)), MouseButton.Left, RawInputModifiers.LeftMouseButton);
        this.MouseMove(Aim(new SKPoint(60, 60)), RawInputModifiers.LeftMouseButton);
        if (_canvas.SampleRing is not { } ring) throw new InvalidOperationException("the sample ring did not come up");
        report.Add($"the eyedropper's sample ring: the colour being replaced is "
            + $"{ring.Original.Red},{ring.Original.Green},{ring.Original.Blue} and the one under the pointer is "
            + $"{ring.Sampled.Red},{ring.Sampled.Green},{ring.Sampled.Blue}");
        if (ring.Original == ring.Sampled) throw new InvalidOperationException("the ring names one colour twice");
        this.MouseUp(Aim(new SKPoint(60, 60)), MouseButton.Left, RawInputModifiers.None);

        // Select ▸ Colour Range, as the Mac build presents it: the panel is a window of its own and the picture
        // stays live behind it, so a colour is picked by clicking the picture rather than typed into a dialog.
        // Every pick rebuilds the selection on the document, so the canvas draws the ants while the panel is
        // used, and OK closes the one history step the whole session is.
        Deselect();
        SetTool(Tool.Brush);
        ColorRange();
        if (OwnedWindows.OfType<ColorRangePanel>().FirstOrDefault() is not { } range)
        {
            throw new InvalidOperationException("the Color Range verb opened no panel");
        }
        range.UpdateLayout();
        report.Add($"the Color Range panel is up, {range.Width:0} wide, over a document with no selection");
        if (document.Selection.Path is not null) throw new InvalidOperationException("the range did not start clear");

        Click(Aim(new SKPoint(30, 30)));
        if (document.Selection.Path is not { IsEmpty: false } pickedSo)
        {
            throw new InvalidOperationException("the click picked no colour");
        }
        var first = pickedSo.Bounds;
        report.Add($"a click on the picture with the panel up selected {first.Width:0}x{first.Height:0} of it, "
            + $"and the panel is {(range.ShowingMask ? "showing the selection" : "showing NOTHING")}");
        if (!range.ShowingMask) throw new InvalidOperationException("the panel drew no preview of the selection");
        if (_colorRange is not { Include.Count: 1 }) throw new InvalidOperationException("the panel took no colour");

        // The panel's own Fuzziness is not only one of its amounts: it is what the selection is rebuilt from, so
        // moving it has to move the selection.
        var rangeFuzziness = AmountIn(range, "Fuzziness");
        var coveredWas = Covered();
        ClickIn(range, rangeFuzziness, 0.05);
        var coveredNow = Covered();
        report.Add($"the panel's Fuzziness clicked down: {coveredWas} → {coveredNow} pixel(s) covered, at a "
            + $"fuzziness of {_colorRange?.Fuzziness:0}");
        if (_colorRange is null || _colorRange.Fuzziness > 15) throw new InvalidOperationException("the fuzziness did not take");
        if (coveredNow >= coveredWas) throw new InvalidOperationException("the fuzziness changed nothing");

        // Add, and Alt held while a click is made, are the panel's two ways of picking more than one colour.
        PressIn(range, "Add");
        Click(Aim(new SKPoint(90, 50)));
        report.Add($"a second click with Add chosen makes the range {_colorRange?.Include.Count} colour(s)");
        if (_colorRange is not { Include.Count: 2 }) throw new InvalidOperationException("Add did not join the range");
        this.MouseDown(Aim(new SKPoint(90, 50)), MouseButton.Left,
            RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
        this.MouseUp(Aim(new SKPoint(90, 50)), MouseButton.Left, RawInputModifiers.None);
        report.Add($"the same colour taken away again with Alt: {_colorRange?.Include.Count} picked, "
            + $"{_colorRange?.Exclude.Count} taken away");
        if (_colorRange is not { Include.Count: 2, Exclude.Count: 1 })
        {
            throw new InvalidOperationException("Alt did not take the colour out of the range");
        }

        // OK keeps the selection as one step; the panel and its sample go with it.
        var stood = document.Selection.Path?.Bounds ?? SKRect.Empty;
        PressIn(range, "OK");
        Dispatcher.UIThread.RunJobs();
        if (_colorRange is not null) throw new InvalidOperationException("OK left the panel up");
        if (_history.UndoName != "Color Range") throw new InvalidOperationException($"the range made a \"{_history.UndoName}\" step");
        report.Add($"OK: the panel is away and the history holds \"{_history.UndoName}\"");

        // Opened again and cancelled, the selection there was stands and no step is added for it.
        ColorRange();
        if (OwnedWindows.OfType<ColorRangePanel>().FirstOrDefault() is not { } second)
        {
            throw new InvalidOperationException("the Color Range panel did not open a second time");
        }
        second.UpdateLayout();
        // A second panel starts with no colours of its own: the ones that made the selection went with the panel.
        if (second.ShowingMask) throw new InvalidOperationException("the second panel started with a selection of its own");
        Click(Aim(new SKPoint(30, 30)));
        PressIn(second, "Cancel");
        Dispatcher.UIThread.RunJobs();
        if (_colorRange is not null) throw new InvalidOperationException("Cancel left the panel up");
        var back = document.Selection.Path?.Bounds ?? SKRect.Empty;
        report.Add($"cancelled: the selection is back at {back.Left:0},{back.Top:0} {back.Width:0}x{back.Height:0}");
        if (!back.Equals(stood)) throw new InvalidOperationException("Cancel did not put the selection back");
        if (_history.UndoName != "Color Range") throw new InvalidOperationException($"Cancel added a \"{_history.UndoName}\" step");

        // And the picker opened once more on the background colour and left up, so the caller can photograph it:
        // it is a window of its own, so the picture of the main window does not hold it.
        if (_rail.SwatchFor(foreground: false) is not { } backSwatch)
        {
            throw new InvalidOperationException("the rail has no background swatch");
        }
        if (backSwatch.TranslatePoint(new Point(backSwatch.Bounds.Width / 2,
                backSwatch.Bounds.Height / 2), this) is not { } onBack)
        {
            throw new InvalidOperationException("the rail's background swatch is not in the window");
        }
        Click(onBack);
        if (_picker is not { } left) throw new InvalidOperationException("the background swatch opened no picker");
        if (left.Field.TranslatePoint(new Point(left.Field.Bounds.Width * 0.35, left.Field.Bounds.Height * 0.25),
                left) is not { } somewhere)
        {
            throw new InvalidOperationException("the picker's field is nowhere a pointer can reach");
        }
        left.MouseDown(somewhere, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        left.MouseUp(somewhere, MouseButton.Left, RawInputModifiers.None);
        report.Add($"the picker left up on the background colour, showing "
            + $"{left.Colour.Red * 255:0},{left.Colour.Green * 255:0},{left.Colour.Blue * 255:0}, for the drawing");
        }
        catch (Exception failure)
        {
            report.Add($"FAILED: {failure.Message}");
        }
        return string.Join(Environment.NewLine, report);
    }

    /// <summary>A single click with the left button, pressed and let go in the same place.</summary>
    private void Click(Point at)
    {
        this.MouseDown(at, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        this.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
    }

    /// <summary>
    /// A dialog's own widget clicked, aimed the way a hand aims: at a fraction across the control, through the
    /// control's own mapping into the dialog. A dialog is a window of its own, so the pointer has to be aimed
    /// at that window rather than this one.
    /// </summary>
    private static void ClickIn(Window dialog, Control control, double across, double down = 0.5)
    {
        Press(dialog, At(dialog, control, across, down));
    }

    /// <summary>A drag inside a dialog, from one fraction of a control to another, as a hand drags it.</summary>
    private static void DragIn(Window dialog, Control control, Point from, Point to)
    {
        var start = control.TranslatePoint(from, dialog)
            ?? throw new InvalidOperationException($"the dialog's {control.GetType().Name} is out of reach");
        var end = control.TranslatePoint(to, dialog)
            ?? throw new InvalidOperationException($"the dialog's {control.GetType().Name} is out of reach");
        dialog.MouseDown(start, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        for (var step = 1; step <= 6; step++)
        {
            dialog.MouseMove(new Point(
                start.X + (end.X - start.X) * step / 6.0,
                start.Y + (end.Y - start.Y) * step / 6.0), RawInputModifiers.LeftMouseButton);
        }
        dialog.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
    }

    /// <summary>The point a fraction across and down one of a dialog's controls sits at in the dialog.</summary>
    private static Point At(Window dialog, Control control, double across, double down = 0.5) =>
        control.TranslatePoint(new Point(control.Bounds.Width * across, control.Bounds.Height * down), dialog)
        ?? throw new InvalidOperationException($"the dialog's {control.GetType().Name} is out of reach");

    /// <summary>What a pop-up item says, whether it was added as text or as an item of its own — the grouped
    /// lists carry items, a plain list of choices carries the strings themselves.</summary>
    private static string? LabelOf(object item) => item switch
    {
        string text => text,
        ComboBoxItem box => box.Content as string,
        _ => null,
    };

    /// <summary>
    /// One of a dialog's amounts, found by the label its row carries rather than by its place in the panel: a
    /// row a look hides is still in the tree, so the last slider built is not the last one a hand can reach.
    /// </summary>
    private static Slider AmountIn(Window dialog, string label)
    {
        var row = dialog.GetVisualDescendants().OfType<StackPanel>().FirstOrDefault(candidate =>
            candidate.Children.OfType<TextBlock>().Any(text => text.Text == L10n.T(label))
            && candidate.Children.OfType<Slider>().Any());
        return row?.Children.OfType<Slider>().FirstOrDefault()
            ?? throw new InvalidOperationException($"the dialog has no amount called {label}");
    }

    /// <summary>The button a dialog labels clicked, found by what it says rather than by where it is.</summary>
    private static void PressIn(Window dialog, string label)
    {
        var button = dialog.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(candidate => (candidate.Content as string) == L10n.T(label))
            ?? throw new InvalidOperationException($"the dialog has no button called {label}");
        Press(dialog, At(dialog, button, 0.5));
    }

    private static void Press(Window dialog, Point at)
    {
        dialog.MouseDown(at, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        dialog.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
    }

    /// <summary>
    /// A dialog scrolled to the bottom, so what is below the fold of its own scroll view can be clicked: a
    /// click outside the viewport is a click on the dialog's edge and reaches nothing.
    /// </summary>
    private static void ScrollToEnd(Window dialog)
    {
        if (dialog.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroller)
        {
            scroller.ScrollToEnd();
            dialog.UpdateLayout();
        }
    }

    internal string ToolsSelfCheck(string project)
    {
        var report = new List<string>();
        Open(project);
        if (_document is not { } document) throw new InvalidOperationException("the project did not open");
        report.Add($"open {System.IO.Path.GetFileName(project)}: {document.Width}x{document.Height}");

        var marks = new List<string>();
        var bars = new List<string>();
        foreach (var tool in Enum.GetValues<Tool>())
        {
            SetTool(tool);
            if (_rail.Marked != tool) throw new InvalidOperationException($"the rail did not mark {tool}");
            // One row lit in the Tools menu, and it is the same tool: the two are built from one list.
            var lit = _toolItems.Where(entry => entry.Value.IsChecked).Select(entry => entry.Key).ToList();
            if (lit.Count != 1 || lit[0] != tool)
            {
                throw new InvalidOperationException(
                    $"the Tools menu marked {string.Join(", ", lit)} rather than {tool}");
            }
            if (_message.Length == 0) throw new InvalidOperationException($"{tool} left the status line empty");
            // The options bar shows the rows this tool can be told about, and nothing else.
            if (_optionsBar.Shows("brush") != (tool is Tool.Brush or Tool.Clone or Tool.Blur or Tool.Liquify
                or Tool.Smudge or Tool.Heal))
            {
                throw new InvalidOperationException($"{tool} got the brush rows wrong");
            }
            if (_optionsBar.Shows("crop") != (tool == Tool.Crop))
            {
                throw new InvalidOperationException($"{tool} got the crop rows wrong");
            }
            marks.Add($"  {tool}: {_message}");
            bars.Add($"  {tool}: {string.Join(", ", _optionsBar.Showing)}");
        }
        report.Add($"{marks.Count} tools picked; the rail and the Tools menu marked the same one each time");
        report.AddRange(marks);
        report.Add("the options bar's rows, per tool:");
        report.AddRange(bars);
        report.Add($"the picture: {_statusInfo.Text}");

        // The swatches show the brush's colour and the background's, and swap and reset move both.
        var before = _rail.Palette;
        SwapColours();
        var after = _rail.Palette;
        if (after.Foreground != before.Background || after.Background != before.Foreground)
        {
            throw new InvalidOperationException("the swap did not carry both colors across");
        }
        if (BrushColour() != after.Foreground) throw new InvalidOperationException("the swap left the brush behind");
        ResetColours();
        if (BrushColour() != new SKColor(0, 0, 0) || BackgroundColour() != new SKColor(255, 255, 255))
        {
            throw new InvalidOperationException("the reset did not put the colors back to black and white");
        }
        report.Add($"colors: {Spell(before.Foreground)}/{Spell(before.Background)} swapped to " +
            $"{Spell(after.Foreground)}/{Spell(after.Background)}, and reset to black over white");

        // The blend list is offered in groups with a rule between them, so an item is not the mode at its
        // index: what each item stands for is what has to reach the layer, rules and all.
        var visible = document.EffectiveVisibleIDs();
        var blendLayer = document.Layers.FirstOrDefault(layer =>
            !layer.IsGroup && layer.Asset is not null && visible.Contains(layer.ID))
            ?? throw new InvalidOperationException("the fixture has no visible image layer to blend");
        Reselect(blendLayer.ID);
        if (!_blend.IsEnabled) throw new InvalidOperationException("the image layer's blend list is disabled");
        var rules = _blendRows.Count(row => row is null);
        if (rules != LayerEdits.BlendGroups.Length - 1)
        {
            throw new InvalidOperationException(
                $"the blend list drew {rules} rules for {LayerEdits.BlendGroups.Length} groups of modes");
        }
        if (Selected is not { } blended || _document is not { } blending)
        {
            throw new InvalidOperationException("nothing is selected to blend");
        }
        // Hard Mix sits past two rules, so an off-by-one in the mapping would land on a mode that is not it.
        var wanted = LayerBlendMode.HardMix;
        var item = _blendRows.IndexOf(wanted);
        if (item < 0) throw new InvalidOperationException($"{wanted} is not in the blend list");
        _blend.SelectedIndex = item;
        var chosen = blending.Layers.First(layer => layer.ID == blended).BlendMode;
        if (chosen != wanted)
        {
            throw new InvalidOperationException($"picking {Spell(wanted)} at item {item} set the layer to {Spell(chosen)}");
        }
        _blend.SelectedIndex = _blendRows.IndexOf(LayerBlendMode.Normal);
        if (blending.Layers.First(layer => layer.ID == blended).BlendMode != LayerBlendMode.Normal)
        {
            throw new InvalidOperationException("the blend list did not go back to Normal");
        }
        report.Add($"blend list: {_blendRows.Count} items with {rules} rules between the {LayerEdits.BlendGroups.Length} groups; "
            + $"{Spell(wanted)} picked and set, then back to Normal");

        // The toolbar's zoom controls are the View menu's own commands, so the status line has to follow them.
        var start = _canvas.Zoom;
        _canvas.ZoomBy(1.25);
        if (Math.Abs(_canvas.Zoom - start * 1.25) > 1e-9) throw new InvalidOperationException("zoom in did not zoom in");
        _canvas.ActualSize();
        if (Math.Abs(_canvas.Zoom - 1) > 1e-9) throw new InvalidOperationException("actual pixels is not one to one");
        Say();
        report.Add($"toolbar: {start * 100:0}% in a step to {start * 1.25 * 100:0}%, then actual pixels, " +
            $"with the status line at {_statusInfo.Text}");

        // Left with a tool from the middle of the rail, so the drawing shows one marked.
        SetTool(Tool.Brush);
        return string.Join(Environment.NewLine, report);
    }

    /// <summary>A colour as the status line names one.</summary>
    private static string Spell(SKColor colour) => $"{colour.Red},{colour.Green},{colour.Blue}";

    /// <summary>
    /// The tab the next project goes into: the empty one when the tab in front holds nothing, and a new one
    /// otherwise. It is put in front, and the caller fills it in.
    /// </summary>
    private Tab TabForNew()
    {
        FinishActiveEdits();
        if (_open.Document is not null)
        {
            var made = new Tab();
            _tabs.Insert(_tabs.IndexOf(_open) + 1, made);
            _open = made;
        }
        return _open;
    }

    /// <summary>
    /// The tab brought in front: the canvas and the panel are given what it holds, and its folder is watched
    /// again. Whatever was half-done in the tab being left — a preview, a draft outline, a drag, a typing
    /// session — belongs to that tab. Text is committed before the next document comes in front.
    /// </summary>
    private void Bring(Tab tab)
    {
        if (!ReferenceEquals(tab, _open))
        {
            FinishActiveEdits();
            _open.SelectedRow = _layers.SelectedIndex;
            _canvas.CancelDraft();
            _transforming = null;
            _transformBox = null;
            _transformOriginals.Clear();
            _transformMaskOriginals.Clear();
            _cropFrame = null;
            _open = tab;
        }
        Show(tab);
    }

    /// <summary>The tab laid out: its document on the canvas, its layers in the panel, its folder watched.</summary>
    private void Show(Tab tab)
    {
        // The Camera Raw panel belongs to the tab it was opened on, so a tab coming in front lets it go: every
        // way the front tab comes to change runs through here.
        CloseColorRange();
        CloseCameraRaw();
        _canvas.Document = tab.Document;
        if (tab.Document is not null)
        {
            ShowLayers(tab.Document);
            _layers.SelectedIndex = tab.SelectedRow >= 0 && tab.SelectedRow < _rows.Count
                ? tab.SelectedRow
                : _rows.Count > 0 ? 0 : -1;
        }
        else
        {
            _rows.Clear();
            _layers.ItemsSource = new List<ListBoxItem>();
            _layers.SelectedIndex = -1;
        }
        WatchProject();
        ShowTransformBox();
        ShowCropBox();
        ShowTextCaret();
        RefreshTabs();
        Refresh();
    }

    /// <summary>
    /// A tab closed: it is asked about first when it holds work that was not saved, the document it owns is
    /// let go, and the tab beside it comes in front. The last tab is not closed — an empty one takes its place,
    /// so there is always somewhere for the next project to go.
    /// </summary>
    private async Task CloseTab(Tab tab)
    {
        if (_windowClosePending || !_tabClosePending.Add(tab)) return;
        try
        {
            if (ReferenceEquals(tab, _open)) FinishActiveEdits();
            if (!await MayReplace(tab)) return;
            var at = _tabs.IndexOf(tab);
            if (at < 0) return;
            var wasOpen = ReferenceEquals(tab, _open);
            _tabs.RemoveAt(at);
            if (_tabs.Count == 0) _tabs.Add(new Tab());
            tab.Document?.Dispose();
            var next = _tabs[Math.Min(at, _tabs.Count - 1)];
            if (wasOpen)
            {
                // Nothing is kept from a tab that has just been closed.
                _open = next;
                Show(next);
            }
            else
            {
                RefreshTabs();
            }
            Say(L10n.F("{0} closed", tab.Name));
        }
        catch (Exception error)
        {
            AppDiagnostics.Report("Closing project tab", error);
            Say(L10n.F("Could not close the project: {0}", OperationErrors.Describe(error)));
        }
        finally
        {
            _tabClosePending.Remove(tab);
        }
    }

    /// <summary>The tab strip: a button a tab, the one in front marked, and a way to start another.</summary>
    private void RefreshTabs()
    {
        _tabStrip.Children.Clear();
        foreach (var tab in _tabs)
        {
            // The buttons carry the tab, so they are left unpainted and the capsule behind them shows.
            var name = new Button { Content = tab.Name, Tag = tab, Background = Brushes.Transparent };
            name.Click += (_, _) => Bring(tab);
            var close = new Button
            {
                Content = "×", Padding = new Thickness(4, 0, 4, 0), Tag = tab, Background = Brushes.Transparent,
            };
            close.Click += (_, _) => _ = CloseTab(tab);
            // A tab is a capsule, as the Mac draws one: the one in front the brighter of the two.
            var front = ReferenceEquals(tab, _open);
            _tabStrip.Children.Add(new Border
            {
                Background = front ? Skin.TabFront : Skin.TabBack,
                BorderBrush = front ? Skin.TabFrontEdge : Skin.TabBackEdge,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(8, 0, 2, 0),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 2,
                    Children = { name, close },
                },
            });
        }
        var add = new Button { Content = "+", Padding = new Thickness(8, 0, 8, 0) };
        add.Click += (_, _) => _ = NewProject();
        _tabStrip.Children.Add(add);
    }

    /// <summary>
    /// File ▸ New: a blank canvas of the size asked for, with one empty layer over it. What was open is closed
    /// with it — a document that has been changed and not saved asks first, as a window would.
    /// </summary>
    private async Task NewProject()
    {
        if (await NewDocumentDialog.Ask(this) is not { } asked) return;
        var made = LayerPlacement.NewDocument(asked.Width, asked.Height, asked.Resolution);
        if (made is null)
        {
            Say("That size is too large for a canvas");
            return;
        }
        _open = TabForNew();
        _document = made;
        _projectPath = null;
        _history.Reset();
        Show(_open);
        if (_document.Layers.Count > 0) Reselect(_document.Layers[^1].ID);
        Say(L10n.F("New {0} by {1} canvas at {2:0.##} per inch, not saved yet", _document.Width, _document.Height, _document.Resolution));
    }

    /// <summary>
    /// Whether the document that is open may be thrown away for something else. It may when it holds nothing
    /// that was not saved; otherwise the person is asked, and only a yes lets it go.
    /// </summary>
    private async Task<bool> MayReplace(Tab? tab = null)
    {
        var which = tab ?? _open;
        if (ReferenceEquals(which, _open)) FinishActiveEdits();
        while (_tabs.Contains(which) && NeedsCloseConfirmation(which))
        {
            var before = (which.Document, which.History.CurrentRevision, which.Path);
            var named = which.Path is { } path
                ? L10n.F("{0} has been changed since it was last saved.", System.IO.Path.GetFileName(path))
                : L10n.T("This project has not been saved.");
            if (!await ConfirmDialog.Ask(this, "Discard unsaved changes?",
                L10n.F("{0} Anything not saved is lost.", named), "Discard", "Keep")) return false;
            if (ReferenceEquals(which, _open)) FinishActiveEdits();
            if (before == (which.Document, which.History.CurrentRevision, which.Path)) return true;
        }
        return _tabs.Contains(which);
    }

    /// <summary>The layer the panel has selected, or the top one when nothing is: what an edit acts on.</summary>
    private Guid? Selected =>
        _layers.SelectedIndex >= 0 && _layers.SelectedIndex < _rows.Count ? _rows[_layers.SelectedIndex]
        : _rows.Count > 0 ? _rows[^1]
        : null;

    /// <summary>
    /// Every layer the panel has selected, which is more than one when several rows are: what the verbs that
    /// can act on several at once — flip, delete, merge, group and the transform box — work from.
    /// </summary>
    private List<Guid> SelectedLayers
    {
        get
        {
            var chosen = _layers.SelectedItems?.OfType<ListBoxItem>()
                .Select(item => item.Tag).OfType<Guid>().ToList() ?? [];
            if (chosen.Count > 0) return chosen;
            return Selected is { } one ? [one] : [];
        }
    }

    /// <summary>One edit, wrapped in the history so it undoes in a single step. False when it changed nothing.</summary>
    private bool Edit(string name, Func<bool> change)
    {
        if (!MayEditImage() || _document is not { } document) return false;
        _history.Begin(name, document, Selected);
        try { return change(); }
        finally
        {
            _history.End(document, Selected);
            Refresh();
        }
    }

    private void Undo()
    {
        if (!MayEditImage()) return;
        if (_document is not { } document || _history.Undo() is not { } snapshot || snapshot.Document is null) return;
        document.Adopt(snapshot.Document);
        Reselect(snapshot.ActiveLayerID);
    }

    private void Redo()
    {
        if (!MayEditImage()) return;
        if (_document is not { } document || _history.Redo() is not { } snapshot || snapshot.Document is null) return;
        document.Adopt(snapshot.Document);
        Reselect(snapshot.ActiveLayerID);
    }

    /// <summary>
    /// The Layer menu follows the panel: the merge row is named for what ⌘E would do, the clipping and mask
    /// rows for what they would change, and a row is off when it would do nothing.
    /// </summary>
    /// <summary>
    /// The two things a layer is combined with: how it blends and how much of it shows. Both act on the
    /// selected layer, and the slider takes effect when it is let go, so a drag is one undo step rather than
    /// one for every pixel of the drag.
    /// </summary>
    private Control Appearance()
    {
        // The list is grouped as the Mac's pop-up is, with a rule between the groups, so an item is not the
        // mode at its index: _blendRows says what each one is.
        _blendRows.Clear();
        _blendRows.AddRange(GroupedChoice.Fill(_blend, LayerEdits.BlendGroups, mode => Spell(mode)));
        _blend.Width = 150;
        _blend.SelectionChanged += (_, _) =>
        {
            if (_showingAppearance) return;
            var index = _blend.SelectedIndex;
            if (index < 0 || index >= _blendRows.Count || _blendRows[index] is not { } mode) return;
            if (_document is not { } document || Selected is not { } id) return;
            Edit("Blend Mode", () => LayerEdits.SetBlendMode(document, id, mode));
        };

        _opacity.PropertyChanged += (_, change) =>
        {
            if (change.Property != Slider.ValueProperty) return;
            _opacityReadout.Text = $"{_opacity.Value:0}%";
            if (_showingAppearance) return;
            if (_opacityDragging)
            {
                // The history was begun when the drag started: this only moves the layer under it.
                ApplyOpacity();
                return;
            }
            Edit("Opacity", ApplyOpacity);
        };
        _opacity.PointerPressed += (_, args) =>
        {
            if (_opacityDragging || !args.GetCurrentPoint(_opacity).Properties.IsLeftButtonPressed
                || !MayEditImage() || _document is not { } document || Selected is null) return;
            _opacityDragging = true;
            _history.Begin("Opacity", document, Selected);
        };
        _opacity.PointerReleased += (_, _) => FinishOpacityEdit();
        _opacity.PointerCaptureLost += (_, _) => FinishOpacityEdit();

        return new StackPanel
        {
            Margin = new Thickness(10, 0, 10, 8),
            Spacing = 4,
            Children =
            {
                _blend,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = L10n.T("Opacity"), Width = 52, VerticalAlignment = VerticalAlignment.Center },
                        _opacity,
                        _opacityReadout,
                    },
                },
            },
        };
    }

    /// <summary>Puts the slider's value on the selected layer, as one edit's worth of change.</summary>
    private bool ApplyOpacity()
    {
        if (_document is not { } document || Selected is not { } id) return false;
        return LayerEdits.SetOpacity(document, id, Math.Clamp(_opacity.Value / 100.0, 0, 1));
    }

    /// <summary>Shows what the selected layer is set to, without that being taken for a change of its own.</summary>
    private void ShowAppearance(ImageLayer? layer)
    {
        _showingAppearance = true;
        try
        {
            _blend.SelectedIndex = layer is null ? -1 : _blendRows.IndexOf(layer.BlendMode);
            _opacity.Value = (layer?.Opacity ?? 1) * 100;
            _opacityReadout.Text = $"{_opacity.Value:0}%";
            _blend.IsEnabled = layer is { IsGroup: false };
            _opacity.IsEnabled = layer is not null;
        }
        finally
        {
            _showingAppearance = false;
        }
    }

    private void UpdateLayerMenu()
    {
        var document = _document;
        var layer = document is not null && Selected is { } id
            ? document.Layers.FirstOrDefault(candidate => candidate.ID == id)
            : null;
        foreach (var item in _layerItems) item.IsEnabled = layer is not null;
        foreach (var item in _documentItems)
            item.IsEnabled = document is not null && document.Layers.Count < LayerPlacement.MaxLayers;
        foreach (var (item, ready) in _layerRows)
        {
            item.IsEnabled = document is not null && layer is not null && ready(document, layer);
        }

        var plan = document is not null && layer is not null ? LayerMerge.Plan(document, SelectedLayers, layer.ID) : null;
        _merge.Header = L10n.T(plan?.Action ?? "Merge Down");
        _merge.IsEnabled = plan is not null;
        _visibility.Header = L10n.T(layer?.IsVisible == false ? "_Show Layer" : "_Hide Layer");
        _visibility.IsEnabled = layer is not null;
        ShowAppearance(layer);
        _clipping.Header = L10n.T(layer?.MaskSourceID is not null ? "Release _Clipping Mask" : "Create _Clipping Mask");
        _clipping.IsEnabled = document is not null && layer is not null && LayerMaskEdits.CanToggle(document, layer.ID);
        _addMask.IsEnabled = layer is { Mask: null };
        _maskToggle.Header = L10n.T(layer?.Mask?.IsEnabled == false ? "_Enable Mask" : "_Disable Mask");
        _maskToggle.IsEnabled = layer?.Mask is not null;
        _maskLink.Header = L10n.T(layer?.Mask?.IsLinked == false ? "Li_nk Mask" : "Un_ink Mask");
        _maskLink.IsEnabled = layer is { IsGroup: false, Mask: not null };
    }

    /// <summary>Rebuilds the panel and puts the selection back on a given layer.</summary>
    private void Reselect(Guid? layer)
    {
        if (_document is not { } document) return;
        ShowLayers(document);
        var row = layer is { } id ? _rows.IndexOf(id) : -1;
        if (row >= 0) _layers.SelectedIndex = row;
        Refresh();
    }

    /// <summary>Inserts a copy of the selected layer just above it, and selects the copy.</summary>
    private void DuplicateLayer()
    {
        if (!MayEditImage()) return;
        if (_document is not { } document || Selected is not { } id) return;
        _history.Begin("Duplicate Layer", document, id);
        var copy = LayerEdits.Duplicate(document, id);
        _history.End(document, copy ?? id);
        if (copy is null)
        {
            Say("That layer could not be copied: there is no room for another 10,000 layers.");
            return;
        }
        Reselect(copy);
    }

    /// <summary>
    /// Deletes the selected layer, and anything a folder holds. Refused when a layer that stays is clipped
    /// to it, because the Mac build asks whether to bake or unlink and this build cannot ask yet.
    /// </summary>
    private void DeleteLayer()
    {
        if (!MayEditImage()) return;
        if (_document is not { } document) return;
        var ids = SelectedLayers;
        if (ids.Count == 0) return;
        // What to select afterwards: whatever takes the place of the one an edit acts on, as the Mac build
        // does. Several go as one step, each with its contents.
        var anchor = Selected ?? ids[0];
        var index = document.Layers.FindIndex(layer => layer.ID == anchor);
        _history.Begin(ids.Count > 1 ? "Delete Layers" : "Delete Layer", document, anchor);
        var deleted = LayerEdits.Delete(document, ids);
        var left = document.Layers;
        var next = deleted ? left.Count == 0 ? (Guid?)null : left[Math.Clamp(index, 0, left.Count - 1)].ID : anchor;
        _history.End(document, next);
        if (!deleted)
        {
            Say("A layer that stayed is clipped to one that went; macOS offers to bake or unlink it and this build cannot yet");
            return;
        }
        Reselect(next);
    }

    private async Task RenameLayer()
    {
        if (!MayEditImage()) return;
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { } layer) return;
        if (await TextPrompt.Ask(this, "Rename Layer", "Layer name", layer.Name) is not { } name) return;
        if (_document is not { } current) return;
        _history.Begin("Rename Layer", current, id);
        var renamed = LayerEdits.Rename(current, id, name);
        _history.End(current, id);
        if (!renamed)
        {
            Say("That name was blank, or was already the layer's name");
            return;
        }
        Reselect(id);
    }

    /// <summary>Moves the selected layer one place through the layers it shares a folder with.</summary>
    private void MoveLayer(int offset)
    {
        if (_document is not { } document || Selected is not { } id) return;
        Edit(offset > 0 ? "Move Layer Up" : "Move Layer Down", () => LayerEdits.MoveBy(document, id, offset));
        Reselect(id);
    }

    private void ToggleVisibility()
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { } layer) return;
        var visible = !layer.IsVisible;
        Edit(visible ? "Show Layer" : "Hide Layer", () => LayerEdits.SetVisible(document, id, visible));
        Reselect(id);
    }

    /// <summary>Adds a layer with no pixels, which it gets on the first paint.</summary>
    private void NewBlankLayer()
    {
        if (!MayEditImage()) return;
        if (_document is not { } document) return;
        _history.Begin("New Blank Layer", document, Selected);
        var made = LayerPlacement.AddBlank(document, Selected);
        _history.End(document, made ?? Selected);
        if (made is null) { Say("This document already holds as many layers as it may."); return; }
        Reselect(made);
    }

    private void NewFolder()
    {
        if (!MayEditImage()) return;
        if (_document is not { } document) return;
        _history.Begin("New Folder", document, Selected);
        var made = LayerPlacement.AddFolder(document, Selected);
        _history.End(document, made ?? Selected);
        if (made is null) { Say("This document already holds as many layers as it may."); return; }
        Reselect(made);
    }

    /// <summary>Wraps the selected layer in a folder.</summary>
    private void GroupSelected()
    {
        if (!MayEditImage()) return;
        if (_document is not { } document) return;
        var ids = SelectedLayers;
        if (ids.Count == 0) return;
        _history.Begin("Group Layers", document, Selected);
        var folder = LayerPlacement.GroupSelected(document, ids);
        _history.End(document, folder ?? Selected);
        if (folder is null) { Say("Those layers could not be wrapped in a folder."); return; }
        Reselect(folder);
    }

    private void MoveOutOfFolder()
    {
        if (_document is not { } document || Selected is not { } id) return;
        Edit("Move Layer", () => LayerPlacement.MoveOutOfFolder(document, id));
        Reselect(id);
    }

    /// <summary>Clips the selected layer to the one beneath it, or releases the clip it holds.</summary>
    private void ToggleClipping()
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { } layer) return;
        Edit(layer.MaskSourceID is not null ? "Release Clipping Mask" : "Create Clipping Mask",
            () => LayerMaskEdits.Toggle(document, id));
        Reselect(id);
    }

    private void AddMask(bool revealing)
    {
        if (_document is not { } document || Selected is not { } id) return;
        Edit(revealing ? "Add Reveal-All Mask" : "Add Hide-All Mask", () => LayerMaskEdits.Add(document, id, revealing));
        Reselect(id);
    }

    private void ToggleMask()
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id)?.Mask is not { } mask) return;
        var enabled = !mask.IsEnabled;
        Edit(enabled ? "Enable Layer Mask" : "Disable Layer Mask", () => LayerMaskEdits.SetEnabled(document, id, enabled));
        Reselect(id);
    }

    private void DeleteMask()
    {
        if (_document is not { } document || Selected is not { } id) return;
        Edit("Delete Layer Mask", () => LayerMaskEdits.Remove(document, id));
        Reselect(id);
    }

    private void ToggleMaskLink()
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id)?.Mask is not { } mask) return;
        var linked = !mask.IsLinked;
        Edit(linked ? "Link Layer Mask" : "Unlink Layer Mask", () => LayerMaskEdits.SetLinked(document, id, linked));
        Reselect(id);
    }

    /// <summary>
    /// ⌘E: the selected layer and what it merges with become one layer, composited the way the canvas shows
    /// them, as one undo step. The result is left selected, so pressing it again carries on down the stack.
    /// </summary>
    private void MergeLayers()
    {
        if (!MayEditImage()) return;
        if (_document is not { } document) return;
        var ids = SelectedLayers;
        if (ids.Count == 0 || Selected is not { } id) return;
        if (LayerMerge.Plan(document, ids, id) is not { } plan) return;
        _history.Begin(plan.Action, document, id);
        var made = LayerMerge.Merge(document, ids, id);
        _history.End(document, made ?? id);
        if (made is not { } merged)
        {
            Say("Those layers cannot be merged while preserving their appearance and hierarchy.");
            return;
        }
        Reselect(merged);
    }

    private void Flip(bool horizontal, bool canvas)
    {
        if (_document is not { } document) return;
        var ids = SelectedLayers;
        if (ids.Count == 0) return;
        Edit(canvas ? $"Flip Canvas {(horizontal ? "Horizontal" : "Vertical")}" : $"Flip {(horizontal ? "Horizontal" : "Vertical")}",
            () =>
            {
                if (canvas)
                {
                    LayerEdits.FlipCanvas(document, horizontal);
                    return true;
                }
                return LayerEdits.Flip(document, ids, horizontal);
            });
    }

    private MenuItem ToolItem(string header, Tool tool, string? key = null)
    {
        var item = new MenuItem
        {
            Header = L10n.T(header),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = tool == Tool.Pan,
        };
        item.Click += (_, _) => SetTool(tool);
        _toolItems[tool] = item;
        ShowKey(item, key, Shortcuts.Canvas);
        return item;
    }

    private void SetTool(Tool tool)
    {
        if (_tool != tool)
        {
            InvalidatePendingClipboardPaste();
            _canvas.FinishInteraction();
        }
        _tool = tool;
        _canvas.SampleSourceOnClick = tool == Tool.Clone;
        // A colour range being picked takes the press whatever tool is in hand, so the canvas keeps sampling
        // until its panel is put away.
        _canvas.EyedropperOnClick = tool == Tool.Eyedropper || _colorRange is not null;
        _canvas.TypeOnClick = tool == Tool.Type;
        if (tool != Tool.Type) CommitText();
        _canvas.CropEnabled = tool == Tool.Crop;
        _canvas.ShapeEnabled = tool == Tool.Shape;
        _canvas.GradientEnabled = tool == Tool.Gradient;
        // A crop frame belongs to the tool: leaving the tool lets go of it.
        if (tool != Tool.Crop) _cropFrame = null;
        // Picking the Crop tool with something selected starts its frame at the selection, as the Mac build's
        // does, rather than at the whole canvas — which is what makes a crop of a selection one gesture.
        if (tool == Tool.Crop && _cropFrame is null && _document is { } cropping
            && cropping.Selection.Path is { IsEmpty: false } selected)
        {
            var bounds = SKRectI.Round(selected.Bounds);
            var held = SKRectI.Intersect(bounds, SKRectI.Create(0, 0, cropping.Width, cropping.Height));
            if (CropEdits.Valid(held)) _cropFrame = held;
        }
        ShowCropBox();
        _canvas.TransformEnabled = tool == Tool.Move;
        _canvas.ShapePreviewFor = tool == Tool.Shape ? dragged => ShapePlan(dragged) : null;
        _canvas.GuidesDraggable = tool == Tool.Move;
        ShowTransformBox();
        _canvas.PaintEnabled = tool is Tool.Brush or Tool.Clone or Tool.Blur or Tool.Liquify or Tool.Smudge or Tool.Heal;
        PushBrush();
        _canvas.Selection = tool switch
        {
            Tool.Marquee => SelectionTool.Rectangle,
            Tool.Ellipse => SelectionTool.Ellipse,
            Tool.Lasso => SelectionTool.Lasso,
            Tool.Polygon => SelectionTool.Polygon,
            Tool.Wand => SelectionTool.Wand,
            _ => SelectionTool.None,
        };
        // An outline that is half drawn is let go when the tool changes, rather than left hanging.
        _canvas.CancelDraft();
        foreach (var (which, item) in _toolItems) item.IsChecked = which == tool;
        _rail.Mark(tool);
        RefreshOptionsBar();
        Say(tool switch
        {
            Tool.Brush => L10n.F("Brush: {0:0} pixels, {1} — drag on the canvas", _options.Brush.Diameter, Spell(_options.Brush)),
            Tool.Clone => _cloneSource is null
                ? "Clone stamp — Alt-click where it should copy from first"
                : L10n.F("Clone stamp copying from {0:0},{1:0} — drag on the canvas", _cloneSource.Value.X, _cloneSource.Value.Y),
            Tool.Blur => L10n.F("Blur brush: {0:0} pixels — drag over what should soften", _options.Brush.Diameter),
            Tool.Liquify => L10n.F("Liquify brush: {0:0} pixels — drag the pixels where they should go", _options.Brush.Diameter),
            Tool.Smudge => L10n.F("Smudge brush: {0:0} pixels — drag the color along", _options.Brush.Diameter),
            Tool.Heal => L10n.F("Spot healing ({0}): {1:0} pixels — drag over what should go", L10n.T(_options.Brush.Healing.ToString()), _options.Brush.Diameter),
            Tool.Eyedropper => "Eyedropper — click the canvas to take its color",
            Tool.Type => "Type — click where the text goes, then type it",
            Tool.Crop => "Crop — drag a frame, Alt to grow it from the middle, then Crop ▸ Apply",
            Tool.Shape => L10n.F("Shape ({0}) — drag it out; Shift squares it, Alt grows it from the middle", L10n.T(_options.Shape.ToString())),
            Tool.Gradient => L10n.F("Gradient ({0}, {1}) — drag the line it runs along", L10n.T(_options.Gradient.ToString()), L10n.T((_options.GradientToBackground ? "to the background colour" : "to nothing"))),
            Tool.Move => "Move — drag the layer, or a handle to scale and turn it",
            Tool.Marquee => "Marquee — drag a rectangle; Shift adds, Alt subtracts",
            Tool.Ellipse => "Elliptical marquee — drag an oval; Shift adds, Alt subtracts",
            Tool.Lasso => "Lasso — drag round a shape; Shift adds, Alt subtracts",
            Tool.Polygon => "Polygonal lasso — click each corner, double-click to close",
            Tool.Wand => "Magic wand — click a color to take everything like it",
            _ => "Pan — drag to scroll",
        });
    }

    /// <summary>What the brush is set to, in words, for the status line.</summary>
    private static string Spell(BrushSettings brush) =>
        L10n.F("hardness {0:0}%, opacity {1:0}%, color {2:0},{3:0},{4:0}",
            brush.Hardness * 100, brush.Opacity * 100, brush.Red * 255, brush.Green * 255, brush.Blue * 255);

    /// <summary>Asks for one of the magic wand's amounts, as the options bar's own buttons do.</summary>
    private async Task SetWand(WandSetting which)
    {
        if (which == WandSetting.Tolerance)
        {
            if (await Ask("Wand tolerance", "How far off the color still counts, 0 to 255",
                    $"{_options.Wand.Tolerance}", 0, 255) is not { } tolerance)
            {
                return;
            }
            _options.Wand = _options.Wand with { Tolerance = (int)Math.Round(tolerance) };
        }
        else
        {
            if (await Ask("Wand sample size", "How wide a patch is read around the click, 0 to 100",
                    $"{_options.Wand.Radius}", 0, 100) is not { } radius)
            {
                return;
            }
            _options.Wand = _options.Wand with { Radius = (int)Math.Round(radius) };
        }
        OptionsChanged();
        Say(L10n.F("Magic wand: tolerance {0}, sampling {1} pixels", _options.Wand.Tolerance, _options.Wand.Radius));
    }

    /// <summary>Asks for one of the Shape tool's amounts, as the options bar's own buttons do.</summary>
    private async Task SetShape(ShapeSetting which)
    {
        if (which == ShapeSetting.CornerRadius)
        {
            if (await Ask("Corner radius", "Pixels, 0 for square corners", $"{_options.ShapeCornerRadius:0}", 0, 1000)
                is { } radius)
            {
                _options.ShapeCornerRadius = radius;
            }
        }
        else
        {
            if (await Ask("Line width", "Pixels, 1 to 200", $"{_options.ShapeLineWidth:0}", 1, 200) is { } width)
            {
                _options.ShapeLineWidth = width;
            }
        }
        OptionsChanged();
        Say(L10n.F("Shape: {0}, radius {1:0}, width {2:0}", L10n.T(_options.Shape.ToString()), _options.ShapeCornerRadius, _options.ShapeLineWidth));
    }

    /// <summary>
    /// An option was changed at the bar. What a change from the Tools menu does is done here too — the canvas is
    /// given the brush, the menu rows that stand for the same setting are ticked, and the status line is
    /// refreshed — so the two faces on one setting can never disagree.
    /// </summary>
    private void OptionsChanged()
    {
        PushBrush();
        _eraseToggle.IsChecked = _options.Erase;
        _paintOnMask.IsChecked = _options.PaintOnMask;
        ShowColours();
        RefreshOptionsBar();
        Say();
    }

    /// <summary>
    /// The options bar told which tool is in hand and what the panel holds, so it shows that tool's rows. Called
    /// when the tool or the panel selection changes, never on a repaint: a row rebuilt under a drag would drop
    /// the drag.
    /// </summary>
    private void RefreshOptionsBar()
    {
        var layer = Selected is { } id && _document is { } document
            ? document.Layers.FirstOrDefault(one => one.ID == id)
            : null;
        _optionsBar.Show(_tool, _document is not null, _options.PaintOnMask && layer?.Mask is not null);
        _optionsBar.ShowZoom(_canvas.Zoom * 100);
    }

    /// <summary>Puts the current brush, with the mode the tool in hand calls for, on the canvas. The rail's
    /// foreground swatch is the same colour, so it is shown whenever the brush moves.</summary>
    private void PushBrush()
    {
        _canvas.Brush = _options.Brush with
        {
            Erasing = _options.Erase,
            Mode = _tool switch
            {
                Tool.Clone => BrushMode.Clone,
                Tool.Blur => BrushMode.Blur,
                Tool.Heal => BrushMode.Heal,
                _ => BrushMode.Paint,
            },
        };
        // The sample ring belongs to the same push: it is a view switch the bar sets, and it goes wherever the
        // brush does so a change from the bar reaches the canvas without a second path.
        _canvas.ShowsSampleRing = _options.ShowsSampleRing;
        ShowColours();
        RefreshOptionsBar();
    }

    /// <summary>The rail's two swatches as the brush and the background have them.</summary>
    private void ShowColours() => _rail.ShowColours(BrushColour(), BackgroundColour());

    /// <summary>Swaps the foreground and background colours, as the Mac's palette does with X.</summary>
    private void SwapColours()
    {
        var (red, green, blue) = _options.GradientBackground;
        _options.GradientBackground = (_options.Brush.Red, _options.Brush.Green, _options.Brush.Blue);
        _options.Brush = _options.Brush with { Red = red, Green = green, Blue = blue };
        PushBrush();
        Say("Swapped the foreground and background colors");
    }

    /// <summary>Puts the colours back to black and white, as the Mac's palette does with D.</summary>
    private void ResetColours()
    {
        _options.Brush = _options.Brush with { Red = 0, Green = 0, Blue = 0 };
        _options.GradientBackground = (1, 1, 1);
        PushBrush();
        Say("Foreground black and background white");
    }

    /// <summary>
    /// Opens the picker on one of the two colours, as clicking its swatch in the rail does. It is a window of
    /// its own rather than a dialog over the editor, because the canvas has to stay live — a click on the
    /// picture samples the colour under it, which is what the picker's own line says.
    /// </summary>
    private void ChooseColour(bool foreground) => OpenPicker(foreground, toBackground: false);

    /// <summary>
    /// The gradient's background colour, which is the colour of the rail's background swatch: choosing it from
    /// the Gradient menu also asks the gradient to run to it, which is what that menu row means.
    /// </summary>
    private void SetGradientBackground() => OpenPicker(foreground: false, toBackground: true);

    private void OpenPicker(bool foreground, bool toBackground)
    {
        if (_picker is { } already)
        {
            already.Activate();
            return;
        }
        var start = foreground
            ? (_options.Brush.Red, _options.Brush.Green, _options.Brush.Blue)
            : _options.GradientBackground;
        var picker = new ColorPickerDialog(
            foreground ? "Color Picker (Foreground Color)" : "Color Picker (Background Color)", start);
        picker.Applied += colour => TakeColour(foreground, toBackground, colour);
        picker.Cancelled += () =>
        {
            _picker = null;
            Say(L10n.F("The {0} color was left as it was", L10n.T((foreground ? "foreground" : "background"))));
        };
        _picker = picker;
        picker.Show(this);
        Say(L10n.F("Picking the {0} color — click the canvas to sample one", L10n.T((foreground ? "foreground" : "background"))));
    }

    /// <summary>Takes the colour the picker ended on: the brush's, or the background's and the gradient's.</summary>
    private void TakeColour(bool foreground, bool toBackground, (double Red, double Green, double Blue) colour)
    {
        _picker = null;
        if (foreground)
        {
            _options.Brush = _options.Brush with { Red = colour.Red, Green = colour.Green, Blue = colour.Blue };
        }
        else
        {
            _options.GradientBackground = colour;
            if (toBackground) _options.GradientToBackground = true;
        }
        PushBrush();
        Say(L10n.F("{0} color: {1:0},{2:0},{3:0}", L10n.T((foreground ? "Foreground" : "Background")), colour.Red * 255, colour.Green * 255, colour.Blue * 255));
    }

    /// <summary>The Gradient tool's options, as the Mac build's gradient bar has them.</summary>
    private void BuildGradientMenu()
    {
        foreach (var shape in Enum.GetValues<GradientShape>())
        {
            _gradientMenu.Items.Add(Command(L10n.T(shape.ToString()), () => SetGradient(shape, null, null)));
        }
        _gradientMenu.Items.Add(new Separator());
        _gradientMenu.Items.Add(Command("_To the background color", () => SetGradient(null, true, null)));
        _gradientMenu.Items.Add(Command("To _nothing", () => SetGradient(null, false, null)));
        _gradientMenu.Items.Add(new Separator());
        _gradientMenu.Items.Add(Command("_Reversed", () => SetGradient(null, null, !_options.GradientReversed)));
        _gradientMenu.Items.Add(Command("_Background color…", SetGradientBackground));
    }

    private void SetGradient(GradientShape? shape, bool? toBackground, bool? reversed)
    {
        if (shape is { } wanted) _options.Gradient = wanted;
        if (toBackground is { } fade) _options.GradientToBackground = fade;
        if (reversed is { } turn) _options.GradientReversed = turn;
        Say(L10n.F("Gradient: {0}, {1}, reversed: {2}, opacity as the brush's",
            L10n.T(_options.Gradient.ToString()),
            L10n.T(_options.GradientToBackground ? "to the background colour" : "to nothing"),
            L10n.T(_options.GradientReversed ? "on" : "off")));
        SetTool(_tool);
    }

    /// <summary>
    /// A gradient drag: the colour runs from one end to the other, over the selected layer's pixels or its
    /// mask, at the brush's opacity, as one undo step.
    /// </summary>
    /// <summary>The gradient's line has been taken hold of: the canvas starts showing what it would do.</summary>
    private void GradientStarted()
    {
        if (_document is not { } document || Selected is not { } id) return;
        StartPreview(document, id);
    }

    /// <summary>
    /// The line has moved: the gradient is filled into the preview so the run of it can be seen before the
    /// mouse comes up. Nothing is committed until it does.
    /// </summary>
    private void GradientChanged(SKPoint start, SKPoint end)
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (!GradientEdits.HasLine(start, end)) return;
        var (mask, from, to, opacity, shape) = GradientPlan(document, id);
        RequestPreview((target, layer) => GradientEdits.Fill(target, layer, mask, start, end, from, to, opacity, shape));
    }

    /// <summary>
    /// What the gradient tool would do with the brush as it stands: which of the layer's two surfaces it fills,
    /// between which colours, and in which shape. The same answer serves the drag's preview and the fill the
    /// drag ends up making.
    /// </summary>
    private (bool Mask, SKColor From, SKColor To, double Opacity, GradientShape Shape) GradientPlan(
        CanvasDocument document, Guid layerID)
    {
        var mask = _options.PaintOnMask && document.Layers.FirstOrDefault(layer => layer.ID == layerID)?.Mask is not null;
        var from = new SKColor(
            (byte)Math.Clamp(Math.Round(_options.Brush.Red * 255), 0, 255),
            (byte)Math.Clamp(Math.Round(_options.Brush.Green * 255), 0, 255),
            (byte)Math.Clamp(Math.Round(_options.Brush.Blue * 255), 0, 255));
        var to = _options.GradientToBackground
            ? new SKColor(
                (byte)Math.Clamp(Math.Round(_options.GradientBackground.Red * 255), 0, 255),
                (byte)Math.Clamp(Math.Round(_options.GradientBackground.Green * 255), 0, 255),
                (byte)Math.Clamp(Math.Round(_options.GradientBackground.Blue * 255), 0, 255))
            : new SKColor(from.Red, from.Green, from.Blue, 0);
        if (_options.GradientReversed) (from, to) = (to, from);
        return (mask, from, to, _options.Brush.Opacity, _options.Gradient);
    }

    /// <summary>The gradient's line has been let go: the fill is made, as one undo step.</summary>
    private void GradientFinished(SKPoint start, SKPoint end)
    {
        if (_document is not { } document || Selected is not { } id) return;
        StopPreview();
        if (!GradientEdits.HasLine(start, end))
        {
            Say("Drag the line the gradient should run along");
            return;
        }
        var (mask, from, to, opacity, shape) = GradientPlan(document, id);
        if (!Edit(mask ? "Gradient Mask" : "Gradient",
                () => GradientEdits.Fill(document, id, mask, start, end, from, to, opacity, shape)))
        {
            Say("Nothing to fill: that layer holds no pixels, or the selection does not reach it");
            return;
        }
        Reselect(id);
        Say(L10n.F("Gradient over {0:0} pixels", Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2))));
    }

    /// <summary>The shapes the Shape tool draws, and the two numbers that shape them.</summary>
    private void BuildShapeKinds()
    {
        foreach (var kind in Enum.GetValues<ShapeKind>())
        {
            var item = Command(L10n.T(kind.ToString()), () => SetShapeKind(kind));
            _shapeKinds.Items.Add(item);
            _shapeKindItems[kind] = item;
        }
        _shapeKinds.Items.Add(new Separator());
        _shapeKinds.Items.Add(Command("Corner _radius…", () => _ = SetShapeNumber(ShapeNumber.CornerRadius)));
        _shapeKinds.Items.Add(Command("_Line width…", () => _ = SetShapeNumber(ShapeNumber.LineWidth)));
        SetShapeKind(ShapeKind.Rectangle);
    }

    private readonly Dictionary<ShapeKind, MenuItem> _shapeKindItems = [];

    private void SetShapeKind(ShapeKind kind)
    {
        _options.Shape = kind;
        foreach (var (which, item) in _shapeKindItems) item.IsChecked = which == kind;
    }

    /// <summary>Asks for one of the two numbers that shape a shape.</summary>
    private async Task SetShapeNumber(ShapeNumber which)
    {
        var corner = which == ShapeNumber.CornerRadius;
        var current = corner ? _options.ShapeCornerRadius : _options.ShapeLineWidth;
        if (await Ask(corner ? "Corner radius" : "Line width", "Document pixels, 0 to 1000",
                $"{current:0.##}", 0, 1000) is not { } value)
        {
            return;
        }
        if (corner) _options.ShapeCornerRadius = value;
        else _options.ShapeLineWidth = Math.Max(1, value);
        Say(L10n.F("Shape: {0}, {1} {2:0.##} pixels", L10n.T(_options.Shape.ToString()), L10n.T((corner ? "corner radius" : "line width")), value));
    }

    private enum ShapeNumber
    {
        CornerRadius,
        LineWidth,
    }

    /// <summary>
    /// A finished shape drag: the box it made becomes a new layer of its own pixels, still knowing the shape
    /// it is, so a later size change draws it again rather than stretching it.
    /// </summary>
    private void ShapeFinished(SKPoint anchor, SKRectI box, SKPoint lineEnd)
    {
        if (_document is not { } document) return;
        var style = new LayerShapeStyle
        {
            Kind = _options.Shape,
            Red = _options.Brush.Red,
            Green = _options.Brush.Green,
            Blue = _options.Brush.Blue,
            CornerRadius = _options.ShapeCornerRadius,
        };
        var target = box;
        if (_options.Shape == ShapeKind.Line)
        {
            // The layer is the box around the line with room for the stroke's own thickness and its round ends.
            var half = (float)(_options.ShapeLineWidth / 2);
            target = CropEdits.Snapped(SKRect.Create(box.Left - half, box.Top - half,
                box.Width + half * 2, box.Height + half * 2));
            style.LineWidth = _options.ShapeLineWidth;
            style.Start = Unit(target, anchor);
            style.End = Unit(target, lineEnd);
        }
        if (ShapeEdits.TooLarge(target.Width, target.Height))
        {
            Say("That shape is too large to draw as one layer");
            return;
        }
        _history.Begin(_options.Shape.ToString(), document, Selected);
        var made = ShapeEdits.Add(document, style, target, Selected);
        _history.End(document, Selected);
        if (made is null)
        {
            Say("That shape could not be drawn");
            return;
        }
        Reselect(made);
        Say(L10n.F("{0}: {1}x{2} at {3},{4}", L10n.T(_options.Shape.ToString()), target.Width, target.Height, target.Left, target.Top));
    }

    /// <summary>Where a document point sits in a box, as a fraction of its sides.</summary>
    private static JsonPoint Unit(SKRectI box, SKPoint point) => new(
        box.Width > 0 ? (point.X - box.Left) / box.Width : 0.5,
        box.Height > 0 ? (point.Y - box.Top) / box.Height : 0.5);

    /// <summary>
    /// The Camera Raw filter: its panel is docked at the window's right edge and the layer is filtered into a
    /// copy of the document as the amounts move, so the canvas shows what the panel is doing while the document
    /// itself is not touched until Apply. It is a panel rather than a dialog because the canvas has to stay
    /// live — the Mac build draws upright guides and picks colours on that same canvas while this panel is up.
    /// </summary>
    private void CameraRawFilter()
    {
        if (!MayEditImage()) return;
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { Asset: not null, IsGroup: false })
        {
            Say("Camera Raw needs a layer with pixels of its own");
            return;
        }
        CloseCameraRaw();
        StartPreview(document, id);
        _cameraRawLayer = id;
        var panel = new CameraRawPanel(_cameraRawAmounts, BrushColour());
        // The preview carries the panel's overlay switches: clipped shadows and highlights and the sharpening
        // mask are shown over the grade while the amounts are moved, and the overlay is what is shown when one
        // is on. The edit that is finally made is the grade alone, never the overlay.
        panel.Preview = PreviewCameraRaw;
        panel.Applied += ApplyCameraRaw;
        panel.Cancelled += CloseCameraRaw;
        _cameraRaw = panel;
        _cameraRawHost.Child = panel.View;
        _cameraRawHost.IsVisible = true;
        _layersSide.IsVisible = false;
        // The readout under the scope follows the pointer over the canvas, and is cleared when it leaves.
        _canvas.PointerMovedAt = point => _cameraRaw?.ShowReadout(UnderCursor(point));
        _canvas.PointerLeftCanvas = () => _cameraRaw?.ShowReadout(null);
        // Guided upright asks for lines to be drawn on the picture, which is the canvas's business.
        panel.CanvasChanged += UprightCanvasChanged;
        _canvas.UprightDrawn = UprightDrawn;
        panel.Show();
        Say("Camera Raw: the panel is docked on the right and the canvas shows what it is doing");
    }

    /// <summary>
    /// Shows what the Camera Raw panel is asking for, over the layer it was opened on. The whole grade and the
    /// geometry are run, not just the grade, because the panel's scope is counted from the same pass — the
    /// picture the scope describes is the picture the canvas is given.
    /// </summary>
    private void PreviewCameraRaw(CameraRawSettings settings, bool shadows, bool highlights, bool mask)
    {
        if (_document is not { } document || _cameraRawLayer is not { } id) return;
        RequestPreview(document =>
        {
            var shown = CameraRawEdits.Preview(document, id, settings, shadows, highlights, mask, out var scope);
            _cameraRawScope = scope;
            return shown;
        });
    }

    /// <summary>
    /// The colour of the pixel under a document point in the picture the panel is showing — the preview's own
    /// copy of the layer, so what the readout names is what the pointer is over. Nothing when the pointer is
    /// off the picture.
    /// </summary>
    private (int Red, int Green, int Blue)? UnderCursor(SKPoint point)
    {
        if (_preview?.Document is not { } shown || _cameraRawLayer is not { } id) return null;
        return shown.Layers.FirstOrDefault(layer => layer.ID == id) is { } layer
            ? CameraRawSample.Under(layer, point)
            : null;
    }

    /// <summary>
    /// The Camera Raw panel's lines have changed — one drawn or cleared, or the panel has started or stopped
    /// asking for one: the canvas is armed for a drag, or let go, and is handed the lines to draw.
    /// </summary>
    private void UprightCanvasChanged()
    {
        if (_cameraRaw is not { } panel) return;
        _canvas.UprightDrawing = panel.DrawingGuides;
        ShowUprightGuides();
    }

    /// <summary>
    /// A line drawn on the picture. It is stored in the layer's own fractions, which is how the geometry reads
    /// it, and the picture is shown with what the line asks for. A line with an end off the layer is refused
    /// rather than stored, since a fraction outside the picture is not a place the geometry can read.
    /// </summary>
    private void UprightDrawn(SKPoint start, SKPoint end)
    {
        if (_cameraRaw is not { } panel || _cameraRawLayer is not { } id) return;
        if (_document is not { } document || document.Layers.FirstOrDefault(layer => layer.ID == id) is not { } layer)
        {
            return;
        }
        if (Fraction(layer, start) is not { } from || Fraction(layer, end) is not { } to)
        {
            Say("A guide has to be drawn inside the layer it straightens");
            return;
        }
        panel.AddGuide(new CameraRawGeometryGuide(from.X, from.Y, to.X, to.Y));
        Say(L10n.F("Upright: {0} line(s) drawn; the picture is turned by what they ask for", panel.Guides.Count));
    }

    /// <summary>The lines the panel has, in document pixels, for the canvas to draw over the picture.</summary>
    private void ShowUprightGuides()
    {
        if (_cameraRaw is not { } panel || _cameraRawLayer is not { } id
            || _document is not { } document || document.Layers.FirstOrDefault(layer => layer.ID == id) is not { } layer)
        {
            _canvas.UprightGuides = [];
            return;
        }
        var box = layer.Transform;
        SKPoint Placed(double x, double y) => box.Point(box.FlipX ? 1 - x : x, box.FlipY ? 1 - y : y);
        _canvas.UprightGuides = panel.Guides
            .Select(guide => (Placed(guide.StartX, guide.StartY), Placed(guide.EndX, guide.EndY)))
            .ToList();
    }

    /// <summary>Where a document point sits in a layer's own grid, as fractions of its sides, or null when the
    /// point is off the layer.</summary>
    private static (double X, double Y)? Fraction(ImageLayer layer, SKPoint point)
    {
        if (layer.Asset is null || !layer.Transform.IsValid) return null;
        if (layer.Transform.InBox(point) is not { } at) return null;
        return (at.X / layer.Transform.Width, at.Y / layer.Transform.Height);
    }

    /// <summary>Writes the panel's amounts into the layer it was opened on, as one undo step.</summary>
    private void ApplyCameraRaw(CameraRawSettings settings)
    {
        // Read before the panel is let go, since putting it away forgets which layer it was opened on.
        var id = _cameraRawLayer;
        CloseCameraRaw();
        // Kept for the next time the panel is opened, as the Mac's filter settings keep the last grade.
        _cameraRawAmounts = settings;
        if (_document is not { } current || id is not { } layer || settings.IsIdentity) return;
        Edit("Camera Raw Filter", () => CameraRawEdits.Apply(current, layer, settings));
        Reselect(layer);
        Say(L10n.F("Camera Raw: exposure {0:0.##}, contrast {1:0}, saturation {2:0}", settings.Exposure, settings.Contrast, settings.Saturation));
    }

    /// <summary>
    /// Puts the Camera Raw panel away and stops the preview it was driving. The document was never touched, so
    /// there is nothing to put back.
    /// </summary>
    private void CloseCameraRaw()
    {
        if (_cameraRaw is null) return;
        _cameraRaw = null;
        _cameraRawLayer = null;
        _cameraRawScope = null;
        _cameraRawHost.Child = null;
        _cameraRawHost.IsVisible = false;
        _layersSide.IsVisible = true;
        _canvas.PointerMovedAt = null;
        _canvas.PointerLeftCanvas = null;
        _canvas.UprightDrawing = false;
        _canvas.UprightDrawn = null;
        _canvas.UprightGuides = [];
        StopPreview();
    }

    /// <summary>
    /// One of the filters that are not Camera Raw. Its amounts are asked for, then it runs over the selected
    /// layer's own pixels, held to the selection, as one undo step.
    /// </summary>
    private async Task ApplyFilter(FilterKind kind)
    {
        if (!MayEditImage()) return;
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { Asset: not null, IsGroup: false })
        {
            Say(L10n.F("{0} needs a layer with pixels of its own", L10n.T(kind.ToString())));
            return;
        }
        StartPreview(document, id);
        var asked = await FilterDialog.Ask(this, kind, _filterAmounts, settings =>
        {
            if (settings is { } amounts)
            {
                // Zero is a valid request to show the original, not a failed filter whose last look should stay.
                if (amounts.IsValid(kind) && !amounts.DoesAnything(kind))
                {
                    HidePreview();
                    return;
                }
                RequestPreview((target, layer) => FilterEdits.Apply(target, layer, kind, amounts));
                return;
            }
            HidePreview();
        });
        StopPreview();
        if (asked is not { } settings) return;
        // The amounts this filter was used with are kept for the next time it is opened, as the Mac's one set
        // of filter settings does.
        _filterAmounts = settings;
        if (_document is not { } current) return;
        Edit($"{kind} Filter", () => FilterEdits.Apply(current, id, kind, settings));
        Reselect(id);
        Say(L10n.F("{0} applied", L10n.T(kind.ToString())));
    }

    /// <summary>
    /// Dither: its look and amounts are asked for, then the layer's own pixels are reduced to ink, held to the
    /// selection, as one undo step.
    /// </summary>
    private async Task DitherFilter()
    {
        if (!MayEditImage()) return;
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { Asset: not null, IsGroup: false })
        {
            Say("Dither needs a layer with pixels of its own");
            return;
        }
        StartPreview(document, id);
        var asked = await DitherDialog.Ask(this, _ditherLook, _ditherAmounts,
            (style, settings) => RequestPreview((target, layer) => DitherEdits.Apply(target, layer, style, settings)));
        StopPreview();
        if (asked is not { } chosen) return;
        // The look as well as the amounts: Dither opens again on the one it was last used with.
        _ditherLook = chosen.Style;
        _ditherAmounts = chosen.Settings;
        if (_document is not { } current) return;
        Edit("Dither", () => DitherEdits.Apply(current, id, chosen.Style, chosen.Settings));
        Reselect(id);
        Say(L10n.F("Dither: {0}, {1:0} tones", L10n.T(chosen.Style.ToString()), chosen.Settings.Levels));
    }

    /// <summary>
    /// Content-Aware Fill: the selected part of the layer is made up out of the pixels around it, as one undo
    /// step. It needs a selection, and something outside it to take the fill from.
    /// </summary>
    private void ContentAwareFill()
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Selection.Path is null)
        {
            Say("Content-Aware Fill needs a selection to fill");
            return;
        }
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { Asset: not null, IsGroup: false })
        {
            Say("Content-Aware Fill needs a layer with pixels of its own");
            return;
        }
        if (!Edit("Content-Aware Fill", () => ContentFillEdits.Apply(document, id)))
        {
            Say("Content-Aware Fill found nothing to fill from: make the selection smaller");
            return;
        }
        Reselect(id);
        Say("Content-Aware Fill applied");
    }

    /// <summary>
    /// The kinds of adjustment layer the Layer menu offers, and the settings verb beside it.
    /// </summary>
    private void BuildAdjustmentMenu()
    {
        foreach (var (label, kind) in new (string Label, AdjustmentKind Kind)[]
                 {
                     ("_Hue/Saturation", AdjustmentKind.HueSaturation),
                     ("_Levels", AdjustmentKind.Levels),
                     ("C_urves", AdjustmentKind.Curves),
                     ("_Exposure", AdjustmentKind.Exposure),
                     ("_Black & White", AdjustmentKind.BlackWhite),
                     ("_Gradient Map", AdjustmentKind.GradientMap),
                     ("_Grain", AdjustmentKind.Grain),
                     ("_Add Noise", AdjustmentKind.AddNoise),
                     ("_Gaussian Blur", AdjustmentKind.GaussianBlur),
                     ("_Motion Blur", AdjustmentKind.MotionBlur),
                     ("C_olor Balance", AdjustmentKind.ColorBalance),
                     ("_Invert", AdjustmentKind.Invert),
                 })
        {
            _adjustmentMenu.Items.Add(Command(label, () => _ = NewAdjustment(kind)));
        }
        _adjustmentSettings = LayerCommand("Adjustment _Settings…", () => _ = EditAdjustment(), null,
            (_, layer) => layer.Adjustment is not null);
        BuildEffectsMenu();
    }

    /// <summary>
    /// The effects a layer draws around itself, one row each plus a row that takes them all away. Each row
    /// opens the panel for that effect, ticked when the layer already has it.
    /// </summary>
    private void BuildEffectsMenu()
    {
        foreach (var kind in Enum.GetValues<EffectKind>())
        {
            var wanted = kind;
            _effectsMenu.Items.Add(LayerCommand(L10n.T(EffectDialog.TitleFor(kind)) + "…", () => _ = EditEffect(wanted), null,
                (_, layer) => layer.IsGroup == false));
        }
        _effectsMenu.Items.Add(new Separator());
        _clearEffects = LayerCommand("_Clear Effects", ClearEffects, null, (_, layer) => layer.Effects is not null);
        _effectsMenu.Items.Add(_clearEffects);
    }

    /// <summary>One effect's panel, with the layer's own effect as it starts out.</summary>
    private async Task EditEffect(EffectKind kind)
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { } layer) return;
        if (await EffectDialog.Ask(this, kind, layer.Effects) is not { } effects) return;
        if (_document is not { } current) return;
        Edit(EffectDialog.TitleFor(kind), () => LayerEdits.SetEffect(current, id, kind, effects));
        Reselect(id);
    }

    /// <summary>Every effect taken off the layer, as one undo step.</summary>
    private void ClearEffects()
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id)?.Effects is null)
        {
            Say("This layer has no effects");
            return;
        }
        Edit("Clear Effects", () => LayerEdits.SetEffects(document, id, null));
        Say("Effects cleared");
    }

    /// <summary>A new adjustment layer over the selected one, with its settings asked for straight away.</summary>
    private async Task NewAdjustment(AdjustmentKind kind)
    {
        if (!MayEditImage()) return;
        if (_document is not { } document) return;
        _history.Begin("New Adjustment Layer", document, Selected);
        var made = LayerPlacement.AddAdjustment(document, kind, Selected);
        _history.End(document, made ?? Selected);
        if (made is null)
        {
            Say("This document already holds as many layers as it may.");
            return;
        }
        Reselect(made);
        await EditAdjustment();
    }

    /// <summary>The selected adjustment layer's settings, changed and put back as one undo step.</summary>
    private async Task EditAdjustment()
    {
        if (!MayEditImage()) return;
        if (_document is not { } document || Selected is not { } id) return;
        if (LayerAdjustmentEdits.Settings(document, id) is not { } settings) return;
        StartPreview(document, id);
        var asked = await AdjustmentDialog.Ask(this, settings,
            changed => RequestPreview((target, layer) => LayerAdjustmentEdits.Set(target, layer, changed)));
        StopPreview();
        if (asked is not { } changed) return;
        if (_document is not { } current) return;
        Edit($"{LayerPlacement.Name(changed.Kind)} Adjustment", () => LayerAdjustmentEdits.Set(current, id, changed));
        Reselect(id);
        Say(L10n.F("{0} adjustment set", L10n.T(LayerPlacement.Name(changed.Kind))));
    }

    /// <summary>
    /// One of the colour adjustments from the Image menu: its amounts are asked for, then it runs over the
    /// selected layer's own pixels, held to the selection, as one undo step. The same settings can be left
    /// on an adjustment layer instead, from the Layer menu.
    /// </summary>
    private async Task ImageAdjustment(AdjustmentKind kind)
    {
        if (!MayEditImage()) return;
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { Asset: not null, IsGroup: false, Adjustment: null })
        {
            Say(L10n.F("{0} needs a layer with pixels of its own", L10n.T(LayerPlacement.Name(kind))));
            return;
        }
        StartPreview(document, id);
        var asked = await AdjustmentDialog.Ask(this, new LayerAdjustment { Kind = kind },
            settings => RequestPreview((target, layer) => FilterEdits.ApplyAdjustment(target, layer, settings)));
        StopPreview();
        if (asked is not { } settings) return;
        if (_document is not { } current) return;
        Edit(LayerPlacement.Name(kind), () => FilterEdits.ApplyAdjustment(current, id, settings));
        Reselect(id);
        Say(L10n.F("{0} applied", L10n.T(LayerPlacement.Name(kind))));
    }

    /// <summary>Edit ▸ Copy: the selected pixels of the active layer, held for a paste.</summary>
    private void Copy()
    {
        if (!MayEditImage()) return;
        if (_recoveryPending || _windowClosePending || _tabClosePending.Count > 0) return;
        if (_document is not { } document || Selected is not { } id) return;
        var copied = SelectionClipboard.Copy(document, id);
        if (copied is null)
        {
            Say("Select something on a layer with pixels of its own first");
            return;
        }
        Adopt(copied);
        Say(L10n.F("Copied {0} x {1}", copied.Region.Width, copied.Region.Height));
        PublishCopiedImage(copied);
    }

    /// <summary>Edit ▸ Copy Merged: the selected pixels of everything that is drawn.</summary>
    private void CopyMerged()
    {
        if (!MayEditImage()) return;
        if (_recoveryPending || _windowClosePending || _tabClosePending.Count > 0) return;
        if (_document is not { } document) return;
        var copied = SelectionClipboard.CopyMerged(document);
        if (copied is null)
        {
            Say("Select something to copy first");
            return;
        }
        Adopt(copied);
        Say(L10n.F("Copied {0} x {1} from the flattened picture", copied.Region.Width, copied.Region.Height));
        PublishCopiedImage(copied);
    }

    /// <summary>Edit ▸ Cut: the selected pixels taken off, and held for a paste.</summary>
    private void Cut()
    {
        if (!MayEditImage()) return;
        if (_recoveryPending || _windowClosePending || _tabClosePending.Count > 0) return;
        if (_document is not { } document || Selected is not { } id) return;
        ClipboardImage? copied;
        _history.Begin("Cut", document, Selected);
        try
        {
            if (!SelectionClipboard.Cut(document, id, out copied) || copied is null)
            {
                Say("Select something on a layer with pixels of its own first");
                return;
            }
        }
        finally
        {
            _history.End(document, Selected);
        }
        Adopt(copied);
        Reselect(id);
        Say(L10n.F("Cut {0} x {1}", copied.Region.Width, copied.Region.Height));
        PublishCopiedImage(copied, cut: true);
    }

    /// <summary>Edit ▸ Paste: the clipboard as a layer, where on the document it came from.</summary>
    private void Paste()
    {
        if (!MayEditImage()) return;
        if (UsesSystemImageClipboard) { _ = PasteFromSystemAsync(); return; }
        if (_document is not { } document) return;
        if (_clipboard is not { } clipboard)
        {
            Say("There is nothing to paste");
            return;
        }
        Guid? made = null;
        _history.Begin("Paste", document, Selected);
        try
        {
            made = SelectionClipboard.Paste(document, clipboard, Selected);
        }
        finally
        {
            _history.End(document, made ?? Selected);
        }
        if (made is null)
        {
            Say("This document already holds as many layers as it may.");
            return;
        }
        Reselect(made);
        Say(L10n.F("Pasted {0} x {1}", clipboard.Region.Width, clipboard.Region.Height));
    }

    /// <summary>Edit ▸ Layer via Copy: the selected pixels of the active layer as a layer of their own.</summary>
    private void LayerViaCopy()
    {
        if (!MayEditImage()) return;
        if (_document is not { } document || Selected is not { } id) return;
        Guid? made = null;
        _history.Begin("Layer via Copy", document, Selected);
        try
        {
            made = SelectionClipboard.LayerViaCopy(document, id, Selected);
        }
        finally
        {
            _history.End(document, made ?? Selected);
        }
        if (made is null)
        {
            Say("Select something on a layer with pixels of its own first");
            return;
        }
        Reselect(made);
        Say("Layer made from the selection");
    }

    /// <summary>The clipboard the window holds: one piece of the canvas at a time, as the Mac build keeps it.</summary>
    private void Adopt(ClipboardImage copied)
    {
        _clipboard?.Dispose();
        _clipboard = copied;
    }

    /// <summary>
    /// Image ▸ Image Size: the canvas and every layer's pixels resampled to a new size, as one undo step.
    /// </summary>
    private async Task ImageSize()
    {
        if (_document is not { } document) return;
        if (await ImageSizeDialog.Ask(this, document.Width, document.Height, document.Resolution,
                LayerSampling.HighQuality) is not { } asked)
        {
            return;
        }
        if (_document is not { } current) return;
        if (!Edit("Image Size", () => ImageEdits.Resize(current, asked.Width, asked.Height, asked.Resolution, asked.Sampling)))
        {
            Say("That size is too large to resample to.");
            return;
        }
        _canvas.Fit();
        Say(L10n.F("Image is now {0} x {1} at {2:0.##} per inch", asked.Width, asked.Height, asked.Resolution));
    }

    /// <summary>A distortion has been taken hold of: one undo step for the whole drag, as a slider drag gets.</summary>
    private void DistortStarted()
    {
        if (_document is not { } document || Selected is not { } id) return;
        _distortBox = _canvas.TransformBox;
        _distortLayers = SelectedLayers;
        _history.Begin(_distortLayers.Count > 1 ? "Distort Layers" : "Distort", document, Selected);
        if (_distortLayers.Count > 1) StartPreview(document, _distortLayers);
        else StartPreview(document, id);
    }

    /// <summary>
    /// A corner has moved: the layers are resampled into the shape the corners make in the preview, so the
    /// distortion can be seen while it is being made rather than only after it is let go. One layer's corners
    /// are the shape itself; several layers are each carried by the box's own perspective, so they keep the
    /// shape they had between them.
    /// </summary>
    private void DistortChanged(IReadOnlyList<SKPoint> corners)
    {
        if (_distortLayers is { Count: > 1 } ids && _distortBox is { } box)
        {
            RequestPreview(document => DistortEdits.Distort(document, ids, box, corners));
            return;
        }
        RequestPreview((target, layer) => DistortEdits.Distort(target, layer, corners));
    }

    /// <summary>
    /// The distortion has been let go: the layers' pixels are resampled into that shape, which is the one
    /// edit. The shape is only drawn while it is dragged — nothing is resampled until it is let go.
    /// </summary>
    private void DistortFinished(IReadOnlyList<SKPoint> corners)
    {
        if (_document is not { } document || Selected is not { } id) return;
        StopPreview();
        var ids = _distortLayers ?? [id];
        var box = _distortBox;
        _distortLayers = null;
        _distortBox = null;
        var distorted = ids.Count > 1 && box is { } group
            ? DistortEdits.Distort(document, ids, group, corners)
            : DistortEdits.Distort(document, id, corners);
        if (!distorted) Say("That shape cannot be made");
        _history.End(document, Selected);
        Reselect(id);
    }

    /// <summary>A guide has been taken hold of: one undo step for the whole drag, as a slider drag gets.</summary>
    private void GuideDragStarted()
    {
        if (_document is not { } document) return;
        _draggedGuide = null;
        _history.Begin("Move Guide", document, Selected);
    }

    /// <summary>The guide follows the pointer; the history step was begun when it was taken hold of.</summary>
    private void GuideMoved(Guid id, double position)
    {
        if (_document is not { } document) return;
        _draggedGuide = id;
        if (!GuideEdits.Move(document, id, position)) return;
        _canvas.InvalidateVisual();
        Say(L10n.F("Guide at {0:0.#}", position));
    }

    /// <summary>
    /// The drag has ended. A guide left off the canvas is taken away, as Photoshop takes it away — which is
    /// how a guide is got rid of without a menu.
    /// </summary>
    private void GuideDragFinished()
    {
        if (_document is not { } document) return;
        if (_draggedGuide is { } id
            && document.Guides.FirstOrDefault(guide => guide.ID == id && !GuideEdits.OnCanvas(document, guide)) is { } away)
        {
            GuideEdits.Remove(document, away.ID);
            Say("Guide taken away");
        }
        _draggedGuide = null;
        _history.End(document, Selected);
        Refresh();
    }

    /// <summary>
    /// A press on a ruler makes a guide there and the drag carries it, as the Mac build and Photoshop do: the
    /// ruler across the top makes a horizontal guide and the one down the side a vertical one, so the ruler's
    /// own axis is the guide's. The point arrives in the strip's coordinates and is read on the canvas, because
    /// here the rulers sit beside the canvas rather than over it.
    /// </summary>
    private void GuideGrabbed(RulerStrip ruler, GuideAxis axis, Point onRuler)
    {
        if (_document is not { } document) return;
        if (_guidesLocked)
        {
            Say("The guides are locked, so none can be pulled off the ruler");
            return;
        }
        var at = Where(axis, ruler, onRuler);
        _history.Begin("New Guide", document, Selected);
        _pulledGuide = GuideEdits.Add(document, axis, at);
        _draggedGuide = _pulledGuide;
        if (_pulledGuide is null)
        {
            _history.End(document, Selected);
            return;
        }
        _canvas.InvalidateVisual();
        Say(L10n.F("Guide at {0:0.#}", at));
    }

    /// <summary>The guide being pulled off a ruler follows the pointer; the history step began when it was made.</summary>
    private void GuidePulled(RulerStrip ruler, Point onRuler)
    {
        if (_document is not { } document || _pulledGuide is not { } id) return;
        var at = Where(ruler.Axis, ruler, onRuler);
        if (!GuideEdits.Move(document, id, at)) return;
        _canvas.InvalidateVisual();
        Say(L10n.F("Guide at {0:0.#}", at));
    }

    /// <summary>
    /// The pull has ended, and it ends the way a guide dragged on the canvas ends — so a guide let go off the
    /// canvas, or back onto the ruler it came from, is taken away however it was made.
    /// </summary>
    private void GuidePulledOff()
    {
        if (_pulledGuide is null) return;
        _pulledGuide = null;
        GuideDragFinished();
    }

    /// <summary>Where a point on a ruler is in document pixels. Which of the two it reads is the ruler's own axis,
    /// as the canvas reads one for a guide drag; the canvas's mapping does the rest.</summary>
    private double Where(GuideAxis axis, RulerStrip ruler, Point onRuler)
    {
        if (ruler.TranslatePoint(onRuler, _canvas) is not { } onCanvas) return 0;
        var document = _canvas.InDocument(onCanvas);
        return axis == GuideAxis.Vertical ? document.X : document.Y;
    }

    /// <summary>The three things a drag can line up with, as the View menu lists them.</summary>
    private (MenuItem Item, SnapTo Flag, string Label)[] SnapRows() =>
    [
        (_snapToCanvas, SnapTo.Canvas, "Snap to Canvas"),
        (_snapToGuides, SnapTo.Guides, "Snap to Guides"),
        (_snapToLayers, SnapTo.Layers, "Snap to Layers"),
        (_snapToGrid, SnapTo.Grid, "Snap to Grid"),
    ];

    /// <summary>View ▸ Snap to …: one kind of thing a drag lines up with, on or off.</summary>
    private void ToggleSnapTo(SnapTo flag, string label)
    {
        _snapTo = _snapTo.HasFlag(flag) ? _snapTo & ~flag : _snapTo | flag;
        var on = _snapTo.HasFlag(flag);
        foreach (var (item, at, _) in SnapRows())
        {
            if (at == flag) item.IsChecked = on;
        }
        KeepSwitches();
        Say(L10n.F("{0} {1}", L10n.T(label), L10n.T((on ? "on" : "off"))));
    }

    /// <summary>The view's switches written down, so the next launch opens the way this one was left.</summary>
    private void KeepSwitches()
    {
        _tools.ShowGrid = _gridVisible;
        _tools.ShowRulers = _rulersVisible;
        _tools.ShowGuides = _guidesVisible;
        _tools.LockGuides = _guidesLocked;
        _tools.ShowTransformControls = _transformShown;
        _tools.PixelGrid = _pixelGridShown;
        _tools.Snapping = _snappingOn;
        _tools.GridSpacing = _grid.Spacing;
        _tools.GridSubdivisions = _grid.Subdivisions;
        _tools.SnapTo = _snapTo;
        _tools.Save(ToolDefaults.DefaultPath);
    }

    /// <summary>View ▸ Show Grid: the layout grid on or off, which the canvas draws under everything else.</summary>
    private void ShowGrid()
    {
        _gridVisible = !_gridVisible;
        _canvas.Grid = _gridVisible ? _grid : null;
        _showGrid.Header = L10n.T(_gridVisible ? "_Hide Grid" : "Show _Grid");
        KeepSwitches();
        _canvas.InvalidateVisual();
        Say(_gridVisible ? L10n.F("Grid every {0} pixels", _grid.Spacing) : "Grid hidden");
    }

    /// <summary>A View menu row that is a switch: it opens where it was left and turns over when clicked.</summary>
    private static void Toggle(string header, MenuItem item, bool on, Action flip)
    {
        item.Header = L10n.T(header);
        item.ToggleType = MenuItemToggleType.CheckBox;
        item.IsChecked = on;
        item.Click += (_, _) => flip();
    }

    /// <summary>View ▸ Guides: whether the guides are drawn, which does not change them.</summary>
    private void ShowGuides()
    {
        _guidesVisible = !_guidesVisible;
        _showGuides.IsChecked = _guidesVisible;
        PushViewSwitches();
        KeepSwitches();
        Say(_guidesVisible ? "Guides shown" : "Guides hidden");
    }

    /// <summary>View ▸ Lock Guides: whether a guide may be dragged. Locked, a click on one passes by it.</summary>
    private void LockGuides()
    {
        _guidesLocked = !_guidesLocked;
        _lockGuides.IsChecked = _guidesLocked;
        PushViewSwitches();
        KeepSwitches();
        Say(_guidesLocked ? "Guides locked" : "Guides unlocked");
    }

    /// <summary>View ▸ Show Transform Controls: whether the Move tool draws its handles.</summary>
    private void ShowTransformControls()
    {
        _transformShown = !_transformShown;
        _showTransform.IsChecked = _transformShown;
        PushViewSwitches();
        KeepSwitches();
        Say(_transformShown ? "Transform controls shown" : "Transform controls hidden");
    }

    /// <summary>View ▸ Pixel Grid: a line around each document pixel when the view is in far enough.</summary>
    private void ShowPixelGrid()
    {
        _pixelGridShown = !_pixelGridShown;
        _pixelGrid.IsChecked = _pixelGridShown;
        PushViewSwitches();
        KeepSwitches();
        Say(_pixelGridShown ? "Pixel grid shown from 800% up" : "Pixel grid hidden");
    }

    /// <summary>View ▸ Snap: whether a drag lines up with anything at all.</summary>
    private void ShowSnapping()
    {
        _snappingOn = !_snappingOn;
        _snapping.IsChecked = _snappingOn;
        KeepSwitches();
        Say(_snappingOn ? "Snapping on" : "Snapping off");
    }

    /// <summary>The canvas told where each view switch stands, so what is drawn and what is caught agree.</summary>
    private void PushViewSwitches()
    {
        _canvas.ShowsGuides = _guidesVisible;
        _canvas.LocksGuides = _guidesLocked;
        _canvas.ShowsTransformControls = _transformShown;
        _canvas.PixelGrid = _pixelGridShown;
        _canvas.InvalidateVisual();
    }

    /// <summary>View ▸ Rulers: the strips along the top and down the side of the canvas, on or off.</summary>
    private void ShowRulers()
    {
        _rulersVisible = !_rulersVisible;
        _showRulers.IsChecked = _rulersVisible;
        _rulerAcross.IsVisible = _rulersVisible;
        _rulerDown.IsVisible = _rulersVisible;
        _rulerCorner.IsVisible = _rulersVisible;
        KeepSwitches();
        UpdateRulers();
        Say(_rulersVisible ? "Rulers shown" : "Rulers hidden");
    }

    /// <summary>
    /// Starts watching the project that is open, so a copy of it written by something else — an editor beside
    /// this window — is taken up. Nothing is watched when no project has been saved yet, since there is no
    /// folder to watch.
    /// </summary>
    private void WatchProject()
    {
        _watch ??= ProjectWatch.For(_projectPath);
        if (_watch is null)
        {
            _watchTimer?.Stop();
            return;
        }
        _watchTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _watchTimer.Tick -= ProjectWritten;
        _watchTimer.Tick += ProjectWritten;
        _watchTimer.Start();
    }

    /// <summary>
    /// The project on disk has been written by something else. What it holds now is put in place of what is
    /// open, with the view and the selection kept, which is how the canvas takes a change an editor outside
    /// made. A document with changes of its own is never thrown away for it: it is only said that the project
    /// has moved on, and the person's own work waits until they save or reopen.
    /// </summary>
    private void ProjectWritten(object? sender, EventArgs e)
    {
        if (_history.HasPendingEdit || _canvas.HasPendingInteraction || _preview is not null || _text is not null
            || _windowClosePending || _tabClosePending.Count > 0 || _recoveryBusy) return;
        if (_watch?.Changed() is not true) return;
        if (_document is not { } document || _projectPath is not { } path) return;
        if (_history.IsModified || _text is not null)
        {
            Say("The project has been written by something else — save or reopen to take the change up");
            return;
        }
        ProjectSnapshot snapshot;
        try
        {
            snapshot = ProjectStore.Load(path);
        }
        catch (Exception error) when (error is ProjectException or IOException or UnauthorizedAccessException)
        {
            _watch?.Retry();
            Say("The project has been written by something else, and what is there now cannot be read");
            return;
        }
        var viewport = _canvas.Viewport;
        var selected = SelectedLayers;
        // As in Open: the document takes the snapshot's pixel references, so the snapshot is left alone rather
        // than disposed — disposing it would free the pixels the document is holding.
        _document = snapshot.ToDocument();
        _canvas.Document = _document;
        _canvas.RestoreViewport(viewport);
        document.Dispose();
        _history.Reset();
        ShowLayers(_document);
        var again = selected.FirstOrDefault(id => _document.Layers.Any(layer => layer.ID == id));
        if (again != Guid.Empty) Reselect(again);
        Refresh();
        Say(L10n.F("{0} — taken up again, {1} layers", Path.GetFileName(path), _document.Layers.Count));
    }

    /// <summary>
    /// The strips numbered the way the canvas is scrolled and zoomed: the same zoom and the same document
    /// place at the top left corner, so a tick lines up with what it measures.
    /// </summary>
    private void UpdateRulers()
    {
        _rulerAcross.Scale = _canvas.Zoom;
        _rulerAcross.Origin = _canvas.OriginX;
        _rulerDown.Scale = _canvas.Zoom;
        _rulerDown.Origin = _canvas.OriginY;
        _rulerAcross.InvalidateVisual();
        _rulerDown.InvalidateVisual();
    }

    /// <summary>View ▸ Grid Settings: how far apart the lines are and how finely each square is split.</summary>
    private async Task GridSettings()
    {
        if (await GridSettingsDialog.Ask(this, _grid) is not { } asked) return;
        _grid = asked;
        if (_gridVisible) _canvas.Grid = _grid;
        KeepSwitches();
        _canvas.InvalidateVisual();
        Say(L10n.F("Grid every {0} pixels, split {1} ways", _grid.Spacing, _grid.Subdivisions));
    }

    /// <summary>View ▸ New Guide: a line across the canvas to line things up against.</summary>
    private async Task NewGuide()
    {
        if (_document is not { } document) return;
        if (await GuideDialog.Ask(this, document.Width, document.Height) is not { } asked) return;
        if (_document is not { } current) return;
        _history.Begin("New Guide", current, Selected);
        var made = GuideEdits.Add(current, asked.Axis, asked.Position);
        _history.End(current, Selected);
        if (made is null)
        {
            Say(GuideEdits.Allowed(asked.Position)
                ? "This document already holds as many guides as it may."
                : "Enter a finite guide position between -1000000 and 1000000.");
            return;
        }
        Refresh();
        Say(L10n.F("{0} guide at {1:0.#}", L10n.T(asked.Axis.ToString()), asked.Position));
    }

    /// <summary>View ▸ Clear Guides: every guide taken away, as one undo step.</summary>
    private void ClearGuides()
    {
        if (_document is not { } document) return;
        if (document.Guides.Count == 0)
        {
            Say("There are no guides to clear");
            return;
        }
        Edit("Clear Guides", () => GuideEdits.Clear(document) > 0);
        Say("Guides cleared");
    }

    /// <summary>
    /// Image ▸ Auto Levels: the levels the picture itself asks for, worked out from its own histogram and
    /// put straight on the layer. The Mac build shows them in the Levels panel instead; this skips the panel,
    /// since what it works out is the whole of the edit.
    /// </summary>
    private void AutoLevels(LevelsAuto mode)
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { Asset: not null, IsGroup: false, Adjustment: null })
        {
            Say("Auto Levels needs a layer with pixels of its own");
            return;
        }
        if (!Edit("Auto Levels", () => LevelsEdits.Auto(document, id, mode))) Say("There is nothing in that layer to stretch");
        else Say(L10n.F("Auto Levels: {0}", L10n.T(mode.ToString())));
        Reselect(id);
    }

    /// <summary>
    /// Starts showing what a panel would do to a layer, before anything is committed. Nothing happens when
    /// the layer cannot be previewed, and the panel still works.
    /// </summary>
    /// <summary>The brush's colour, which the panels that think in colours start from.</summary>
    private SKColor BrushColour() => new(
        (byte)Math.Clamp(Math.Round(_options.Brush.Red * 255), 0, 255),
        (byte)Math.Clamp(Math.Round(_options.Brush.Green * 255), 0, 255),
        (byte)Math.Clamp(Math.Round(_options.Brush.Blue * 255), 0, 255));

    /// <summary>Starts showing what a panel would do to a layer, before anything is committed.
    private void StartPreview(CanvasDocument document, Guid layerID) =>
        StartPreview(FilterPreview.Begin(document, layerID), layerID);

    /// <summary>The same for a look that changes several layers at once, which is what a group distortion is.</summary>
    private void StartPreview(CanvasDocument document, IReadOnlyList<Guid> layerIDs) =>
        StartPreview(FilterPreview.Begin(document, layerIDs), null);

    private void StartPreview(FilterPreview? preview, Guid? layerID)
    {
        StopPreview();
        if (preview is null) return;
        _preview = preview;
        _previewLayer = layerID;
        _canvas.PreviewDocument = preview.Document;
    }

    /// <summary>
    /// An amount has moved: the edit is remembered and runs once the amounts have been still for a moment,
    /// rather than on every tick of a drag.
    /// </summary>
    /// <summary>
    /// What the shape tool would make of a drag from <paramref name="box"/> outward: the style it will be
    /// drawn with and the box its pixels will cover. The same answer serves the drag's preview and the layer
    /// the drag ends up making, so what is seen while dragging is what arrives.
    /// </summary>
    private (LayerShapeStyle Style, SKRectI Box) ShapePlan(SKRectI box, SKPoint? anchor = null, SKPoint? lineEnd = null)
    {
        var style = new LayerShapeStyle
        {
            Kind = _options.Shape,
            Red = _options.Brush.Red,
            Green = _options.Brush.Green,
            Blue = _options.Brush.Blue,
            CornerRadius = _options.ShapeCornerRadius,
        };
        if (_options.Shape != ShapeKind.Line) return (style, box);
        // A line's layer is the box around it with room for the stroke's own thickness and its round ends.
        var half = (float)(_options.ShapeLineWidth / 2);
        var target = CropEdits.Snapped(SKRect.Create(box.Left - half, box.Top - half,
            box.Width + half * 2, box.Height + half * 2));
        style.LineWidth = _options.ShapeLineWidth;
        style.Start = Unit(target, anchor ?? new SKPoint(box.Left, box.Top));
        style.End = Unit(target, lineEnd ?? new SKPoint(box.Right, box.Bottom));
        return (style, target);
    }

    private void RequestPreview(Func<CanvasDocument, Guid, bool> apply)
    {
        if (_previewLayer is not { } layer) return;
        RequestPreview(document => apply(document, layer));
    }

    private void RequestPreview(Func<CanvasDocument, bool> apply)
    {
        if (_preview is null) return;
        _previewApply = apply;
        _previewTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _previewTimer.Stop();
        _previewTimer.Tick -= ShowPreviewOnce;
        _previewTimer.Tick += ShowPreviewOnce;
        _previewTimer.Start();
    }

    /// <summary>Runs the edit on the preview once the amounts have settled, and draws it.</summary>
    private void ShowPreviewOnce(object? sender, EventArgs e)
    {
        _previewTimer?.Stop();
        if (_preview is not { } preview || _previewApply is not { } apply) return;
        if (!preview.Show(apply)) return;
        // The canvas is put back on the preview every time it is shown: the panel's Preview tick may have been
        // off, which takes the canvas back to the document as it stands.
        _canvas.PreviewDocument = preview.Document;
        // The panel's scope describes the picture the canvas has just been given, so the two are shown together.
        if (_cameraRaw is { } panel) panel.ShowScope(_cameraRawScope);
        _canvas.InvalidateVisual();
    }

    /// <summary>
    /// Puts the preview away. The canvas is told to stop drawing it before it is disposed, or a redraw could
    /// reach pixels that have just been freed.
    /// </summary>
    /// <summary>
    /// Shows the picture as it is, with no filter on it, which is what a panel's Preview tick being off means.
    /// The preview is kept rather than thrown away, so ticking it back on has something to show again — that is
    /// the difference between this and <see cref="StopPreview"/>.
    /// </summary>
    private void HidePreview()
    {
        _previewTimer?.Stop();
        if (_preview is null) return;
        _canvas.PreviewDocument = null;
        _canvas.InvalidateVisual();
    }

    private void StopPreview()
    {
        _previewTimer?.Stop();
        _previewApply = null;
        _previewLayer = null;
        _canvas.PreviewDocument = null;
        _preview?.Dispose();
        _preview = null;
        _canvas.InvalidateVisual();
    }

    /// <summary>
    /// Select ▸ Colour Range: everything in the picture near a colour, wherever it is. What is matched is the
    /// canvas as shown, as the Mac build matches, so a colour counts wherever it appears.
    /// <para>
    /// The panel is the Mac's — not modal, so the picture stays live and a colour is picked by clicking it.
    /// The selection is built into the document as each colour goes in, so the canvas draws the ants while the
    /// panel is up; the history step is opened before the first of them and closed by OK, so the whole session
    /// undoes in one, and a Cancel puts the selection there was back and leaves no step behind at all.
    /// </para>
    /// </summary>
    private void ColorRange()
    {
        if (!MayEditImage()) return;
        if (_document is not { } document) return;
        if (_colorRange is not null)
        {
            _colorRangePanel?.Activate();
            return;
        }
        if (ColorRangeSession.Begin(document) is not { } session)
        {
            Say("There is no picture to pick a color from");
            return;
        }
        _colorRange = session;
        var originalSelection = document.Selection;
        var history = _history;
        var selected = Selected;
        history.Begin("Color Range", document, selected);
        var panel = new ColorRangePanel(session);
        void Finish(bool keep)
        {
            if (!ReferenceEquals(_colorRange, session)) return;
            if (!keep) document.Selection = originalSelection;
            history.End(document, selected);
            _finishColorRange = null;
            _colorRangePanel = null;
            _colorRange = null;
            _canvas.EyedropperOnClick = _tool == Tool.Eyedropper;
            panel.Close();
            session.Dispose();
            if (ReferenceEquals(_document, document)) Refresh();
        }
        _finishColorRange = Finish;
        panel.Changed += () =>
        {
            if (ReferenceEquals(_document, document) && ReferenceEquals(_colorRange, session))
                ShowColorRange(document, session.Rebuild(document));
        };
        panel.Applied += () =>
        {
            var count = session.Include.Count;
            var fuzziness = session.Fuzziness;
            Finish(true);
            Say(L10n.F("Selected what is near {0} color(s) within {1:0}", count, fuzziness));
        };
        panel.Cancelled += () => Finish(false);
        _colorRangePanel = panel;
        // The picture is live and every press on it samples, whichever tool is in hand, as the Mac's does.
        _canvas.EyedropperOnClick = true;
        panel.Show(this);
        ShowColorRange(document, false);
        Say("Color Range: click the picture to pick the color to select");
    }

    /// <summary>Shows the panel the selection as it now stands, and lets the canvas draw it.</summary>
    private void ShowColorRange(CanvasDocument document, bool changed)
    {
        _canvas.InvalidateVisual();
        _colorRangePanel?.Showing(_colorRange?.Mask(document), _colorRange?.Include.Count ?? 0,
            _colorRange?.Exclude.Count ?? 0);
        if (!changed) return;
        Say(_colorRange?.Problem ?? L10n.F("{0} color(s) picked", _colorRange?.Include.Count ?? 0));
    }

    /// <summary>Puts the panel away and lets the session and its sample go.</summary>
    private void CloseColorRange() => _finishColorRange?.Invoke(false);

    /// <summary>Image ▸ Canvas Size: the canvas in pixels, with the picture kept at one of nine anchors.</summary>
    private async Task CanvasSize()
    {
        if (_document is not { } document) return;
        if (await CanvasSizeDialog.Ask(this, document.Width, document.Height, CanvasEdits.CentreAnchor) is not { } asked) return;
        if (_document is not { } current) return;
        Edit("Canvas Size", () => CanvasEdits.Resize(current, asked.Width, asked.Height, asked.Anchor));
        Say(L10n.F("Canvas is now {0} x {1}", asked.Width, asked.Height));
    }

    /// <summary>Image ▸ Trim: the canvas cut back to what is actually drawn on it.</summary>
    private async Task Trim()
    {
        if (_document is not { } document) return;
        if (await TrimDialog.Ask(this, new TrimOptions()) is not { } options) return;
        if (_document is not { } current) return;
        if (!Edit("Trim", () => TrimEdits.Trim(current, options))) Say("There was nothing to trim");
        else Say(L10n.F("Trimmed to {0} x {1}", current.Width, current.Height));
    }

    /// <summary>
    /// File ▸ Open Recent: the projects opened or saved lately, and a row that forgets them. The list is read
    /// back each time the menu is rebuilt, so a project that has since been moved or deleted is not offered.
    /// </summary>
    private void RefreshRecent()
    {
        _recentMenu.Items.Clear();
        var projects = _recent.All();
        foreach (var project in projects)
        {
            var path = project;
            _recentMenu.Items.Add(Command(Path.GetFileName(path), () => OpenRecent(path), translate: false));
        }
        if (projects.Count == 0)
        {
            _recentMenu.Items.Add(new MenuItem { Header = L10n.T("Nothing yet"), IsEnabled = false });
        }
        _recentMenu.Items.Add(new Separator());
        var clear = Command("_Clear Menu", () =>
        {
            _recent.Forgot();
            RefreshRecent();
        });
        clear.IsEnabled = projects.Count > 0;
        _recentMenu.Items.Add(clear);
    }

    /// <summary>Notes a project as one of the recent ones, as opening or saving it does.</summary>
    private void NoteRecent(string path)
    {
        _recent.Note(path);
        RefreshRecent();
    }

    /// <summary>Opens a project from the recent list; one that has gone says so rather than failing quietly.</summary>
    private async void OpenRecent(string path)
    {
        try
        {
            await OpenProjectPathAsync(path);
        }
        catch (Exception error)
        {
            AppDiagnostics.Report("Open recent project", error);
            Say(L10n.F("Could not open {0}: {1}", Path.GetFileName(path), OperationErrors.Describe(error)));
            RefreshRecent();
        }
    }

    /// <summary>
    /// The ratios the Crop tool offers, in one list because both the Tools menu and the options bar offer them.
    /// The menu marks a letter of each as its key; the bar is a pop-up, which shows the text as written.
    /// </summary>
    private static readonly (string Label, double? Ratio)[] CropRatios =
    [
        ("Free", null), ("Original", -1), ("1:1", 1), ("4:3", 4.0 / 3), ("3:4", 3.0 / 4),
        ("16:9", 16.0 / 9), ("9:16", 9.0 / 16),
    ];

    /// <summary>The ratios the Crop tool offers, as the Mac build's ratio menu does.</summary>
    private void BuildCropRatios()
    {
        foreach (var (label, ratio) in CropRatios)
        {
            _cropRatios.Items.Add(Command(label.Replace(":", ":_", StringComparison.Ordinal), () => SetCropRatio(ratio)));
        }
        _cropRatios.Items.Add(new Separator());
        // The same two rows the canvas answers to, so they show the key that really applies the frame.
        var apply = Command("_Apply", ApplyCrop);
        var cancel = Command("_Cancel", CancelCrop);
        ShowKey(apply, "Apply Canvas Operation", Shortcuts.Canvas);
        ShowKey(cancel, "Cancel Canvas Operation", Shortcuts.Canvas);
        _cropRatios.Items.Add(apply);
        _cropRatios.Items.Add(cancel);
    }

    /// <summary>Holds the crop frame to a ratio from now on, and shapes the frame it has to it.</summary>
    private void SetCropRatio(double? ratio)
    {
        _canvas.CropRatio = ratio;
        if (_document is not { } document) return;
        if (ratio is null)
        {
            Say("Crop: any shape");
            return;
        }
        var wanted = ratio == -1 ? CropEdits.OriginalRatio(document) : ratio.Value;
        var frame = _cropFrame ?? CropEdits.Snapped(SKRect.Create(0, 0, document.Width, document.Height));
        _cropFrame = CropEdits.ApplyRatio(frame, wanted);
        ShowCropBox();
        Refresh();
        Say(L10n.F("Crop: {0:0.##} to 1", wanted));
    }

    /// <summary>The crop frame follows the tool: the whole canvas until it is dragged.</summary>
    private void ShowCropBox()
    {
        if (_tool != Tool.Crop || _document is not { } document)
        {
            _canvas.CropBox = null;
            return;
        }
        _canvas.CropBox = _cropFrame ?? SKRectI.Create(0, 0, document.Width, document.Height);
    }

    /// <summary>
    /// A crop drag: the frame is snapped to whatever edge is nearby — a frame being moved by its nearest
    /// edge, so it keeps its size, and one being shaped by the edge the pointer is on.
    /// </summary>
    private void CropChanged(SKRectI frame, SKPoint pointer)
    {
        if (_document is not { } document || _tool != Tool.Crop) return;
        var tolerance = TransformSnap.Distance / Math.Max(_canvas.Zoom, 0.0001);
        double? lineX, lineY;
        var middle = new SKPoint((float)frame.MidX, (float)frame.MidY);
        var snapped = _canvas.CropMoving
            ? CropEdits.SnapMove(document, frame, tolerance, out lineX, out lineY)
            : CropEdits.Snap(document, frame, pointer, middle, symmetric: false, tolerance, out lineX, out lineY);
        _cropFrame = CropEdits.Valid(snapped) ? snapped : frame;
        _canvas.SnapLines = (lineX, lineY);
        _canvas.CropBox = _cropFrame;
        Refresh();
    }

    /// <summary>Takes the crop, as one undo step, and lets the frame go.</summary>
    private void ApplyCrop()
    {
        if (_document is not { } document) return;
        if (_cropFrame is not { } frame)
        {
            Say("Drag a frame first");
            return;
        }
        if (!CropEdits.Valid(frame)) { Say("That frame is not one the canvas can be cropped to"); return; }
        _canvas.SnapLines = (null, null);
        Edit("Crop", () => CanvasEdits.Crop(document, frame));
        _cropFrame = null;
        ShowCropBox();
    }

    /// <summary>Lets the crop frame go, leaving the canvas as it is.</summary>
    private void CancelCrop()
    {
        _cropFrame = null;
        _canvas.SnapLines = (null, null);
        ShowCropBox();
        Refresh();
        Say("Crop: let go");
    }

    /// <summary>
    /// The Type tool: a click says where the text goes, and the dialog says what it says. A text layer is
    /// pixels and the style that drew them, so choosing Edit Text on one draws it again rather than painting
    /// over it.
    /// </summary>
    /// <summary>
    /// A click with the Type tool: on a live text layer it joins that layer's words, and anywhere else it
    /// starts new text there. Typing then draws the layer as it goes, so the words appear where they go.
    /// </summary>
    private void TypeHere(SKPoint origin)
    {
        InvalidatePendingClipboardPaste();
        if (_document is not { } document) return;
        // A click inside the text already being typed stays in that session, and puts the caret where it
        // was clicked rather than at the end.
        if (_text is { } session && session.Contains(document, origin))
        {
            if (session.PlaceCaret(document, origin)) ShowTextCaret();
            return;
        }
        CommitText();
        if (TextEdits.HitTest(document, origin) is { } target)
        {
            _text = TextSession.Editing(target);
            _text.PlaceCaret(document, origin);
            _history.Begin("Edit Text", document, target.ID);
        }
        else
        {
            _text = TextSession.New(new LayerTextStyle
            {
                Content = "",
                FontName = "Arial",
                FontSize = 72,
                Red = _options.Brush.Red,
                Green = _options.Brush.Green,
                Blue = _options.Brush.Blue,
            }, origin);
            _history.Begin("Type", document, Selected);
        }
        _canvas.BeginText();
        ShowTextCaret();
        Refresh();
        Say("Typing — Escape lets it go, Ctrl+Enter keeps it");
    }

    /// <summary>Keys that were typed, put into the text and drawn as they go.</summary>
    private void TypedText(string typed)
    {
        if (_document is not { } document || _text is not { } session) return;
        // A carriage return arrives with Enter as well as the key press, and one newline is enough.
        typed = typed.Replace("\r", "");
        if (typed.Length == 0) return;
        if (!session.Type(document, typed))
        {
            Say("That text could not be drawn: its box is too big for one surface");
            return;
        }
        ShowText();
    }

    /// <summary>The delete key: the last character goes.</summary>
    private void BackspacedText()
    {
        if (_document is not { } document || _text is not { } session) return;
        if (session.Backspace(document)) ShowText();
    }

    /// <summary>The delete key: the character after the caret goes, which does not change the layer's size.</summary>
    private void DeletedText()
    {
        if (_document is not { } document || _text is not { } session) return;
        if (session.Delete(document)) ShowText();
    }

    /// <summary>An arrow or Home or End: the caret moves through the words, and is drawn where it lands.</summary>
    private void MovedTextCaret(TextSession.TextMove move)
    {
        if (_text is not { } session) return;
        if (session.MoveCaret(move)) ShowTextCaret();
    }

    /// <summary>Puts the caret where the text ends and keeps the panel on the layer being typed on.</summary>
    private void ShowText()
    {
        if (_document is not { } document || _text is not { } session) return;
        ShowTextCaret();
        ShowLayers(document);
        if (session.LayerID is { } made)
        {
            var row = _rows.IndexOf(made);
            if (row >= 0) _layers.SelectedIndex = row;
        }
        Refresh();
    }

    private void ShowTextCaret()
    {
        _canvas.TextCaretLine = _document is { } document && _text is { } session ? session.CaretLine(document) : null;
        _canvas.TextCaret = _document is { } current && _text is { } text ? text.Caret(current) : null;
    }

    /// <summary>Ctrl and Enter: the words are kept, and the whole session is one undo step.</summary>
    private void CommitText()
    {
        if (_text is not { } session) return;
        var typed = session.Content;
        _text = null;
        _canvas.EndText();
        if (_document is not { } document) return;
        var kept = session.Commit(document);
        _history.End(document, kept);
        if (kept is { } layer) Reselect(layer);
        else Refresh();
        if (kept is not null) Say(L10n.F("Text: {0} characters", typed.Replace('\n', ' ').Trim().Length));
    }

    /// <summary>Escape: the words go back to what they were, and the history drops the step.</summary>
    private void CancelText()
    {
        if (_text is not { } session) return;
        _text = null;
        _canvas.EndText();
        if (_document is not { } document) return;
        session.Cancel(document);
        _history.End(document, session.LayerID);
        Refresh();
        Say("Text let go");
    }

    /// <summary>Changes the selected text layer's face, size or colour and draws it again.</summary>
    private async Task EditText()
    {
        CommitText();
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { LiveText: { } text } layer)
        {
            Say("That layer is not text");
            return;
        }
        var origin = new SKPoint((float)layer.Transform.X, (float)layer.Transform.Y);
        if (await TextDialog.Ask(this, "Edit text", text) is not { } wanted) return;
        if (_document is not { } current) return;
        _history.Begin("Edit Text", current, id);
        var changed = TextEdits.SetStyle(current, id, wanted);
        _history.End(current, id);
        if (!changed) { Say("That text could not be drawn"); return; }
        Reselect(id);
        Say(L10n.F("Text: {0} characters at {1:0},{2:0}", wanted.Content.Length, origin.X, origin.Y));
    }

    /// <summary>Where a brush stroke goes: the layer's pixels, or its mask.</summary>
    private void SetPaintingMask(bool mask)
    {
        _options.PaintOnMask = mask;
        _paintOnMask.IsChecked = mask;
        RefreshOptionsBar();
        Say(mask
            ? "The brush paints on the layer's mask — white reveals, and Erase paints black"
            : "The brush paints on the layer's pixels");
    }

    /// <summary>The brush erases rather than paints: on a mask, that is black rather than white.</summary>
    private void SetErasing(bool erasing)
    {
        _options.Erase = erasing;
        _eraseToggle.IsChecked = erasing;
        PushBrush();
        Say(erasing ? "The brush erases" : "The brush paints");
    }

    /// <summary>
    /// The eyedropper: the colour under the click becomes the brush's — or, when a panel that picks colours is
    /// up, goes into that instead, because that is what the click was for.
    /// </summary>
    private void Picked(SKPoint point, KeyModifiers keys)
    {
        if (_document is not { } document) return;
        if ((long)document.Width * document.Height > DocumentLimits.MaxSurfacePixels)
        {
            Say("This canvas is too big to read a color from in one piece");
            return;
        }
        var x = (int)Math.Floor(point.X);
        var y = (int)Math.Floor(point.Y);
        if (x < 0 || y < 0 || x >= document.Width || y >= document.Height) return;
        using var rendered = DocumentRenderer.Render(document);
        var colour = rendered.GetPixel(x, y);
        if (colour.Alpha == 0)
        {
            Say("Nothing is drawn there");
            return;
        }
        // Colour Range is up: the colour is one of the ones being looked for, and Shift or Alt says whether it
        // joins the range or is taken out of it — as the Mac build's panel does with the same two keys.
        if (_colorRange is { } ranging)
        {
            var mode = keys.HasFlag(KeyModifiers.Alt) ? ColorRangeSession.Picking.Remove
                : keys.HasFlag(KeyModifiers.Shift) ? ColorRangeSession.Picking.Add
                : ranging.Mode;
            ShowColorRange(document, ranging.Pick(document, colour, mode));
            return;
        }
        // Sampling goes into the picker while it is open rather than to the brush, as the Mac build's does:
        // the colour is being chosen there, and what is under the pointer is one of the ways to choose it.
        if (_picker is { } picking)
        {
            picking.Sample((colour.Red / 255.0, colour.Green / 255.0, colour.Blue / 255.0));
            Say(L10n.F("Sampled {0},{1},{2} into the picker", colour.Red, colour.Green, colour.Blue));
            return;
        }
        // A transparent pixel has no colour to take; a part-transparent one is read as it looks on white.
        _options.Brush = _options.Brush with
        {
            Red = colour.Red / 255.0,
            Green = colour.Green / 255.0,
            Blue = colour.Blue / 255.0,
        };
        PushBrush();
        Say(L10n.F("Brush color {0},{1},{2}", colour.Red, colour.Green, colour.Blue));
    }

    /// <summary>How Spot Healing works out what to put in the painted area.</summary>
    private void Heal(HealingMode mode)
    {
        _options.Brush = _options.Brush with { Healing = mode };
        PushBrush();
        Say(L10n.F("Spot healing: {0}", L10n.T(mode.ToString())));
    }

    /// <summary>Asks for one of the brush's settings and takes it, as an options bar would.</summary>
    private async Task SetBrush(BrushSetting which)
    {
        switch (which)
        {
            case BrushSetting.Size:
                if (await Ask("Brush size", "Diameter in pixels, 1 to 2000",
                        $"{_options.Brush.Diameter:0}", 1, 2000) is { } size)
                {
                    _options.Brush = _options.Brush with { Diameter = size };
                }
                break;
            case BrushSetting.Hardness:
                if (await Ask("Brush hardness", "Percent, 0 for a soft tip and 100 for a hard one",
                        $"{_options.Brush.Hardness * 100:0}", 0, 100) is { } hardness)
                {
                    _options.Brush = _options.Brush with { Hardness = hardness / 100.0 };
                }
                break;
            case BrushSetting.Opacity:
                if (await Ask("Brush opacity", "Percent, 1 to 100", $"{_options.Brush.Opacity * 100:0}", 1, 100) is { } opacity)
                {
                    _options.Brush = _options.Brush with { Opacity = opacity / 100.0 };
                }
                break;
            case BrushSetting.Radius:
                if (await Ask("Blur radius", "How far the blur reaches, in pixels, 0.5 to 50",
                        $"{_options.Brush.BlurRadius:0.#}", 0.5, 50) is { } radius)
                {
                    _options.Brush = _options.Brush with { BlurRadius = radius };
                }
                break;
            default:
                // The colour is the picker's business, which is what the Mac's own Color button opens.
                ChooseColour(foreground: true);
                return;
        }
        PushBrush();
        Say(L10n.F("Brush: {0:0} pixels, {1}", _options.Brush.Diameter, Spell(_options.Brush)));
    }

    private async Task<double?> Ask(string title, string label, string initial, double least, double most)
    {
        if (await TextPrompt.Ask(this, title, label, initial) is not { } typed) return null;
        if (!double.TryParse(typed.Trim(), out var value) || value < least || value > most)
        {
            Say(L10n.F("That has to be a number from {0:0} to {1:0}", least, most));
            return null;
        }
        return value;
    }

    /// <summary>A colour typed as three numbers from 0 to 255.</summary>
    private static (double Red, double Green, double Blue)? Colour(string typed)
    {
        var parts = typed.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return null;
        var values = new double[3];
        for (var index = 0; index < 3; index++)
        {
            if (!double.TryParse(parts[index], out values[index]) || values[index] < 0 || values[index] > 255) return null;
            values[index] /= 255;
        }
        return (values[0], values[1], values[2]);
    }

    /// <summary>One of the brush settings the options bar would show.</summary>
    /// <summary>One change to the selection, as one undo step.</summary>
    private bool Change(string name, Func<CanvasDocument, bool> change)
    {
        if (_document is not { } document) return false;
        return Edit(name, () => change(document));
    }

    /// <summary>Lets the selection go, and any half-drawn outline with it.</summary>
    private void Deselect()
    {
        _canvas.CancelDraft();
        Change("Deselect", SelectionEdits.Deselect);
    }

    /// <summary>
    /// Select ▸ Layer's Pixels: what the layer shows becomes the selection, in its place on the document —
    /// what Photoshop takes when a layer's thumbnail is command-clicked.
    /// </summary>
    private void SelectLayerPixels()
    {
        if (Selected is not { } id) return;
        if (!Change("Select Layer's Pixels", document => SelectionEdits.SelectLayerPixels(document, id)))
        {
            Say("That layer shows no pixels to take a selection from");
        }
    }

    /// <summary>Select ▸ Mask's Black Areas: what the layer's mask hides becomes the selection.</summary>
    private void SelectMaskBlack()
    {
        if (Selected is not { } id) return;
        if (!Change("Select Mask's Black Areas", document => SelectionEdits.SelectMaskDark(document, id)))
        {
            Say("That layer has no mask, or none of it is hidden");
        }
    }

    /// <summary>The Windows build's version and repository, without consulting the macOS release feed.</summary>
    private Task CheckForUpdates() => UpdateDialog.ShowWindowsReleaseInfo(this);

    private async Task ExportDiagnostics()
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L10n.T("Export Diagnostics"),
                SuggestedFileName = $"Compositor-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
                DefaultExtension = "zip",
                FileTypeChoices = [new FilePickerFileType("ZIP") { Patterns = ["*.zip"] }],
            });
            if (file?.TryGetLocalPath() is not { } path) return;
            if (!AppDiagnostics.Export(path, out var error))
            {
                await UpdateDialog.Ask(this, L10n.T("Diagnostics"), L10n.F("Could not export diagnostics: {0}", error));
                return;
            }
            await UpdateDialog.Ask(this, L10n.T("Diagnostics"),
                L10n.F("Diagnostics saved to {0}\n\nThe archive contains application logs and system information. Error messages may contain file paths. Review it before sharing. Project files and images are not included, and nothing is uploaded automatically.", path));
        }
        catch (Exception error)
        {
            AppDiagnostics.Report("Export diagnostics", error);
            await UpdateDialog.Ask(this, L10n.T("Diagnostics"), L10n.F("Could not export diagnostics: {0}", OperationErrors.Describe(error)));
        }
    }

    private async Task OpenDiagnosticsFolder()
    {
        try
        {
            var path = AppDiagnostics.DirectoryPath;
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            AppDiagnostics.Report("Open diagnostics folder", error);
            await UpdateDialog.Ask(this, L10n.T("Diagnostics"), L10n.F("Could not open the diagnostics folder: {0}", OperationErrors.Describe(error)));
        }
    }

    /// <summary>The background colour, which the gradient tool draws towards and a fill can use.</summary>
    private SKColor BackgroundColour() => new(
        (byte)Math.Clamp(Math.Round(_options.GradientBackground.Red * 255), 0, 255),
        (byte)Math.Clamp(Math.Round(_options.GradientBackground.Green * 255), 0, 255),
        (byte)Math.Clamp(Math.Round(_options.GradientBackground.Blue * 255), 0, 255));

    /// <summary>
    /// Edit ▸ Fill: what the selection covers takes the colour. A mask is the exception — filling it sets how
    /// much it reveals rather than painting a colour, so its three channels are read as one gray.
    /// </summary>
    private void FillPixels(SKColor colour, string name)
    {
        if (_document is not { } document || Selected is not { } id) return;
        var filled = Edit(_options.PaintOnMask ? "Fill Mask" : name, () => _options.PaintOnMask
            ? FillEdits.FillMask(document, id,
                (byte)Math.Clamp(Math.Round((colour.Red + colour.Green + colour.Blue) / 3.0), 0, 255))
            : FillEdits.Fill(document, id, colour));
        if (!filled)
        {
            Say("Nothing to fill: that layer holds no pixels, or the selection does not reach it");
        }
    }

    /// <summary>
    /// Edit ▸ Clear: what the selection covers is made transparent. On a mask there is no transparency to
    /// clear, so it is filled with black instead, which is what hiding that part of the layer means.
    /// </summary>
    private void ClearPixels()
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (_options.PaintOnMask)
        {
            if (!Edit("Clear Mask", () => FillEdits.FillMask(document, id, 0)))
            {
                Say("Nothing to clear: the selection does not reach that mask");
            }
            return;
        }
        if (!Edit("Clear", () => FillEdits.Clear(document, id)))
        {
            Say("Nothing to clear: that layer holds no pixels, or the selection does not reach it");
        }
    }

    /// <summary>
    /// A marquee drag: the box becomes the selection, or is added to it or taken out of it, as the
    /// modifiers asked.
    /// </summary>
    private void MarqueeFinished(SKRectI box, SelectionMode mode, bool ellipse) =>
        Change(ellipse ? "Elliptical Marquee" : "Rectangular Marquee", document => mode == SelectionMode.Replace
            ? ellipse
                ? SelectionEdits.SelectEllipse(document, box, _options.SelectionAntialiased)
                : SelectionEdits.Select(document, box, _options.SelectionAntialiased)
            : SelectionEdits.Apply(document, SelectionEdits.Shape(box, ellipse), mode,
                _options.SelectionAntialiased));

    /// <summary>A lasso or polygonal lasso drag: the outline through the points it gathered.</summary>
    private void LassoFinished(IReadOnlyList<SKPoint> points, SelectionMode mode, bool polygonal) =>
        Change(polygonal ? "Polygonal Lasso" : "Lasso", document => mode == SelectionMode.Replace
            ? SelectionEdits.SelectLasso(document, points, _options.SelectionAntialiased)
            : SelectionEdits.Apply(document, SelectionEdits.Lasso(points), mode, _options.SelectionAntialiased));

    /// <summary>A click of the wand: everything like the pixel under it, read from the canvas as shown. The
    /// amounts are the options bar's: how far off the colour counts, how wide a sample is read, whether the
    /// outline has to stay joined, and whether every visible layer is read or only the one in hand.</summary>
    private void WandClicked(SKPoint point, SelectionMode mode) =>
        Change("Magic Wand", document =>
        {
            using var sample = SelectionEdits.Sample(document, _options.WandAllLayers ? null : Selected);
            return sample is not null && SelectionEdits.SelectWand(document, sample,
                (int)Math.Floor(point.X), (int)Math.Floor(point.Y), _options.Wand, mode,
                _options.SelectionAntialiased);
        });

    /// <summary>Asks for an amount and modifies the selection by it, as Select ▸ Modify does.</summary>
    private async Task ModifySelection(SelectionAmount which)
    {
        if (_document is not { } document || document.Selection.Path is null)
        {
            Say("Select something first");
            return;
        }
        var most = which == SelectionAmount.Feather ? SelectionEdits.MaxFeather : SelectionEdits.MaxAmount;
        var label = which == SelectionAmount.Feather ? "Feather radius in pixels" : "Pixels";
        if (await TextPrompt.Ask(this, L10n.F("Modify Selection: {0}", L10n.T(which.ToString())), label, "4") is not { } typed) return;
        if (!int.TryParse(typed.Trim(), out var amount) || amount < 1 || amount > most)
        {
            Say(L10n.F("The amount has to be a whole number from 1 to {0}", most));
            return;
        }
        if (_document is not { } current) return;
        Edit($"{which} Selection", () => which switch
        {
            SelectionAmount.Expand => SelectionEdits.Expand(current, amount),
            SelectionAmount.Contract => SelectionEdits.Contract(current, amount),
            _ => SelectionEdits.Feather(current, amount),
        });
    }

    private enum SelectionAmount
    {
        Expand,
        Contract,
        Feather,
    }

    /// <summary>Paints a finished stroke into the selected layer, as one undo step.</summary>
    private void Painted(IReadOnlyList<SKPoint> stroke)
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (BrushFor(stroke) is not { } settings) return;
        var name = _tool switch
        {
            Tool.Clone => "Clone Stamp",
            Tool.Blur => "Blur",
            Tool.Liquify => "Liquify",
            Tool.Smudge => "Smudge",
            Tool.Heal => "Spot Healing",
            _ => "Brush",
        };
        Edit(_options.PaintOnMask ? $"{name} on the mask" : name, () =>
        {
            if (_options.PaintOnMask)
            {
                // White reveals and black hides; the brush's Erase is what paints black, as the Mac build's
                // paint-white switch does.
                var value = _options.Erase ? 0 : 1;
                return BrushEdits.PaintMask(document, id,
                    stroke, settings with { Red = value, Green = value, Blue = value, Erasing = false });
            }
            // Liquify and Smudge work on pixels that are already there: a blank layer has nothing to push.
            if (_tool is Tool.Liquify or Tool.Smudge)
            {
                return WarpEdits.Warp(document, id, stroke, Warp(_tool), settings);
            }
            // A blank layer gets its pixels on the first paint, as the Mac build does.
            BrushEdits.EnsurePixels(document, id);
            return BrushEdits.Paint(document, id, stroke, settings);
        });
    }

    /// <summary>Which of the two push modes the tool in hand is.</summary>
    private static WarpMode Warp(Tool tool) => tool == Tool.Smudge ? WarpMode.Smudge : WarpMode.Liquify;

    /// <summary>
    /// The brush a stroke should be painted with. A Clone Stamp stroke needs a source, and its offset is
    /// fixed by the stroke that follows the click: later strokes keep it, so the source travels with the
    /// brush as the Mac build's alignment does.
    /// </summary>
    private BrushSettings? BrushFor(IReadOnlyList<SKPoint> stroke)
    {
        if (_tool != Tool.Clone) return _canvas.Brush;
        if (_cloneSource is not { } source)
        {
            Say("Alt-click where the Clone Stamp should copy from first");
            return null;
        }
        // A clone stroke that is not aligned takes the place it starts from as the new source, so the offset is
        // worked out again each time; an aligned one keeps copying from where the last stroke did.
        var offset = new SKPointI((int)Math.Round(source.X - stroke[0].X), (int)Math.Round(source.Y - stroke[0].Y));
        _cloneOffset = _options.Brush.CloneAligned ? _cloneOffset ?? offset : offset;
        return _canvas.Brush with { CloneFrom = _cloneOffset };
    }

    /// <summary>Alt-clicking with the Clone Stamp: where the next stroke copies from.</summary>
    private void CloneSourceChosen(SKPoint point)
    {
        _cloneSource = point;
        // A new source starts a new alignment, as the Mac build's does.
        _cloneOffset = null;
        Say(L10n.F("Clone stamp copying from {0:0},{1:0} — drag on the canvas", point.X, point.Y));
    }

    /// <summary>
    /// The transform handles follow the selected layer: the tool shows its box while nothing is being
    /// dragged, and nothing at all when there is no layer with pixels to transform.
    /// </summary>
    private void ShowTransformBox()
    {
        if (_tool != Tool.Move || _document is not { } document)
        {
            _canvas.TransformBox = null;
            _canvas.DistortEnabled = false;
            return;
        }
        // One box around everything the transform moves, which for one layer is its own.
        _canvas.TransformBox = TransformEdits.GroupBox(document, SelectedLayers);
        // A corner can be dragged on its own whenever the box stands for something with pixels: a box around
        // several layers resamples each of them into the shape the box is dragged into.
        _canvas.DistortEnabled = _canvas.TransformBox is not null;
    }

    /// <summary>
    /// The pointer took hold of the box: the whole drag is one step in the history, and the layers it moves
    /// are remembered as they are now, so every step of the drag is measured from where it began.
    /// </summary>
    private void TransformStarted()
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (TransformEdits.GroupBox(document, SelectedLayers) is not { } box) return;
        _transforming = id;
        _transformBox = box;
        _transformOriginals = TransformEdits.GroupMembers(document, SelectedLayers)
            .ToDictionary(layer => layer.ID, layer => layer.Transform);
        _transformMaskOriginals = TransformEdits.GroupMembers(document, SelectedLayers)
            .Where(layer => layer.Mask is not null).ToDictionary(layer => layer.ID, layer => layer.Mask!.Placement);
        _history.Begin(_transformOriginals.Count > 1 ? "Transform Layers" : "Transform", document, id);
    }

    /// <summary>
    /// A drag under way: the box the handles worked out goes on the layer, snapped to whatever is nearby,
    /// and the lines it snapped to are drawn along.
    /// </summary>
    private void TransformChanged(LayerTransform draft)
    {
        if (_document is not { } document || _transforming is null) return;
        if (_transformBox is not { } from) return;
        var tolerance = TransformSnap.Distance / Math.Max(_canvas.Zoom, 0.0001);
        // The grid is only a target while it is being shown: snapping to lines that are not there would be
        // a surprise. It is the one target that carries a value rather than a place.
        var placed = TransformEdits.Snap(document, draft, _transformOriginals.Keys, tolerance,
            out var lineX, out var lineY, _snappingOn ? _snapTo : SnapTo.None, _gridVisible ? _grid : null);
        _canvas.SnapLines = (lineX, lineY);
        // Every layer is carried along by the box's own move, so several keep the shape they had.
        TransformEdits.Carry(document, _transformOriginals, from, placed, _transformMaskOriginals);
        _canvas.TransformBox = placed;
        Refresh();
    }

    private void TransformFinished()
    {
        if (_document is not { } document || _transforming is not { } id) return;
        _transforming = null;
        _transformBox = null;
        _transformOriginals.Clear();
        _transformMaskOriginals.Clear();
        _canvas.SnapLines = (null, null);
        _history.End(document, id);
        ShowTransformBox();
        Refresh();
    }

    /// <summary>Repaints the canvas and says where the history stands.</summary>
    private void Refresh()
    {
        _canvas.InvalidateVisual();
        UpdateLayerMenu();
        if (_transforming is null) ShowTransformBox();
        var undo = _history.CanUndo ? L10n.F("Undo {0}", L10n.T(_history.UndoName ?? "")) : "";
        var redo = _history.CanRedo ? L10n.F("Redo {0}", L10n.T(_history.RedoName ?? "")) : "";
        var edited = _history.IsModified ? L10n.T("edited") : "";
        Say(string.Join("    ", new[] { undo, redo, edited }.Where(part => part.Length > 0)));
    }

    private void ShowLayers(CanvasDocument document)
    {
        var rows = new List<ListBoxItem>();
        _rows.Clear();
        // Top of the stack first, as the Mac build's panel lists it.
        foreach (var entry in document.HierarchyEntries(topFirst: true))
        {
            var record = entry.Layer;
            var notes = new List<string>();
            if (!entry.Visible) notes.Add(L10n.T("hidden"));
            if (record.BlendMode is { } blend && blend != LayerBlendMode.Normal) notes.Add(L10n.T(Spell(blend)));
            if (record.Opacity is { } opacity and < 1) notes.Add($"{opacity:0.##}");
            if (record.MaskFile is not null) notes.Add(L10n.T("mask"));
            if (record.MaskSourceID is not null) notes.Add(L10n.T("clipped"));
            rows.Add(new ListBoxItem
            {
                // The row carries the layer it stands for, so a multi-selection can be read back.
                Tag = record.ID,
                Content = new TextBlock
                {
                    Text = new string(' ', entry.Depth * 3) + record.Name +
                        (notes.Count > 0 ? "  ·  " + string.Join(", ", notes) : ""),
                    Foreground = Ink,
                },
            });
            _rows.Add(record.ID);
        }
        var selected = _layers.SelectedIndex;
        _layers.ItemsSource = rows;
        // A row is the layer an edit acts on, so the top of the stack starts selected.
        _layers.SelectedIndex = selected >= 0 && selected < rows.Count ? selected : rows.Count > 0 ? 0 : -1;
    }

    /// <summary>
    /// Imports an image as a layer, or as the whole project when none is open. Everything the importer reads
    /// is offered, HEIC and camera RAW included: the file picker lists exactly what it can read.
    /// </summary>
    private async Task ImportImage()
    {
        try
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L10n.T("Import an image"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType(L10n.T("Images")) { Patterns = [.. ImageImporter.Extensions.Select(e => "*" + e)] },
                    FilePickerFileTypes.All,
                ],
            });
            if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return;
            if (!ImageImporter.LooksImportable(path))
            {
                Say(L10n.F("{0} files are not read; {1} are", Path.GetExtension(path), string.Join(", ", ImageImporter.Extensions)));
                return;
            }
            ImportPath(path);
        }
        catch (Exception error)
        {
            AppDiagnostics.Report("Import image", error);
            Say(L10n.F("Could not import that image: {0}", OperationErrors.Describe(error)));
        }
    }

    /// <summary>The canvas an SVG should be drawn to fit, if one is open.</summary>
    private SKSizeI? Fitting() => _document is { } document ? new SKSizeI(document.Width, document.Height) : null;

    private void Save()
    {
        if (_document is null) return;
        if (_projectPath is null)
        {
            SaveAs();
            return;
        }
        WriteTo(_open, _projectPath);
    }

    private async void SaveAs()
    {
        FinishActiveEdits();
        if (_document is not { } document) return;
        var tab = _open;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L10n.T("Save the project"),
                SuggestedFileName = tab.Path is { } known ? Path.GetFileName(known) : "Untitled.comp",
                DefaultExtension = "comp",
            });
            if (file?.TryGetLocalPath() is not { } path) return;
            // The picker may stay open while another tab is selected or the original one is closed.
            if (!_tabs.Contains(tab) || !ReferenceEquals(tab.Document, document)) return;
            WriteTo(tab, path);
        }
        catch (Exception error)
        {
            AppDiagnostics.Report("Save project", error);
            Say(L10n.F("Could not save: {0}", OperationErrors.Describe(error)));
        }
    }

    private bool WriteTo(Tab tab, string path)
    {
        if (tab.Document is not { } document) return false;
        try
        {
            if (ReferenceEquals(tab, _open)) FinishActiveEdits();
            if (tab.History.HasPendingEdit)
                throw new InvalidOperationException(L10n.T("Finish the current edit before saving."));
            // A project is a folder on Windows. Never replace an ordinary file chosen in the picker.
            if (File.Exists(path)) throw new IOException(L10n.T("A project is a folder, and that path is a file."));
            // The snapshot shares the document's pixels and only reads them, so it is not disposed here.
            ProjectStore.Save(ProjectSnapshot.FromDocument(document), path);
        }
        catch (Exception error)
        {
            AppDiagnostics.Report("Save project", error);
            Say(L10n.F("Could not save: {0}", OperationErrors.Describe(error)));
            return false;
        }

        // Only a completed write changes the destination or the saved revision. A failed Save As keeps
        // pointing at the original project, with the edits and its existing watch intact.
        tab.Path = path;
        tab.History.MarkSaved();
        tab.Watch = ProjectWatch.For(path);
        if (ReferenceEquals(tab, _open))
        {
            WatchProject();
            Refresh();
        }
        NoteRecent(path);
        RefreshTabs();
        Say(L10n.F("Saved {0}", path));
        return true;
    }

    /// <summary>Checks save failure and Save As bookkeeping without displaying a native file picker.</summary>
    internal string SaveSelfCheck()
    {
        if (_tabs.Count != 1 || _document is not null)
            throw new InvalidOperationException("save checks require an empty window");
        var root = Path.Combine(Path.GetTempPath(), "Compositor-save-" + Guid.NewGuid().ToString("N"), "中文路径");
        Directory.CreateDirectory(root);
        var recentBefore = File.Exists(_recent.Path) ? File.ReadAllBytes(_recent.Path) : null;
        var report = new List<string>();
        try
        {
            var original = Path.Combine(root, "原工程.comp");
            using (var seed = new CanvasDocument(Guid.NewGuid(), 32, 24))
            {
                var pixels = new SKBitmap(Bitmaps.ColorInfo(32, 24));
                pixels.Erase(SKColors.CornflowerBlue);
                seed.Layers.Add(new ImageLayer(Guid.NewGuid(), ImportedImage.Create(pixels, "中文图层"),
                    new LayerTransform(0, 0, 32, 24), "中文图层"));
                ProjectStore.Save(ProjectSnapshot.FromDocument(seed), original);
            }
            Open(original);
            var tab = _open;
            var document = tab.Document ?? throw new InvalidOperationException("saved fixture did not open");
            var originalBytes = Contents(original);
            Rename("已编辑的中文图层");
            var revision = tab.History.CurrentRevision;
            var watch = tab.Watch;
            if (!tab.History.IsModified) throw new InvalidOperationException("save fixture was not modified");

            var occupied = Path.Combine(root, "已有文件.comp");
            var sentinel = new byte[] { 0, 1, 127, 255 };
            File.WriteAllBytes(occupied, sentinel);
            CheckFailure(occupied);
            if (!File.ReadAllBytes(occupied).SequenceEqual(sentinel))
                throw new InvalidOperationException("failed save overwrote an existing file");
            report.Add("occupied destination: failure reported; path, dirty revision, watch and existing bytes retained");

            var blockedParent = Path.Combine(root, "被文件占用的父目录");
            File.WriteAllBytes(blockedParent, sentinel);
            CheckFailure(Path.Combine(blockedParent, "无法保存.comp"));
            if (!File.ReadAllBytes(blockedParent).SequenceEqual(sentinel))
                throw new InvalidOperationException("failed save overwrote an occupied parent");
            report.Add("occupied parent: failure reported; original project and unsaved edits retained");

            var destination = Path.Combine(root, "新目录", "另存工程.comp");
            if (!WriteTo(tab, destination)) throw new InvalidOperationException("valid Save As failed");
            if (tab.Path != destination || tab.History.IsModified || tab.Watch is null
                || ReferenceEquals(tab.Watch, watch) || tab.Watch.Changed())
                throw new InvalidOperationException("successful Save As did not move the path, revision and watch together");
            if (_recent.All().FirstOrDefault() != destination)
                throw new InvalidOperationException("successful Save As did not update recent projects");
            CheckStored(destination, "已编辑的中文图层");
            CheckUnchanged(original, originalBytes);
            report.Add("Save As: Chinese destination reopened correctly; new path and watch active; original package unchanged");

            Rename("第二次修改");
            Save();
            if (tab.History.IsModified || tab.Path != destination)
                throw new InvalidOperationException("Save did not retain the new destination or clear the dirty revision");
            CheckStored(destination, "第二次修改");
            CheckUnchanged(original, originalBytes);
            report.Add("Save after Save As: new destination updated; original package unchanged");

            Rename("后台工程修改");
            _open = TabForNew();
            var frontDocument = new CanvasDocument(Guid.NewGuid(), 10, 12);
            _open.Document = frontDocument;
            var front = _open;
            front.History.Begin("Resize", frontDocument, null);
            frontDocument.Width = 11;
            front.History.End(frontDocument, null);
            Show(front);
            var backgroundDestination = Path.Combine(root, "后台另存.comp");
            if (!WriteTo(tab, backgroundDestination) || !ReferenceEquals(_open, front)
                || front.Path is not null || !front.History.IsModified || front.Watch is not null)
                throw new InvalidOperationException("saving a background tab changed the active tab's save state");
            CheckStored(backgroundDestination, "后台工程修改");
            report.Add("tab changed during Save As: only the original tab's save state changed");
            return string.Join(Environment.NewLine, report);

            void Rename(string name)
            {
                tab.History.Begin("Rename layer", document, document.Layers[0].ID);
                document.Layers[0].Name = name;
                tab.History.End(document, document.Layers[0].ID);
            }

            void CheckFailure(string path)
            {
                var recent = File.ReadAllBytes(_recent.Path);
                if (WriteTo(tab, path)) throw new InvalidOperationException("unwritable destination was reported as saved");
                if (tab.Path != original || !tab.History.IsModified || tab.History.CurrentRevision != revision
                    || !ReferenceEquals(tab.Watch, watch) || !ReferenceEquals(tab.Document, document)
                    || document.Layers[0].Name != "已编辑的中文图层")
                    throw new InvalidOperationException("failed save changed the original project or its unsaved state");
                if (!File.ReadAllBytes(_recent.Path).SequenceEqual(recent))
                    throw new InvalidOperationException("failed save changed recent projects");
                CheckUnchanged(original, originalBytes);
            }
        }
        finally
        {
            _watchTimer?.Stop();
            _canvas.Document = null;
            foreach (var tab in _tabs) tab.Document?.Dispose();
            _tabs.Clear();
            _open = new Tab();
            _tabs.Add(_open);
            if (recentBefore is null) File.Delete(_recent.Path);
            else File.WriteAllBytes(_recent.Path, recentBefore);
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }

        static Dictionary<string, byte[]> Contents(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(path, file), File.ReadAllBytes);

        static void CheckUnchanged(string path, Dictionary<string, byte[]> expected)
        {
            var actual = Contents(path);
            if (actual.Count != expected.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out var bytes)
                || !bytes.SequenceEqual(pair.Value)))
                throw new InvalidOperationException("the original package changed during Save As");
        }

        static void CheckStored(string path, string layerName)
        {
            using var saved = ProjectStore.Load(path);
            if (saved.Manifest.Layers.Count != 1 || saved.Manifest.Layers[0].Name != layerName
                || saved.Images.Values.Single().Image.GetPixel(0, 0) != SKColors.CornflowerBlue)
                throw new InvalidOperationException("saved document did not round trip its Chinese name and pixels");
        }
    }

    private async void ExportPng()
    {
        FinishActiveEdits();
        if (_document is not { } document)
        {
            Say("Nothing to export yet.");
            return;
        }
        var tab = _open;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L10n.T("Export PNG"),
                SuggestedFileName = "Compositor export.png",
                DefaultExtension = "png",
            });
            if (file?.TryGetLocalPath() is not { } path) return;
            if (!_tabs.Contains(tab) || !ReferenceEquals(tab.Document, document))
            {
                Say("The project changed while exporting. Export it again.");
                return;
            }
            if (ReferenceEquals(tab, _open)) FinishActiveEdits();
            // A band of tiles at a time, so the canvas size does not have to fit in one buffer.
            TiledPngWriter.Write(document, path);
            Say(L10n.F("Exported {0}", path));
        }
        catch (Exception error)
        {
            AppDiagnostics.Report("Export image", error);
            Say(L10n.F("Could not export: {0}", OperationErrors.Describe(error)));
        }
    }

    /// <summary>
    /// File ▸ Export JPEG: the flattened document written at a quality that is asked for. A JPEG has to be
    /// made whole, so a canvas too big to hold is refused rather than quietly written wrong.
    /// </summary>
    private async Task ExportJpeg()
    {
        FinishActiveEdits();
        if (_document is not { } document)
        {
            Say("Nothing to export yet.");
            return;
        }
        var tab = _open;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L10n.T("Export JPEG"),
                SuggestedFileName = "Compositor export.jpg",
                DefaultExtension = "jpg",
            });
            if (file?.TryGetLocalPath() is not { } path) return;
            if (!_tabs.Contains(tab) || !ReferenceEquals(tab.Document, document))
            {
                Say("The project changed while exporting. Export it again.");
                return;
            }
            if (await QualityDialog.Ask(this) is not { } quality) return;
            if (!_tabs.Contains(tab) || !ReferenceEquals(tab.Document, document))
            {
                Say("The project changed while exporting. Export it again.");
                return;
            }
            if (ReferenceEquals(tab, _open)) FinishActiveEdits();
            if (!ImageWriter.Write(document, path, quality))
            {
                Say("That canvas is too large to write as one JPEG.");
                return;
            }
            Say(L10n.F("Exported {0}", path));
        }
        catch (Exception error)
        {
            AppDiagnostics.Report("Export image", error);
            Say(L10n.F("Could not export: {0}", OperationErrors.Describe(error)));
        }
    }

    private static string Spell<T>(T value) where T : struct, Enum =>
        System.Text.Json.JsonSerializer.Serialize(value, ManifestJson.Options).Trim('"');

    private string _message = "";

    /// <summary>
    /// Puts a message or the tool's own line on the right of the status bar, and refreshes what the picture is
    /// on the left: an empty message leaves the last one standing.
    /// </summary>
    private void Say(string message = "")
    {
        if (message.Length > 0) _message = L10n.T(message);
        _status.Text = _message;
        var limit = _canvas.ZoomedOutAsFarAsItGoes ? L10n.T("    as far out as one screenful can be drawn") : "";
        _statusInfo.Text = _document is not { } document
            ? L10n.T("Ready when you are")
            : L10n.F("{0:0}%    {1} × {2} px    sRGB · Transparent{3}", _canvas.Zoom * 100, document.Width, document.Height, limit);
    }
}
