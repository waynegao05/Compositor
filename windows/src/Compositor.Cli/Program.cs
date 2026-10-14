using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.IO.PSD;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Cli;

/// <summary>
/// Reads and writes `.comp` packages without the editor: what a project holds, what it looks like
/// flattened, and whether reading and writing it back leaves it alone.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Usage();
            return args.Length == 0 ? 2 : 0;
        }
        try
        {
            return args[0] switch
            {
                "info" => Info(args),
                "render" => Render(args),
                "save" => Save(args),
                "import" => Import(args),
                "new" => New(args),
                "crop" => Crop(args),
                "guide" => Guide(args),
                "distort" => Distort(args),
                "warp" => Warp(args),
                "fill" => Fill(args),
                "canvas" => Canvas(args),
                "resize" => Resize(args),
                "trim" => Trim(args),
                "merge" => Merge(args),
                "type" => Type(args),
                "shape" => Shape(args),
                "gradient" => Gradient(args),
                "camera-raw" => CameraRaw(args),
                "sample" => Sample(args),
                "verify-sample" => VerifySample(args),
                _ => Fail($"'{args[0]}' is not a command. Try --help."),
            };
        }
        catch (Exception error)
        {
            return Fail(error.Message);
        }
    }

    private static void Usage() => Console.WriteLine(
        """
        compositor — read and write Compositor projects

          info   <project.comp>               what the project holds
          render <project.comp> <out.png>     flatten it to a PNG
          save   <project.comp> <out.comp>    read it and write it back
          import <image> <out.comp>           start a project from one image
          new    <out.comp> w h [dpi]         start a blank project, with one empty layer
          crop   <in> <out> x y w h           crop the canvas to a rectangle
          guide  <in> <out> h|v <position>    add a guide; - means take them all away
          distort <in> <out> <layer> x1 y1 x2 y2 x3 y3 x4 y4
                                              move a layer's four corners (top left, top right,
                                              bottom right, bottom left) and resample it;
                                              comma-separate layer names to distort several together
          warp   <in> <out> <layer> smudge|liquify x1 y1 x2 y2 [diameter] [strength]
                                              drag the layer's pixels along a stroke
          fill   <in> <out> <layer> r g b [x y w h]
                                              fill the layer, inside a rectangle when one is given
          canvas <in> <out> w h [anchor]      resize the canvas, moving content          resize <in> <out> w h [dpi]         resample the image and every layer
          trim   <in> <out> [tolerance]       crop the canvas to what is drawn on it
          merge  <in> <out> <layer>           merge a layer into what lies beneath it
          type   <in> <out> <text> [size]     add a text layer
          shape  <in> <out> rectangle|ellipse|line [size]   add a shape layer
          gradient <in> <out> <layer> [linear|radial]       fill a layer with a gradient
          camera-raw <in> <out> <layer> [exposure] [contrast] [saturation] [vignette]
                                                              the Camera Raw filter over a layer
          sample <new-output-directory>       create the Chinese Windows acceptance sample
          verify-sample <directory>           check the untouched sample without changing it
        """);

    private static int Sample(string[] args)
    {
        if (args.Length != 2) return Fail("sample needs a new or empty output directory.");
        AcceptanceSample.Create(args[1]);
        Console.WriteLine($"Created and verified the acceptance sample in {Path.GetFullPath(args[1])}");
        return 0;
    }

    private static int VerifySample(string[] args)
    {
        if (args.Length != 2) return Fail("verify-sample needs the sample directory.");
        AcceptanceSample.Verify(args[1]);
        Console.WriteLine("PASS: format 11, Chinese names/text, layers, transparency, blending, guides, save/reopen and PNG export.");
        Console.WriteLine("This checks saved content, not native IME, display scaling or font equality between platforms.");
        return 0;
    }

    private static int Info(string[] args)
    {
        if (args.Length != 2) return Fail("info needs a project path.");
        using var snapshot = ProjectStore.Load(args[1]);
        var manifest = snapshot.Manifest;
        Console.WriteLine($"{args[1]}");
        Console.WriteLine($"  format v{manifest.Version}, {manifest.ColorSpace}, {manifest.Width}x{manifest.Height}, " +
            $"{manifest.Resolution ?? 72} pixels/inch");
        Console.WriteLine($"  {manifest.Layers.Count} layers, {snapshot.Images.Count} images, {snapshot.Masks.Count} masks" +
            (manifest.Guides is { Count: > 0 } guides ? $", {guides.Count} guides" : ""));
        using var document = snapshot.ToDocument();
        foreach (var entry in document.HierarchyEntries())
        {
            var layer = entry.Layer;
            var indent = new string(' ', entry.Depth * 2);
            var flags = new List<string>();
            if (!entry.Visible) flags.Add("hidden");
            if (layer.BlendMode is { } blend && blend != LayerBlendMode.Normal) flags.Add(Spell(blend));
            if (layer.Opacity is { } opacity and < 1) flags.Add($"{opacity:0.##} opacity");
            if (layer.MaskFile is not null) flags.Add(layer.MaskEnabled == false ? "mask off" : "mask");
            if (layer.MaskSourceID is not null) flags.Add("clipped");
            if (layer.MaskPlacement is not null) flags.Add("mask moved");
            var suffix = flags.Count > 0 ? "  [" + string.Join(", ", flags) + "]" : "";
            Console.WriteLine($"    {indent}{layer.Name}  {Kind(layer, snapshot)}{suffix}");
        }
        return 0;
    }

    /// <summary>What the layer is, and how big its pixels are. Adjustments have no image of their own.</summary>
    private static string Kind(ProjectLayerRecord layer, ProjectSnapshot snapshot) => layer switch
    {
        { IsGroup: true } => "folder",
        { Adjustment: { } adjustment } => Spell(adjustment.Kind),
        { Text: not null } => "text",
        { Shape: not null } => "shape",
        _ when layer.ImageFile is not null && snapshot.Images.TryGetValue(layer.ID, out var image) => $"{image.Width}x{image.Height}",
        _ => "empty",
    };

    /// <summary>The name the format spells an enum with, rather than the C# member's name.</summary>
    private static string Spell<T>(T value) where T : struct, Enum =>
        System.Text.Json.JsonSerializer.Serialize(value, ManifestJson.Options).Trim('"');

    private static int Render(string[] args)
    {
        if (args.Length != 3) return Fail("render needs a project path and an output path.");
        if (!ImageWriter.Knows(args[2])) return Fail("The output has to end in .png, .jpg or .jpeg.");
        using var snapshot = ProjectStore.Load(args[1]);
        using var document = snapshot.ToDocument();
        // PNG is written a band of tiles at a time, so any canvas size works; JPEG has to be made whole.
        if (!ImageWriter.Write(document, args[2]))
        {
            return Fail("That canvas is too big to write as one picture; use PNG.");
        }
        Console.WriteLine($"wrote {args[2]} ({document.Width}x{document.Height})");
        return 0;
    }

    private static int Save(string[] args)
    {
        if (args.Length != 3) return Fail("save needs a project path and an output path.");
        using var snapshot = ProjectStore.Load(args[1]);
        ProjectStore.Save(snapshot, args[2]);
        Console.WriteLine($"wrote {args[2]}");
        return 0;
    }

    private static int Import(string[] args)
    {
        if (args.Length != 3) return Fail("import needs an image path and an output path.");
        var name = Path.GetFileName(args[1]);
        var extension = Path.GetExtension(args[1]);
        if (extension.Equals(".psd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".psb", StringComparison.OrdinalIgnoreCase))
        {
            var photoshop = PsdImporter.Read(args[1]);
            ProjectStore.Save(photoshop.Snapshot(), args[2]);
            Console.WriteLine($"wrote {args[2]} ({photoshop.Manifest.Width}x{photoshop.Manifest.Height}, " +
                $"{photoshop.Manifest.Layers.Count} layers from {name})");
            foreach (var note in photoshop.Notes) Console.WriteLine($"  kept as pixels: {note.Layer} — {note.What}");
            return 0;
        }
        var image = ImageImporter.Decode(args[1]);
        using var document = ImageImporter.NewDocument(image);
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), args[2]);
        Console.WriteLine($"wrote {args[2]} ({document.Width}x{document.Height} from {name})");
        return 0;
    }

    private static int Crop(string[] args)
    {
        if (args.Length != 7) return Fail("crop needs an input, an output and x y width height.");
        if (!int.TryParse(args[3], out var x) || !int.TryParse(args[4], out var y)
            || !int.TryParse(args[5], out var width) || !int.TryParse(args[6], out var height))
        {
            return Fail("crop's x, y, width and height must be whole numbers.");
        }
        using var snapshot = ProjectStore.Load(args[1]);
        using var document = snapshot.ToDocument();
        if (!CanvasEdits.Crop(document, SKRectI.Create(x, y, width, height))) return Fail("That crop leaves the document as it was.");
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), args[2]);
        Console.WriteLine($"wrote {args[2]} ({document.Width}x{document.Height})");
        return 0;
    }

    private static int Canvas(string[] args)
    {
        if (args.Length is not (5 or 6)) return Fail("canvas needs an input, an output, width and height, and an anchor if you want one.");
        if (!int.TryParse(args[3], out var width) || !int.TryParse(args[4], out var height))
        {
            return Fail("canvas's width and height must be whole numbers.");
        }
        var anchor = CanvasEdits.CentreAnchor;
        if (args.Length == 6 && !int.TryParse(args[5], out anchor)) return Fail("canvas's anchor must be 0 to 8.");
        using var snapshot = ProjectStore.Load(args[1]);
        using var document = snapshot.ToDocument();
        if (!CanvasEdits.Resize(document, width, height, anchor)) return Fail("That canvas size leaves the document as it was.");
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), args[2]);
        Console.WriteLine($"wrote {args[2]} ({document.Width}x{document.Height}, anchor {anchor})");
        return 0;
    }

    private static int Resize(string[] args)
    {
        if (args.Length is not (5 or 6)) return Fail("resize needs an input, an output and width and height, and a resolution if you want one.");
        if (!int.TryParse(args[3], out var width) || !int.TryParse(args[4], out var height))
        {
            return Fail("resize's width and height must be whole numbers.");
        }
        using var snapshot = ProjectStore.Load(args[1]);
        using var document = snapshot.ToDocument();
        var resolution = document.Resolution;
        if (args.Length == 6 && !double.TryParse(args[5], out resolution)) return Fail("resize's resolution must be a number.");
        if (!ImageEdits.Resize(document, width, height, resolution)) return Fail("That image size leaves the document as it was.");
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), args[2]);
        Console.WriteLine($"wrote {args[2]} ({document.Width}x{document.Height} at {document.Resolution:0} pixels per inch)");
        return 0;
    }

    /// <summary>
    /// Adds one alignment guide, or takes them all away with a dash: the only way to put a guide on a project
    /// without a pointer, which is what makes the canvas' guide drawing checkable from a command line.
    /// </summary>
    private static int Guide(string[] args)
    {
        if (args.Length is not (4 or 5)) return Fail("guide needs an input, an output, an axis (h or v) and a position, or - to clear them.");
        using var snapshot = ProjectStore.Load(args[1]);
        using var document = snapshot.ToDocument();
        if (args.Length == 4 && args[3] == "-")
        {
            var cleared = GuideEdits.Clear(document);
            if (cleared == 0) return Fail("That project has no guides to clear.");
            ProjectStore.Save(ProjectSnapshot.FromDocument(document), args[2]);
            Console.WriteLine($"wrote {args[2]} ({cleared} guides cleared)");
            return 0;
        }
        if (args.Length == 4) return Fail("A guide needs a position as well as an axis.");
        var axis = args[3] switch
        {
            "h" or "horizontal" => GuideAxis.Horizontal,
            "v" or "vertical" => GuideAxis.Vertical,
            _ => (GuideAxis?)null,
        };
        if (axis is not { } direction) return Fail("A guide runs h or v.");
        if (!double.TryParse(args[4], out var position)) return Fail("A guide's position is a number of pixels.");
        if (GuideEdits.Add(document, direction, position) is null)
        {
            return Fail("The guide was refused: that project holds as many as it may, or the position is not one a guide may sit at.");
        }
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), args[2]);
        Console.WriteLine($"wrote {args[2]} ({direction} guide at {position:0.##})");
        return 0;
    }

    /// <summary>
    /// Free distortion from the command line: the four corners in document pixels, in handle order. The only
    /// way to distort a layer without a pointer, which is what makes the whole path checkable.
    /// </summary>
    private static int Distort(string[] args)
    {
        if (args.Length != 12) return Fail("distort needs an input, an output, a layer and four corners as eight numbers.");
        var numbers = new float[8];
        for (var index = 0; index < 8; index++)
        {
            if (!float.TryParse(args[index + 4], out numbers[index])) return Fail("A corner is a pair of numbers.");
        }
        using var snapshot = ProjectStore.Load(args[1]);
        using var document = snapshot.ToDocument();
        var ids = new List<Guid>();
        // Several names, comma-separated, are distorted together: the corners are the box around them all and
        // each layer is carried by that one perspective, as dragging the group's corner does in the window.
        foreach (var name in args[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var matches = document.Layers
                .Where(layer => string.Equals(layer.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) return Fail($"No layer in that project is called '{name}'.");
            if (matches.Count > 1) return Fail($"{matches.Count} layers are called '{name}'; rename one of them first.");
            ids.Add(matches[0].ID);
        }
        if (ids.Count == 0) return Fail("distort needs the name of at least one layer.");
        var corners = new SKPoint[4];
        for (var index = 0; index < 4; index++)
        {
            corners[index] = new SKPoint(numbers[index * 2], numbers[index * 2 + 1]);
        }
        var box = ids.Count > 1 ? TransformEdits.GroupBox(document, ids) : null;
        if (ids.Count > 1 && box is null) return Fail("There is nothing with pixels in those layers to distort.");
        var distorted = box is { } group
            ? DistortEdits.Distort(document, ids, group, corners)
            : DistortEdits.Distort(document, ids[0], corners);
        if (!distorted)
        {
            return Fail("The distortion was refused: that shape cannot be made from those corners, or it would not fit in memory.");
        }
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), args[2]);
        var names = string.Join(", ", ids.Select(id => document.Layers.First(layer => layer.ID == id).Name));
        Console.WriteLine($"wrote {args[2]} (distorted {names})");
        return 0;
    }

    private static int Warp(string[] args)
    {
        if (args.Length is < 9 or > 11)
        {
            return Fail("warp needs an input, an output, a layer, a mode and two points, with a diameter and a strength if you want them.");
        }
        var mode = args[4].ToLowerInvariant() switch
        {
            "smudge" => WarpMode.Smudge,
            "liquify" => WarpMode.Liquify,
            _ => (WarpMode?)null,
        };
        if (mode is not { } kind) return Fail("warp's mode is 'smudge' or 'liquify'.");
        var numbers = new float[4];
        for (var index = 0; index < 4; index++)
        {
            if (!float.TryParse(args[index + 5], out numbers[index])) return Fail("A point is a pair of numbers.");
        }
        var diameter = 40.0;
        var strength = 1.0;
        if (args.Length >= 10 && !double.TryParse(args[9], out diameter)) return Fail("The diameter is a number of pixels.");
        if (args.Length >= 11 && !double.TryParse(args[10], out strength)) return Fail("The strength is a number from 0 to 1.");
        using var snapshot = ProjectStore.Load(args[1]);
        using var document = snapshot.ToDocument();
        var matches = document.Layers
            .Where(layer => string.Equals(layer.Name, args[3], StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return Fail($"No layer in that project is called '{args[3]}'.");
        if (matches.Count > 1) return Fail($"{matches.Count} layers are called '{args[3]}'; rename one of them first.");
        var points = new[] { new SKPoint(numbers[0], numbers[1]), new SKPoint(numbers[2], numbers[3]) };
        if (!WarpEdits.Warp(document, matches[0].ID, points, kind, new BrushSettings(Diameter: diameter, Opacity: strength)))
        {
            return Fail("The stroke was refused: the layer holds no pixels, or the stroke never reached them.");
        }
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), args[2]);
        var layer = document.Layers.First(entry => entry.ID == matches[0].ID);
        Console.WriteLine($"wrote {args[2]} ({kind} over '{layer.Name}' at {layer.Asset!.Width}x{layer.Asset.Height})");
        return 0;
    }

    private static int Fill(string[] args)
    {
        if (args.Length is not (7 or 11))
        {
            return Fail("fill needs an input, an output, a layer, three color numbers and, if you want one, a rectangle.");
        }
        var colour = new byte[3];
        for (var index = 0; index < 3; index++)
        {
            if (!byte.TryParse(args[index + 4], out colour[index])) return Fail("The color is three numbers, 0 to 255.");
        }
        var numbers = new int[4];
        for (var index = 0; index < args.Length - 7; index++)
        {
            if (!int.TryParse(args[index + 7], out numbers[index])) return Fail("A rectangle is four whole numbers.");
        }
        using var snapshot = ProjectStore.Load(args[1]);
        using var document = snapshot.ToDocument();
        var matches = document.Layers
            .Where(layer => string.Equals(layer.Name, args[3], StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return Fail($"No layer in that project is called '{args[3]}'.");
        if (matches.Count > 1) return Fail($"{matches.Count} layers are called '{args[3]}'; rename one of them first.");
        var where = args.Length == 11
            ? SelectionEdits.Select(document, SKRectI.Create(numbers[0], numbers[1], numbers[2], numbers[3]))
            : true;
        if (!where) return Fail("That rectangle selects nothing.");
        if (!FillEdits.Fill(document, matches[0].ID, new SKColor(colour[0], colour[1], colour[2])))
        {
            return Fail("The fill was refused: the layer holds no pixels, or the selection does not reach it.");
        }
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), args[2]);
        Console.WriteLine($"wrote {args[2]} (filled '{args[3]}' with {colour[0]},{colour[1]},{colour[2]}" +
            (args.Length == 11
                ? $" inside {numbers[2]}x{numbers[3]} at {numbers[0]},{numbers[1]})"
                : ")"));
        return 0;
    }

    private static int New(string[] args)
    {
        if (args.Length is not (4 or 5)) return Fail("new needs an output and a size, and a resolution if you want one.");
        if (!int.TryParse(args[2], out var width) || !int.TryParse(args[3], out var height))
        {
            return Fail("A canvas size is two whole numbers of pixels.");
        }
        var resolution = 72.0;
        if (args.Length == 5 && !double.TryParse(args[4], out resolution))
        {
            return Fail("The resolution is a number of pixels per inch.");
        }
        using var document = LayerPlacement.NewDocument(width, height, resolution);
        if (document is null)
        {
            return Fail($"A canvas is at least one pixel each way, at most {DocumentLimits.MaxSide} a side, " +
                $"and holds at most {DocumentLimits.MaxSurfaceMegapixels} megapixels.");
        }
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), args[1]);
        Console.WriteLine($"wrote {args[1]} ({document.Width}x{document.Height} at {document.Resolution:0.##} per inch, " +
            $"{document.Layers.Count} empty layer)");
        return 0;
    }

    private static int Trim(string[] args)
    {
        if (args.Length is not (3 or 4)) return Fail("trim needs an input and an output, and a tolerance if you want one.");
        byte tolerance = 0;
        if (args.Length == 4 && !byte.TryParse(args[3], out tolerance)) return Fail("trim's tolerance must be 0 to 255.");
        using var snapshot = ProjectStore.Load(args[1]);
        using var document = snapshot.ToDocument();
        if (!TrimEdits.Trim(document, new TrimOptions(Tolerance: tolerance)))
        {
            return Fail("There was nothing to trim: the canvas is already as tight as it goes, or is empty.");
        }
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), args[2]);
        Console.WriteLine($"wrote {args[2]} ({document.Width}x{document.Height})");
        return 0;
    }

    /// <summary>The layer is named rather than numbered, because a number would move under the merge.</summary>
    private static int Merge(string[] args)
    {
        if (args.Length != 4) return Fail("merge needs an input, an output and the name of the layer to merge.");
        using var snapshot = ProjectStore.Load(args[1]);
        using var document = snapshot.ToDocument();
        var matches = document.Layers
            .Where(layer => string.Equals(layer.Name, args[3], StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return Fail($"No layer in that project is called '{args[3]}'.");
        if (matches.Count > 1) return Fail($"{matches.Count} layers are called '{args[3]}'; rename one of them first.");
        var id = matches[0].ID;
        if (LayerMerge.Plan(document, [id], id) is not { } plan) return Fail($"'{args[3]}' has nothing to merge with.");
        if (LayerMerge.Merge(document, [id], id) is not { } made)
        {
            return Fail("The merge was refused: that canvas is too big to composite at once, or the result would not load back.");
        }
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), args[2]);
        var merged = document.Layers.First(layer => layer.ID == made);
        Console.WriteLine($"wrote {args[2]} ({plan.Action}: '{merged.Name}' is now " +
            $"{merged.Asset!.Width}x{merged.Asset.Height} at {merged.Transform.X:0.##},{merged.Transform.Y:0.##}, " +
            $"{document.Layers.Count} layers left)");
        return 0;
    }

    /// <summary>Sets a string of text as a new layer, drawn from the style the way the Type tool does.</summary>
    private static int Type(string[] args)
    {
        if (args.Length is not (4 or 5)) return Fail("type needs a project, an output, the text, and a size if you want one.");
        using var snapshot = ProjectStore.Load(args[1]);
        using var document = snapshot.ToDocument();
        var style = new LayerTextStyle
        {
            Content = args[3],
            FontName = "Arial",
            FontSize = 72,
            Red = 0,
            Green = 0,
            Blue = 0,
        };
        if (args.Length == 5)
        {
            if (!double.TryParse(args[4], out var size)) return Fail("type's size must be a number.");
            style.FontSize = size;
        }
        if (!style.IsValid) return Fail("That text or size is not one a text layer may hold.");
        var origin = new SKPoint((float)(document.Width / 4.0), (float)(document.Height / 4.0));
        if (TextEdits.Add(document, style, origin) is not { } id)
        {
            return Fail("That text could not be drawn: its box is too big for one surface, or the project holds too many layers.");
        }
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), args[2]);
        var layer = document.Layers.First(candidate => candidate.ID == id);
        Console.WriteLine($"wrote {args[2]} (text '{layer.Name}' {layer.Asset!.Width}x{layer.Asset.Height} " +
            $"at {origin.X:0},{origin.Y:0}, {document.Layers.Count} layers)");
        return 0;
    }

    /// <summary>Adds a shape layer, drawn the way the Shape tool draws one.</summary>
    private static int Shape(string[] args)
    {
        if (args.Length is not (4 or 5)) return Fail("shape needs a project, an output, a shape name, and a size if you want one.");
        var kind = args[3].ToLowerInvariant() switch
        {
            "rectangle" => ShapeKind.Rectangle,
            "ellipse" => ShapeKind.Ellipse,
            "line" => ShapeKind.Line,
            _ => (ShapeKind?)null,
        };
        if (kind is not { } shape) return Fail($"'{args[3]}' is not a shape; try rectangle, ellipse or line.");
        using var snapshot = ProjectStore.Load(args[1]);
        using var document = snapshot.ToDocument();
        var style = new LayerShapeStyle { Kind = shape, Red = 0.9, Green = 0.2, Blue = 0.1, CornerRadius = 8 };
        var side = 160;
        if (args.Length == 5 && !int.TryParse(args[4], out side)) return Fail("shape's size must be a whole number.");
        var box = SKRectI.Create(document.Width / 4, document.Height / 4, side, (int)(side * 0.6));
        if (shape == ShapeKind.Line)
        {
            style.LineWidth = 6;
            style.Start = new JsonPoint(0, 0);
            style.End = new JsonPoint(1, 1);
        }
        if (ShapeEdits.Add(document, style, box, null) is not { } id)
        {
            return Fail("That shape could not be drawn: its box is too big for one surface, or the project holds too many layers.");
        }
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), args[2]);
        var layer = document.Layers.First(candidate => candidate.ID == id);
        Console.WriteLine($"wrote {args[2]} ({shape} '{layer.Name}' {layer.Asset!.Width}x{layer.Asset.Height} " +
            $"at {box.Left},{box.Top}, {document.Layers.Count} layers)");
        return 0;
    }

    /// <summary>Fills a layer's pixels with a gradient, the way the Gradient tool does.</summary>
    private static int Gradient(string[] args)
    {
        if (args.Length is not (4 or 5)) return Fail("gradient needs a project, an output, the layer to fill, and a shape if you want one.");
        var shape = args.Length == 5
            ? args[4].ToLowerInvariant() switch
            {
                "linear" => GradientShape.Linear,
                "radial" => GradientShape.Radial,
                _ => (GradientShape?)null,
            }
            : GradientShape.Linear;
        if (shape is not { } fill) return Fail($"'{args[4]}' is not a gradient shape; try linear or radial.");
        using var snapshot = ProjectStore.Load(args[1]);
        using var document = snapshot.ToDocument();
        var matches = document.Layers.Where(layer => string.Equals(layer.Name, args[3], StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return Fail($"No layer in that project is called '{args[3]}'.");
        if (matches.Count > 1) return Fail($"{matches.Count} layers are called '{args[3]}'; rename one of them first.");
        var id = matches[0].ID;
        var start = new SKPoint(0, document.Height / 2f);
        var end = new SKPoint(document.Width, document.Height / 2f);
        var from = new SKColor(255, 255, 255, 255);
        if (!GradientEdits.Fill(document, id, mask: false, start, end, from, new SKColor(255, 255, 255, 0), 1, fill))
        {
            return Fail("That layer holds no pixels to fill, or the drag was too short.");
        }
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), args[2]);
        Console.WriteLine($"wrote {args[2]} ({fill} gradient over '{matches[0].Name}', from {start.X},{start.Y} to {end.X},{end.Y})");
        return 0;
    }

    /// <summary>Runs the Camera Raw filter's Light and Color stages, and a vignette, over one layer.</summary>
    private static int CameraRaw(string[] args)
    {
        if (args.Length is < 4 or > 8)
        {
            return Fail("camera-raw needs a project, an output, the layer to filter, and up to four amounts "
                + "(exposure, contrast, saturation, vignette).");
        }
        var amounts = new double[4];
        for (var index = 4; index < args.Length; index++)
        {
            if (!double.TryParse(args[index], out amounts[index - 4]))
            {
                return Fail("camera-raw's amounts must be numbers.");
            }
        }
        using var snapshot = ProjectStore.Load(args[1]);
        using var document = snapshot.ToDocument();
        var matches = document.Layers
            .Where(layer => string.Equals(layer.Name, args[3], StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return Fail($"No layer in that project is called '{args[3]}'.");
        if (matches.Count > 1) return Fail($"{matches.Count} layers are called '{args[3]}'; rename one of them first.");
        var settings = new CameraRawSettings
        {
            Exposure = amounts[0],
            Contrast = amounts[1],
            Saturation = amounts[2],
            VignetteAmount = amounts[3],
        };
        if (!settings.IsValid) return Fail("Those amounts are outside the ranges the filter allows.");
        if (!CameraRawEdits.Apply(document, matches[0].ID, settings))
        {
            return Fail("The filter was refused: that layer holds no pixels, or they would not fit in memory.");
        }
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), args[2]);
        Console.WriteLine($"wrote {args[2]} (Camera Raw over '{matches[0].Name}': exposure {settings.Exposure:0.##}, " +
            $"contrast {settings.Contrast:0}, saturation {settings.Saturation:0}, vignette {settings.VignetteAmount:0})");
        return 0;
    }

    private static int Fail(string message)    {
        Console.Error.WriteLine($"compositor: {message}");
        return 1;
    }
}
