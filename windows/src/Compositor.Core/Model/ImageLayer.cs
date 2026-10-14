using SkiaSharp;

namespace Compositor.Core.Model;

/// <summary>A layer made with the Shape tool: an ordinary raster plus the style that drew it.</summary>
public sealed class LayerShape
{
    public LayerShape(Format.LayerShapeStyle style, SKBitmap image)
    {
        Style = style;
        Image = image;
    }

    public Format.LayerShapeStyle Style { get; }
    public SKBitmap Image { get; }
}

/// <summary>A layer made with the Type tool: an ordinary raster plus the text metadata that drew it.</summary>
public sealed class LayerText
{
    public LayerText(Format.LayerTextStyle style, SKBitmap image)
    {
        Style = style;
        Image = image;
    }

    public Format.LayerTextStyle Style { get; }
    public SKBitmap Image { get; }
}

public sealed class ImageLayer : IDisposable
{
    public ImageLayer(Guid id, ImportedImage? asset, LayerTransform transform, string name)
    {
        ID = id;
        Asset = asset;
        Transform = transform;
        Name = name;
    }

    public Guid ID { get; init; }
    public ImportedImage? Asset { get; set; }
    public LayerTransform Transform { get; set; }
    public string Name { get; set; }
    public bool IsVisible { get; set; } = true;
    public Guid? ParentID { get; set; }
    public bool IsGroup { get; set; }
    public double Opacity { get; set; } = 1;
    public Format.LayerBlendMode BlendMode { get; set; } = Format.LayerBlendMode.Normal;
    public Guid? MaskSourceID { get; set; }
    public LayerMask? Mask { get; set; }
    public Format.LayerAdjustment? Adjustment { get; set; }
    public LayerShape? Shape { get; set; }
    public Format.LayerEffects? Effects { get; set; }
    public LayerText? Text { get; set; }

    /// <summary>The shape this layer still is: null once its pixels were edited some other way.</summary>
    public Format.LayerShapeStyle? LiveShape =>
        Shape is { } shape && Asset is { } asset && ReferenceEquals(asset.Image, shape.Image) ? shape.Style : null;

    /// <summary>The text this layer still is: null once its pixels were edited some other way.</summary>
    public Format.LayerTextStyle? LiveText =>
        Text is { } text && Asset is { } asset && ReferenceEquals(asset.Image, text.Image) ? text.Style : null;

    /// <summary>Where the mask's pixels sit on the document: its own placement, else the layer's.</summary>
    public LayerTransform MaskTransform => Mask?.Placement ?? Transform;

    /// <summary>
    /// A copy holding the same values and the same pixels. The pixels are shared, not copied — that is what
    /// makes a history snapshot cheap — so a clone must never dispose them.
    /// </summary>
    public ImageLayer Clone() => Copy(ID, Name, ParentID, MaskSourceID);

    /// <summary>
    /// The same layer under a new id, name, parent and clip source, still sharing its pixels. The whole
    /// list is given so a copy of a folder can be made in one pass with every reference remapped.
    /// </summary>
    public ImageLayer Copy(Guid id, string name, Guid? parentID, Guid? maskSourceID) =>
        new(id, Asset, Transform, name)
        {
            IsVisible = IsVisible,
            ParentID = parentID,
            IsGroup = IsGroup,
            Opacity = Opacity,
            BlendMode = BlendMode,
            MaskSourceID = maskSourceID,
            Mask = Mask?.Clone(),
            Adjustment = Adjustment,
            Shape = Shape,
            Effects = Effects,
            Text = Text,
        };

    /// <summary>
    /// Whether two layers hold the same thing, with pixels compared by identity. This is how a snapshot and
    /// the live document are compared to tell a real edit from selecting or navigating.
    /// </summary>
    public bool SameAs(ImageLayer other) =>
        ID == other.ID && Name == other.Name && IsVisible == other.IsVisible && Transform == other.Transform
        && ParentID == other.ParentID && IsGroup == other.IsGroup && Opacity == other.Opacity
        && BlendMode == other.BlendMode && MaskSourceID == other.MaskSourceID
        && ReferenceEquals(Asset?.Image, other.Asset?.Image)
        && SameMask(Mask, other.Mask)
        && ReferenceEquals(Adjustment, other.Adjustment) && ReferenceEquals(Shape, other.Shape)
        && ReferenceEquals(Effects, other.Effects) && ReferenceEquals(Text, other.Text);

    private static bool SameMask(LayerMask? left, LayerMask? right) =>
        left is null ? right is null
        : right is not null && ReferenceEquals(left.Asset.Image, right.Asset.Image)
            && left.IsEnabled == right.IsEnabled && left.IsLinked == right.IsLinked
            && left.Placement == right.Placement;

    public double EffectiveOpacity(IReadOnlyDictionary<Guid, ImageLayer> byID) =>
        LayerOpacity.Effective(this, byID);

    public void Dispose()
    {
        Asset?.Dispose();
        Mask?.Dispose();
    }
}

/// <summary>
/// A folder's opacity multiplies into everything inside it: a layer at 50% in a folder at 50% shows at
/// 25%, while the layer itself still reads 50%. Folders are pass-through, so a folder's opacity is
/// applied to each of those layers rather than to the folder as a whole.
/// </summary>
public static class LayerOpacity
{
    public static double Effective(ImageLayer layer, IReadOnlyDictionary<Guid, ImageLayer> byID)
    {
        var opacity = layer.Opacity;
        var parent = layer.ParentID;
        for (var depth = 0; parent is { } id && depth < 64; depth++)
        {
            if (!byID.TryGetValue(id, out var folder)) break;
            opacity *= folder.Opacity;
            parent = folder.ParentID;
        }
        return opacity;
    }

    public static double Effective(Format.ProjectLayerRecord layer, IReadOnlyDictionary<Guid, Format.ProjectLayerRecord> byID)
    {
        var opacity = layer.Opacity ?? 1;
        var parent = layer.ParentID;
        for (var depth = 0; parent is { } id && depth < 64; depth++)
        {
            if (!byID.TryGetValue(id, out var folder)) break;
            opacity *= folder.Opacity ?? 1;
            parent = folder.ParentID;
        }
        return opacity;
    }
}
