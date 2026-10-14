using Avalonia.Platform.Storage;
using Compositor.Core.IO;
using Compositor.Core.Model;

namespace Compositor.Desktop;

public sealed partial class MainWindow
{
    private bool _recoveryPending;
    private bool _recoveryBusy;
    private string? _lastProjectOpenError;

    /// <summary>Called once by the desktop lifetime, after its native window is ready.</summary>
    internal async Task StartDesktopSessionAsync(IEnumerable<string> paths)
    {
        try
        {
            var candidates = await Task.Run(() => ProjectRecovery.FindAvailable());
            if (candidates.Count > 0 && IsVisible && !_windowClosePending
                && await ConfirmDialog.Ask(this, "Interrupted saves found",
                    L10n.F("{0} recovery copies are available. Review them now? You can also use File → Recover interrupted saves later.", candidates.Count),
                    "Review copies", "Later"))
                await RecoverInterruptedSaveAsync(candidates: candidates);
            if (IsVisible) await OpenPathsAsync(paths);
        }
        catch (Exception error)
        {
            AppDiagnostics.Report("Opening startup projects and recovery copies", error);
            Say(L10n.F("Could not open: {0}", OperationErrors.Describe(error)));
        }
    }

    /// <summary>Every interactive project entry point uses the same open and recovery behavior.</summary>
    private async Task<bool> OpenProjectPathAsync(string path)
    {
        _lastProjectOpenError = null;
        if (_windowClosePending || _tabClosePending.Count > 0 || _recoveryPending) return false;
        try
        {
            Open(path);
            return true;
        }
        catch (Exception error)
        {
            AppDiagnostics.Report("Opening project path", error);
            _lastProjectOpenError = OperationErrors.Describe(error);
            Say(L10n.F("Could not open {0}: {1}", Path.GetFileName(path), _lastProjectOpenError));
            var candidates = await Task.Run(() => ProjectRecovery.FindAvailable(path));
            var recovered = candidates.Count > 0 && IsVisible && await RecoverInterruptedSaveAsync(candidates: candidates);
            if (recovered) _lastProjectOpenError = null;
            return recovered;
        }
    }

    private async Task<bool> RecoverInterruptedSaveAsync(string? targetPath = null,
        IReadOnlyList<ProjectRecoveryCandidate>? candidates = null)
    {
        if (_recoveryPending || _windowClosePending || _tabClosePending.Count > 0) return false;
        _recoveryPending = true;
        try
        {
            candidates ??= await Task.Run(() => ProjectRecovery.FindAvailable(targetPath));
            if (!IsVisible) return false;
            if (candidates.Count == 0)
            {
                Say("No recoverable interrupted saves were found.");
                return false;
            }
            var candidate = await RecoveryDialog.Ask(this, candidates);
            if (candidate is null || !IsVisible) return false;
            var selected = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L10n.T("Recover to a new project folder"),
                SuggestedFileName = Path.GetFileNameWithoutExtension(candidate.TargetPath) + "-recovered.comp",
                DefaultExtension = "comp",
            });
            if (selected?.TryGetLocalPath() is not { } destination || !IsVisible) return false;
            _recoveryBusy = true;
            var restored = await Task.Run(() => ProjectRecovery.RestoreCopy(candidate, destination));
            if (!IsVisible) return true;
            Open(restored);
            Say(L10n.F("Recovered to {0}. The original project and recovery copy were kept.", restored));
            return true;
        }
        catch (Exception error)
        {
            AppDiagnostics.Report("Recovering interrupted save", error);
            Say(L10n.F("Could not recover the project: {0}", OperationErrors.Describe(error)));
            return false;
        }
        finally
        {
            _recoveryBusy = false;
            _recoveryPending = false;
        }
    }

    /// <summary>Core tests cover interrupted writes; this verifies UI selection and rejection preserve the current tab.</summary>
    internal static string RecoverySelfCheck()
    {
        if (ProjectRecovery.FindAvailable().Count != 0)
            throw new InvalidOperationException("recovery check requires its isolated empty profile");
        var window = new MainWindow();
        var root = Directory.CreateTempSubdirectory("compositor-recovery-ui-").FullName;
        try
        {
            window._document = new CanvasDocument(Guid.NewGuid(), 16, 12);
            window.Show(window._open);
            var tab = window._open;
            var document = window._document;
            var revision = window._history.CurrentRevision;
            var candidate = new ProjectRecoveryCandidate(Guid.NewGuid(), Path.Combine(root, "原工程.comp"),
                Path.Combine(root, "unregistered.old"), DateTimeOffset.UtcNow, ProjectRecoveryKind.PreviousSave,
                document.ID, 16, 12);
            var destination = Path.Combine(root, "恢复副本.comp");
            var refused = false;
            try { ProjectRecovery.RestoreCopy(candidate, destination); }
            catch (Exception error) when (error is IOException or InvalidOperationException or ProjectException or UnauthorizedAccessException)
            {
                refused = true;
            }
            if (!refused || Directory.Exists(destination) || File.Exists(destination)
                || !ReferenceEquals(window._open, tab) || !ReferenceEquals(window._document, document)
                || window._history.CurrentRevision != revision)
                throw new InvalidOperationException("unregistered recovery modified the current document or destination");
            return RecoveryDialog.SelfCheck() + Environment.NewLine
                + "recovery entry point: empty isolated profile and forged candidate rejection retain the original tab and destination";
        }
        finally
        {
            window.CloseForHeadlessCheck();
            window._document?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }
}
