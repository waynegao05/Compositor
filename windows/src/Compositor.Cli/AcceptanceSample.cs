using System.Text;
using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.Model;
using SkiaSharp;
using RuntimeTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Cli;

/// <summary>A small, self-contained project for exercising the Windows editor with Chinese content.</summary>
internal static class AcceptanceSample
{
    private const string ProjectName = "中文验收工程.comp";
    private const string ReferenceName = "参考导出.png";
    private const string SourceName = "导入素材.png";
    private const string InstructionsName = "验收步骤.zh-CN.md";
    private const int Width = 960;
    private const int Height = 640;
    private const string Title = "中文验收 Compositor";
    private const string Paragraph = "中文输入、透明图层与混合模式。\n保存后重新打开，文字和参考线应保留。";
    private static readonly string[] LayerNames =
        ["浅色底板", "青色矩形", "橙色正片叠底", "导入素材", "中文标题", "中文段落"];

    public static void Create(string directory)
    {
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        RequireEmptyTarget(target);
        RequireChineseFont();
        var parent = Path.GetDirectoryName(target)
            ?? throw new IOException("The sample needs a directory below a filesystem root.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".compositor-sample-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            using (var source = SourceImage()) WritePng(source, Path.Combine(staging, SourceName));
            using (var document = Document(staging))
            {
                ProjectStore.Save(ProjectSnapshot.FromDocument(document), Path.Combine(staging, ProjectName));
                Require(ImageWriter.Write(document, Path.Combine(staging, ReferenceName)), "Could not export the reference PNG.");
            }
            File.WriteAllText(Path.Combine(staging, InstructionsName), Instructions, new UTF8Encoding(false));
            Verify(staging);
            // Recheck after generation. Nonrecursive deletion also refuses a directory populated meanwhile.
            RequireEmptyTarget(target);
            if (Directory.Exists(target)) Directory.Delete(target);
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public static void Verify(string directory)
    {
        var root = Path.GetFullPath(directory);
        using var snapshot = ProjectStore.Load(Path.Combine(root, ProjectName));
        ValidateManifest(snapshot);
        Require(File.Exists(Path.Combine(root, InstructionsName)), "The manual acceptance instructions are missing.");
        using var reference = ReadPng(Path.Combine(root, ReferenceName), Width, Height);
        CheckCanvasPixels(reference);
        using var source = ReadPng(Path.Combine(root, SourceName), 256, 160);
        Require(source.GetPixel(0, 0).Alpha == 0 && source.GetPixel(128, 80).Alpha == 255,
            "The import image must contain both transparent and opaque pixels.");

        // The snapshot owns these pixels; this view does not allocate a second set of images.
        var document = snapshot.ToDocument();
        var temporary = Directory.CreateTempSubdirectory("Compositor-中文验收-").FullName;
        try
        {
            var roundTripPath = Path.Combine(temporary, "保存重开.comp");
            ProjectStore.Save(ProjectSnapshot.FromDocument(document), roundTripPath);
            using var reopened = ProjectStore.Load(roundTripPath);
            ValidateManifest(reopened);
            Require(ManifestJson.Serialize(snapshot.Manifest).AsSpan().SequenceEqual(ManifestJson.Serialize(reopened.Manifest)),
                "The manifest changed during save/reopen.");
            foreach (var (id, original) in snapshot.Images)
            {
                Require(reopened.Images.TryGetValue(id, out var saved), "A layer image was lost during save/reopen.");
                CheckSamePixels(original.Image, saved!.Image, "Layer pixels changed during save/reopen.");
            }

            var exportedPath = Path.Combine(temporary, "重新导出.png");
            Require(ImageWriter.Write(reopened.ToDocument(), exportedPath), "PNG export was refused.");
            using var exported = ReadPng(exportedPath, Width, Height);
            CheckCanvasPixels(exported);
            // Stored text rasters are used here. This does not compare freshly laid-out fonts across OSes.
            CheckSamePixels(reference, exported, "The stored project does not reproduce its reference PNG.");
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static void RequireEmptyTarget(string path)
    {
        if (File.Exists(path)) throw new IOException("The sample destination is already a file.");
        if (!Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The sample destination must not be a symbolic link or junction.");
        if (Directory.EnumerateFileSystemEntries(path).Any())
            throw new IOException("The sample destination is not empty. Choose a new directory; existing files were not changed.");
    }

    private static void RequireChineseFont()
    {
        foreach (var rune in (Title + Paragraph).EnumerateRunes().Where(rune => rune.Value >= 0x3000).Distinct())
        {
            using var face = SKFontManager.Default.MatchCharacter("Arial", SKFontStyle.Normal, null, rune.Value);
            using var font = new SKFont(face);
            Require(face is not null && font.ContainsGlyph(rune.Value),
                $"A Chinese font is needed to draw '{rune}'. Install a CJK font such as Noto Sans CJK SC, then retry.");
        }
    }

    private static CanvasDocument Document(string directory)
    {
        var document = new CanvasDocument(ID(0), Width, Height, 144);
        try
        {
            AddPixels(document, 1, Solid(864, 544, new SKColor(239, 246, 252)), 48, 48);
            AddPixels(document, 2, Solid(300, 180, new SKColor(83, 191, 204)), 128, 290);
            var circle = new SKBitmap(Bitmaps.ColorInfo(240, 200));
            circle.Erase(SKColors.Transparent);
            using (var canvas = new SKCanvas(circle))
            using (var paint = new SKPaint { Color = new SKColor(245, 160, 90), IsAntialias = true })
                canvas.DrawOval(120, 100, 120, 100, paint);
            var blended = AddPixels(document, 3, circle, 320, 280);
            blended.BlendMode = LayerBlendMode.Multiply;
            blended.Opacity = 0.8;
            var imported = ImageImporter.Decode(Path.Combine(directory, SourceName));
            document.Layers.Add(new ImageLayer(ID(4), imported, new RuntimeTransform(608, 304, 256, 160), LayerNames[3]));
            AddText(document, 5, new LayerTextStyle
            {
                Content = Title, FontName = "Arial", FontSize = 38,
                Red = 0.10, Green = 0.20, Blue = 0.29, BoxSize = new JsonSize(800, 88),
            }, 70, 82);
            AddText(document, 6, new LayerTextStyle
            {
                Content = Paragraph, FontName = "Arial", FontSize = 22,
                Red = 0.20, Green = 0.29, Blue = 0.36, BoxSize = new JsonSize(800, 90),
            }, 70, 178);
            document.Guides.Add(new CanvasGuide { ID = ID(7), Axis = GuideAxis.Vertical, Position = 480 });
            document.Guides.Add(new CanvasGuide { ID = ID(8), Axis = GuideAxis.Horizontal, Position = 320 });
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static Guid ID(int number) => Guid.Parse($"434F4D50-4F53-4954-4F52-{number:D12}");

    private static ImageLayer AddPixels(CanvasDocument document, int index, SKBitmap bitmap, int x, int y)
    {
        var layer = new ImageLayer(ID(index), ImportedImage.Create(bitmap, LayerNames[index - 1]),
            new RuntimeTransform(x, y, bitmap.Width, bitmap.Height), LayerNames[index - 1]);
        document.Layers.Add(layer);
        return layer;
    }

    private static void AddText(CanvasDocument document, int index, LayerTextStyle style, int x, int y)
    {
        var bitmap = TextEdits.Image(style) ?? throw new InvalidOperationException("The sample text could not be rendered.");
        AddPixels(document, index, bitmap, x, y).Text = new LayerText(style, bitmap);
    }

    private static SKBitmap Solid(int width, int height, SKColor color)
    {
        var image = new SKBitmap(Bitmaps.ColorInfo(width, height));
        image.Erase(color);
        return image;
    }

    private static SKBitmap SourceImage()
    {
        var bitmap = Solid(256, 160, SKColors.Transparent);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(55, 108, 158) };
        canvas.DrawRoundRect(new SKRect(16, 16, 240, 144), 20, 20, paint);
        paint.Color = new SKColor(242, 210, 111);
        canvas.DrawCircle(128, 80, 42, paint);
        return bitmap;
    }

    private static void WritePng(SKBitmap bitmap, string path)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new IOException("The sample image could not be encoded.");
        using var stream = new FileStream(path, FileMode.CreateNew);
        encoded.SaveTo(stream);
    }

    private static SKBitmap ReadPng(string path, int width, int height)
    {
        using var codec = SKCodec.Create(path) ?? throw new IOException($"Could not decode {Path.GetFileName(path)}.");
        Require(codec.EncodedFormat == SKEncodedImageFormat.Png && codec.Info.Width == width && codec.Info.Height == height,
            $"{Path.GetFileName(path)} must be a {width}×{height} PNG.");
        return SKBitmap.Decode(codec) ?? throw new IOException($"The pixels of {Path.GetFileName(path)} could not be decoded.");
    }

    private static void CheckCanvasPixels(SKBitmap image)
    {
        Require(image.GetPixel(0, 0).Alpha == 0 && image.GetPixel(Width - 1, Height - 1).Alpha == 0,
            "The canvas border lost its transparency.");
        Require(image.GetPixel(60, 60).Alpha == 255, "The background card is missing.");
        Require(image.GetPixel(200, 380) != image.GetPixel(380, 380), "The overlapping blended shapes are missing.");
    }

    private static void CheckSamePixels(SKBitmap left, SKBitmap right, string message)
    {
        Require(left.Width == right.Width && left.Height == right.Height, message);
        for (var y = 0; y < left.Height; y++)
            for (var x = 0; x < left.Width; x++)
                Require(left.GetPixel(x, y) == right.GetPixel(x, y), message);
    }

    private static void ValidateManifest(ProjectSnapshot snapshot)
    {
        var manifest = snapshot.Manifest;
        ProjectStore.Validate(manifest);
        Require(manifest.Version == 11 && manifest.DocumentID == ID(0) && manifest.ColorSpace == "sRGB"
            && manifest.Width == Width && manifest.Height == Height && manifest.Resolution == 144,
            "The sample canvas or format metadata changed.");
        Require(manifest.Layers.Select(layer => layer.Name).SequenceEqual(LayerNames)
            && manifest.Layers.Select(layer => layer.ID).SequenceEqual(Enumerable.Range(1, 6).Select(ID)),
            "The six Chinese layer names or their order changed.");
        Require(manifest.Layers.All(layer => layer.IsVisible && layer.IsGroup != true && layer.ParentID is null)
            && snapshot.Images.Count == 6 && snapshot.Masks.Count == 0,
            "The sample layer visibility or assets changed.");
        Require(manifest.Layers[2].BlendMode == LayerBlendMode.Multiply && manifest.Layers[2].Opacity == 0.8,
            "The Multiply layer must retain 80% opacity.");
        Require(manifest.Layers[4].Text?.Content == Title && manifest.Layers[5].Text?.Content == Paragraph,
            "The editable Chinese text was lost or changed.");
        Require(manifest.Guides is { Count: 2 } guides
            && guides.Any(guide => guide.Axis == GuideAxis.Vertical && guide.Position == 480)
            && guides.Any(guide => guide.Axis == GuideAxis.Horizontal && guide.Position == 320),
            "The horizontal and vertical guides changed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private const string Instructions = """
        # Windows 11 中文验收样本

        此样本用于复现问题。开始前复制整个目录，在副本中编辑。

        - `中文验收工程.comp` 是一个目录包。通过“文件 → 打开项目文件夹”选择这个文件夹。
        - 画布为 960×640 像素、144 DPI、sRGB，使用项目格式 11。
        - `参考导出.png` 是生成时的参考效果，保留透明边缘。参考线仅供编辑对齐，不应出现在导出中。
        - `导入素材.png` 是自行绘制的 256×160 透明 PNG，可用于菜单导入、文件拖放及图像复制粘贴测试。
        - 包含六个图层：浅色底板、青色矩形、橙色正片叠底、导入素材、中文标题、中文段落。
        - 橙色层为“正片叠底”，不透明度 80%；参考线位于 X=480、Y=320。

        ## 人工操作

        1. 解压完整便携包，启动 Compositor.Desktop.exe，在编辑菜单切换为简体中文并重启。
        2. 将样本副本放在含中文和空格的路径，例如“桌面/中文 测试/”。打开工程，确认六个图层名称完整。
        3. 比较画布与参考图。四周应透明，青橙重叠处颜色变深，中文无方框或乱码。必要时在视图菜单显示参考线。
        4. 编辑中文段落，使用微软拼音输入“今天测试中文输入，保存成功。”。检查候选窗跟随光标；组词时按 Enter 确认、按 Escape 取消，不应误触画布快捷键。
        5. 输入中英混排文字，测试方向键、删除、换行、撤销和重做。另行记录组合 emoji 的显示情况；本轮未保证复杂字形排版完全兼容。
        6. 隐藏并显示橙色图层，调整不透明度后撤销。通过“文件 → 导入图像”选择导入素材.png，移动新图层，再撤销。另从资源管理器拖入该 PNG，检查新增图层且源文件仍在。测试应用内图像选区及与其他应用之间的图像复制粘贴；文本和文件列表不属于图像剪贴板验收。
        7. 另存为“修改后 工程.comp”，关闭后重新打开，核对中文内容、图层、混合模式与参考线。
        8. 导出 PNG，检查 960×640 尺寸、文字和透明边缘。需要打印尺寸时单独检查 DPI 元数据，尺寸与 DPI 不能混为一谈。
        9. 在 Windows 显示设置分别选 100%、125%、150%、200% 缩放，重启程序后查看菜单、工具栏、颜色选择与保存对话框，记录截断或遮挡。
        10. 如发生异常，记录版本、Windows 版本、缩放比例、输入法、逐步操作和截图；按本版本诊断功能说明提取日志。
        11. 在 PowerShell 中运行 `.\Compositor.Desktop.exe "样本路径\中文验收工程.comp"`，检查含中文与空格的工程路径能打开。也可传入工程内的 manifest.json 或图片路径。多个参数按顺序处理；同批重复项跳过，失败项不阻止后续文件。

        ## 自动检查的范围

        开发环境可运行 `dotnet run --project windows/src/Compositor.Cli -- verify-sample <原始样本目录>`。
        命令检查原始工程语义、中文内容、保存重开与 PNG 导出，并在临时目录操作，不改动原始样本。
        手工修改后的副本不满足固定样本条件，验证失败是预期结果。

        参考字形取决于生成机器的系统字体。保存的文字栅格可供显示；重新编辑文字时会按当前机器字体排版，
        Windows 与 Linux 的字形、字距和抗锯齿可能不同，不要求重新排版后的像素完全一致。
        自动检查通过不能替代 Windows 11 的真实输入法、高 DPI、资源管理器拖放和系统图像剪贴板验收。
        外部入口仅接受本地路径，拖放按复制处理。便携包不会自动注册文件关联，也不把文件转发到已有进程。
        """;
}
