using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Compositor.Core.Document;
using Compositor.Core.Format;
using SkiaSharp;

namespace Compositor.Desktop;

/// <summary>Exercises the real dialog controls and their Apply/Preview paths without native input.</summary>
internal static class DialogRegressionChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static string Run()
    {
        var report = new List<string>();
        NewDocument(); report.Add("A4/A3 select 300 PPI; screen presets restore 72 PPI");
        Effects(); report.Add("all six effects apply RGB values to the returned effect and leave the original unchanged");
        Levels(); report.Add("five Levels fields edit all four channels independently and survive channel switching");
        Hsv(); report.Add("HSV retains Master, all color ranges and bands, with independent edits and cancel safety");
        Points(); report.Add("Camera Raw point colors retain repeated edits, selection changes, additions and removals");
        Filters(); report.Add("Filter sliders and checkboxes honor Preview on/off, including Reset");
        Dither(); report.Add("Dither look, pixel shape, ink, marks and ASCII text all refresh preview");
        Guides(); report.Add("guide positions outside the canvas are accepted; non-finite and out-of-range values are rejected");
        CameraLayout(); report.Add("112 Camera Raw rows fit a 440-pixel panel; choices preview and Reset restores model defaults");
        report.Add(MainWindow.PreviewBoundarySelfCheck());
        return string.Join(Environment.NewLine, report.Select(line => "PASS: " + line));
    }

    private static void NewDocument()
    {
        var dialog = new NewDocumentDialog();
        var presets = Field<ComboBox>(dialog, "_preset");
        foreach (var index in new[] { 8, 9 })
        {
            presets.SelectedIndex = index;
            Require(Field<TextBox>(dialog, "_resolution").Text == "300", "paper preset has the wrong PPI");
        }
        presets.SelectedIndex = 3;
        Require(Field<TextBox>(dialog, "_resolution").Text == "72", "screen preset retains paper PPI");
        dialog.Close();
    }

    private static void Effects()
    {
        foreach (var kind in Enum.GetValues<EffectKind>())
        {
            var bag = new LayerEffects();
            var name = kind == EffectKind.DropShadow ? "Shadow" : kind.ToString();
            var property = typeof(LayerEffects).GetProperty(name)!;
            var original = Activator.CreateInstance(property.PropertyType)!;
            foreach (var (channel, value) in new[] { ("Red", .13), ("Green", .27), ("Blue", .41) })
                original.GetType().GetProperty(channel)!.SetValue(original, value);
            property.SetValue(bag, original);
            var dialog = Create<EffectDialog>(kind, bag);
            var rows = Field<List<(Slider Slider, Action<object, double> Set)>>(dialog, "_rows");
            var colorStart = kind switch
            {
                EffectKind.Stroke => 1, EffectKind.DropShadow or EffectKind.InnerShadow => 3,
                EffectKind.ColorOverlay => 0, _ => 1,
            };
            rows[colorStart].Slider.Value = .2;
            rows[colorStart + 1].Slider.Value = .4;
            rows[colorStart + 2].Slider.Value = .6;
            Invoke(dialog, "Accept");
            var result = property.GetValue(Field<LayerEffects>(dialog, "_result"))!;
            Require(Read(result, "Red") == .2 && Read(result, "Green") == .4 && Read(result, "Blue") == .6,
                kind + " discarded selected RGB values");
            Require(Read(original, "Red") == .13 && Read(original, "Green") == .27 && Read(original, "Blue") == .41,
                kind + " mutated the original effect");
        }
    }

    private static void Levels()
    {
        var start = new LayerAdjustment { Kind = AdjustmentKind.Levels };
        start.Levels.Ranges[3].Gamma = 1.7;
        var dialog = new AdjustmentDialog(start);
        var rows = AdjustmentRows(dialog);
        var channel = Field<ComboBox>(dialog, "_levelsChannel");
        for (var c = 0; c < 4; c++)
        {
            channel.SelectedIndex = c;
            double[] values = [10 + c, 1.1 + c / 10.0, 240 - c, 20 + c, 220 - c];
            for (var i = 0; i < 5; i++) rows[i].Slider.Value = values[i];
        }
        channel.SelectedIndex = 0;
        Require(rows[0].Slider.Value == 10 && rows[1].Slider.Value == 1.1, "Levels switching lost RGB values");
        var built = (LayerAdjustment)Invoke(dialog, "Built", start)!;
        for (var c = 0; c < 4; c++)
        {
            var range = built.Levels.Ranges[c];
            Require(range.Black == 10 + c && range.Gamma == 1.1 + c / 10.0 && range.White == 240 - c
                && range.OutputBlack == 20 + c && range.OutputWhite == 220 - c, "Levels values were ignored");
        }
        Require(start.Levels.Ranges[0].Black == 0 && start.Levels.Ranges[3].Gamma == 1.7,
            "Levels preview mutated original settings");
        dialog.Close();
    }

    private static void Hsv()
    {
        var start = new LayerAdjustment { Kind = AdjustmentKind.HueSaturation, HsvSettings = new HueSaturationSettings() };
        start.HsvSettings.Set(9, 8, 7);
        start.HsvSettings.Bands.Set(ColorRange.Reds, new HueBand { FalloffStart = 310, RangeStart = 340, RangeEnd = 20, FalloffEnd = 50 });
        var dialog = new AdjustmentDialog(start);
        var range = Field<ComboBox>(dialog, "_range");
        var rows = AdjustmentRows(dialog);
        for (var c = 1; c < HueBand.Ranges.Count; c++)
        {
            range.SelectedIndex = c;
            rows[0].Slider.Value = c * 10;
            rows[1].Slider.Value = c * 2;
            rows[2].Slider.Value = -c;
        }
        range.SelectedIndex = 0;
        Require(rows[0].Slider.Value == 9 && rows[1].Slider.Value == 8 && rows[2].Slider.Value == 7,
            "switching HSV ranges changed Master");
        var built = ((LayerAdjustment)Invoke(dialog, "Built", start)!).ResolvedHSV;
        for (var c = 1; c < HueBand.Ranges.Count; c++)
        {
            var amount = built.Adjustments.Find(HueBand.Ranges[c])!;
            Require(amount.Hue == c * 10 && amount.Saturation == c * 2 && amount.Lightness == -c,
                "HSV lost a previously edited range");
        }
        Require(built.Bands.Find(ColorRange.Reds)!.RangeStart == 340
            && start.HsvSettings.Current.Hue == 9 && start.HsvSettings.Adjustments.Find(ColorRange.Blues) is null,
            "HSV dropped a custom band or changed original settings");
        dialog.Close();
    }

    private static void Points()
    {
        var start = new CameraRawSettings
        {
            Points = [new() { Hue = 20, Saturation = .3, Luminance = .4 }, new() { Hue = 210, Saturation = .7, Luminance = .6 }],
            ShadowHue = 81,
        };
        var panel = new CameraRawPanel(start, SKColors.Coral);
        CameraRawSettings? preview = null;
        panel.Preview = (settings, _, _, _) => preview = settings;
        panel.Move("Turn the hue", 12);
        panel.Move("Turn the hue", 24);
        Require(preview?.Points[0].HueShift == 24 && panel.Current().ShadowHue == 81,
            "repeated point edits use a stale object or change a grading row");
        var list = Field<ListBox>(panel, "_points");
        list.SelectedIndex = 1;
        Require(panel.SetTo("Hue") == 210 && panel.SetTo("Turn the hue") == 0, "point selection loaded the wrong rows");
        panel.Move("Turn the hue", -35);
        list.SelectedIndex = 0;
        Require(panel.SetTo("Turn the hue") == 24 && panel.Current().Points[1].HueShift == -35,
            "point switching lost edits");
        Invoke(panel, "AddPoint");
        panel.Move("Turn the hue", 50);
        Require(panel.Current().Points.Count == 3 && panel.Current().Points[2].HueShift == 50, "new point did not receive edits");
        Invoke(panel, "RemovePoint");
        Require(panel.Current().Points.Count == 2 && start.Points[0].HueShift == 0, "removal or cancel safety failed");
    }

    private static void Filters()
    {
        var dialog = Create<FilterDialog>(FilterKind.AddNoise, new FilterSettings());
        var seen = new List<FilterSettings?>();
        dialog.Preview = settings => seen.Add(settings?.Copy());
        var preview = Field<CheckBox>(dialog, "_preview");
        var rows = Field<List<(Slider Slider, Action<FilterSettings, double> Set)>>(dialog, "_rows");
        var checks = Field<List<(CheckBox Box, Action<FilterSettings, bool> Set, bool Fallback)>>(dialog, "_checks");
        preview.IsChecked = false;
        rows[0].Slider.Value = 23;
        checks[0].Box.IsChecked = true;
        Require(seen.Count == 3 && seen.All(value => value is null), "disabled Filter preview still applied changes");
        preview.IsChecked = true;
        Require(seen[^1] is { NoiseAmount: 23, NoiseGaussian: true }, "reenabling preview omitted current settings");
        checks[1].Box.IsChecked = true;
        Require(seen[^1]?.NoiseMonochromatic == true, "Filter checkbox did not preview");
        Invoke(dialog, "Restore");
        Require(seen[^1] is { NoiseGaussian: false, NoiseMonochromatic: false }, "Filter Reset did not refresh");
        dialog.Close();
    }

    private static void Dither()
    {
        var dialog = Create<DitherDialog>(DitherStyle.Atkinson, new DitherSettings());
        var count = 0;
        DitherSettings? seen = null;
        DitherStyle look = default;
        dialog.Preview = (style, settings) => { count++; look = style; seen = settings.Copy(); };
        var styles = Field<List<DitherStyle?>>(dialog, "_styleRows");
        Field<ComboBox>(dialog, "_style").SelectedIndex = styles.IndexOf(DitherStyle.Ascii);
        Field<ComboBox>(dialog, "_shape").SelectedIndex = 1;
        Field<ComboBox>(dialog, "_colors").SelectedIndex = 1;
        Field<CheckBox>(dialog, "_lightOnDark").IsChecked = false;
        Field<TextBox>(dialog, "_characters").Text = "Aa中文";
        Require(count == 5 && look == DitherStyle.Ascii && seen?.Characters == "Aa中文"
            && !seen.LightOnDark && seen.PixelShape == DitherPixelShape.Dot && seen.Colors == DitherColors.TwoColors,
            "Dither choices or characters did not preview");
        dialog.Close();
    }

    private static void Guides()
    {
        foreach (var (text, accepted) in new[]
        {
            ("-1", true), ("1001", true), ("1000000", true), ("-1000000", true),
            ("NaN", false), ("Infinity", false), ("1000001", false),
        })
        {
            var dialog = Create<GuideDialog>(100, 80);
            Field<TextBox>(dialog, "_position").Text = text;
            Invoke(dialog, "Accept");
            var result = dialog.GetType().GetField("_result", Private)!.GetValue(dialog);
            Require((result is not null) == accepted, "guide validation failed for " + text);
            if (!accepted) Require(!string.IsNullOrEmpty(Field<TextBlock>(dialog, "_error").Text), "invalid guide lacks an explanation");
            dialog.Close();
        }
    }

    private static void CameraLayout()
    {
        var panel = new CameraRawPanel(new CameraRawSettings { SharpenRadius = 36, ProcessVersion = 2 }, SKColors.White);
        var previews = 0;
        panel.Preview = (_, _, _, _) => previews++;
        Field<ComboBox>(panel, "_glowStyle").SelectedIndex = 1;
        Field<ComboBox>(panel, "_vignetteStyle").SelectedIndex = 1;
        Field<ComboBox>(panel, "_geometryProjection").SelectedIndex = 1;
        Require(previews == 3, "Camera Raw choices did not preview");
        panel.Reset();
        var settings = panel.Current();
        Require(settings.SharpenRadius == 10 && settings.ProcessVersion == 6 && settings.IsValid
            && settings.GlowStyle == 0 && settings.Geometry.Projection == GeometryProjection.Perspective,
            "Camera Raw Reset differs from model defaults");
        var host = new Window { Width = 440, Height = 720, Content = panel.View };
        try
        {
            host.Show();
            Dispatcher.UIThread.RunJobs();
            host.UpdateLayout();
            var rows = Field<List<(Slider Slider, Action<CameraRawSettings, double> Set, TextBlock Readout, string Format)>>(panel, "_rows");
            Require(rows.Count == 112, "Camera Raw row count changed unexpectedly");
            foreach (var (slider, _, readout, _) in rows)
            {
                var row = (Grid)slider.Parent!;
                Require(row.Bounds.Width <= 440 && slider.Bounds.Width > 10
                    && readout.Bounds.Right <= row.Bounds.Width + .5,
                    "Camera Raw slider/readout extends outside its panel");
            }
        }
        finally { host.Close(); }
    }

    private static List<(Slider Slider, Action<LayerAdjustment, double> Set)> AdjustmentRows(AdjustmentDialog dialog) =>
        Field<List<(Slider Slider, Action<LayerAdjustment, double> Set)>>(dialog, "_rows");

    private static T Create<T>(params object?[] arguments) => (T)Activator.CreateInstance(typeof(T), Private, null, arguments, null)!;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] arguments) =>
        target.GetType().GetMethod(name, Private)!.Invoke(target, arguments);
    private static double Read(object target, string property) => (double)target.GetType().GetProperty(property)!.GetValue(target)!;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
