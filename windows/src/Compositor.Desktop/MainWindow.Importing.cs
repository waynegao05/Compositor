using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.IO.PSD;
using Compositor.Core.Model;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Desktop;

public sealed partial class MainWindow
{
    private string? _lastImportNotes;

    /// <summary>Decode before touching tabs or history. Photoshop documents keep their own layer stack.</summary>
    private void ImportPath(string path)
    {
        if (!ImageImporter.LooksImportable(path))
            throw new ImportException(ImportError.Unsupported,
                L10n.F("{0} files are not read; {1} are", Path.GetExtension(path), string.Join(", ", ImageImporter.Extensions)));

        var extension = Path.GetExtension(path);
        if (extension.Equals(".psd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".psb", StringComparison.OrdinalIgnoreCase))
        {
            ImportPhotoshop(path);
            return;
        }

        if (_document is { } current && current.Layers.Count >= LayerPlacement.MaxLayers)
            throw new InvalidOperationException(L10n.T("This project already has the maximum number of layers."));
        if (OperatingSystem.IsWindows() && ImageImporter.IsRaw(path)
            && !NativeDependencyChecks.InspectRaw().Available)
            throw new InvalidOperationException(NativeDependencyChecks.UnavailableMessage);

        var remaining = RemainingImportPixels(_document);
        // Decoded once, with the whole document's budget rather than a fresh budget for each import.
        var image = ImageImporter.Decode(path, Fitting(), remaining);
        var adopted = false;
        try
        {
            CommitText();
            if (_document is not { } document)
            {
                _document = ImageImporter.NewDocument(image);
                adopted = true;
                _projectPath = null;
                _history.Reset();
                Show(_open);
                Say(L10n.F("{0} — {1} by {2}, {3} layer, not saved yet",
                    Path.GetFileName(path), _document.Width, _document.Height, _document.Layers.Count));
            }
            else
            {
                // Committing a text draft can change its raster size. Check again before adding pixels.
                if (document.Layers.Count >= LayerPlacement.MaxLayers
                    || (long)image.Width * image.Height > RemainingImportPixels(document))
                    throw new ImportException(ImportError.TooLarge);
                var origin = new SKPoint((float)((document.Width - image.Width) / 2.0),
                    (float)((document.Height - image.Height) / 2.0));
                _history.Begin("Import image", document, Selected);
                var layer = new ImageLayer(Guid.NewGuid(), image,
                    new LayerTransform(origin.X, origin.Y, image.Width, image.Height), image.Name);
                document.Layers.Add(layer);
                adopted = true;
                _history.End(document, layer.ID);
                Reselect(layer.ID);
                Say(L10n.F("Imported {0} at {1} by {2}", Path.GetFileName(path), image.Width, image.Height));
            }
            _lastImportNotes = null;
        }
        finally
        {
            if (!adopted) image.Dispose();
        }
    }

    private static int RemainingImportPixels(CanvasDocument? document)
    {
        var used = document?.Layers.Sum(layer => layer.Asset is { } asset ? (long)asset.Width * asset.Height : 0) ?? 0;
        return (int)Math.Max(0, DocumentLimits.DocumentPixelBudget - used);
    }

    private void ImportPhotoshop(string path)
    {
        var imported = PsdImporter.Read(path);
        var snapshot = imported.Snapshot();
        CanvasDocument? made = null;
        var adopted = false;
        try
        {
            ProjectStore.Validate(snapshot.Manifest);
            made = snapshot.ToDocument();
            // The source PSD is never a .comp save destination. Saving this tab must use Save As.
            _open = TabForNew();
            _document = made;
            adopted = true;
            _projectPath = null;
            _history.Reset();
            Show(_open);
            if (snapshot.Manifest.ActiveLayerID is { } selected) Reselect(selected);
            _lastImportNotes = imported.Notes.Count == 0 ? null : string.Join(Environment.NewLine + Environment.NewLine,
                imported.Notes.Select(note => $"{note.Layer}: {L10n.T(note.What)}"));
            Say(L10n.F("Imported Photoshop document {0}: {1} layers, {2} conversion notes. Save as a .comp project.",
                Path.GetFileName(path), made.Layers.Count, imported.Notes.Count));
            // Keep every conversion note visible rather than silently dropping unsupported Photoshop data.
            if (_lastImportNotes is not null && IsVisible) new PhotoshopNotesDialog(_lastImportNotes).Show(this);
        }
        finally
        {
            if (!adopted)
            {
                if (made is not null) made.Dispose();
                else snapshot.Dispose();
            }
        }
    }

    private sealed class PhotoshopNotesDialog : DialogWindow
    {
        internal PhotoshopNotesDialog(string notes)
        {
            Title = L10n.T("Photoshop import notes");
            Width = 620;
            Height = 420;
            MinWidth = 400;
            MinHeight = 260;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var close = new Button
            {
                Content = L10n.T("Close"), IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right,
            };
            close.Click += (_, _) => Close();
            var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(16) };
            var summary = new TextBlock
            {
                Text = L10n.T("Some Photoshop features were converted or could not be preserved. Review the imported image before saving."),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            };
            var details = new TextBox
            {
                Text = notes, IsReadOnly = true, AcceptsReturn = true,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            };
            Grid.SetRow(details, 1);
            Grid.SetRow(close, 2);
            grid.Children.Add(summary);
            grid.Children.Add(details);
            grid.Children.Add(close);
            Content = grid;
        }
    }

    /// <summary>Drive the same import path as the picker without displaying native dialogs.</summary>
    internal static string ImportSelfCheck()
    {
        var root = Directory.CreateTempSubdirectory("Compositor-import-").FullName;
        var directory = Path.Combine(root, "中文 路径");
        Directory.CreateDirectory(directory);
        var window = new MainWindow();
        var report = new List<string>();
        try
        {
            var corruptPng = Write("损坏图片.png", [1, 2, 3]);
            CheckRefused(corruptPng);
            Check(window._document is null && window._tabs.Count == 1, "A failed first import populated the empty tab.");
            report.Add("damaged PNG in empty tab: no document or history was created");

            foreach (var extension in new[] { ".png", ".jpg" })
            {
                var densityPath = Path.Combine(directory, "300ppi" + extension);
                using (var source = new CanvasDocument(Guid.NewGuid(), 4, 3, 300))
                    Check(ImageWriter.Write(source, densityPath), "The density fixture could not be exported.");
                var densityWindow = new MainWindow();
                try
                {
                    densityWindow.ImportPath(densityPath);
                    var imported = densityWindow._document!;
                    Check(Math.Abs(imported.Resolution - 300) < 0.02,
                        "Import into an empty tab lost the image's physical resolution.");
                    imported.Resolution = 144;
                    densityWindow.ImportPath(densityPath);
                    Check(imported.Resolution == 144 && imported.Layers.Count == 2,
                        "Importing another layer changed the destination document's resolution.");
                }
                finally
                {
                    densityWindow.CloseForHeadlessCheck();
                    densityWindow._document?.Dispose();
                }
            }
            report.Add("PNG/JPEG density: new documents inherit 300 PPI; additional layers retain the target's 144 PPI");

            var png = Path.Combine(directory, "普通图片.PNG");
            using (var pixels = new SKBitmap(Bitmaps.ColorInfo(4, 3)))
            {
                pixels.Erase(new SKColor(10, 20, 30));
                using var image = SKImage.FromBitmap(pixels);
                using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
                    ?? throw new InvalidOperationException("The PNG fixture could not be encoded.");
                using var file = File.Create(png);
                encoded.SaveTo(file);
            }
            window.ImportPath(png);
            var originalTab = window._open;
            var originalDocument = window._document ?? throw new InvalidOperationException("PNG import did not create a document.");
            Check(originalDocument.Layers.Count == 1 && NeedsCloseConfirmation(originalTab) && originalTab.Path is null,
                "A newly imported PNG did not remain an unsaved project.");
            var package = Path.Combine(directory, "原工程.comp");
            ProjectStore.Save(ProjectSnapshot.FromDocument(originalDocument), package);
            originalTab.Path = package;
            window.Show(originalTab);
            originalTab.History.Begin("Rename layer", originalDocument, originalDocument.Layers[0].ID);
            originalDocument.Layers[0].Name = "原有未保存修改";
            originalTab.History.End(originalDocument, originalDocument.Layers[0].ID);
            var originalRevision = originalTab.History.CurrentRevision;
            var originalWatch = originalTab.Watch;
            var originalManifest = File.ReadAllBytes(Path.Combine(package, ProjectStore.ManifestName));
            CheckRefused(corruptPng);
            CheckRefused(Write("损坏分层.psd", [1, 2, 3]));
            CheckRefused(Write("损坏大型.psb", [1, 2, 3]));
            var wrongDepth = ImportFixture(1, layered: false);
            wrongDepth[23] = 16;
            CheckRefused(Write("不支持16位.psd", wrongDepth));
            report.Add("damaged PNG/PSD/PSB and unsupported 16-bit PSD: original tab, document, path, watch and dirty revision retained");

            window.ImportPath(png);
            Check(ReferenceEquals(window._open, originalTab) && originalDocument.Layers.Count == 2
                && originalTab.History.UndoName == "Import image" && originalTab.History.IsModified,
                "An ordinary image did not become one undoable layer in the existing project.");
            var beforeUndo = originalTab.History.Undo();
            Check(beforeUndo?.Document is not null, "The image import could not be undone.");
            originalDocument.Adopt(beforeUndo!.Value.Document!);
            Check(originalDocument.Layers.Count == 1 && originalTab.History.CurrentRevision == originalRevision,
                "Undo did not restore the original layer stack and revision.");
            window.Show(originalTab);
            report.Add("PNG in an open project: one new centered layer, one undo step, original stack restored");

            foreach (var version in new[] { 1, 2 })
            {
                var extension = version == 1 ? ".PSD" : ".PSB";
                var layeredPath = Write("中文分层" + extension, ImportFixture(version, layered: true));
                var sourceBytes = File.ReadAllBytes(layeredPath);
                var count = window._tabs.Count;
                window.ImportPath(layeredPath);
                var importedTab = window._open;
                var document = window._document ?? throw new InvalidOperationException("Photoshop import did not create a document.");
                Check(window._tabs.Count == count + 1 && !ReferenceEquals(importedTab, originalTab)
                    && importedTab.Path is null && NeedsCloseConfirmation(importedTab), "Photoshop import did not create a new unsaved tab.");
                Check(document.Width == 4 && document.Height == 3,
                    $"Photoshop canvas changed to {document.Width}×{document.Height}.");
                Check(document.Layers.Select(layer => layer.Name).SequenceEqual(new[] { "中文背景", "中文前景" }),
                    $"Photoshop layer names/order changed: {string.Join(", ", document.Layers.Select(layer => layer.Name))}.");
                Check(document.Layers[1].BlendMode == LayerBlendMode.Multiply,
                    $"Photoshop foreground blend changed to {document.Layers[1].BlendMode}.");
                Check(Math.Abs(document.Layers[1].Opacity - 128 / 255.0) < 1e-9,
                    $"Photoshop foreground opacity changed to {document.Layers[1].Opacity:R}.");
                Check(File.ReadAllBytes(layeredPath).SequenceEqual(sourceBytes), "Import changed the source Photoshop file.");
                CheckOriginal();
                var flatPath = Write("中文合成" + extension, ImportFixture(version, layered: false));
                window.ImportPath(flatPath);
                var flat = window._document ?? throw new InvalidOperationException("Flattened Photoshop import failed.");
                Check(flat.Layers.Count == 1 && flat.Layers[0].Name == "中文合成"
                    && flat.Layers[0].Asset!.Image.GetPixel(0, 0) == new SKColor(10, 20, 30),
                    "A Photoshop file without layer records did not use its merged image.");
                report.Add($"{extension}: Chinese paths and names preserved; layered import has a new unsaved tab; merged-only fallback retained");
            }
            window.ImportPath(Write("转换说明.psd", ImportFixture(1, layered: true, blend: "diss")));
            Check(window._lastImportNotes is { } notes && notes.Contains("中文前景", StringComparison.Ordinal)
                && notes.Contains("diss", StringComparison.Ordinal), "Photoshop conversion notes were discarded.");
            CheckOriginal();
            report.Add("Photoshop conversion notes: original layer and unsupported feature retained for the report");
            return string.Join(Environment.NewLine, report);

            string Write(string name, byte[] bytes)
            {
                var path = Path.Combine(directory, name);
                File.WriteAllBytes(path, bytes);
                return path;
            }

            void CheckRefused(string path)
            {
                var tab = window._open;
                var document = window._document;
                using var before = document?.Clone();
                var revision = tab.History.CurrentRevision;
                var dirty = tab.History.IsModified;
                var watch = tab.Watch;
                var savedPath = tab.Path;
                var count = window._tabs.Count;
                try
                {
                    window.ImportPath(path);
                    throw new InvalidOperationException("A damaged or unsupported import was accepted.");
                }
                catch (ImportException) { }
                Check(ReferenceEquals(window._open, tab) && ReferenceEquals(window._document, document)
                    && window._tabs.Count == count && tab.Path == savedPath && ReferenceEquals(tab.Watch, watch)
                    && tab.History.CurrentRevision == revision && tab.History.IsModified == dirty
                    && (document is null || before is not null && document.SameAs(before)),
                    "A failed import changed tab or save state.");
            }

            void CheckOriginal()
            {
                Check(ReferenceEquals(originalTab.Document, originalDocument) && originalTab.Path == package
                    && ReferenceEquals(originalTab.Watch, originalWatch) && originalTab.History.IsModified
                    && originalTab.History.CurrentRevision == originalRevision && originalDocument.Layers.Count == 1
                    && originalDocument.Layers[0].Name == "原有未保存修改"
                    && File.ReadAllBytes(Path.Combine(package, ProjectStore.ManifestName)).SequenceEqual(originalManifest),
                    "Photoshop import changed the original project or its unsaved edits.");
            }
        }
        finally
        {
            window._watchTimer?.Stop();
            window._canvas.Document = null;
            foreach (var tab in window._tabs) tab.Document?.Dispose();
            window._tabs.Clear();
            window.CloseForHeadlessCheck();
            Directory.Delete(root, recursive: true);
        }

        static void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
        }
    }

    /// <summary>Independent 4×3 RGB fixtures: two raw layers, or only merged pixels; PSD and PSB lengths.</summary>
    private static byte[] ImportFixture(int version, bool layered, string blend = "mul ")
    {
        var psb = version == 2;
        using var file = new MemoryStream();
        var writer = new ImportFixtureWriter(file);
        writer.Ascii("8BPS"); writer.U16(version); writer.Bytes(new byte[6]); writer.U16(3);
        writer.U32(3); writer.U32(4); writer.U16(8); writer.U16(3);
        writer.U32(0); writer.U32(0);
        if (layered)
        {
            using var layerInfo = new MemoryStream();
            var records = new ImportFixtureWriter(layerInfo);
            records.U16(2);
            Layer("中文背景", "norm", 255);
            Layer("中文前景", blend, 128);
            foreach (var value in new byte[] { 10, 20, 30, 50, 180, 70 })
            {
                records.U16(0);
                records.Bytes(Enumerable.Repeat(value, 12).ToArray());
            }
            using var section = new MemoryStream();
            var outer = new ImportFixtureWriter(section);
            outer.Length(psb, layerInfo.Length); outer.Bytes(layerInfo.ToArray()); outer.U32(0);
            writer.Length(psb, section.Length); writer.Bytes(section.ToArray());

            void Layer(string name, string mode, byte opacity)
            {
                records.U32(0); records.U32(0); records.U32(3); records.U32(4); records.U16(3);
                for (var channel = 0; channel < 3; channel++) { records.U16(channel); records.Length(psb, 14); }
                records.Ascii("8BIM"); records.Ascii(mode); records.Byte(opacity); records.Bytes([0, 0, 0]);
                using var extra = new MemoryStream();
                var extras = new ImportFixtureWriter(extra);
                extras.U32(0); extras.U32(0); extras.Bytes([0, 0, 0, 0]);
                var unicode = Encoding.BigEndianUnicode.GetBytes(name);
                extras.Ascii("8BIM"); extras.Ascii("luni"); extras.U32(4 + unicode.Length);
                extras.U32(name.Length); extras.Bytes(unicode);
                records.U32((int)extra.Length); records.Bytes(extra.ToArray());
            }
        }
        else writer.Length(psb, 0);
        writer.U16(0);
        foreach (var value in new byte[] { 10, 20, 30 }) writer.Bytes(Enumerable.Repeat(value, 12).ToArray());
        return file.ToArray();
    }

    private sealed class ImportFixtureWriter(Stream stream)
    {
        internal void Byte(byte value) => stream.WriteByte(value);
        internal void Bytes(byte[] bytes) => stream.Write(bytes);
        internal void Ascii(string text) => Bytes(Encoding.ASCII.GetBytes(text));
        internal void U16(int value) { Byte((byte)(value >> 8)); Byte((byte)value); }
        internal void U32(int value) { for (var shift = 24; shift >= 0; shift -= 8) Byte((byte)(value >> shift)); }
        internal void Length(bool psb, long value)
        {
            if (!psb) U32(checked((int)value));
            else for (var shift = 56; shift >= 0; shift -= 8) Byte((byte)(value >> shift));
        }
    }
}
