using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Compositor.Core.Document;
using Compositor.Core.IO;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Desktop;

public sealed partial class MainWindow
{
    private bool _externalFilesInitialized;
    private bool _externalFilesBusy;
    private bool _externalFilesClosed;
    private Task _externalDropTask = Task.CompletedTask;
    private ExternalFilesResult _lastExternalFilesResult = new(0, 0, 0, []);

    private sealed record ExternalFilesResult(int Opened, int Failed, int Skipped, IReadOnlyList<string> Details);

    private bool ExternalFilesBlocked => _externalFilesClosed || _windowClosePending || _windowCloseApproved || _recoveryPending
        || _tabClosePending.Count > 0;

    private string ExternalFilesBlockedMessage => _recoveryPending
        ? "Finish project recovery before opening external files."
        : "Finish the close confirmation before opening external files.";

    private void InitializeExternalFiles()
    {
        if (_externalFilesInitialized) return;
        _externalFilesInitialized = true;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, ExternalDragOver);
        AddHandler(DragDrop.DragOverEvent, ExternalDragOver);
        AddHandler(DragDrop.DropEvent, ExternalDrop);
        Closed += (_, _) => _externalFilesClosed = true;
    }

    private static bool ExternalTextTarget(object? source) => source is Visual visual
        && (visual is TextBox or NumericUpDown
            || visual.GetVisualAncestors().Any(parent => parent is TextBox or NumericUpDown));

    private void ExternalDragOver(object? sender, DragEventArgs args)
    {
        // Text/number inputs retain their own text-drop behavior, including transfers with several formats.
        if (ExternalTextTarget(args.Source) || !args.DataTransfer.Contains(DataFormat.File)) return;
        var accepted = !ExternalFilesBlocked && !_externalFilesBusy
            && args.DragEffects.HasFlag(DragDropEffects.Copy);
        try
        {
            accepted &= args.DataTransfer.TryGetFiles()?.Any(item =>
                item.TryGetLocalPath() is { } path && ExternalPathCandidate(path)) == true;
        }
        catch { accepted = false; }
        args.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        args.Handled = true;
    }

    private void ExternalDrop(object? sender, DragEventArgs args)
    {
        if (ExternalTextTarget(args.Source) || !args.DataTransfer.Contains(DataFormat.File)) return;
        args.Handled = true;
        if (ExternalFilesBlocked || _externalFilesBusy || !args.DragEffects.HasFlag(DragDropEffects.Copy))
        {
            args.DragEffects = DragDropEffects.None;
            Say(ExternalFilesBlocked ? ExternalFilesBlockedMessage
                : "Wait for the current file operation to finish, then try again.");
            return;
        }
        try
        {
            // Resolve storage objects synchronously while the native drag payload is still alive. Only
            // ordinary strings are kept across awaits; the drag system continues owning the transfer.
            var paths = args.DataTransfer.TryGetFiles()?.Select(item => item.TryGetLocalPath()
                ?? "content://remote/" + item.Name).ToArray() ?? [];
            args.DragEffects = DragDropEffects.Copy;
            _externalDropTask = ObserveExternalDropAsync(paths);
        }
        catch (Exception error)
        {
            args.DragEffects = DragDropEffects.None;
            AppDiagnostics.Report("Reading dropped files", error);
            Say(L10n.F("Could not read dropped files: {0}", OperationErrors.Describe(error)));
        }
    }

    private async Task ObserveExternalDropAsync(string[] paths)
    {
        try { await OpenPathsAsync(paths); }
        catch (Exception error)
        {
            AppDiagnostics.Report("Opening dropped files", error);
            Say(L10n.F("Could not read dropped files: {0}", OperationErrors.Describe(error)));
        }
    }

    /// <summary>Open a local batch in order. One bad file never discards an open project or stops later files.</summary>
    public async Task OpenPathsAsync(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Dispatcher.UIThread.VerifyAccess();
        if (ExternalFilesBlocked || _externalFilesBusy)
        {
            Say(ExternalFilesBlocked ? ExternalFilesBlockedMessage
                : "Wait for the current file operation to finish, then try again.");
            return;
        }
        var inputs = paths.ToArray();
        if (inputs.Length == 0) return;
        _externalFilesBusy = true;
        var opened = 0;
        var failed = 0;
        var skipped = 0;
        var details = new List<string>();
        var seen = new HashSet<string>(ExternalPathComparer);
        try
        {
            foreach (var input in inputs)
            {
                if (ExternalFilesBlocked)
                {
                    skipped += inputs.Length - opened - failed - skipped;
                    details.Add(L10n.T("Remaining files were skipped because the editor is closing."));
                    break;
                }
                try
                {
                    var path = NormalizeExternalPath(input, out var project);
                    if (!seen.Add(path)) { skipped++; continue; }
                    if (project)
                    {
                        var existing = _tabs.FirstOrDefault(tab => tab.Path is { } known
                            && ExternalPathComparer.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(known)), path));
                        if (existing is not null)
                        {
                            Bring(existing);
                            skipped++;
                            continue;
                        }
                        if (await OpenProjectPathAsync(path)) opened++;
                        else if (_lastProjectOpenError is { } reason)
                        {
                            failed++;
                            details.Add(L10n.F("{0}: {1}", ExternalDisplayName(path), reason));
                        }
                        else skipped++;
                    }
                    else
                    {
                        ImportPath(path);
                        opened++;
                    }
                }
                catch (Exception error)
                {
                    failed++;
                    AppDiagnostics.Report("Opening external file", error);
                    details.Add(L10n.F("{0}: {1}", ExternalDisplayName(input), OperationErrors.Describe(error)));
                }
            }
        }
        finally { _externalFilesBusy = false; }
        _lastExternalFilesResult = new(opened, failed, skipped, details);
        var summary = L10n.F("Files opened or imported: {0}; failed: {1}; skipped: {2}.", opened, failed, skipped);
        Say(details.Count == 0 ? summary : summary + " " + details[0]);
        ToolTip.SetTip(_status, string.Join(Environment.NewLine, new[] { summary }.Concat(details)));
    }

    private static StringComparer ExternalPathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string ExternalDisplayName(string input)
    {
        try { return Path.GetFileName(input.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); }
        catch { return input; }
    }

    private static bool ExternalPathCandidate(string input)
    {
        try { NormalizeExternalPath(input, out _); return true; }
        catch { return false; }
    }

    private static string NormalizeExternalPath(string input, out bool project)
    {
        project = false;
        if (string.IsNullOrWhiteSpace(input) || input.Contains("://", StringComparison.Ordinal)
            || input.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || input.StartsWith("content:", StringComparison.OrdinalIgnoreCase)
            || input.StartsWith("\\\\", StringComparison.Ordinal) || input.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidOperationException(L10n.T("Choose a local file or a local .comp project folder. URLs and remote items cannot be opened here."));
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
        if (Path.GetFileName(path).Equals(ProjectStore.ManifestName, StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(path);
            if (parent is null || !Path.GetExtension(parent).Equals(".comp", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(L10n.T("The manifest must be inside a .comp project folder."));
            path = parent;
        }
        // A save interrupted between directory moves can leave the target absent. Preserve this route
        // so the project opener can look for its recovery journal before reporting a missing project.
        if (Path.GetExtension(path).Equals(".comp", StringComparison.OrdinalIgnoreCase) && !File.Exists(path))
        {
            project = true;
            return path;
        }
        if (Directory.Exists(path))
        {
            throw new InvalidOperationException(L10n.T("Only .comp project folders can be opened as directories."));
        }
        if (!File.Exists(path)) throw new FileNotFoundException(L10n.T("The selected file or project folder no longer exists."), path);
        if (!ImageImporter.LooksImportable(path))
            throw new InvalidOperationException(L10n.T("This file type cannot be opened. Choose a supported image or a .comp project folder."));
        return path;
    }

    /// <summary>Exercise real routed file-transfer events and the same batch entry point used at startup.</summary>
    internal static async Task<string> ExternalFilesSelfCheck()
    {
        var temporary = Directory.CreateTempSubdirectory("Compositor-external-").FullName;
        var root = Path.Combine(temporary, "中文 空格目录");
        Directory.CreateDirectory(root);
        var window = new MainWindow();
        window.InitializeExternalFiles();
        var recentBefore = File.Exists(window._recent.Path) ? File.ReadAllBytes(window._recent.Path) : null;
        var report = new List<string>();
        try
        {
            var project = Path.Combine(root, "中文 工程.comp");
            using (var document = LayerPlacement.NewDocument(24, 18)!)
                ProjectStore.Save(ProjectSnapshot.FromDocument(document), project);
            var png = Path.Combine(root, "中文 图片.png");
            using (var pixels = new SKBitmap(Bitmaps.ColorInfo(8, 6)))
            {
                pixels.Erase(SKColors.CornflowerBlue);
                using var image = SKImage.FromBitmap(pixels);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100)
                    ?? throw new InvalidOperationException("The external-file PNG fixture could not be encoded.");
                using var file = File.Create(png);
                data.SaveTo(file);
            }
            var broken = Path.Combine(root, "坏 文件.png");
            File.WriteAllBytes(broken, [1, 2, 3]);
            var unsupported = Path.Combine(root, "不支持.txt");
            File.WriteAllText(unsupported, "not an image");
            var brokenProject = Path.Combine(root, "损坏 工程.comp");
            Directory.CreateDirectory(brokenProject);
            File.WriteAllText(Path.Combine(brokenProject, ProjectStore.ManifestName), "{}");
            var missingProject = Path.Combine(root, "缺失 工程.comp");
            var psd = Path.Combine(root, "中文 分层.psd");
            File.WriteAllBytes(psd, ImportFixture(1, layered: true));
            var sourceBytes = File.ReadAllBytes(png);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            using var transfer = new DataTransfer();
            foreach (var path in new[] { project, Path.Combine(project, ProjectStore.ManifestName), broken, png,
                Path.Combine(root, ".", Path.GetFileName(png)), unsupported, psd })
            {
                var item = Directory.Exists(path)
                    ? (IStorageItem?)await window.StorageProvider.TryGetFolderFromPathAsync(path)
                    : await window.StorageProvider.TryGetFileFromPathAsync(path);
                Require(item is not null, "The headless storage provider could not create a local file item.");
                transfer.Add(DataTransferItem.CreateFile(item!));
            }
            var over = Raise(DragDrop.DragOverEvent, transfer, DragDropEffects.Copy | DragDropEffects.Move);
            Require(over.Handled && over.DragEffects == DragDropEffects.Copy, "File drag did not negotiate Copy exclusively.");
            var drop = Raise(DragDrop.DropEvent, transfer, DragDropEffects.Copy | DragDropEffects.Move);
            await window._externalDropTask;
            Require(drop.Handled && drop.DragEffects == DragDropEffects.Copy, "File drop did not retain Copy semantics.");
            var result = window._lastExternalFilesResult;
            Require(result.Opened == 3 && result.Failed == 2 && result.Skipped == 2,
                $"Mixed drop result differs: {result.Opened} opened, {result.Failed} failed, {result.Skipped} skipped.");
            Require(window._tabs.Count == 2 && window._tabs[0].Path == project
                && window._tabs[0].Document!.Layers.Count == 2 && window._tabs[0].History.IsModified
                && window._open.Path is null && window._document!.Layers.Count == 2,
                "Mixed project/image/Photoshop drop changed the intended tab or layer semantics.");
            Require(File.ReadAllBytes(png).SequenceEqual(sourceBytes), "A Copy drop modified its source image.");
            report.Add("routed file drop: Chinese/space paths, project + manifest deduplication, PNG layer import, PSD new tab, partial failures and Copy semantics");

            var original = window._tabs[0];
            var revision = original.History.CurrentRevision;
            await window.OpenPathsAsync([Path.Combine(project, ProjectStore.ManifestName)]);
            Require(ReferenceEquals(window._open, original) && original.History.CurrentRevision == revision
                && original.History.IsModified && window._tabs.Count == 2,
                "Opening an already-open project replaced its unsaved edits.");
            report.Add("existing project: its current tab is activated with unsaved edits preserved");

            await window.OpenPathsAsync(["https://example.invalid/image.png", "content://provider/image.png", unsupported, broken,
                brokenProject, missingProject, Path.Combine(missingProject, ProjectStore.ManifestName)]);
            Require(window._lastExternalFilesResult is { Opened: 0, Failed: 6, Skipped: 1 }
                && ReferenceEquals(window._open, original) && original.History.CurrentRevision == revision,
                "Rejected external paths polluted the current document.");
            Require(window._lastExternalFilesResult.Details.Any(detail => detail.Contains("损坏 工程.comp", StringComparison.Ordinal)),
                "A failed project open lost its error in the batch summary.");
            Require(window._lastExternalFilesResult.Details.Any(detail => detail.Contains("缺失 工程.comp", StringComparison.Ordinal))
                && window._lastProjectOpenError is not null && !Directory.Exists(missingProject),
                "A missing project did not reach the recovery-aware opener or created an empty target.");
            report.Add("URLs, remote content, unsupported and damaged files/projects: rejected without changing the active document; project error retained");

            using var text = new DataTransfer();
            text.Add(DataTransferItem.CreateText("文本拖放"));
            var textEvent = Raise(DragDrop.DragOverEvent, text, DragDropEffects.Copy);
            Require(!textEvent.Handled, "The file handler swallowed a text transfer.");
            var input = new TextBox { Text = "文字输入", Width = 180, Height = 32 };
            var host = window.Content as Panel ?? throw new InvalidOperationException("The editor layout is not a panel.");
            host.Children.Add(input);
            try
            {
                Dispatcher.UIThread.RunJobs();
                object? routedInputSource = null;
                EventHandler<DragEventArgs> observeInput = (_, args) => routedInputSource = args.Source;
                var inputEvent = new DragEventArgs(DragDrop.DragOverEvent, transfer, input, default, KeyModifiers.None)
                    { DragEffects = DragDropEffects.Copy };
                window.AddHandler(DragDrop.DragOverEvent, observeInput, RoutingStrategies.Bubble, handledEventsToo: true);
                try { input.RaiseEvent(inputEvent); }
                finally { window.RemoveHandler(DragDrop.DragOverEvent, observeInput); }
                Require(ExternalTextTarget(routedInputSource), "The routed input event did not originate from the attached text input.");
                // TextBox may mark its own events handled. Check this handler independently, then verify
                // that a real Drop raised by the attached input does not start a file import.
                var guard = new DragEventArgs(DragDrop.DragOverEvent, transfer, input, default, KeyModifiers.None)
                    { Source = input, DragEffects = DragDropEffects.Copy };
                window.ExternalDragOver(window, guard);
                Require(!guard.Handled && guard.DragEffects == DragDropEffects.Copy,
                    "The file handler swallowed a transfer addressed to a text input.");
                var previousDrop = window._externalDropTask;
                input.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, transfer, input, default, KeyModifiers.None)
                    { DragEffects = DragDropEffects.Copy });
                Require(ReferenceEquals(window._externalDropTask, previousDrop) && window._tabs.Count == 2
                    && original.History.CurrentRevision == revision,
                    "A file transfer addressed to a text input changed an editor document.");
            }
            finally { host.Children.Remove(input); }
            var moveOnly = Raise(DragDrop.DragOverEvent, transfer, DragDropEffects.Move);
            Require(moveOnly.DragEffects == DragDropEffects.None, "A move-only drag was accepted.");
            report.Add("text inputs keep their own drag handling; move-only file drags are rejected");

            window._windowClosePending = true;
            var blocked = Raise(DragDrop.DragOverEvent, transfer, DragDropEffects.Copy);
            await window.OpenPathsAsync([png]);
            Require(blocked.DragEffects == DragDropEffects.None && original.History.CurrentRevision == revision,
                "External files changed a document during window-close confirmation.");
            window._windowClosePending = false;
            window._tabClosePending.Add(original);
            await window.OpenPathsAsync([png]);
            Require(original.History.CurrentRevision == revision, "External files changed a document during tab-close confirmation.");
            window._tabClosePending.Clear();
            window._recoveryPending = true;
            await window.OpenPathsAsync([png]);
            Require(original.History.CurrentRevision == revision && window._tabs.Count == 2
                && ReferenceEquals(window._open, original), "External files changed the document during project recovery.");
            window._recoveryPending = false;
            report.Add("window/tab close confirmations and project recovery block external file changes");
            return string.Join(Environment.NewLine, report);

            DragEventArgs Raise(RoutedEvent<DragEventArgs> routed, IDataTransfer data, DragDropEffects effects)
            {
                var args = new DragEventArgs(routed, data, window._canvas, default, KeyModifiers.None)
                    { DragEffects = effects };
                window._canvas.RaiseEvent(args);
                return args;
            }
        }
        finally
        {
            window._windowClosePending = false;
            window._recoveryPending = false;
            window._tabClosePending.Clear();
            window._watchTimer?.Stop();
            window._canvas.Document = null;
            foreach (var tab in window._tabs) tab.Document?.Dispose();
            window._tabs.Clear();
            window.CloseForHeadlessCheck();
            if (recentBefore is null) File.Delete(window._recent.Path);
            else File.WriteAllBytes(window._recent.Path, recentBefore);
            Directory.Delete(temporary, recursive: true);
        }

        static void Require(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
        }
    }
}
