using Compositor.Core.Document;
using SkiaSharp;

namespace Compositor.Core.Model;

/// <summary>
/// Normalized layer-local coverage. Regular grayscale images use white for reveal, black for
/// hide, and intermediate gray for soft coverage.
/// </summary>
public sealed class LayerMask : IDisposable
{
    public LayerMask(ImportedImage asset, bool isEnabled = true, LayerTransform? placement = null, bool isLinked = true)
    {
        Asset = asset;
        IsEnabled = isEnabled;
        Placement = placement;
        IsLinked = isLinked;
    }

    public ImportedImage Asset { get; private set; }
    public bool IsEnabled { get; set; }

    /// <summary>Where the mask sits once it has been moved apart from its layer.</summary>
    public LayerTransform? Placement { get; set; }

    /// <summary>Linked, layer and mask move together; unlinked, each transforms on its own.</summary>
    public bool IsLinked { get; set; }

    public SKBitmap? EnabledImage => IsEnabled ? Asset.Image : null;

    /// <summary>A separate placement and state record sharing the same immutable pixels.</summary>
    public LayerMask Clone() => new(Asset, IsEnabled, Placement, IsLinked);

    public static bool IsValid(SKBitmap image) => Bitmaps.IsValidMask(image);

    public static LayerMask? Solid(bool revealing)
    {
        var pixels = Bitmaps.SolidMask(revealing);
        return new LayerMask(new ImportedImage(pixels, pixels.Copy(SKColorType.Gray8), "Layer Mask"));
    }

    public static LayerMask AssetFrom(SKBitmap image)
    {
        if (!IsValid(image)) throw new IO.ProjectException(IO.ProjectError.Invalid);
        var (width, height) = ImportedImage.ThumbnailSize(image.Width, image.Height);
        return new LayerMask(new ImportedImage(image, Bitmaps.DrawAsMask(image, width, height), "Layer Mask"));
    }

    /// <summary>The same mask with new pixels, still enabled or not, linked or not, and where it sits.</summary>
    public LayerMask Replacing(ImportedImage asset) => new(asset, IsEnabled, Placement, IsLinked);

    /// <summary>
    /// Where the mask sits once its layer moves: carried along when linked, left where it was when
    /// unlinked. A uniform mask looks the same wherever it sits, so it stays attached (nil).
    /// </summary>
    public LayerTransform? PlacementMoving(LayerTransform from, LayerTransform to)
        => PlacementMoving(from, to, Placement);

    /// <summary>The placement measured from the beginning of a drag, without accumulating resampling errors.</summary>
    public LayerTransform? PlacementMoving(LayerTransform from, LayerTransform to, LayerTransform? originalPlacement)
    {
        if (Asset.Width <= 1 && Asset.Height <= 1) return null;
        var moved = IsLinked
            ? originalPlacement is { } placed ? TransformEdits.Following(placed, from, to) : (LayerTransform?)null
            : originalPlacement ?? from;
        return moved is { } placement && placement.SamePlacement(to) ? null : moved;
    }

    public void Dispose() => Asset.Dispose();
}
