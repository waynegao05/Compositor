using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using Compositor.Core.Pixels;
using SkiaSharp;

namespace Compositor.Core.Rendering;

/// <summary>
/// Flattens a document to one bitmap, the way the Mac build's export path does.
/// <para>
/// Folders are pass-through and never get a surface of their own: a folder's opacity multiplies into each
/// descendant, and a folder's mask clips each descendant. A layer's mask and its clip's coverage are both
/// clips, and a clip bounds the whole layer, not just the part the clip's image happens to cover — so
/// restricting to one builds a fresh bitmap rather than multiplying in place. A clipping stack (a base
/// plus the contiguous siblings clipped to it) is composited once, with the base's blend mode and
/// effective opacity, after its members have blended into it with their own.
/// </para>
/// </summary>
public static class DocumentRenderer
{
    /// <summary>
    /// Renders the whole canvas in one buffer. The result is premultiplied sRGB on a transparent
    /// background. A canvas too big for one buffer has to go through <see cref="RenderRegion"/> in pieces.
    /// </summary>
    public static SKBitmap Render(CanvasDocument document) =>
        RenderRegion(document, SKRectI.Create(0, 0, document.Width, document.Height));

    /// <summary>
    /// Renders one rectangle of the canvas, in the rectangle's own coordinates, so a canvas larger than any
    /// one buffer can be drawn in pieces.
    /// <para>
    /// A blur-type adjustment reads its neighbours, so the rectangle is rendered with a halo around it and
    /// cut back afterwards: everything that could reach inside is rendered too, which is what makes a piece
    /// identical to the same piece of a whole-canvas render.
    /// </para>
    /// </summary>
    public static SKBitmap RenderRegion(CanvasDocument document, SKRectI region)
    {
        var result = Allocate(region.Width, region.Height);
        if (region.Width <= 0 || region.Height <= 0) return result;
        var canvas = SKRectI.Create(0, 0, document.Width, document.Height);
        var wanted = SKRectI.Intersect(region, canvas);
        if (wanted.Width <= 0 || wanted.Height <= 0) return result;
        var grown = SKRectI.Intersect(Inflate(wanted, Halo(document)), canvas);
        using var bitmap = Allocate(grown.Width, grown.Height);
        new Renderer(document, bitmap, grown).Draw();
        using var surface = new SKCanvas(result);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        // The grown render's inner rectangle, into the piece's own corner.
        surface.DrawBitmap(bitmap,
            SKRect.Create(wanted.Left - grown.Left, wanted.Top - grown.Top, wanted.Width, wanted.Height),
            SKRect.Create(wanted.Left - region.Left, wanted.Top - region.Top, wanted.Width, wanted.Height),
            OneToOne, paint);
        return result;
    }

    /// <summary>
    /// The halo the document needs: what every visible blur-type adjustment can reach, summed, because one
    /// blur's output can feed the next. Hidden layers are left out since they change nothing.
    /// </summary>
    private static int Halo(CanvasDocument document)
    {
        var halo = 0L;
        foreach (var layer in document.Layers)
        {
            if (!layer.IsVisible) continue;
            halo += layer.Adjustment?.SamplingMargin ?? 0;
        }
        return (int)Math.Min(halo, DocumentLimits.MaxSide);
    }

    private static SKRectI Inflate(SKRectI rect, int by) =>
        SKRectI.Create(rect.Left - by, rect.Top - by, rect.Width + by * 2, rect.Height + by * 2);

    /// <summary>A rendered layer is already at its final size, so resampling it would only blur it.</summary>
    private static readonly SKSamplingOptions OneToOne = new(SKFilterMode.Nearest, SKMipmapMode.None);

    internal static SKBitmap Allocate(int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        var bitmap = new SKBitmap(info);
        if (!bitmap.ReadyToDraw) throw new InvalidOperationException($"Could not allocate a {width}x{height} bitmap.");
        bitmap.Erase(SKColors.Transparent);
        return bitmap;
    }

    /// <summary>
    /// One layer's own pixels drawn at its transform, whole, with no mask, no opacity and no blend — how a
    /// command-click reads them, and what the sample a selection tool matches against holds.
    /// </summary>
    public static void DrawLayerPixels(SKCanvas canvas, ImageLayer layer)
    {
        if (layer.Asset is not { } asset) return;
        using var paint = new SKPaint { IsAntialias = true };
        DrawTransformed(canvas, asset.Image, layer.Transform, SKBlendMode.SrcOver, paint);
    }

    /// <summary>
    /// A layer whose content is not a raster is not drawn yet, and drawing it as nothing would quietly
    /// change the picture. This says so instead.
    /// </summary>
    internal static NotSupportedException Unsupported(ImageLayer layer) =>
        new($"Rendering layer effects is not implemented yet (layer '{layer.Name}').");

    /// <summary>
    /// Draws one image at a layer transform: rotation clockwise about the rect's center, with the flips
    /// applied before the rotation reads it.
    /// </summary>
    private static void DrawTransformed(SKCanvas canvas, SKBitmap image, Model.LayerTransform transform,
        SKBlendMode blend, SKPaint paint)
    {
        canvas.Save();
        canvas.Translate((float)(transform.X + transform.Width / 2), (float)(transform.Y + transform.Height / 2));
        canvas.RotateDegrees((float)transform.Rotation);
        canvas.Scale(transform.FlipX ? -1 : 1, transform.FlipY ? -1 : 1);
        canvas.Translate((float)(-transform.Width / 2), (float)(-transform.Height / 2));
        paint.BlendMode = blend;
        using var source = SKImage.FromBitmap(image);
        canvas.DrawImage(source, SKRect.Create(0, 0, (float)transform.Width, (float)transform.Height),
            Sampling(transform, image.Width, image.Height), paint);
        canvas.Restore();
    }

    /// <summary>
    /// How an image is resampled into its rectangle. The Mac build overrides the requested quality: an
    /// upright image that lands exactly on its own pixels is drawn with none at all, and anything being
    /// shrunk is drawn low, because a filter over a reduction only blurs it further.
    /// </summary>
    private static SKSamplingOptions Sampling(Model.LayerTransform transform, int sourceWidth, int sourceHeight)
    {
        if (transform.Rotation == 0 && transform.Width == sourceWidth && transform.Height == sourceHeight) return OneToOne;
        if (transform.Width <= sourceWidth && transform.Height <= sourceHeight)
        {
            return new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        }
        return Sampling(transform.Sampling);
    }

    /// <summary>The filter a layer was imported or placed with.</summary>
    private static SKSamplingOptions Sampling(LayerSampling sampling) => sampling switch
    {
        LayerSampling.Nearest => new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None),
        LayerSampling.Smooth => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
        _ => new SKSamplingOptions(SKCubicResampler.Mitchell),
    };

    private sealed class Renderer
    {
        private readonly CanvasDocument _document;
        private readonly SKBitmap _canvas;
        private readonly SKRectI _region;
        private readonly Dictionary<Guid, ImageLayer> _byID = [];
        private readonly Dictionary<Guid, List<ImageLayer>> _children = [];

        public Renderer(CanvasDocument document, SKBitmap canvas, SKRectI region)
        {
            _document = document;
            _canvas = canvas;
            _region = region;
            foreach (var layer in document.Layers)
            {
                _byID[layer.ID] = layer;
                if (layer.ParentID is { } parent)
                {
                    if (!_children.TryGetValue(parent, out var siblings)) _children[parent] = siblings = [];
                    siblings.Add(layer);
                }
            }
        }

        public void Draw()
        {
            using var canvas = new SKCanvas(_canvas);
            // Every surface is drawn in document coordinates, so a piece's own corner is a translation.
            canvas.Translate(-_region.Left, -_region.Top);
            DrawSiblings(_document.Layers.Where(layer => layer.ParentID is null).ToList(),
                new Target(_canvas, canvas, _region.Location));
        }

        /// <summary>A rectangle held to the piece of the canvas being rendered.</summary>
        private SKRectI Clip(SKRectI rect) => SKRectI.Intersect(rect, _region);

        private List<ImageLayer> ChildrenOf(ImageLayer folder) =>
            _children.TryGetValue(folder.ID, out var children) ? children : [];

        /// <summary>
        /// One sibling list, bottom to top. A base with no clipping source and no adjustment claims the
        /// siblings that immediately follow it and are clipped to it; they composite as one. The base is
        /// claimed whether or not it is visible, because it still supplies the clip's coverage.
        /// </summary>
        private void DrawSiblings(List<ImageLayer> siblings, Target target)
        {
            for (var index = 0; index < siblings.Count;)
            {
                var layer = siblings[index];
                var end = index + 1;
                if (layer.MaskSourceID is null && layer.Adjustment is null && !layer.IsGroup)
                {
                    while (end < siblings.Count && siblings[end].MaskSourceID == layer.ID) end++;
                }
                if (end > index + 1) DrawStack(siblings, index, end, target);
                else if (layer.IsVisible) DrawLayer(layer, target);
                index = end;
            }
        }

        private void DrawLayer(ImageLayer layer, Target target)
        {
            if (layer.IsGroup)
            {
                DrawSiblings(ChildrenOf(layer), target);
                return;
            }
            if (layer.Effects is not null && layer.Asset is null) throw Unsupported(layer);
            if (layer.Adjustment is not null)
            {
                DrawAdjustment(layer, target);
                return;
            }

            var opacity = LayerOpacity.Effective(layer, _byID);
            if (opacity <= 0) return;
            var content = Content(layer, out var bounds);
            if (content is null) return;
            using (content) Composite(target, content, bounds, layer.BlendMode, opacity);
        }

        /// <summary>
        /// An adjustment changes what is already on the surface, so it reads the target back, adjusts the
        /// whole of it, mixes the result in at the layer's effective opacity, and holds it to the layer's
        /// mask. It is not a layer of its own: it affects everything composited below it, inside its folder
        /// and out, because a folder is pass-through.
        /// </summary>
        private void DrawAdjustment(ImageLayer layer, Target target)
        {
            if (layer.Adjustment is not { } adjustment) return;
            var opacity = LayerOpacity.Effective(layer, _byID);
            if (opacity <= 0) return;
            var width = target.Bitmap.Width;
            var height = target.Bitmap.Height;
            if (width <= 0 || height <= 0) return;

            using var original = Copy(target.Bitmap);
            using var adjusted = Copy(target.Bitmap);
            AdjustmentOperators.Apply(adjustment, 1, adjusted.GetPixelSpan(), width, height, width * 4,
                target.Origin.X, target.Origin.Y);
            if (layer.BlendMode != LayerBlendMode.Normal) Reblend(original, adjusted, layer.BlendMode);

            using var mixed = Mix(original, adjusted, (float)opacity, null, default, Coverage.Gray);
            SKBitmap? masked = null;
            var painted = mixed;
            if (layer.Mask is { } mask && mask.EnabledImage is { } maskPixels)
            {
                var mapping = MaskToPixels(maskPixels, layer.MaskTransform, target.Origin);
                masked = Mix(original, mixed, 1f, maskPixels, default, Coverage.Gray, mapping);
                painted = masked;
            }
            var region = SKRectI.Create(target.Origin.X, target.Origin.Y, width, height);
            using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
            {
                target.Canvas.DrawBitmap(painted, region, OneToOne, paint);
            }
            masked?.Dispose();
        }

        /// <summary>
        /// Blends an adjustment's colours over the original at full coverage and puts the original's alpha
        /// back, which is what the Mac build does for an adjustment that is not Normal. Source-over of two
        /// translucent copies would thicken soft edges.
        /// </summary>
        private static void Reblend(SKBitmap original, SKBitmap adjusted, LayerBlendMode mode)
        {
            var width = original.Width;
            var height = original.Height;
            var stride = width * 4;
            var coverage = new byte[width * height];
            BrushPixels.ExtractAlpha(original.GetPixelSpan(), stride, coverage, width, width, height);
            BrushPixels.UnpremultiplyOpaque(original.GetPixelSpan(), stride, width, height);
            BrushPixels.UnpremultiplyOpaque(adjusted.GetPixelSpan(), stride, width, height);

            var composite = BlendModes.From(mode);
            if (BlendModes.Skia(composite) is { } native)
            {
                using var canvas = new SKCanvas(original);
                using var source = SKImage.FromBitmap(adjusted);
                using var paint = new SKPaint { BlendMode = native };
                canvas.DrawImage(source, SKRect.Create(0, 0, width, height), OneToOne, paint);
            }
            else
            {
                BlendModes.Composite(composite, adjusted.GetPixelSpan(), original.GetPixelSpan(),
                    original.GetPixelSpan(), width, height, stride);
            }
            BrushPixels.RestoreAlpha(original.GetPixelSpan(), stride, coverage, width, width, height);
        }

        /// <summary>
        /// A base and everything clipped to it. The base's coverage clips each member, the members blend
        /// into a surface of their own with their own modes and opacities, and the result composites once,
        /// carrying the base's mode and opacity.
        /// </summary>
        private void DrawStack(List<ImageLayer> siblings, int start, int end, Target target)
        {
            var baseLayer = siblings[start];
            if (baseLayer.Effects is not null && baseLayer.Asset is null) throw Unsupported(baseLayer);
            var opacity = LayerOpacity.Effective(baseLayer, _byID);
            if (opacity <= 0) return;

            // The coverage comes from the base's pixels whether or not the base is drawn: it is the shape
            // the layers above it sit inside. No pixels, no coverage, and nothing above shows.
            var baseContent = Content(baseLayer, out var baseBounds);
            if (baseContent is null) return;

            // Every member is painted before the surface is made, because a layer's effects make it bigger
            // than its transform says and the surface has to hold all of them.
            var painted = new Dictionary<int, (SKBitmap? Content, SKRectI Bounds)>();
            var bounds = baseBounds;
            for (var index = start + 1; index < end; index++)
            {
                var child = siblings[index];
                if (!child.IsVisible || child.Adjustment is not null) continue;
                if (child.Effects is not null && child.Asset is null) throw Unsupported(child);
                var content = Content(child, out var childBounds);
                painted[index] = (content, childBounds);
                if (content is not null) bounds = SKRectI.Union(bounds, childBounds);
            }

            using (baseContent)
            {
                using var surface = Allocate(bounds.Width, bounds.Height);
                using (var canvas = new SKCanvas(surface))
                {
                    canvas.Translate(-bounds.Left, -bounds.Top);
                    if (baseLayer.IsVisible) canvas.DrawBitmap(baseContent, baseBounds, OneToOne, new SKPaint());
                    var group = new Target(surface, canvas, bounds.Location);
                    for (var index = start + 1; index < end; index++)
                    {
                        var child = siblings[index];
                        if (!child.IsVisible) continue;
                        if (child.Adjustment is not null)
                        {
                            DrawAdjustment(child, group);
                            continue;
                        }
                        if (!painted.TryGetValue(index, out var member) || member.Content is null) continue;
                        using (member.Content)
                        {
                            using var clipped = Restrict(member.Content, member.Bounds, baseContent, baseBounds, luminance: false);
                            Composite(group, clipped, member.Bounds, child.BlendMode,
                                LayerOpacity.Effective(child, _byID));
                        }
                    }
                }
                Composite(target, surface, bounds, baseLayer.BlendMode, opacity);
            }
        }

        /// <summary>
        /// The layer's own pixels with its mask and every enclosing folder mask applied, in a bitmap whose
        /// top-left corner is the layer's rotated bounds. Null when the layer holds no pixels. A layer with
        /// effects goes the other way round: its mask hides part of its pixels first and the effects are
        /// drawn around what is left, so its own mask must not be applied to the result a second time.
        /// </summary>
        private SKBitmap? Content(ImageLayer layer, out SKRectI bounds)
        {
            bounds = Clip(Bounds(layer.Transform));
            if (layer.Asset is not { } asset || bounds.Width <= 0 || bounds.Height <= 0) return null;
            if (layer.Effects is { } effects)
            {
                using var shown = ImageWithOwnMask(layer, asset);
                if (EffectRasterizer.Render(shown, effects) is { } raster)
                {
                    using (raster) return Raster(layer, raster, out bounds);
                }
            }
            var content = Allocate(bounds.Width, bounds.Height);
            using (var canvas = new SKCanvas(content))
            {
                canvas.Translate(-bounds.Left, -bounds.Top);
                using var paint = new SKPaint { IsAntialias = true };
                DrawTransformed(canvas, asset.Image, layer.Transform, SKBlendMode.SrcOver, paint);
            }
            foreach (var (mask, transform) in Masks(layer, includeOwn: true))
            {
                var restricted = RestrictMask(content, mask, MaskToPixels(mask, transform, bounds.Location));
                content.Dispose();
                content = restricted;
            }
            return content;
        }

        /// <summary>
        /// A rendered effects raster, placed by the transform the effects grew: the raster is
        /// <c>inset</c> pixels bigger on every side, so its transform is scaled up by the same proportion to
        /// land on the same centre.
        /// </summary>
        private SKBitmap? Raster(ImageLayer layer, EffectRaster raster, out SKRectI bounds)
        {
            var transform = Grown(layer.Transform, raster);
            bounds = Clip(Bounds(transform));
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;
            var content = Allocate(bounds.Width, bounds.Height);
            using (var canvas = new SKCanvas(content))
            {
                canvas.Translate(-bounds.Left, -bounds.Top);
                using var paint = new SKPaint { IsAntialias = true };
                DrawTransformed(canvas, raster.Pixels, transform, SKBlendMode.SrcOver, paint);
            }
            foreach (var (mask, maskTransform) in Masks(layer, includeOwn: false))
            {
                var restricted = RestrictMask(content, mask, MaskToPixels(mask, maskTransform, bounds.Location));
                content.Dispose();
                content = restricted;
            }
            return content;
        }

        private static Model.LayerTransform Grown(Model.LayerTransform transform, EffectRaster raster)
        {
            var width = raster.Pixels.Width;
            var height = raster.Pixels.Height;
            var inset = -raster.OffsetX;
            var innerWidth = width - inset * 2;
            var innerHeight = height - inset * 2;
            if (innerWidth <= 0 || innerHeight <= 0) return transform;
            var grownWidth = transform.Width * width / innerWidth;
            var grownHeight = transform.Height * height / innerHeight;
            var centreX = transform.X + transform.Width / 2;
            var centreY = transform.Y + transform.Height / 2;
            return new Model.LayerTransform(centreX - grownWidth / 2, centreY - grownHeight / 2, grownWidth, grownHeight,
                transform.Rotation, transform.FlipX, transform.FlipY, transform.Sampling);
        }

        /// <summary>The layer's pixels in their own grid with its own mask applied, as the effects path wants.</summary>
        private static SKBitmap ImageWithOwnMask(ImageLayer layer, ImportedImage asset)
        {
            var image = Allocate(asset.Width, asset.Height);
            using (var canvas = new SKCanvas(image))
            {
                using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
                using var source = SKImage.FromBitmap(asset.Image);
                canvas.DrawImage(source, SKRect.Create(0, 0, asset.Width, asset.Height), OneToOne, paint);
            }
            if (layer.Mask is { } mask && mask.EnabledImage is { } maskPixels)
            {
                // Effects run in the asset's pixel grid. An independent mask must first be mapped back
                // from its document placement, rather than being attached to the layer again.
                var layerToDocument = BrushEdits.PixelToDocument(layer.Transform, asset.Width, asset.Height);
                if (!layerToDocument.TryInvert(out var documentToLayer))
                {
                    image.Erase(SKColors.Transparent);
                    return image;
                }
                var maskToLayer = SKMatrix.Concat(documentToLayer,
                    BrushEdits.PixelToDocument(layer.MaskTransform, maskPixels.Width, maskPixels.Height));
                var restricted = RestrictMask(image, maskPixels, maskToLayer);
                image.Dispose();
                image = restricted;
            }
            return image;
        }

        /// <summary>The layer's own enabled mask, then each enclosing folder's, innermost first.</summary>
        private IEnumerable<(SKBitmap Image, Model.LayerTransform Transform)> Masks(ImageLayer layer, bool includeOwn)
        {
            if (includeOwn && layer.Mask is { } mask && mask.EnabledImage is { } own) yield return (own, layer.MaskTransform);
            var parent = layer.ParentID;
            for (var depth = 0; parent is { } id && depth < 64; depth++)
            {
                if (!_byID.TryGetValue(id, out var folder)) break;
                if (folder.Mask is { } folderMask && folderMask.EnabledImage is { } pixels)
                {
                    yield return (pixels, folderMask.Placement ?? folder.Transform);
                }
                parent = folder.ParentID;
            }
        }

        /// <summary>
        /// A copy of a rendered layer held to one clip: nothing outside the clip's rectangle, and nothing
        /// the clip's image does not cover inside it. It has to be a fresh bitmap — a canvas clip bounds
        /// only what is drawn after it, and a blend mode bounds only the rectangle it is drawn into, so
        /// neither can take away pixels that are already there.
        /// </summary>
        private static SKBitmap Restrict(SKBitmap content, SKRectI bounds, SKBitmap clip, SKRectI clipBounds, bool luminance)
        {
            using var empty = Allocate(bounds.Width, bounds.Height);
            var local = Shift(clipBounds, bounds.Location);
            return Mix(empty, content, 1f, clip, local, luminance ? Coverage.Gray : Coverage.Alpha);
        }

        /// <summary>A mask's own pixels mapped into a render tile, including its rotation and flips.</summary>
        private static SKMatrix MaskToPixels(SKBitmap mask, Model.LayerTransform transform, SKPointI origin) =>
            SKMatrix.Concat(SKMatrix.CreateTranslation(-origin.X, -origin.Y),
                BrushEdits.PixelToDocument(transform, mask.Width, mask.Height));

        private static SKBitmap RestrictMask(SKBitmap content, SKBitmap mask, SKMatrix maskToPixels)
        {
            using var empty = Allocate(content.Width, content.Height);
            return Mix(empty, content, 1f, mask, default, Coverage.Gray, maskToPixels);
        }

        /// <summary>
        /// Per channel, <c>under + (over − under) · strength · coverage</c> on premultiplied bytes. This is
        /// the one shape a mask, a clip and an adjustment's opacity all take: a clip is this with nothing
        /// under it, and an adjustment is this between the surface and its adjusted self.
        /// <para>
        /// A gray clip is a mask, and a mask is stretched to the rectangle it is placed in — that is what
        /// lets a uniform 1×1 mask cover a whole layer. A clip's own alpha is the base's coverage, which is
        /// already the same size as the rectangle, so it is read straight across.
        /// </para>
        /// </summary>
        private static SKBitmap Mix(SKBitmap under, SKBitmap over, float strength, SKBitmap? clip,
            SKRectI clipInBitmaps, Coverage kind, SKMatrix? maskToPixels = null)
        {
            var width = under.Width;
            var height = under.Height;
            var result = Allocate(width, height);
            var underPixels = under.GetPixelSpan();
            var overPixels = over.GetPixelSpan();
            var outPixels = result.GetPixelSpan();
            var clipPixels = clip is null ? Span<byte>.Empty : clip.GetPixelSpan();
            var pixelsToMask = SKMatrix.Identity;
            var mappedMask = maskToPixels.HasValue;
            var invertible = !mappedMask || maskToPixels!.Value.TryInvert(out pixelsToMask);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var coverage = strength;
                    if (clip is not null)
                    {
                        if (mappedMask)
                        {
                            var sample = pixelsToMask.MapPoint(x + 0.5f, y + 0.5f);
                            coverage *= invertible && sample.X >= 0 && sample.Y >= 0
                                && sample.X < clip.Width && sample.Y < clip.Height
                                ? clipPixels[(int)sample.Y * clip.RowBytes + (int)sample.X] / 255f : 0f;
                        }
                        else
                        {
                            var cx = x - clipInBitmaps.Left;
                            var cy = y - clipInBitmaps.Top;
                            if (cx < 0 || cy < 0 || cx >= clipInBitmaps.Width || cy >= clipInBitmaps.Height)
                            {
                                coverage = 0f;
                            }
                            else if (kind == Coverage.Gray)
                            {
                                var sx = Math.Clamp((cx * 2 + 1) * clip.Width / (clipInBitmaps.Width * 2), 0, clip.Width - 1);
                                var sy = Math.Clamp((cy * 2 + 1) * clip.Height / (clipInBitmaps.Height * 2), 0, clip.Height - 1);
                                coverage *= clipPixels[sy * clip.RowBytes + sx] / 255f;
                            }
                            else
                            {
                                coverage *= clipPixels[cy * clip.RowBytes + cx * 4 + 3] / 255f;
                            }
                        }
                    }
                    var at = (y * width + x) * 4;
                    for (var channel = 0; channel < 4; channel++)
                    {
                        var value = underPixels[at + channel]
                            + (overPixels[at + channel] - underPixels[at + channel]) * coverage;
                        outPixels[at + channel] = (byte)MathF.Round(value, MidpointRounding.AwayFromZero);
                    }
                }
            }
            return result;
        }

        /// <summary>A rectangle moved into a bitmap whose top-left corner is <paramref name="origin"/>.</summary>
        private static SKRectI Shift(SKRectI rect, SKPointI origin) =>
            SKRectI.Create(rect.Left - origin.X, rect.Top - origin.Y, rect.Width, rect.Height);

        private static SKBitmap Copy(SKBitmap source)
        {
            var copy = Allocate(source.Width, source.Height);
            using var canvas = new SKCanvas(copy);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(source, SKRectI.Create(0, 0, source.Width, source.Height), OneToOne, paint);
            return copy;
        }

        /// <summary>Whether a clip image's coverage is its gray or its alpha.</summary>
        private enum Coverage
        {
            Gray,
            Alpha,
        }

        /// <summary>Places a rendered layer on the target: Skia draws the sixteen modes it has, the rest are done by hand.</summary>
        private static void Composite(Target target, SKBitmap content, SKRectI bounds, LayerBlendMode mode, double opacity)
        {
            if (opacity < 1) ScaleChannels(content, opacity);
            var composite = BlendModes.From(mode);
            if (BlendModes.Skia(composite) is { } native)
            {
                using var paint = new SKPaint { BlendMode = native };
                target.Canvas.DrawBitmap(content, bounds, OneToOne, paint);
                return;
            }
            var canvasBounds = SKRectI.Create(target.Origin.X, target.Origin.Y, target.Bitmap.Width, target.Bitmap.Height);
            var overlap = SKRectI.Intersect(bounds, canvasBounds);
            if (overlap.Width <= 0 || overlap.Height <= 0) return;
            var from = Shift(overlap, bounds.Location);
            var into = Shift(overlap, target.Origin);
            var row = content.Width * 4;
            var stride = target.Bitmap.Width * 4;
            var source = (from.Top * content.Width + from.Left) * 4;
            var destination = (into.Top * target.Bitmap.Width + into.Left) * 4;
            BlendModes.Composite(composite,
                content.GetPixelSpan()[source..], row,
                target.Bitmap.GetPixelSpan()[destination..], stride,
                target.Bitmap.GetPixelSpan()[destination..], stride,
                overlap.Width, overlap.Height);
        }

        /// <summary>
        /// Scales every channel of a premultiplied bitmap, which is how opacity reaches it: scaling alpha
        /// and the colours it was premultiplied into together leaves the colour untouched.
        /// </summary>
        private static void ScaleChannels(SKBitmap bitmap, double opacity)
        {
            var scale = (float)opacity;
            var pixels = bitmap.GetPixelSpan();
            for (var index = 0; index < pixels.Length; index++)
            {
                pixels[index] = (byte)MathF.Round(pixels[index] * scale, MidpointRounding.AwayFromZero);
            }
        }

        /// <summary>
        /// The rounded-out box a rotated rectangle covers, plus <paramref name="padding"/> pixels for the
        /// edges. A mask is placed on the rectangle itself, so it is asked for with no padding.
        /// </summary>
        private static SKRectI Bounds(Model.LayerTransform transform, int padding = 1)
        {
            var radians = transform.Rotation * Math.PI / 180;
            var cos = MathF.Abs((float)Math.Cos(radians));
            var sin = MathF.Abs((float)Math.Sin(radians));
            var width = (float)transform.Width * cos + (float)transform.Height * sin;
            var height = (float)transform.Width * sin + (float)transform.Height * cos;
            var centreX = transform.X + transform.Width / 2;
            var centreY = transform.Y + transform.Height / 2;
            var left = (float)Math.Floor(centreX - width / 2) - padding;
            var top = (float)Math.Floor(centreY - height / 2) - padding;
            var right = (float)Math.Ceiling(centreX + width / 2) + padding;
            var bottom = (float)Math.Ceiling(centreY + height / 2) + padding;
            return SKRectI.Create((int)left, (int)top, (int)(right - left), (int)(bottom - top));
        }

        private readonly record struct Target(SKBitmap Bitmap, SKCanvas Canvas, SKPointI Origin);
    }
}
