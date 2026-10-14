using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Compositor.Core.IO;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Desktop;

public sealed partial class MainWindow
{
    /// <summary>Checks text ownership across saves, tabs, close questions and external project writes.</summary>
    internal static string EditingSelfCheck()
    {
        var root = Path.Combine(Path.GetTempPath(), "compositor-editing-" + Guid.NewGuid().ToString("N"), "中文工程");
        Directory.CreateDirectory(root);
        var windows = new List<MainWindow>();
        var report = new List<string>();
        try
        {
            var saving = Fixture(saved: true);
            Type(saving, "保存中文 Ab");
            saving.Save();
            Require(saving._text is null && saving._history.CanUndo && !saving._history.IsModified,
                "save did not finish the text history before marking it saved");
            using (var stored = ProjectStore.Load(saving._projectPath!))
                Require(stored.Manifest.Layers.Single().Text?.Content == "保存中文 Ab", "save lost active text");
            saving.Undo();
            Require(saving._document!.Layers.Count == 0 && saving._history.IsModified, "saved text did not undo");
            saving.Redo();
            Require(saving._document!.Layers.Single().Text?.Style.Content == "保存中文 Ab"
                && !saving._history.IsModified, "redo did not recover the saved revision");
            report.Add("saving active Chinese text commits one undo step; save, undo and redo keep correct dirty state");

            var switching = Fixture(saved: true);
            var first = switching._open;
            Type(switching, "切换保留");
            var next = new Tab { Document = new CanvasDocument(Guid.NewGuid(), 128, 96) };
            switching._tabs.Add(next);
            switching.Bring(next);
            Require(switching._text is null && first.History.IsModified && first.History.CanUndo
                && first.Document!.Layers.Single().Text?.Style.Content == "切换保留"
                && next.Document.Layers.Count == 0, "switching tabs lost text or moved it to another document");
            switching.Bring(first);
            Type(switching, "继续");
            var beforeNew = first.Document!.Layers.Single().Text!.Style.Content;
            var blankPath = Path.Combine(root, "打开新工程.comp");
            using (var blank = new CanvasDocument(Guid.NewGuid(), 12, 10))
                ProjectStore.Save(ProjectSnapshot.FromDocument(blank), blankPath);
            switching.Open(blankPath);
            Require(switching._text is null && first.Document.Layers.Single().Text!.Style.Content == beforeNew
                && switching._document!.Layers.Count == 0, "opening another project carried an active text session");
            report.Add("switching tabs and opening another project retain Chinese text in its original tab");

            var closing = Fixture(saved: true);
            var closingTab = closing._open;
            Type(closing, "关闭保护");
            var request = closing.CloseTab(closingTab);
            var question = Question(closing);
            var repeat = closing.CloseTab(closingTab);
            Require(repeat.IsCompleted && closing.OwnedWindows.OfType<ConfirmDialog>().Count() == 1,
                "repeated tab close opened duplicate questions");
            closing.Close();
            Dispatcher.UIThread.RunJobs();
            Require(closing.IsVisible && ReferenceEquals(question, Question(closing)),
                "window close bypassed a pending tab question");
            Answer(closing, discard: false);
            Finish(request);
            Require(closing._tabs.Contains(closingTab) && closingTab.History.IsModified
                && closingTab.Document!.Layers.Single().Text!.Style.Content == "关闭保护",
                "canceling tab close lost text");
            request = closing.CloseTab(closingTab);
            Answer(closing, discard: true);
            Finish(request);
            Require(closing._document is null && closing._text is null && closing._tabs.Count == 1,
                "discarding the last tab retained a session on a disposed document");
            report.Add("text-only edits prompt on tab close; repeated and window close are deduplicated; Keep retains text");

            var unsaved = Fixture(saved: false);
            request = unsaved.CloseTab(unsaved._open);
            Question(unsaved).Close();
            Finish(request);
            Require(unsaved._document is not null, "dismissing a new unsaved tab did not protect it");
            request = unsaved.CloseTab(unsaved._open);
            Question(unsaved);
            Type(unsaved, "确认期间修改");
            Answer(unsaved, discard: true);
            Question(unsaved);
            Answer(unsaved, discard: false);
            Finish(request);
            Require(unsaved._document!.Layers.Single().Text!.Style.Content == "确认期间修改",
                "an earlier tab approval discarded later text");
            report.Add("new unsaved tabs are protected; text entered after a close question requires renewed approval");

            var windowClosing = Fixture(saved: true);
            Type(windowClosing, "原有文字");
            windowClosing.Close();
            Question(windowClosing);
            Type(windowClosing, "确认时新增");
            Answer(windowClosing, discard: true);
            Question(windowClosing);
            Answer(windowClosing, discard: false);
            Pump(() => !windowClosing._windowClosePending, "window close did not finish after renewed approval");
            Require(windowClosing.IsVisible && windowClosing._document!.Layers.Any(layer =>
                layer.Text?.Style.Content.Contains("确认时新增", StringComparison.Ordinal) == true),
                "window close discarded a text session started after the original question");
            report.Add("window close commits text added during confirmation and asks again before discarding it");

            var watched = Fixture(saved: true);
            var active = watched._document;
            Type(watched, "外部修改保留");
            using (var changed = new CanvasDocument(Guid.NewGuid(), 24, 20))
                ProjectStore.Save(ProjectSnapshot.FromDocument(changed), watched._projectPath!);
            watched.ProjectWritten(null, EventArgs.Empty);
            Require(ReferenceEquals(active, watched._document) && watched._text?.Content == "外部修改保留",
                "external reload replaced a document with an active text session");

            var retry = Fixture(saved: true);
            var held = retry._document;
            var manifest = Path.Combine(retry._projectPath!, ProjectStore.ManifestName);
            var bytes = File.ReadAllBytes(manifest);
            File.Delete(manifest);
            retry.ProjectWritten(null, EventArgs.Empty);
            Require(ReferenceEquals(held, retry._document), "incomplete external save discarded the open document");
            File.WriteAllBytes(manifest, bytes);
            retry.ProjectWritten(null, EventArgs.Empty);
            Require(!ReferenceEquals(held, retry._document) && retry._document!.Width == 128,
                "external reload did not retry after a temporarily missing manifest");
            report.Add("external writes preserve active text; temporarily unreadable projects retain the canvas and retry");
            return string.Join(Environment.NewLine, report);
        }
        finally
        {
            foreach (var window in windows)
            {
                window.CloseForHeadlessCheck();
                window._canvas.Document = null;
                foreach (var tab in window._tabs) tab.Document?.Dispose();
            }
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
            Dispatcher.UIThread.RunJobs();
        }

        MainWindow Fixture(bool saved)
        {
            var window = new MainWindow();
            windows.Add(window);
            window._document = new CanvasDocument(Guid.NewGuid(), 128, 96);
            if (saved)
            {
                var path = Path.Combine(root, $"工程 {windows.Count}.comp");
                Require(window.WriteTo(window._open, path), "could not save the text fixture");
            }
            window.Show(window._open);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window._watchTimer?.Stop();
            return window;
        }

        static void Type(MainWindow window, string text)
        {
            window.TypeHere(new SKPoint(4, 4));
            window.TypedText(text);
            Require(window._text is not null, "fixture did not enter text editing");
        }

        static ConfirmDialog Question(MainWindow window)
        {
            Pump(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(), "tab close question did not appear");
            return window.OwnedWindows.OfType<ConfirmDialog>().Single();
        }

        static void Answer(MainWindow window, bool discard)
        {
            var label = L10n.T(discard ? "Discard" : "Keep");
            Question(window).GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, label))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }

        static void Finish(Task task)
        {
            Pump(() => task.IsCompleted, "tab close did not complete");
            task.GetAwaiter().GetResult();
        }

        static void Pump(Func<bool> done, string error)
        {
            var elapsed = Stopwatch.StartNew();
            while (!done() && elapsed.Elapsed < TimeSpan.FromSeconds(3))
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(1);
            }
            Require(done(), error);
        }

        static void Require(bool condition, string error)
        {
            if (!condition) throw new InvalidOperationException(error);
        }
    }
}
