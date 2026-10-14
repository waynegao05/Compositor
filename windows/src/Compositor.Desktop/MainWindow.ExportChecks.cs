using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Compositor.Core.IO;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Desktop;

public sealed partial class MainWindow
{
    /// <summary>Real export continuations with a delayed file picker and external project replacement.</summary>
    internal static string ExportSelfCheck()
    {
        var root = Directory.CreateTempSubdirectory("Compositor-export-context-").FullName;
        var cases = new[] { "png-same", "png-switch", "png-reload", "png-close", "png-cancel",
            "jpeg-reload", "jpeg-quality-reload", "jpeg-same" };
        try
        {
            foreach (var scenario in cases) Check(scenario);
            return "export context: 8 PNG/JPEG cases preserve existing targets on cancellation or stale documents; "
                + "normal exports and tab switches export the original live document";
        }
        finally { Directory.Delete(root, recursive: true); }

        void Check(string scenario)
        {
            var project = Path.Combine(root, scenario + ".comp");
            using (var seed = Solid(SKColors.Red)) ProjectStore.Save(ProjectSnapshot.FromDocument(seed), project);
            var jpeg = scenario.StartsWith("jpeg", StringComparison.Ordinal);
            var target = Path.Combine(root, scenario + (jpeg ? ".jpg" : ".png"));
            using (var old = Solid(SKColors.Lime)) ImageWriter.Write(old, target);
            var original = File.ReadAllBytes(target);
            var window = new MainWindow();
            try
            {
                window.Open(project);
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var tab = window._open;
                var provider = window.StorageProvider;
                var file = provider.TryGetFileFromPathAsync(new Uri(target)).GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException("The local export fixture was not found.");
                var delayed = DispatchProxy.Create<IStorageProvider, DeferredExportPicker>();
                var picker = (DeferredExportPicker)(object)delayed;
                picker.Original = provider;
                var storage = typeof(TopLevel).GetField("_storageProvider", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("The headless storage provider cannot be replaced.");
                storage.SetValue(window, delayed);
                Task? export = null;
                if (jpeg) export = window.ExportJpeg(); else window.ExportPng();
                Require(picker.Requested, "Export did not wait for its file picker.");
                if (scenario is "png-reload" or "jpeg-reload") Reload();
                if (scenario == "png-close")
                {
                    var closing = window.CloseTab(tab);
                    Wait(() => closing.IsCompleted);
                    closing.GetAwaiter().GetResult();
                }
                if (scenario == "png-switch")
                {
                    var second = new Tab { Document = Solid(SKColors.Blue) };
                    window._tabs.Add(second);
                    window.Bring(second);
                }
                picker.Completion.SetResult(scenario == "png-cancel" ? null : file);
                if (scenario is "jpeg-quality-reload" or "jpeg-same")
                {
                    Wait(() => window.OwnedWindows.OfType<QualityDialog>().Any());
                    var quality = window.OwnedWindows.OfType<QualityDialog>().Single();
                    if (scenario == "jpeg-quality-reload") Reload();
                    var accept = quality.GetVisualDescendants().OfType<Button>().Single(button => button.IsDefault);
                    accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                var canceled = scenario.Contains("reload", StringComparison.Ordinal) || scenario is "png-close" or "png-cancel";
                if (scenario == "png-cancel") Dispatcher.UIThread.RunJobs();
                else Wait(() => window._message == (canceled
                    ? L10n.T("The project changed while exporting. Export it again.")
                    : L10n.F("Exported {0}", target)));
                if (export is not null)
                {
                    Wait(() => export.IsCompleted);
                    export.GetAwaiter().GetResult();
                }
                if (canceled) Require(File.ReadAllBytes(target).SequenceEqual(original), scenario + " overwrote the previous export.");
                else
                {
                    using var image = ImageImporter.Decode(target);
                    var pixel = image.Image.GetPixel(2, 2);
                    Require(pixel.Red > 245 && pixel.Green < 10 && pixel.Blue < 10 && pixel.Alpha == 255,
                        scenario + " exported an empty or different document.");
                }

                void Reload()
                {
                    var before = tab.Document;
                    using (var replacement = Solid(SKColors.Blue))
                        ProjectStore.Save(ProjectSnapshot.FromDocument(replacement), project);
                    window.ProjectWritten(null, EventArgs.Empty);
                    Require(!ReferenceEquals(before, tab.Document) && before!.Layers.Count == 0,
                        "The export fixture did not exercise document disposal during reload.");
                }
            }
            finally
            {
                foreach (var child in window.OwnedWindows.ToArray()) child.Close();
                window.CloseForHeadlessCheck();
                foreach (var tab in window._tabs) tab.Document?.Dispose();
            }
        }

        static CanvasDocument Solid(SKColor color)
        {
            var bitmap = new SKBitmap(Bitmaps.ColorInfo(8, 6));
            bitmap.Erase(color);
            return ImageImporter.NewDocument(ImportedImage.Create(bitmap, "Color"));
        }

        static void Wait(Func<bool> completed)
        {
            var elapsed = Stopwatch.StartNew();
            while (!completed() && elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(1);
            }
            Require(completed(), "The export continuation did not finish.");
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    private class DeferredExportPicker : DispatchProxy
    {
        public DeferredExportPicker() { }
        internal IStorageProvider Original { get; set; } = null!;
        internal TaskCompletionSource<IStorageFile?> Completion { get; } = new();
        internal bool Requested { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IStorageProvider.SaveFilePickerAsync))
            {
                Requested = true;
                return Completion.Task;
            }
            return targetMethod?.Invoke(Original, args);
        }
    }
}
