using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Compositor.Core.Model;

namespace Compositor.Desktop;

public sealed partial class MainWindow
{
    private bool _windowClosePending;
    private bool _windowCloseApproved;
    private readonly HashSet<Tab> _tabClosePending = [];

    private static bool NeedsCloseConfirmation(Tab tab) =>
        tab.Document is not null && (tab.History.IsModified || tab.Path is null);

    private void OnWindowClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_recoveryBusy)
        {
            args.Cancel = true;
            return;
        }
        if (_windowCloseApproved)
        {
            _picker?.Close();
            return;
        }
        if (_windowClosePending || _tabClosePending.Count > 0)
        {
            args.Cancel = true;
            return;
        }
        try
        {
            // Text sessions keep their history step open until committed. Include that work in the check.
            FinishActiveEdits();
            if (!_tabs.Any(NeedsCloseConfirmation))
            {
                _picker?.Close();
                return;
            }
        }
        catch (Exception error)
        {
            args.Cancel = true;
            AppDiagnostics.Report("Preparing to close the editor", error);
            return;
        }
        args.Cancel = true;
        _windowClosePending = true;
        // Leave the original Closing event before opening a dialog or issuing a second Close call.
        Dispatcher.UIThread.Post(() => _ = ConfirmWindowClose());
    }

    private async Task ConfirmWindowClose()
    {
        var approved = new Dictionary<Tab, (CanvasDocument? Document, Guid Revision, string? Path)>();
        try
        {
            while (true)
            {
                // A text session started while a question was open has not advanced history yet.
                FinishActiveEdits();
                var tab = _tabs.FirstOrDefault(candidate => NeedsCloseConfirmation(candidate)
                    && (!approved.TryGetValue(candidate, out var state)
                        || state != (candidate.Document, candidate.History.CurrentRevision, candidate.Path)));
                if (tab is null) break;
                var before = (tab.Document, tab.History.CurrentRevision, tab.Path);
                var message = tab.Path is { } path
                    ? L10n.F("{0} has been changed since it was last saved.", Path.GetFileName(path))
                    : L10n.T("This project has not been saved.");
                if (!await ConfirmDialog.Ask(this, "Discard unsaved changes?",
                        L10n.F("{0} Anything not saved is lost.", message), "Discard", "Keep")) return;
                // Approval applies only to the document state the question described. A later edit or
                // a new tab gets its own question; earlier approvals never clear a tab's dirty flag.
                approved[tab] = before;
            }
            _windowCloseApproved = true;
            Close();
        }
        catch (Exception error)
        {
            AppDiagnostics.Report("Confirming editor close", error);
        }
        finally
        {
            _windowClosePending = false;
            _windowCloseApproved = false;
        }
    }

    /// <summary>Only the headless command fixtures may discard their generated documents at teardown.</summary>
    internal void CloseForHeadlessCheck()
    {
        if (Application.Current?.ApplicationLifetime is not null)
            throw new InvalidOperationException("Fixture teardown cannot run in an application lifetime.");
        foreach (var dialog in OwnedWindows.OfType<ConfirmDialog>().ToArray()) dialog.Close();
        FinishActiveEdits();
        _windowCloseApproved = true;
        try { Close(); }
        finally { _windowCloseApproved = false; }
        _watchTimer?.Stop();
    }

    /// <summary>Drive real headless windows and their two-button confirmation dialogs through Closing.</summary>
    internal static string CloseSelfCheck()
    {
        var windows = new List<MainWindow>();
        var report = new List<string>();
        try
        {
            var empty = Fixture();
            empty.Close();
            Require(!empty.IsVisible, "empty window did not close");
            var saved = Fixture((false, true));
            saved.Close();
            Require(!saved.IsVisible, "clean saved window did not close");
            report.Add("empty and clean saved windows close without a confirmation");

            var single = Fixture((true, true));
            var document = single._open.Document;
            var revision = single._open.History.CurrentRevision;
            single.Close();
            var first = Question(single);
            single.Close();
            Dispatcher.UIThread.RunJobs();
            Require(single.OwnedWindows.OfType<ConfirmDialog>().Count() == 1
                && ReferenceEquals(first, Question(single)), "repeated close opened another question");
            Answer(single, false);
            PumpUntil(() => !single._windowClosePending, "Keep did not finish the close request");
            Require(single.IsVisible && single._open.Document == document
                && single._open.History.CurrentRevision == revision && single._open.History.IsModified,
                "Keep changed the document or closed the window");
            single.Close();
            Question(single).Close();
            PumpUntil(() => !single._windowClosePending, "dialog dismissal did not cancel the close request");
            Require(single.IsVisible, "dismissing the dialog closed the editor");
            single.Close();
            Answer(single, true);
            PumpUntil(() => !single.IsVisible, "Discard did not close the window");
            report.Add("modified window: repeated close is deduplicated; Keep and dialog X retain edits; Discard closes");

            var multiple = Fixture((true, true), (true, true), (false, true));
            var originalTabs = multiple._tabs.ToArray();
            var originalRevisions = originalTabs.Select(tab => tab.History.CurrentRevision).ToArray();
            multiple.Close();
            Answer(multiple, true);
            Answer(multiple, false);
            PumpUntil(() => !multiple._windowClosePending, "canceling a later tab did not finish");
            Require(multiple.IsVisible && multiple._tabs.SequenceEqual(originalTabs)
                && multiple._tabs.Select(tab => tab.History.CurrentRevision).SequenceEqual(originalRevisions)
                && multiple._tabs.Take(2).All(tab => tab.History.IsModified),
                "canceling a later tab discarded an earlier tab or its dirty state");
            multiple.Close();
            Answer(multiple, true);
            Answer(multiple, true);
            PumpUntil(() => !multiple.IsVisible, "approving all modified tabs did not close");
            report.Add("two modified background tabs behind a clean active tab: every tab checked; partial approval remains reversible");

            var unsaved = Fixture((false, false));
            unsaved.Close();
            Answer(unsaved, false);
            PumpUntil(() => !unsaved._windowClosePending, "new project Keep did not finish");
            Require(unsaved.IsVisible, "new unsaved project closed without protection");
            report.Add("new unsaved project is protected even before the first history edit");

            var changed = Fixture((true, true), (true, true));
            changed.Close();
            Answer(changed, true);
            Question(changed);
            Edit(changed._tabs[0]);
            Answer(changed, true);
            Answer(changed, false);
            PumpUntil(() => !changed._windowClosePending, "revised state confirmation did not finish");
            Require(changed.IsVisible && changed._tabs[0].History.IsModified,
                "an edit after approval was discarded without a new question");
            report.Add("edits made after an earlier approval require a fresh confirmation");
            return string.Join(Environment.NewLine, report);
        }
        finally
        {
            foreach (var window in windows)
            {
                if (window.IsVisible) window.CloseForHeadlessCheck();
                foreach (var tab in window._tabs) tab.Document?.Dispose();
            }
            Dispatcher.UIThread.RunJobs();
        }

        MainWindow Fixture(params (bool Modified, bool Saved)[] specs)
        {
            var window = new MainWindow();
            windows.Add(window);
            if (specs.Length > 0)
            {
                window._tabs.Clear();
                foreach (var (modified, isSaved) in specs)
                {
                    var tab = new Tab
                    {
                        Document = new CanvasDocument(Guid.NewGuid(), 16, 12),
                        Path = isSaved ? $"fixture-{window._tabs.Count}.comp" : null,
                    };
                    if (modified) Edit(tab);
                    window._tabs.Add(tab);
                }
                window._open = window._tabs[^1];
            }
            window.Show();
            Dispatcher.UIThread.RunJobs();
            return window;
        }

        static void Edit(Tab tab)
        {
            var document = tab.Document!;
            tab.History.Begin("Resize", document, null);
            document.Width++;
            tab.History.End(document, null);
        }

        static ConfirmDialog Question(MainWindow window)
        {
            PumpUntil(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(), "close confirmation did not appear");
            return window.OwnedWindows.OfType<ConfirmDialog>().Single();
        }

        static void Answer(MainWindow window, bool discard)
        {
            var dialog = Question(window);
            var label = L10n.T(discard ? "Discard" : "Keep");
            var button = dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, label));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }

        static void PumpUntil(Func<bool> condition, string error)
        {
            var timeout = Stopwatch.StartNew();
            while (!condition() && timeout.Elapsed < TimeSpan.FromSeconds(3))
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(1);
            }
            Require(condition(), error);
        }

        static void Require(bool condition, string error)
        {
            if (!condition) throw new InvalidOperationException(error);
        }
    }
}
