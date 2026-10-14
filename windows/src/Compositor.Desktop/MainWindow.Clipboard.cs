using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Compositor.Core.Document;
using Compositor.Core.IO;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Desktop;

public sealed partial class MainWindow
{
    private ISystemImageClipboard? _systemImageClipboard;
    private Task _clipboardPublication = Task.CompletedTask;
    private Guid? _clipboardToken;
    private long _pasteRequest;

    private void InvalidatePendingClipboardPaste() => _pasteRequest++;

    // Headless checks deliberately keep their original synchronous, internal clipboard unless they inject
    // a fake. They must never read or overwrite the developer's actual clipboard.
    private ISystemImageClipboard? ImageClipboard => _systemImageClipboard ??=
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
            ? new SystemImageClipboard(Clipboard) : null;

    private bool UsesSystemImageClipboard => ImageClipboard is not null;

    /// <summary>Called after Adopt and its normal copy/cut status. Capture pixels before the first await.</summary>
    private void PublishCopiedImage(ClipboardImage copied, bool cut = false)
    {
        if (ImageClipboard is not { } clipboard) return;
        if (_recoveryPending || _windowClosePending || _tabClosePending.Count > 0)
        {
            Say("The system clipboard copy was canceled while a confirmation is open. The internal copy is retained.");
            return;
        }
        var token = Guid.NewGuid();
        _clipboardToken = token;
        try
        {
            // A subsequent Adopt can dispose copied. The queued write owns encoded bytes, not that bitmap.
            var png = SystemImageClipboard.EncodePng(copied.Image);
            var previous = _clipboardPublication;
            _clipboardPublication = Publish(clipboard, png, token, cut, previous);
        }
        catch (Exception error) { ClipboardWriteFailed(error, cut); }
    }

    private async Task Publish(ISystemImageClipboard clipboard, byte[] png, Guid token, bool cut, Task previous)
    {
        try
        {
            // Serial order plus a generation check prevents a slow old copy from replacing a later one.
            await previous;
            if (_clipboardToken != token) return;
            if (_recoveryPending || _windowClosePending || _tabClosePending.Count > 0)
            {
                Say("The system clipboard copy was canceled while a confirmation is open. The internal copy is retained.");
                return;
            }
            await clipboard.PublishAsync(png, token);
        }
        catch (Exception error)
        {
            AppDiagnostics.Report("Copy image to system clipboard", error);
            if (_clipboardToken == token) ClipboardWriteFailed(error, cut, recorded: true);
        }
    }

    private void ClipboardWriteFailed(Exception error, bool cut, bool recorded = false)
    {
        if (!recorded) AppDiagnostics.Report("Copy image to system clipboard", error);
        Say(L10n.F(cut
            ? "Could not copy to the system clipboard. The image is kept inside Compositor, and the cut can be undone. {0}"
            : "Could not copy to the system clipboard. The image is kept inside Compositor. {0}",
            OperationErrors.Describe(error)));
    }

    /// <summary>Read current system content, then insert only if the original editing context still exists.</summary>
    private async Task PasteFromSystemAsync()
    {
        if (ImageClipboard is not { } clipboard) return;
        if (Typing() || _canvas.HasPendingInteraction || _preview is not null || _history.HasPendingEdit
            || _windowClosePending || _recoveryPending || _tabClosePending.Count > 0)
        {
            Say("Finish text editing or close the open confirmation before pasting an image.");
            return;
        }
        var tab = _open;
        var document = tab.Document;
        var selected = Selected;
        var revision = tab.History.CurrentRevision;
        var generation = tab.History.EditGeneration;
        var request = ++_pasteRequest;
        bool StillCurrent() => request == _pasteRequest && _tabs.Contains(tab) && ReferenceEquals(_open, tab)
            && ReferenceEquals(tab.Document, document) && tab.History.CurrentRevision == revision
            && tab.History.EditGeneration == generation && !tab.History.HasPendingEdit
            && !_canvas.HasPendingInteraction && _preview is null
            && IsVisible && !Typing() && !_windowClosePending && !_recoveryPending && _tabClosePending.Count == 0;
        try
        {
            // Ctrl+C followed immediately by Ctrl+V must read the completed copy, not the previous owner.
            Task publication;
            do
            {
                publication = _clipboardPublication;
                await publication;
                if (!StillCurrent()) { Say("Image paste was canceled because the editing context changed."); return; }
            } while (!ReferenceEquals(publication, _clipboardPublication));
            if (!StillCurrent()) { Say("Image paste was canceled because the editing context changed."); return; }
            CheckClipboardCapacity(document);
            using var copied = await clipboard.ReadAsync(ClipboardPixelBudget(document));
            if (!StillCurrent()) { Say("Image paste was canceled because the editing context changed."); return; }
            if (copied is null)
            {
                Say("The system clipboard contains no image. The previous internal copy was not pasted.");
                return;
            }
            CheckClipboardCapacity(document);
            SystemImageClipboard.CheckSize(copied.Image.Width, copied.Image.Height, ClipboardPixelBudget(document));
            var width = copied.Image.Width;
            var height = copied.Image.Height;
            if (document is null)
            {
                var pixels = copied.Take();
                ImportedImage? image = null;
                try
                {
                    image = ImportedImage.Create(pixels, "Pasted Layer");
                    _document = ImageImporter.NewDocument(image);
                }
                catch
                {
                    if (image is not null) image.Dispose();
                    else pixels.Dispose();
                    throw;
                }
                _projectPath = null;
                _history.Reset();
                Show(tab);
                Say(L10n.F("Pasted {0} x {1} into a new unsaved project", width, height));
                return;
            }
            var region = copied.Token is { } token && token == _clipboardToken && _clipboard is { } held
                && held.Region.Width == width && held.Region.Height == height
                ? held.Region
                : SKRectI.Create((document.Width - width) / 2, (document.Height - height) / 2, width, height);
            var bitmap = copied.Take();
            Guid? made = null;
            _history.Begin("Paste", document, selected);
            try
            {
                made = LayerPlacement.AddImage(document, bitmap, region, "Pasted Layer", selected);
                if (made is null) throw new InvalidOperationException(L10n.T("This project already has the maximum number of layers."));
            }
            finally
            {
                if (made is null) bitmap.Dispose();
                _history.End(document, made ?? selected);
            }
            Reselect(made);
            Say(L10n.F("Pasted {0} x {1}", width, height));
        }
        catch (Exception error)
        {
            AppDiagnostics.Report("Paste image from system clipboard", error);
            Say(L10n.F("Could not paste the system clipboard image: {0}", OperationErrors.Describe(error)));
        }
    }

    private static void CheckClipboardCapacity(CanvasDocument? document)
    {
        if (document?.Layers.Count >= LayerPlacement.MaxLayers)
            throw new InvalidOperationException(L10n.T("This project already has the maximum number of layers."));
    }

    private static int ClipboardPixelBudget(CanvasDocument? document)
    {
        long used = 0;
        if (document is not null)
        {
            foreach (var layer in document.Layers)
            {
                if (layer.Asset is { } asset) used += (long)asset.Width * asset.Height;
                if (layer.Mask is { } mask) used += (long)mask.Asset.Width * mask.Asset.Height;
            }
        }
        return (int)Math.Max(0, DocumentLimits.DocumentPixelBudget - used);
    }

    /// <summary>Use detached PNGs and a controllable fake; never touch the host clipboard in this check.</summary>
    internal static async Task<string> ClipboardSelfCheck()
    {
        var windows = new List<MainWindow>();
        var fakes = new List<FakeImageClipboard>();
        var passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            passed++;
        }
        (MainWindow Window, FakeImageClipboard Clipboard) Fixture()
        {
            var fake = new FakeImageClipboard();
            var window = new MainWindow { _systemImageClipboard = fake };
            windows.Add(window);
            fakes.Add(fake);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            return (window, fake);
        }
        static byte[] Png(SKColor color, int width = 4, int height = 3)
        {
            using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            bitmap.Erase(color);
            return SystemImageClipboard.EncodePng(bitmap);
        }
        static void CopyColor(MainWindow window, SKColor color)
        {
            var pixels = new SKBitmap(4, 3, SKColorType.Bgra8888, SKAlphaType.Premul);
            pixels.Erase(color);
            var copied = new ClipboardImage(pixels, SKRectI.Create(5, 6, 4, 3));
            window.Adopt(copied);
            window.PublishCopiedImage(copied);
        }
        try
        {
            var (window, fake) = Fixture();
            var red = new SKColor(255, 0, 0, 128);
            fake.SetExternal(Png(red));
            await window.PasteFromSystemAsync();
            var document = window._document ?? throw new InvalidOperationException("Pasting into an empty window did not create a document.");
            Check(document.Width == 4 && document.Height == 3 && document.Layers.Count == 1 && window._projectPath is null,
                "Clipboard image did not create one unsaved project with its pixel dimensions.");
            Check(document.Layers[0].Asset!.Image.GetPixel(0, 0) == red, "PNG clipboard decode lost alpha or color.");
            SelectionEdits.SelectAll(document);
            window.Copy();
            await window._clipboardPublication;
            Check(fake.PublishCalls == 1 && fake.Marker == window._clipboardToken, "Copy did not publish the selected pixels to the clipboard boundary.");
            window.CopyMerged();
            await window._clipboardPublication;
            Check(fake.PublishCalls == 2, "Copy Merged did not publish its result.");

            CopyColor(window, SKColors.Green);
            await window._clipboardPublication;
            await window.PasteFromSystemAsync();
            var own = document.Layers.First(layer => layer.ID == window.Selected);
            Check(own.Transform.X == 5 && own.Transform.Y == 6 && own.Asset!.Image.GetPixel(0, 0) == SKColors.Green,
                "The system marker did not retain this app's copied selection position.");
            fake.SetExternal(Png(SKColors.Blue, 2, 2));
            await window.PasteFromSystemAsync();
            var external = document.Layers.First(layer => layer.ID == window.Selected);
            Check(external.Asset!.Width == 2 && external.Asset.Height == 2 && external.Asset.Image.GetPixel(0, 0) == SKColors.Blue
                && external.Transform.X == 1, "External clipboard content did not replace the earlier internal copy.");
            var layers = document.Layers.Count;
            fake.SetExternal(null);
            await window.PasteFromSystemAsync();
            Check(document.Layers.Count == layers && window._message == L10n.T("The system clipboard contains no image. The previous internal copy was not pasted."),
                "An empty/text clipboard silently pasted the previous internal image.");
            fake.ReadFailure = new IOException("clipboard locked for read");
            await window.PasteFromSystemAsync();
            Check(document.Layers.Count == layers && window._message.Contains("clipboard locked for read", StringComparison.Ordinal),
                "A clipboard read failure mutated the document or was not reported.");
            fake.ReadFailure = null;
            fake.SetExternal([1, 2, 3]);
            await window.PasteFromSystemAsync();
            Check(document.Layers.Count == layers, "Malformed clipboard bytes added a layer.");

            fake.PublishFailure = new IOException("clipboard locked for write");
            window.Reselect(document.Layers[0].ID);
            SelectionEdits.SelectAll(document);
            var cutLayer = document.Layers[0].ID;
            window.Cut();
            await window._clipboardPublication;
            Check(window._clipboard is not null && window._message.Contains("clipboard locked for write", StringComparison.Ordinal),
                "A failed system copy discarded the internal cut data or its failure message.");
            window.Undo();
            Check(document.Layers.First(layer => layer.ID == cutLayer).Asset!.Image.GetPixel(0, 0) == red,
                "A failed clipboard publication made the cut impossible to undo.");
            fake.PublishFailure = null;

            // The first write is held while two more copies replace the internal bitmap. A paste must wait
            // for the newest queued write, and no queued write may use a bitmap that Adopt has disposed.
            var writeGate = fake.BlockNextPublish();
            CopyColor(window, SKColors.Red);
            CopyColor(window, SKColors.Green);
            CopyColor(window, SKColors.Blue);
            var immediatePaste = window.PasteFromSystemAsync();
            Check(!immediatePaste.IsCompleted, "Immediate Paste did not wait for a pending Copy.");
            writeGate.SetResult();
            await immediatePaste;
            Check(fake.Marker == window._clipboardToken && document.Layers.First(layer => layer.ID == window.Selected)
                    .Asset!.Image.GetPixel(0, 0) == SKColors.Blue,
                "An old queued Copy overwrote or was pasted instead of the newest Copy.");

            var originalTab = window._open;
            layers = document.Layers.Count;
            var readGate = fake.BlockNextRead();
            var delayedPaste = window.PasteFromSystemAsync();
            var other = new Tab { Document = LayerPlacement.NewDocument(8, 8) };
            window._tabs.Add(other);
            window.Bring(other);
            readGate.SetResult();
            await delayedPaste;
            Check(document.Layers.Count == layers && other.Document!.Layers.Count == 1,
                "A delayed clipboard read wrote into a different tab.");
            window.Bring(originalTab);

            var readsBeforeEdit = fake.ReadCalls;
            window._history.Begin("Pending edit", document, window.Selected);
            await window.PasteFromSystemAsync();
            Check(fake.ReadCalls == readsBeforeEdit && document.Layers.Count == layers,
                "Paste started a clipboard read during a pending edit.");
            window._history.End(document, window.Selected);

            readGate = fake.BlockNextRead();
            delayedPaste = window.PasteFromSystemAsync();
            var revision = window._history.CurrentRevision;
            window._history.Begin("No-op edit", document, window.Selected);
            window._history.End(document, window.Selected);
            Check(window._history.CurrentRevision == revision, "No-op fixture unexpectedly changed its revision.");
            readGate.SetResult();
            await delayedPaste;
            Check(document.Layers.Count == layers, "A no-op edit did not invalidate an older paste.");

            readGate = fake.BlockNextRead();
            delayedPaste = window.PasteFromSystemAsync();
            window._history.Begin("Pending transform", document, window.Selected);
            readGate.SetResult();
            await delayedPaste;
            Check(document.Layers.Count == layers, "A clipboard read was inserted into a pending transform.");
            window._history.End(document, window.Selected);

            window._preview = FilterPreview.Begin(document, document.Layers[0].ID);
            readsBeforeEdit = fake.ReadCalls;
            await window.PasteFromSystemAsync();
            Check(fake.ReadCalls == readsBeforeEdit, "Paste started during a filter preview.");
            window.StopPreview();
            readGate = fake.BlockNextRead();
            delayedPaste = window.PasteFromSystemAsync();
            window._preview = FilterPreview.Begin(document, document.Layers[0].ID);
            readGate.SetResult();
            await delayedPaste;
            Check(document.Layers.Count == layers, "An in-flight paste changed a document with a filter preview.");
            window.StopPreview();

            var (brushing, brushClipboard) = Fixture();
            var brushDocument = LayerPlacement.NewDocument(128, 96)
                ?? throw new InvalidOperationException("Could not create the brush fixture.");
            brushing._document = brushDocument;
            brushing._history.Reset();
            brushing.Show(brushing._open);
            brushing.SetTool(Tool.Brush);
            brushing.UpdateLayout();
            brushing._canvas.Fit();
            brushClipboard.SetExternal(Png(SKColors.Blue));
            var brushGate = brushClipboard.BlockNextRead();
            var brushPaste = brushing.PasteFromSystemAsync();
            Point BrushAt(float x, float y) => brushing._canvas.TranslatePoint(
                brushing._canvas.InView(new SKPoint(x, y)), brushing)!.Value;
            brushing.MouseDown(BrushAt(40, 40), MouseButton.Left, RawInputModifiers.LeftMouseButton);
            brushing.MouseMove(BrushAt(60, 50), RawInputModifiers.LeftMouseButton);
            Check(brushing._canvas.HasPendingInteraction && !brushing._history.HasPendingEdit,
                "The brush fixture did not exercise a gesture before its history transaction starts.");
            brushGate.SetResult();
            await brushPaste;
            Check(brushDocument.Layers.Count == 1, "An in-flight paste interrupted an active brush gesture.");
            readsBeforeEdit = brushClipboard.ReadCalls;
            await brushing.PasteFromSystemAsync();
            Check(brushClipboard.ReadCalls == readsBeforeEdit, "Paste read the clipboard while the brush was held.");
            brushing.MouseUp(BrushAt(60, 50), MouseButton.Left);

            readGate = fake.BlockNextRead();
            delayedPaste = window.PasteFromSystemAsync();
            window.SetTool(Tool.Type);
            window.TypeHere(new SKPoint(1, 1));
            readGate.SetResult();
            await delayedPaste;
            Check(document.Layers.Count == layers && window._text is not null,
                "A delayed paste changed a newly opened inline text session.");
            window.CancelText();
            window.SetTool(Tool.Pan);

            foreach (var recovery in new[] { false, true })
            {
                readGate = fake.BlockNextRead();
                delayedPaste = window.PasteFromSystemAsync();
                if (recovery) window._recoveryPending = true;
                else window._windowClosePending = true;
                readGate.SetResult();
                await delayedPaste;
                Check(document.Layers.Count == layers, "A delayed paste changed a document during a close/recovery confirmation.");
                window._recoveryPending = false;
                window._windowClosePending = false;
            }
            readGate = fake.BlockNextRead();
            delayedPaste = window.PasteFromSystemAsync();
            window._tabClosePending.Add(originalTab);
            readGate.SetResult();
            await delayedPaste;
            Check(document.Layers.Count == layers, "A delayed paste changed a document during its tab-close confirmation.");
            window._tabClosePending.Clear();

            // A newer Paste supersedes an older in-flight read instead of inserting twice out of order.
            readGate = fake.BlockNextRead();
            delayedPaste = window.PasteFromSystemAsync();
            await window.PasteFromSystemAsync();
            readGate.SetResult();
            await delayedPaste;
            Check(document.Layers.Count == layers + 1, "Out-of-order clipboard reads inserted an obsolete paste.");

            var beforeMask = ClipboardPixelBudget(document);
            var masked = document.Layers[0];
            masked.Mask = LayerMask.Solid(true);
            Check(ClipboardPixelBudget(document) == beforeMask - 1, "The clipboard pixel budget ignored mask pixels.");
            masked.Mask!.Dispose();
            masked.Mask = null;
            try
            {
                using var overBudget = SystemImageClipboard.DecodePng(Png(SKColors.Blue), null, 11);
                throw new InvalidOperationException("An over-budget clipboard image was decoded.");
            }
            catch (ImportException error) when (error.Error == ImportError.TooLarge) { passed++; }
            try
            {
                SystemImageClipboard.CheckSize(DocumentLimits.MaxSide + 1, 1, DocumentLimits.DocumentPixelBudget);
                throw new InvalidOperationException("An over-size clipboard image passed validation.");
            }
            catch (ImportException error) when (error.Error == ImportError.TooLarge) { passed++; }

            var countBeforeLimit = document.Layers.Count;
            var readsBeforeLimit = fake.ReadCalls;
            while (document.Layers.Count < LayerPlacement.MaxLayers)
                document.Layers.Add(new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 1, 1), "Limit fixture"));
            await window.PasteFromSystemAsync();
            Check(fake.ReadCalls == readsBeforeLimit && document.Layers.Count == LayerPlacement.MaxLayers,
                "A full layer stack still read/added a clipboard image.");
            document.Layers.RemoveRange(countBeforeLimit, document.Layers.Count - countBeforeLimit);

            var (closed, closedClipboard) = Fixture();
            closedClipboard.SetExternal(Png(SKColors.Blue));
            readGate = closedClipboard.BlockNextRead();
            delayedPaste = closed.PasteFromSystemAsync();
            closed.CloseForHeadlessCheck();
            readGate.SetResult();
            await delayedPaste;
            Check(closed._document is null, "A clipboard read created a document after its window closed.");
            var (tabClosed, tabClipboard) = Fixture();
            tabClipboard.SetExternal(Png(SKColors.Blue));
            readGate = tabClipboard.BlockNextRead();
            delayedPaste = tabClosed.PasteFromSystemAsync();
            var emptyTab = tabClosed._open;
            await tabClosed.CloseTab(emptyTab);
            readGate.SetResult();
            await delayedPaste;
            Check(!tabClosed._tabs.Contains(emptyTab) && tabClosed._document is null,
                "A delayed clipboard read replaced a tab that was closed while reading.");
            return $"system image clipboard: {passed} fake-boundary checks passed; native Windows inter-application behavior was not exercised";
        }
        finally
        {
            foreach (var fake in fakes) fake.ReleaseGates();
            foreach (var window in windows)
            {
                window._recoveryPending = false;
                window._windowClosePending = false;
                window._tabClosePending.Clear();
                if (window.IsVisible) window.CloseForHeadlessCheck();
            }
        }
    }

    private sealed class FakeImageClipboard : ISystemImageClipboard
    {
        private byte[]? _png;
        internal Guid? Marker { get; private set; }
        internal int PublishCalls { get; private set; }
        internal int ReadCalls { get; private set; }
        internal Exception? PublishFailure { get; set; }
        internal Exception? ReadFailure { get; set; }
        private TaskCompletionSource? _writeGate;
        private TaskCompletionSource? _readGate;
        private readonly List<TaskCompletionSource> _gates = [];
        internal TaskCompletionSource BlockNextPublish() => _writeGate = Gate();
        internal TaskCompletionSource BlockNextRead() => _readGate = Gate();
        private TaskCompletionSource Gate()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _gates.Add(gate);
            return gate;
        }
        internal void ReleaseGates()
        {
            foreach (var gate in _gates) gate.TrySetResult();
        }
        internal void SetExternal(byte[]? png)
        {
            _png = png;
            Marker = null;
        }
        public async Task PublishAsync(byte[] png, Guid token)
        {
            PublishCalls++;
            var gate = _writeGate;
            _writeGate = null;
            if (gate is not null) await gate.Task;
            if (PublishFailure is { } error) throw error;
            _png = png;
            Marker = token;
        }
        public async Task<ClipboardBitmap?> ReadAsync(int remainingPixels)
        {
            ReadCalls++;
            var gate = _readGate;
            _readGate = null;
            var png = _png;
            var marker = Marker;
            if (gate is not null) await gate.Task;
            if (ReadFailure is { } error) throw error;
            return png is null ? null : SystemImageClipboard.DecodePng(png, marker, remainingPixels);
        }
    }
}
