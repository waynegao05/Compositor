using System.Text;
using System.Text.Json;
using Compositor.Core.Format;

namespace Compositor.Core.IO;

/// <summary>
/// Reads and writes `.comp` packages: a directory holding `manifest.json`, `images/&lt;id&gt;.png` assets
/// and an optional `QuickLook/Preview.jpg`.
/// </summary>
public static class ProjectStore
{
    public const string ManifestName = "manifest.json";
    public const string ImagesName = "images";
    public const string QuickLookFolderName = "QuickLook";
    public const string QuickLookPreviewName = "Preview.jpg";

    private static readonly object SaveLock = new();

    /// <summary>
    /// Everything the format holds, validated the way the Swift build validates it. Rejections are the
    /// point: a manifest that reaches the app must not be able to describe something it cannot draw.
    /// </summary>
    public static void Validate(ProjectManifest manifest)
    {
        if (manifest is null || manifest.Layers is null) throw new ProjectException(ProjectError.Invalid);
        if (manifest.Format != ProjectManifest.FormatIdentifier) throw new ProjectException(ProjectError.Invalid);
        if (!ProjectManifest.IsSupported(manifest.Version)) throw new ProjectException(ProjectError.Version, manifest.Version);
        if (manifest.ColorSpace != "sRGB") throw new ProjectException(ProjectError.Invalid);
        if (manifest.Resolution is { } resolution)
        {
            if (!double.IsFinite(resolution) || resolution is < 1 or > 9600) throw new ProjectException(ProjectError.Invalid);
        }
        if (manifest.Width is < 1 or > Model.DocumentLimits.MaxSide || manifest.Height is < 1 or > Model.DocumentLimits.MaxSide
            || manifest.Layers is not { Count: <= 10_000 })
        {
            throw new ProjectException(ProjectError.TooLarge);
        }

        var version = manifest.Version;
        foreach (var layer in manifest.Layers)
        {
            // Nullable annotations do not constrain collection elements read from JSON.
            if (!HasRequiredStructure(layer)) throw new ProjectException(ProjectError.Invalid);
            if (layer.Text is { } text)
            {
                // Per-letter colors arrived in version 10, per-letter faces in version 11.
                if (!text.IsValid
                    || (text.ColorRuns is not null && version < 10)
                    || (text.FontRuns is not null && version < 11)
                    || layer.ImageFile is null || layer.IsGroup == true || layer.Adjustment is not null)
                {
                    throw new ProjectException(ProjectError.Invalid);
                }
            }
            if (layer.Adjustment is { } adjustment)
            {
                if (version < 7 || layer.IsGroup == true || layer.ImageFile is not null || !adjustment.IsValid)
                {
                    throw new ProjectException(ProjectError.Invalid);
                }
                if (adjustment.NeedsVersion9 && version < 9) throw new ProjectException(ProjectError.Invalid);
            }
            if (!LayerMask.FromRecord(layer).IsValid(version, layer.IsGroup == true, layer.ID))
            {
                throw new ProjectException(ProjectError.Invalid);
            }

            // Folders took an opacity of their own in version 8, which multiplies into what is inside
            // them; their blend mode is still pass-through, so it stays Normal.
            var opacity = layer.Opacity ?? 1;
            var blend = layer.BlendMode ?? LayerBlendMode.Normal;
            if (!double.IsFinite(opacity) || opacity is < 0 or > 1
                || (version < 3 && (opacity != 1 || blend != LayerBlendMode.Normal))
                || (layer.IsGroup == true && (blend != LayerBlendMode.Normal || (version < 8 && opacity != 1))))
            {
                throw new ProjectException(ProjectError.Invalid);
            }
        }

        Model.LayerHierarchy.Validate(manifest.Layers);
        Model.LiveMaskGraph.Validate(manifest.Layers);

        if (version < 5 && manifest.Layers.Any(layer => layer.MaskSourceID is not null)) throw new ProjectException(ProjectError.Invalid);
        if (version == 1 && manifest.Layers.Any(layer => layer.ParentID is not null || layer.IsGroup == true))
        {
            throw new ProjectException(ProjectError.Invalid);
        }

        var ids = new HashSet<Guid>();
        foreach (var layer in manifest.Layers)
        {
            if (!ids.Add(layer.ID) || !layer.Transform.ToRuntime().IsValid
                || string.IsNullOrWhiteSpace(layer.Name)
                || Encoding.UTF8.GetByteCount(layer.Name) > 16_384
                || (layer.ImageFile is not null && layer.ImageFile != LayerMask.ExpectedImageFile(layer.ID)))
            {
                throw new ProjectException(ProjectError.Invalid);
            }
        }
        if (manifest.ActiveLayerID is { } active && !ids.Contains(active)) throw new ProjectException(ProjectError.Invalid);

        ValidateGuides(manifest);
    }

    private static bool HasRequiredStructure(ProjectLayerRecord? layer)
    {
        if (layer is null || layer.Transform is null) return false;
        if (layer.Text is { } text && (text.ColorRuns?.Any(run => run is null) == true
            || text.FontRuns?.Any(run => run is null) == true)) return false;
        if (layer.Adjustment is not { } adjustment) return true;
        if (adjustment.Levels is null || adjustment.Curves is null) return false;
        if (adjustment.Curves.Channels?.Any(points => points is null || points.Any(point => point is null)) == true)
            return false;
        if (adjustment.HsvSettings is { } hsv && (hsv.Adjustments is null || hsv.Bands is null
            || hsv.Adjustments.Values.Any(value => value is null) || hsv.Bands.Values.Any(value => value is null)))
            return false;
        if (adjustment.GradientMapSettings is { } gradient && (gradient.Shadows is null || gradient.Highlights is null))
            return false;
        return true;
    }

    private static void ValidateGuides(ProjectManifest manifest)
    {
        var guides = manifest.Guides ?? [];
        if (manifest.Version < 8)
        {
            if (guides.Count > 0) throw new ProjectException(ProjectError.Invalid);
            return;
        }
        if (guides.Count > 1_000) throw new ProjectException(ProjectError.TooLarge);
        var ids = new HashSet<Guid>();
        foreach (var guide in guides)
        {
            if (guide is null || !ids.Add(guide.ID) || !double.IsFinite(guide.Position) || Math.Abs(guide.Position) > 1_000_000)
            {
                throw new ProjectException(ProjectError.Invalid);
            }
        }
    }

    /// <summary>
    /// One image or mask, held to the side limit and to the document's shared pixel budget. Images and
    /// masks are counted separately, each against a full budget.
    /// </summary>
    public static void CheckSize(int width, int height, ref int used) =>
        CheckSize(width, height, ref used, Model.DocumentLimits.DocumentPixelBudget);

    public static void CheckSize(int width, int height, ref int used, int budget)
    {
        if (width is < 1 or > Model.DocumentLimits.MaxSide || height is < 1 or > Model.DocumentLimits.MaxSide
            || (long)width * height > budget - used)
        {
            throw new ProjectException(ProjectError.TooLarge);
        }
        used += width * height;
    }

    /// <summary>
    /// Writes the manifest and every referenced asset into a staging directory beside the target, then
    /// moves it into place. A watcher sees either the old package or the new one, never a half-written
    /// one; the only gap is the pair of directory renames Windows needs to swap a package.
    /// </summary>
    public static void Save(ProjectSnapshot snapshot, string path, byte[]? quickLookPreview = null) =>
        SaveCore(snapshot, path, quickLookPreview, null);

    internal static void SaveWithCheckpoints(ProjectSnapshot snapshot, string path, Action<ProjectSaveCheckpoint> checkpoint) =>
        SaveCore(snapshot, path, null, checkpoint);

    private static void SaveCore(ProjectSnapshot snapshot, string path, byte[]? quickLookPreview,
        Action<ProjectSaveCheckpoint>? checkpoint)
    {
        Validate(snapshot.Manifest);

        var images = new Dictionary<string, byte[]>();
        var pixels = 0;
        var maskPixels = 0;
        foreach (var layer in snapshot.Manifest.Layers)
        {
            foreach (var isMask in new[] { false, true })
            {
                var filename = isMask ? layer.MaskFile : layer.ImageFile;
                if (filename is null) continue;
                var assets = isMask ? snapshot.Masks : snapshot.Images;
                if (!assets.TryGetValue(layer.ID, out var asset)) throw new ProjectException(ProjectError.MissingImage);

                if (isMask)
                {
                    if (!Model.LayerMask.IsValid(asset.Image)) throw new ProjectException(ProjectError.Invalid);
                    CheckSize(asset.Width, asset.Height, ref maskPixels);
                }
                else
                {
                    CheckSize(asset.Width, asset.Height, ref pixels);
                }
                images[filename] = PngCodec.Encode(asset.Image);
            }
        }

        var metadata = ManifestJson.Serialize(snapshot.Manifest);
        if (metadata.Length > Model.DocumentLimits.MaxManifestBytes) throw new ProjectException(ProjectError.TooLarge);

        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        _ = Path.GetDirectoryName(target) ?? throw new ProjectException(ProjectError.Encode);
        var transactionID = Guid.NewGuid();
        var staging = ProjectRecovery.SidecarPath(target, transactionID, ProjectRecoveryKind.PendingSave);
        var backup = ProjectRecovery.SidecarPath(target, transactionID, ProjectRecoveryKind.PreviousSave);
        using var operation = ProjectRecovery.AcquireOperationLocks(transactionID, target);
        lock (SaveLock)
        {
            ProjectRecovery.Transaction? transaction = null;
            var interrupted = false;
            try
            {
                Directory.CreateDirectory(staging);
                var imagesDirectory = Path.Combine(staging, ImagesName);
                Directory.CreateDirectory(imagesDirectory);
                foreach (var (filename, bytes) in images)
                {
                    ProjectRecovery.WriteDurable(Path.Combine(imagesDirectory, filename), bytes);
                }
                if (quickLookPreview is not null)
                {
                    var quickLook = Path.Combine(staging, QuickLookFolderName);
                    Directory.CreateDirectory(quickLook);
                    ProjectRecovery.WriteDurable(Path.Combine(quickLook, QuickLookPreviewName), quickLookPreview);
                }
                ProjectRecovery.WriteDurable(Path.Combine(staging, ManifestName), metadata);
                checkpoint?.Invoke(ProjectSaveCheckpoint.Staged);

                // Validate immediately before replacement. An ordinary directory, a damaged project, or
                // files the project does not own must never disappear as a side effect of saving.
                VerifyReplacementTarget(target);
                transaction = ProjectRecovery.Register(transactionID, target);
                checkpoint?.Invoke(ProjectSaveCheckpoint.Journaled);
                ProjectRecovery.CheckDestination(transaction);
                VerifyReplacementTarget(target);
                if (Directory.Exists(target))
                {
                    Directory.Move(target, backup);
                }
                checkpoint?.Invoke(ProjectSaveCheckpoint.PreviousMoved);
                Directory.Move(staging, target);
                checkpoint?.Invoke(ProjectSaveCheckpoint.ReplacementMoved);
                ProjectRecovery.Complete(transaction);
            }
            catch (ProjectSaveInterruptionException)
            {
                // Test-only interruption checkpoints model an exited process without killing the runner.
                interrupted = true;
                throw;
            }
            catch
            {
                if (transaction is not null) ProjectRecovery.TryRollback(transaction);
                throw;
            }
            finally
            {
                // A registered staging package can hold the person's newest edits. Keep it discoverable
                // after a failure; before registration the original destination was never moved.
                if (transaction is null && !interrupted && Directory.Exists(staging)) TryDelete(staging);
            }
        }
    }

    private static void VerifyReplacementTarget(string target)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(target);
        }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new ProjectException(ProjectError.UnsafeDestination);

        ProjectSnapshot existing;
        try
        {
            existing = LoadUnderOperationLock(target);
        }
        catch (Exception error) when (error is ProjectException or IOException or UnauthorizedAccessException)
        {
            throw new ProjectException(ProjectError.UnsafeDestination);
        }
        using (existing)
        {
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var files = new HashSet<string>(comparer)
            {
                ManifestName,
                Path.Combine(QuickLookFolderName, QuickLookPreviewName),
            };
            foreach (var layer in existing.Manifest.Layers)
            {
                if (layer.ImageFile is { } image) files.Add(Path.Combine(ImagesName, image));
                if (layer.MaskFile is { } mask) files.Add(Path.Combine(ImagesName, mask));
            }
            var directories = new HashSet<string>([ImagesName, QuickLookFolderName], comparer);
            CheckDirectory(target);

            void CheckDirectory(string directory)
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var entryAttributes = File.GetAttributes(entry);
                    // Inspect the entry before recursing: a link can point outside this package.
                    if (entryAttributes.HasFlag(FileAttributes.ReparsePoint))
                        throw new ProjectException(ProjectError.UnsafeDestination);
                    var relative = Path.GetRelativePath(target, entry);
                    if (entryAttributes.HasFlag(FileAttributes.Directory))
                    {
                        if (!directories.Contains(relative)) throw new ProjectException(ProjectError.ExtraFiles);
                        CheckDirectory(entry);
                    }
                    else if (!files.Contains(relative)) throw new ProjectException(ProjectError.ExtraFiles);
                }
            }
        }
    }

    /// <summary>Reads a package. Nothing is decoded until the manifest has been validated.</summary>
    public static ProjectSnapshot Load(string path)
    {
        using var operation = ProjectRecovery.AcquireReadLock(path);
        return LoadUnderOperationLock(path);
    }

    /// <summary>The caller holds the target path lock, and the transaction lock for its sidecars.</summary>
    internal static ProjectSnapshot LoadUnderOperationLock(string path)
    {
        var package = Path.GetFullPath(path);
        if (!Directory.Exists(package)) throw new ProjectException(ProjectError.Invalid);

        var metadata = File.ReadAllBytes(ResolveAsset(package, ManifestName, Model.DocumentLimits.MaxManifestBytes));

        ManifestJson.Header header;
        try
        {
            header = ManifestJson.ReadHeader(metadata);
        }
        catch (Exception error) when (error is JsonException or FormatException or OverflowException or InvalidOperationException)
        {
            throw new ProjectException(ProjectError.Invalid);
        }
        if (header.Format != ProjectManifest.FormatIdentifier) throw new ProjectException(ProjectError.Invalid);
        if (!ProjectManifest.IsSupported(header.Version)) throw new ProjectException(ProjectError.Version, header.Version);

        ProjectManifest manifest;
        try
        {
            manifest = ManifestJson.Deserialize(metadata);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or FormatException or InvalidOperationException or OverflowException)
        {
            throw new ProjectException(ProjectError.Invalid);
        }
        Validate(manifest);

        var snapshot = new ProjectSnapshot(manifest);
        var pixels = 0;
        var maskPixels = 0;
        try
        {
            foreach (var layer in manifest.Layers)
            {
                foreach (var isMask in new[] { false, true })
                {
                    var filename = isMask ? layer.MaskFile : layer.ImageFile;
                    if (filename is null) continue;
                    var file = ResolveAsset(package, $"{ImagesName}/{filename}", Model.DocumentLimits.MaxAssetBytes);
                    var bytes = File.ReadAllBytes(file);
                    var headerOfAsset = PngCodec.ReadHeader(bytes);
                    if (!headerOfAsset.IsEightBitOrLess) throw new ProjectException(ProjectError.MissingImage);
                    if (isMask) CheckSize(headerOfAsset.Width, headerOfAsset.Height, ref maskPixels);
                    else CheckSize(headerOfAsset.Width, headerOfAsset.Height, ref pixels);

                    var bitmap = isMask ? PngCodec.DecodeMask(headerOfAsset, bytes) : PngCodec.DecodeImage(headerOfAsset, bytes);
                    var asset = new Model.ImportedImage(bitmap, Model.Bitmaps.Thumbnail(bitmap), layer.Name);
                    if (isMask) snapshot.Masks[layer.ID] = asset;
                    else snapshot.Images[layer.ID] = asset;
                }
            }
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
        return snapshot;
    }

    /// <summary>
    /// A file inside the package, with the checks the Swift build makes: regular, not a link, no larger
    /// than `maximumBytes`, and rooted in the package once `..` is resolved.
    /// </summary>
    private static string ResolveAsset(string package, string relative, int maximumBytes)
    {
        var root = package.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var file = Path.GetFullPath(Path.Combine(root, relative));
        if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ProjectException(ProjectError.Invalid);
        var info = new FileInfo(file);
        // Existence first: walking the path for links on a file that is not there throws out of the API.
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.Directory)) throw new ProjectException(ProjectError.MissingImage);
        if (IsLink(package, file)) throw new ProjectException(ProjectError.Invalid);
        if (info.Length > maximumBytes) throw new ProjectException(ProjectError.TooLarge);
        return file;
    }

    /// <summary>Whether any directory on the way to `file`, or the file itself, is a symbolic link.</summary>
    private static bool IsLink(string package, string file)
    {
        var root = Path.GetFullPath(package);
        var relative = Path.GetRelativePath(root, file);
        var current = root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
        }
        return false;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover staging directory is harmless; the next save picks a new name.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
