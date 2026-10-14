using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => AppDiagnostics.Run(() => Run(args),
        interactive: !args.Any(arg => arg.StartsWith("--", StringComparison.Ordinal)
            && !arg.StartsWith("--language=", StringComparison.Ordinal)));

    private static int Run(string[] args)
    {
        string[] checks = ["--localization", "--ime", "--save-check", "--close-check", "--editing-check", "--import-check",
            "--recovery-check", "--clipboard-check", "--external-files-check", "--render", "--rulers", "--window",
            "--tabs", "--camera-raw", "--tools", "--shortcuts", "--clicks", "--theme-probe", "--dialogs", "--dialogs-check"];
        var check = args.FirstOrDefault(arg => checks.Contains(arg, StringComparer.Ordinal));
        using var profile = check is null ? null : AppDataPaths.UseTemporaryProfile();
        if (check is not null) AppDiagnostics.Record($"Validation mode: {check}; temporary preferences; failures may be intentional test cases.");
        var language = args.FirstOrDefault(arg => arg.StartsWith("--language=", StringComparison.Ordinal));
        if (language is not null)
        {
            var selected = language["--language=".Length..];
            if (selected is not (L10n.English or L10n.SimplifiedChinese or L10n.SystemLanguage))
            {
                Console.Error.WriteLine("Use --language=en, --language=zh-Hans or --language=system.");
                return 2;
            }
            args = args.Where(arg => !arg.StartsWith("--language=", StringComparison.Ordinal)).ToArray();
            L10n.Initialize(selected);
        }
        else L10n.Initialize();
        AppDiagnostics.Record($"Interface language: {L10n.CurrentLanguage}.");
        if (args is ["--diagnostics", var diagnosticOutput])
        {
            if (!AppDiagnostics.Export(diagnosticOutput, out var error))
            {
                Console.Error.WriteLine($"Could not export diagnostics: {error}");
                return 1;
            }
            Console.WriteLine($"Diagnostics written to {diagnosticOutput}");
            return 0;
        }
        if (args is ["--diagnostics-check"]) return AppDiagnostics.Check();
        if (args is ["--native-dependencies"]) return NativeDependencyChecks.Run();
        if (args is ["--recovery-check"])
        {
            BuildHeadless().SetupWithoutStarting();
            Console.WriteLine(MainWindow.RecoverySelfCheck());
            return 0;
        }
        if (args is ["--dialogs-check"])
        {
            BuildHeadless().SetupWithoutStarting();
            Console.WriteLine(DialogRegressionChecks.Run());
            return 0;
        }
        if (args is ["--clipboard-check"]) return RunAsyncCheck(MainWindow.ClipboardSelfCheck);
        if (args is ["--external-files-check"]) return RunAsyncCheck(MainWindow.ExternalFilesSelfCheck);
        if (args is ["--editing-check"])
        {
            BuildHeadless().SetupWithoutStarting();
            Console.WriteLine(MainWindow.EditingSelfCheck());
            Console.WriteLine(MainWindow.TransactionSelfCheck());
            Console.WriteLine(MainWindow.TextInteractionSelfCheck());
            Console.WriteLine(MainWindow.CanvasRegressionSelfCheck());
            Console.WriteLine(MainWindow.TextShortcutSelfCheck());
            Console.WriteLine(TextDialog.RichTextSelfCheck());
            Console.WriteLine(CanvasInteractionChecks.Run());
            var boundaryRoot = Path.Combine(Path.GetTempPath(), "compositor-boundaries-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(boundaryRoot);
            try { Console.WriteLine(MainWindow.EditBoundarySelfCheck(boundaryRoot)); }
            finally { Directory.Delete(boundaryRoot, recursive: true); }
            Console.WriteLine(ShortcutDialog.RestoreDefaultsSelfCheck());
            Console.WriteLine(OperationErrors.SelfCheck());
            return 0;
        }
        if (args is ["--import-check"])
        {
            BuildHeadless().SetupWithoutStarting();
            Console.WriteLine(MainWindow.ImportSelfCheck());
            Console.WriteLine(MainWindow.ExportSelfCheck());
            return 0;
        }
        if (args is ["--save-check"])
        {
            BuildHeadless().SetupWithoutStarting();
            var window = new MainWindow();
            try { Console.WriteLine(window.SaveSelfCheck()); }
            finally { window.CloseForHeadlessCheck(); }
            return 0;
        }
        if (args is ["--close-check"])
        {
            BuildHeadless().SetupWithoutStarting();
            Console.WriteLine(MainWindow.CloseSelfCheck());
            return 0;
        }
        if (args is ["--localization", var localizedOutput]) return Localization(localizedOutput);
        if (args is ["--ime"])
        {
            BuildHeadless().SetupWithoutStarting();
            return ImeChecks.Run();
        }
        // Draws the canvas control straight to a PNG, so the interface can be checked without a window.
        // `--grid` turns the layout grid on for the render, which is how that drawing is checked.
        if (args is ["--render", var project, var output]) return Render(project, output, showGrid: false);
        if (args is ["--render", var gridProject, var gridOutput, "--grid"]) return Render(gridProject, gridOutput, showGrid: true);
        // `--shape` draws the shape tool's drag preview, which needs a pointer to make.
        if (args is ["--render", var shapeProject, var shapeOutput, "--shape"])
        {
            return Render(shapeProject, shapeOutput, showGrid: false, shape: true);
        }
        // `--gradient` draws the gradient tool's drag line, which needs a pointer to make.
        if (args is ["--render", var gradProject, var gradOutput, "--gradient"])
        {
            return Render(gradProject, gradOutput, showGrid: false, gradient: true);
        }
        // `--preview` opens a preview of the top layer inverted, so the canvas drawing one can be checked.
        if (args is ["--render", var previewProject, var previewOutput, "--preview"])
        {
            return Render(previewProject, previewOutput, showGrid: false, preview: true);
        }
        // `--pixel-grid` is the same render with the pixel grid asked for, and `--zoom-in` with the view taken
        // past 800% where the grid draws at all: the two together are how it is checked without a pointer.
        if (args is ["--render", var gridProject2, var gridOutput2, "--pixel-grid"])
        {
            return Render(gridProject2, gridOutput2, showGrid: false, pixelGrid: true);
        }
        if (args is ["--render", var zoomProject, var zoomOutput, "--zoom-in"])
        {
            return Render(zoomProject, zoomOutput, showGrid: false, zoomIn: true);
        }
        if (args is ["--render", var zoomGridProject, var zoomGridOutput, "--zoom-in", "--pixel-grid"])
        {
            return Render(zoomGridProject, zoomGridOutput, showGrid: false, zoomIn: true, pixelGrid: true);
        }
        // `--rulers` draws a ruler strip straight to a PNG, which is how its ticks are checked without a
        // pointer: the strip is measured and arranged the way the window does, at a known zoom.
        if (args is ["--rulers", var rulerOutput, var rulerScale, var rulerOrigin])
        {
            return Rulers(rulerOutput, double.Parse(rulerScale), double.Parse(rulerOrigin));
        }
        // `--window` builds the whole window and draws it, which is the only way to check the parts that are
        // not the canvas — the menus, the tab strip and the panel — without a display to click them on.
        if (args is ["--window", var windowOutput]) return Window(windowOutput);
        // `--tabs` drives the tab strip without a pointer: two projects into tabs, one brought back in front,
        // one closed, and the last closed as well. It draws the window afterwards so the strip can be looked at.
        if (args is ["--tabs", var tabsOutput]) return Tabs(tabsOutput);
        // `--camera-raw` drives the Camera Raw panel without a pointer — opened on a layer, amounts moved, the
        // preview run, then Apply and Cancel — and draws the window with the panel still up, which is the only
        // way to look at the panel docked at the window's right edge.
        if (args is ["--camera-raw", var rawOutput]) return CameraRaw(rawOutput);
        // `--tools` drives the tool rail and the window's toolbar without a pointer: every tool is picked in
        // turn, the colours are swapped and reset, and the zoom is stepped. The window is drawn at the end, so
        // what the rail looks like is in the PNG beside the report.
        if (args is ["--tools", var toolsOutput]) return Tools(toolsOutput);
        // `--shortcuts` drives the key table without a keyboard: every row's key and the menu row that shows
        // it, a few keys pressed through the window's own routed event, a rebind, and the sheet refusing a
        // clash. It draws the window and the sheet's own list of rows beside the report.
        if (args is ["--shortcuts", var keysOutput]) return Shortcuts(keysOutput);
        // `--clicks` drives the window with a pointer instead of only building it: a stroke painted, a marquee
        // dragged, the wand clicked — each aimed at a document point and checked against the document and the
        // history. It runs on Avalonia's headless platform, which is what makes hit-testing and pointer capture
        // real without a display.
        if (args is ["--clicks", var clicksOutput]) return Clicks(clicksOutput);
        // This community Windows build reports its own version without contacting the macOS update feed.
        if (args is ["--updates"]) return Updates();
        if (args.Length >= 1 && args[0] == "--theme-probe") return ThemeProbe(args[1..]);
        // `--dialogs` draws a few dialog bodies under the real theme, which is the only way to look at their
        // colours: what a control's text and fill resolve to is the theme's business, not the dialog's.
        if (args is ["--dialogs", var dialogOutput]) return Dialogs(dialogOutput);
        if (args.Any(arg => arg.StartsWith("--", StringComparison.Ordinal)))
        {
            Console.Error.WriteLine("Unknown or incomplete option. Diagnostics: --diagnostics <output.zip>, --diagnostics-check, --save-check. Language: --language=en|zh-Hans|system.");
            return 2;
        }
        Build().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>Run asynchronous UI checks with a live dispatcher, without starting a desktop lifetime.</summary>
    private static int RunAsyncCheck(Func<Task<string>> run)
    {
        BuildHeadless().SetupWithoutStarting();
        var task = run();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && elapsed.Elapsed < TimeSpan.FromSeconds(45))
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        if (!task.IsCompleted) throw new TimeoutException("The asynchronous interface check did not finish.");
        Console.WriteLine(task.GetAwaiter().GetResult());
        return 0;
    }

    /// <summary>
    /// Prints the colours the Fluent theme resolves to: every key, or only those whose names hold one of the
    /// given words. The colours a control is drawn with are the theme's rather than the app's, so this is how
    /// they are read — it is what found the hundred keys that follow Fluent's own blue, which <see cref="Skin"/>
    /// moves onto the Mac's. What it says first is whether that took: no key should still hold Fluent's blue.
    /// </summary>
    private static int ThemeProbe(string[] names)
    {
        BuildHeadless().SetupWithoutStarting();
        var theme = Application.Current!.Styles.OfType<FluentTheme>().First();
        if (theme.Resources is not ResourceDictionary resources) return 0;
        foreach (var variant in resources.ThemeDictionaries)
        {
            if (variant.Key != ThemeVariant.Dark || variant.Value is not IResourceDictionary dark) continue;
            var flaunted = 0;
            foreach (var key in dark.Keys)
            {
                dark.TryGetResource(key, null, out var held);
                if (HoldsFluentBlue(held)) flaunted++;
            }
            Console.WriteLine($"variant {Application.Current!.RequestedThemeVariant}; {dark.Count} resources, " +
                $"{flaunted} still in Fluent's own blue");
            foreach (var key in dark.Keys.OrderBy(k => k.ToString(), StringComparer.Ordinal))
            {
                var name = key.ToString()!;
                if (names.Length > 0 && !names.All(part => name.Contains(part, StringComparison.OrdinalIgnoreCase))) continue;
                dark.TryGetResource(key, null, out var value);
                Console.WriteLine($"  {name} = {value}");
            }
        }
        return 0;
    }

    /// <summary>Whether a resource still holds one of the three shades Fluent draws its accent in.</summary>
    private static bool HoldsFluentBlue(object? value)
    {
        var colour = value switch
        {
            Color one => one,
            ISolidColorBrush brush => brush.Color,
            _ => (Color?)null,
        };
        return colour is { } held && (held == Color.FromRgb(0x00, 0x78, 0xD4)
            || held == Color.FromRgb(0x23, 0xA0, 0xFF) || held == Color.FromRgb(0x00, 0x58, 0x9B));
    }

    /// <summary>
    /// A few dialogs' bodies drawn one under another under the real theme. A dialog's colours are mostly the
    /// theme's — the text, a field's fill, a slider's track and thumb, a tick box — so this is how they are
    /// looked at without a pointer: each is composed the way its window composes it, its own background behind
    /// its content, and the background it resolved to is printed beside it.
    /// </summary>
    private static int Dialogs(string output)
    {
        BuildHeadless().SetupWithoutStarting();
        var folder = Path.GetDirectoryName(Path.GetFullPath(output))!;
        foreach (var (name, file, width, body) in new (string, string, double, Control)[]
                 {
                     ("New Project", "new-project.png", 400, new NewDocumentDialog().TakeBody()),
                     ("Grid Settings", "grid-settings.png", 380, new GridSettingsDialog(new LayoutGrid()).TakeBody()),
                     ("Controls", "controls.png", 420, Controls()),
                 })
        {
            Draw(name, Path.Combine(folder, file), width, body);
        }
        Console.WriteLine("Camera Raw is captured by --camera-raw; Dither visibility is checked below.");
        var newProjectImage = Path.Combine(folder, "new-project.png");
        if (!string.Equals(Path.GetFullPath(newProjectImage), Path.GetFullPath(output), StringComparison.Ordinal))
            File.Copy(newProjectImage, output, overwrite: true);
        // The Dither panel's rows are driven below to verify conditional visibility. Its rows are
        // built and then hidden by what the look uses, which is the check: an amount that would do nothing
        // for the look chosen must not be on the panel when Apply is pressed. Driving it works without a
        // window because a box's selection is a property, not a template.
        var dither = (ScrollViewer)DitherDialog.Body(DitherStyle.Atkinson, new DitherSettings());
        var panel = (StackPanel)dither.Content!;
        var look = panel.Children.OfType<StackPanel>()
            .SelectMany(row => row.Children.OfType<Control>()).OfType<ComboBox>().First();
        var built = panel.Children.Count;
        // A look is chosen by its name rather than its place in the list: the list draws a rule between its
        // groups, so an item's index is not the look's.
        foreach (var name in new[] { "Atkinson (Classic Mac)", "Bayer 2 × 2", "Halftone Dots", "Mac Patterns", "ASCII" })
        {
            var chosen = look.Items.OfType<ComboBoxItem>().ToList().FindIndex(item => (item.Content as string) == L10n.T(name));
            if (chosen < 0)
            {
                Console.WriteLine($"the Dither panel has no look called {name}");
                continue;
            }
            look.SelectedIndex = chosen;
            var shown = panel.Children.Count(child => child.IsVisible);
            Console.WriteLine($"Dither, {name}: {shown} of {built} controls shown "
                + $"({string.Join(", ", panel.Children.Where(row => row.IsVisible).OfType<TextBlock>().Select(text => text.Text))})");
        }
        return 0;
    }

    /// <summary>
    /// The controls a dialog is made of, side by side under the app's own window: this is where a slider's
    /// track and thumb, a tick box's mark, a pop-up's field and a selected row show what the theme draws them.
    /// </summary>
    private static Control Controls()
    {
        var selected = new ListBoxItem { Content = "A selected row", IsSelected = true };
        return new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "A label" },
                new TextBlock { Text = "A second line", Foreground = Skin.SecondaryBrush },
                new Slider { Minimum = 0, Maximum = 100, Value = 40 },
                new CheckBox { Content = "A tick box", IsChecked = true },
                new RadioButton { Content = "A radio button", IsChecked = true },
                new ComboBox
                {
                    ItemsSource = new[] { "A pop-up", "Another choice" },
                    SelectedIndex = 0,
                },
                new ListBox { ItemsSource = new object[] { "A row", selected }, Height = 72 },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { new Button { Content = "Cancel" }, new Button { Content = "OK" } },
                },
            },
        };
    }

    /// <summary>One body drawn on a window of the app's own make, at the width its dialog asks for.</summary>
    private static void Draw(string name, string path, double width, Control body)
    {
        var holder = new CheckWindow { Width = width, SizeToContent = SizeToContent.Height };
        holder.Content = new Border { Background = holder.Background, Child = body };
        try
        {
            holder.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            holder.UpdateLayout();
            using var target = holder.CaptureRenderedFrame()
                ?? throw new InvalidOperationException("The dialog did not render a frame.");
            target.Save(path, new PngBitmapEncoderOptions());
            Console.WriteLine($"{name}: {body.GetType().Name} rendered in a shown headless window to {path}");
        }
        finally { holder.Close(); }
    }

    private static void CaptureWindow(MainWindow window, string output)
    {
        try
        {
            if (!window.IsVisible) window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            using var target = window.CaptureRenderedFrame()
                ?? throw new InvalidOperationException("The editor did not render a frame.");
            target.Save(output, new PngBitmapEncoderOptions());
        }
        finally { window.CloseForHeadlessCheck(); }
    }

    /// <summary>A window of the app's own make, to hold a dialog's body while it is drawn.</summary>
    private sealed class CheckWindow : DialogWindow;

    /// <summary>
    /// Prints this distribution's version without consulting the macOS update feed.
    /// </summary>
    private static int Updates()
    {
        Console.WriteLine($"Compositor {UpdateDialog.Running}: Windows community build; no Windows update feed is configured.");
        return 0;
    }

    /// <summary>Checks localized menus, stable shortcuts and a project under a Unicode path together.</summary>
    private static int Localization(string output)
    {
        BuildHeadless().SetupWithoutStarting();
        var folder = Path.Combine(Path.GetTempPath(), "compositor-中文-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        MainWindow? window = null;
        try
        {
            var project = Path.Combine(folder, "中文项目.comp");
            using (var document = Demo())
            {
                document.Layers[0].Name = "中文图层 Save";
                ProjectStore.Save(ProjectSnapshot.FromDocument(document), project);
            }
            using (var loaded = ProjectStore.Load(project))
            {
                if (loaded.Manifest.Layers[0].Name != "中文图层 Save")
                    throw new InvalidOperationException("A Unicode layer name changed during project round trip.");
            }
            window = new MainWindow();
            Console.WriteLine(window.ShortcutsSelfCheck(project));
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var menu = window.GetVisualDescendants().OfType<Menu>().Single();
            var headers = menu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()).ToArray();
            foreach (var source in new[] { "_File", "_Edit", "_Layer", "_Image", "_Filter", "_Tools", "_Select", "_View", "_Help" })
            {
                var expected = L10n.T(source);
                if (L10n.CurrentLanguage == L10n.SimplifiedChinese && expected == source)
                    throw new InvalidOperationException($"Missing Chinese menu translation: {source}");
                if (!headers.Contains(expected)) throw new InvalidOperationException($"Menu did not display {expected}.");
            }
            CaptureWindow(window, output);
            Console.WriteLine($"Localization passed: {L10n.CurrentLanguage}; Unicode project, menus and shortcut rebinding; wrote {output}");
            return 0;
        }
        finally
        {
            window?.CloseForHeadlessCheck();
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// The tab strip driven without a pointer. Two projects are written and opened into tabs, one is brought
    /// back in front, one is closed, the last is closed as well, and the window is drawn at the end, so what
    /// the strip looks like with an empty tab in it is in the PNG beside the report.
    /// </summary>
    private static int Tabs(string output)
    {
        BuildHeadless().SetupWithoutStarting();
        var folder = Path.Combine(Path.GetTempPath(), "compositor-tabs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var first = Path.Combine(folder, "first.comp");
            var second = Path.Combine(folder, "second.comp");
            using (var document = Demo()) ProjectStore.Save(ProjectSnapshot.FromDocument(document), first);
            using (var document = LayerPlacement.NewDocument(100, 80))
            {
                ProjectStore.Save(ProjectSnapshot.FromDocument(document!), second);
            }
            var window = new MainWindow();
            Console.WriteLine(window.SelfCheck(first, second));
            CaptureWindow(window, output);
            Console.WriteLine($"wrote {output}: the tab strip driven and drawn");
            return 0;
        }
        finally
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// The Camera Raw panel driven without a pointer, then drawn. The panel is a docked column inside the
    /// window's own content, so — unlike the scroll-view bodies <c>--dialogs</c> cannot lay out — it can be
    /// measured, arranged and drawn here, which is what puts the docked panel in the PNG beside the report.
    /// </summary>
    private static int CameraRaw(string output)
    {
        BuildHeadless().SetupWithoutStarting();
        var folder = Path.Combine(Path.GetTempPath(), "compositor-raw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var project = Path.Combine(folder, "raw.comp");
            using (var document = Demo()) ProjectStore.Save(ProjectSnapshot.FromDocument(document), project);
            var window = new MainWindow();
            Console.WriteLine(window.CameraRawSelfCheck(project));
            CaptureWindow(window, output);
            Console.WriteLine($"wrote {output}: the window with the Camera Raw panel docked");
            return 0;
        }
        finally
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// The tool rail and the window's toolbar driven without a pointer, then drawn. The rail's marks are drawn
    /// shapes rather than a font, so the only way to look at them is a picture of the window — which can be
    /// drawn because the rail is part of the window's own content rather than a body on its own.
    /// </summary>
    private static int Tools(string output)
    {
        BuildHeadless().SetupWithoutStarting();
        var folder = Path.Combine(Path.GetTempPath(), "compositor-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var project = Path.Combine(folder, "tools.comp");
            using (var document = Demo()) ProjectStore.Save(ProjectSnapshot.FromDocument(document), project);
            var window = new MainWindow();
            Console.WriteLine(window.ToolsSelfCheck(project));
            CaptureWindow(window, output);
            Console.WriteLine($"wrote {output}: the window with the rail and the toolbar");
            // Capture the rail separately as well so its marks can be inspected at their own scale.
            var rail = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "tools-rail.png");
            Draw("the tool rail", rail, 56, new ToolRail().TakeTools());
            Console.WriteLine("the tool rail is also captured in tools-rail.png");
            return 0;
        }
        finally
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// The key table driven without a keyboard, then drawn: the window, whose menu rows carry their gestures,
    /// and the sheet's own list of rows — which is drawn by itself, because a bitmap does not lay out the content
    /// of a scroll view and the sheet is one.
    /// </summary>
    private static int Shortcuts(string output)
    {
        BuildHeadless().SetupWithoutStarting();
        var folder = Path.Combine(Path.GetTempPath(), "compositor-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var project = Path.Combine(folder, "keys.comp");
            using (var document = Demo()) ProjectStore.Save(ProjectSnapshot.FromDocument(document), project);
            var window = new MainWindow();
            Console.WriteLine(window.ShortcutsSelfCheck(project));
            CaptureWindow(window, output);
            Console.WriteLine($"wrote {output}: the window, whose menu rows carry their keys");
            var sheet = new ShortcutDialog(new Dictionary<string, ShortcutChord>());
            var rows = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "shortcuts-sheet.png");
            Draw("the shortcut sheet's rows", rows, 640, sheet.TakeRows());
            Console.WriteLine("the shortcut rows are also captured in shortcuts-sheet.png");
            return 0;
        }
        finally
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// The window itself, built and drawn to a PNG. It is a smoke test rather than a picture of anything: what
    /// it proves is that the window's construction runs — every menu row, the tab strip and the layer panel.
    /// </summary>
    private static int Window(string output)
    {
        BuildHeadless().SetupWithoutStarting();
        var window = new MainWindow();
        CaptureWindow(window, output);
        Console.WriteLine($"wrote {output}: the window built and drew");
        return 0;
    }

    /// <summary>
    /// A horizontal ruler from <paramref name="origin"/> along the document at <paramref name="scale"/> points
    /// to the pixel, drawn to a PNG. It is the same control the window puts along the top of the canvas.
    /// </summary>
    private static int Rulers(string output, double scale, double origin)
    {
        BuildHeadless().SetupWithoutStarting();
        var strip = new RulerStrip { Axis = GuideAxis.Horizontal, Scale = scale, Origin = origin };
        strip.Measure(new Size(600, 18));
        strip.Arrange(new Rect(0, 0, 600, 18));
        using var target = new RenderTargetBitmap(new PixelSize(600, 18));
        target.Render(strip);
        target.Save(output, new PngBitmapEncoderOptions());
        Console.WriteLine($"wrote {output} for a ruler at {scale} points to the pixel from {origin}, " +
            $"numbered every {RulerScale.MajorStep(scale)} pixels");
        return 0;
    }

    public static AppBuilder Build() =>
        AppBuilder.Configure<DesktopApp>().UsePlatformDetect().WithInterFont().LogToTrace();

    /// <summary>
    /// The app on Avalonia's headless platform, for the checks that drive the window with a pointer. Headless
    /// drawing is off, so the real Skia renderer draws as usual and the window can be photographed as well as
    /// clicked; what the platform leaves out is only the desktop window itself.
    /// </summary>
    private static AppBuilder BuildHeadless() =>
        AppBuilder.Configure<DesktopApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// The window driven with a pointer, then photographed. Every aiming point is a document point put through
    /// the canvas's own mapping, so what the check clicks is where the tool believes it clicked, and every
    /// assertion is made against the document and the history rather than against the picture.
    /// </summary>
    private static int Clicks(string output)
    {
        BuildHeadless().SetupWithoutStarting();
        var folder = Path.Combine(Path.GetTempPath(), "compositor-clicks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var project = Path.Combine(folder, "clicks.comp");
            using (var document = Demo()) ProjectStore.Save(ProjectSnapshot.FromDocument(document), project);
            var window = new MainWindow();
            // Showing it is what creates the window the pointer can hit and lays the content out; headless, no
            // desktop window appears.
            window.Show();
            var report = window.PointerSelfCheck(project);
            Console.WriteLine(report);
            window.CaptureRenderedFrame()?.Save(output, new PngBitmapEncoderOptions());
            Console.WriteLine($"wrote {output}: the window after the pointer drove it");
            // The picker is a window of its own, so it is photographed on its own as well — the check leaves it
            // up on the background colour for exactly this.
            if (window.Picker is { } picker)
            {
                var pickerOutput = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "color-picker.png");
                picker.CaptureRenderedFrame()?.Save(pickerOutput, new PngBitmapEncoderOptions());
                Console.WriteLine($"wrote {pickerOutput}: the colour picker the check left up");
            }
            // The check reports a failure in its own words rather than throwing, so that what it managed to do
            // is still on the screen; the exit code is what says it failed.
            return report.Contains("FAILED:", StringComparison.Ordinal) ? 1 : 0;
        }
        finally
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static int Render(string project, string output, bool showGrid, bool preview = false, bool shape = false,
        bool zoomIn = false, bool pixelGrid = false, bool gradient = false)
    {
        BuildHeadless().SetupWithoutStarting();
        using var document = project == "--demo" ? Demo() : ProjectStore.Load(project).ToDocument();

        // Walk the same path the Edit menu does, and report what the history made of it.
        var history = new DocumentHistory();
        var layer = document.Layers[^1];
        var before = layer.Transform.X;
        history.Begin("Flip Canvas Horizontal", document, layer.ID);
        LayerEdits.FlipCanvas(document, horizontally: true);
        history.End(document, layer.ID);
        var after = layer.Transform.X;
        var undone = history.Undo();
        var back = undone?.Document?.Layers[^1].Transform.X;
        var redone = history.Redo();
        var again = redone?.Document?.Layers[^1].Transform.X;
        Console.WriteLine($"edit: layer X {before} → {after} → undo {back} → redo {again}; " +
            $"modified {history.IsModified}, can undo {history.CanUndo}, can redo {history.CanRedo}");

        // Save the edited document the way the File menu does, and read it back.
        var saved = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "saved.comp");
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), saved);
        history.MarkSaved();
        using (var reloaded = ProjectStore.Load(saved).ToDocument())
        {
            Console.WriteLine($"saved and read back: layer X {reloaded.Layers[^1].Transform.X}, " +
                $"flipped {reloaded.Layers[^1].Transform.FlipX}, modified {history.IsModified}");
        }
        Directory.Delete(saved, recursive: true);

        var view = new CanvasView();
        view.Measure(new Size(640, 480));
        view.Arrange(new Rect(0, 0, 640, 480));
        view.Document = document;
        if (showGrid) view.Grid = new LayoutGrid();
        if (pixelGrid) view.PixelGrid = true;
        // Ten steps of a quarter is about 930%, which is past the 800% the pixel grid starts at.
        if (zoomIn)
        {
            for (var step = 0; step < 10; step++) view.ZoomBy(1.25);
        }
        if (shape)
        {
            // The shape tool's drag preview: an ellipse in a colour of its own, over a box of the canvas.
            var box = SKRectI.Create(80, 60, 240, 160);
            view.ShapeKind = Compositor.Core.Format.ShapeKind.Ellipse;
            view.ShapePreviewFor = dragged => (new Compositor.Core.Format.LayerShapeStyle
            {
                Kind = Compositor.Core.Format.ShapeKind.Ellipse, Red = 1, Green = 0.2, Blue = 0.1,
            }, dragged);
            view.PreviewShape(box);
            Console.WriteLine($"showing a shape preview over {box}");
        }
        if (gradient)
        {
            // The gradient tool's drag line, from one corner of the picture towards the other.
            view.PreviewGradient(new SKPoint(document.Width * 0.15f, document.Height * 0.25f),
                new SKPoint(document.Width * 0.8f, document.Height * 0.7f));
            Console.WriteLine("showing the gradient tool's drag line");
        }
        // A preview of the top layer, as a filter panel would show one: the canvas draws it in the document's
        // place while the document is left as it was.
        FilterPreview? shown = null;
        if (preview && document.Layers.Count > 0)
        {
            var id = document.Layers[^1].ID;
            shown = FilterPreview.Begin(document, id);
            if (shown is not null)
            {
                shown.Show((target, layer) => FilterEdits.ApplyAdjustment(target, layer,
                    new LayerAdjustment { Kind = AdjustmentKind.Invert }));
                view.PreviewDocument = shown.Document;
            }
        }
        using var target = new RenderTargetBitmap(new PixelSize(640, 480));
        target.Render(view);
        target.Save(output, new PngBitmapEncoderOptions());
        Console.WriteLine($"wrote {output} for {document.Width}x{document.Height} document at {view.Zoom * 100:0}%" +
            (shown is null ? "" : ", showing a preview"));
        // Stop drawing the preview before it is put away, as the window does.
        view.PreviewDocument = null;
        shown?.Dispose();
        return 0;
    }

    /// <summary>A small document for the self check: a backdrop, a masked patch with a stroke, and a folder.</summary>
    private static CanvasDocument Demo()
    {
        var document = new CanvasDocument(Guid.NewGuid(), 240, 160);
        document.Layers.Add(Solid(new SKColor(40, 70, 120), 0, 0, 240, 160));
        var mask = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Gray8, SKAlphaType.Opaque));
        mask.Erase(new SKColor(210, 210, 210));
        var folder = Solid(SKColors.White, 0, 0, 1, 1, opacity: 0.6);
        folder.IsGroup = true;
        folder.Asset!.Dispose();
        folder.Asset = null;
        var patch = Solid(new SKColor(230, 90, 60), 40, 30, 120, 80);
        patch.Mask = Compositor.Core.Model.LayerMask.AssetFrom(mask);
        patch.Effects = new LayerEffects
        {
            Stroke = new StrokeEffect { Size = 3, Red = 1, Green = 1, Blue = 1, Opacity = 1 },
        };
        patch.ParentID = folder.ID;
        document.Layers.Add(folder);
        document.Layers.Add(patch);
        return document;
    }

    private static ImageLayer Solid(SKColor colour, double x, double y, int width, int height,
        double opacity = 1, LayerBlendMode blend = LayerBlendMode.Normal)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(colour);
        return new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Layer"),
            new Compositor.Core.Model.LayerTransform(x, y, width, height, 0, false, false, LayerSampling.HighQuality), "Layer")
        {
            Opacity = opacity,
            BlendMode = blend,
        };
    }
}
