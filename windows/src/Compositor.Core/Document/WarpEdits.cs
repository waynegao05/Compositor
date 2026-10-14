using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>
/// The Blur tool's two modes that push pixels around rather than mixing them: Liquify shoves the picture along
/// with the brush, Smudge drags its colour along.
/// </summary>
public enum WarpMode
{
    /// <summary>Pixels under the brush move with it, most at its middle and none at its rim.</summary>
    Liquify,

    /// <summary>The colour under the brush is dragged along with it, and the brush carries some of it on.</summary>
    Smudge,
}

/// <summary>
/// A Smudge or Liquify stroke: the layer as the canvas shows it is pushed around, dab by dab, at document size,
/// and the result goes back into the layer's own pixels under the stroke. The Mac build works the same way,
/// which is why a stroke is resampled at document resolution rather than the layer's own.
/// </summary>
public static class WarpEdits
{
    /// <summary>
    /// Pushes a layer's pixels along <paramref name="points"/>. False when there is no such layer with pixels,
    /// when the stroke is empty, or when the stroke never reaches anywhere the layer is — in which case the
    /// layer is left exactly as it was.
    /// </summary>
    public static bool Warp(CanvasDocument document, Guid layerID, IReadOnlyList<SKPoint> points, WarpMode mode,
        BrushSettings settings)
    {
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { Asset: { } asset } layer) return false;
        if (layer.IsGroup || points.Count == 0 || document.Selection.IsEmpty) return false;
        if (!(settings.Diameter >= 2) || settings.Opacity <= 0) return false;
        var width = document.Width;
        var height = document.Height;
        if (width <= 0 || height <= 0 || (long)width * height > DocumentLimits.MaxSurfacePixels) return false;

        SKBitmap? work = null;
        try
        {
            work = DocumentRenderer.Allocate(width, height);
            using (var canvas = new SKCanvas(work)) DocumentRenderer.DrawLayerPixels(canvas, layer);
        }
        catch (InvalidOperationException)
        {
            work?.Dispose();
            return false;
        }

        using (work)
        {
            var stroke = new Stroke(work, mode, settings);
            foreach (var point in points) stroke.Append(point);
            if (stroke.Dabs.Count == 0) return false;
            using var coverage = FillEdits.Coverage(document, out var region);
            return StrokeInto(layer, work, stroke, asset, coverage, region);
        }
    }

    /// <summary>
    /// The stroke's result, put back into the layer: every layer pixel the brush passed over takes the colour
    /// the stroke left where that pixel was on the document. What the brush did not reach is untouched, and a
    /// pixel whose place on the document is off the canvas keeps what it had rather than falling to nothing.
    /// </summary>
    private static bool StrokeInto(ImageLayer layer, SKBitmap work, Stroke stroke, ImportedImage asset,
        SKBitmap? coverage, SKRectI region)
    {
        var width = asset.Width;
        var height = asset.Height;
        var toDocument = BrushEdits.PixelToDocument(layer.Transform, width, height);
        var painted = new SKBitmap(Bitmaps.ColorInfo(width, height));
        using (var canvas = new SKCanvas(painted))
        {
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            using var source = SKImage.FromBitmap(asset.Image);
            canvas.DrawImage(source, SKRect.Create(0, 0, width, height),
                new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }
        var source2 = work.GetPixelSpan();
        var stride = work.RowBytes;
        var target = painted.GetPixelSpan();
        var radius = (stroke.Diameter + 4) / 2;
        var covered = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = toDocument.MapPoint(x + 0.5f, y + 0.5f);
                if (!stroke.Covered(at, radius)) continue;
                var amount = FillEdits.Amount(coverage, region, at);
                if (amount <= 0) continue;
                var sx = (int)Math.Floor(at.X);
                var sy = (int)Math.Floor(at.Y);
                if (sx < 0 || sy < 0 || sx >= work.Width || sy >= work.Height) continue;
                var from = sy * stride + sx * 4;
                var to = y * painted.RowBytes + x * 4;
                var originalAlpha = target[to + 3];
                var alpha = originalAlpha * (1 - amount) + source2[from + 3] * amount;
                // Feather in premultiplied space, then store straight color again.
                for (var channel = 0; channel < 3; channel++)
                {
                    var premultiplied = target[to + channel] * originalAlpha / 255.0 * (1 - amount)
                        + source2[from + channel] * amount;
                    target[to + channel] = alpha <= 0 ? (byte)0
                        : (byte)Math.Clamp(Math.Round(premultiplied * 255 / alpha), 0, 255);
                }
                target[to + 3] = (byte)Math.Clamp(Math.Round(alpha), 0, 255);
                covered++;
            }
        }
        if (covered == 0)
        {
            painted.Dispose();
            return false;
        }
        layer.Asset = ImportedImage.Create(painted, asset.Name);
        return true;
    }

    /// <summary>
    /// A stroke being pushed through the pixels, dab by dab. Dabs sit close together along the path, as the
    /// brush's own do, so a stroke leaves a continuous trail rather than a row of discs.
    /// </summary>
    private sealed class Stroke
    {
        private const double SmudgeSpacing = 0.08;
        private const double LiquifySpacing = 0.025;

        private readonly SKBitmap _work;
        private readonly WarpMode _mode;
        private readonly double _diameter;
        private readonly double _hardness;
        private readonly double _strength;
        private readonly int _radius;
        private readonly int _width;
        private readonly int _height;
        private SKPoint? _last;
        /// <summary>Smudge: the colour the brush is carrying, a (2r+1)² square of floats.</summary>
        private float[] _carried = [];
        /// <summary>Liquify: the pixels a dab reads from, as they were before that dab.</summary>
        private float[] _scratch = [];

        public Stroke(SKBitmap work, WarpMode mode, BrushSettings settings)
        {
            _work = work;
            _mode = mode;
            _diameter = Math.Max(2, settings.Diameter);
            _hardness = Math.Clamp(settings.Hardness, 0, 0.98);
            _strength = Math.Clamp(settings.Opacity, 0.01, 1);
            _radius = (int)Math.Ceiling(_diameter / 2);
            _width = work.Width;
            _height = work.Height;
        }

        /// <summary>Every dab's middle, in document pixels, which is what tells whether the brush passed over a place.</summary>
        public List<SKPoint> Dabs { get; } = [];

        /// <summary>The brush's width in document pixels, which is how wide a dab reaches.</summary>
        public double Diameter => _diameter;

        /// <summary>Whether the brush passed within <paramref name="radius"/> of a document point.</summary>
        public bool Covered(SKPoint point, double radius)
        {
            foreach (var dab in Dabs)
            {
                if (Math.Sqrt(Math.Pow(point.X - dab.X, 2) + Math.Pow(point.Y - dab.Y, 2)) <= radius) return true;
            }
            return false;
        }

        /// <summary>The path continued to <paramref name="point"/>, dabbing all the way.</summary>
        public void Append(SKPoint point)
        {
            if (_last is not { } from)
            {
                _last = point;
                if (_mode == WarpMode.Smudge) PickUp(point);
                return;
            }
            var distance = Math.Sqrt(Math.Pow(point.X - from.X, 2) + Math.Pow(point.Y - from.Y, 2));
            var spacing = Math.Max(1, _diameter * (_mode == WarpMode.Smudge ? SmudgeSpacing : LiquifySpacing));
            if (distance < spacing) return;
            var steps = (int)Math.Ceiling(distance / spacing);
            var previous = from;
            for (var step = 1; step <= steps; step++)
            {
                var t = (double)step / steps;
                var next = new SKPoint(
                    (float)(from.X + (point.X - from.X) * t), (float)(from.Y + (point.Y - from.Y) * t));
                if (_mode == WarpMode.Smudge) Smudge(next);
                else Push(previous, next);
                Dabs.Add(next);
                previous = next;
            }
            _last = point;
        }

        /// <summary>How much a dab moves the pixels at <paramref name="u"/>: all of it at the middle, none at the rim.</summary>
        private float Weight(float u)
        {
            if (u >= 1) return 0;
            var hardness = (float)_hardness;
            if (u <= hardness) return 1;
            var t = (1 - u) / (1 - hardness);
            return t * t * (3 - 2 * t);
        }

        private Span<byte> Pixels => _work.GetPixelSpan();

        /// <summary>The colour under the brush, taken on before the stroke drags it anywhere.</summary>
        private void PickUp(SKPoint centre)
        {
            var radius = _radius;
            var side = radius * 2 + 1;
            _carried = new float[side * side * 4];
            var pixels = Pixels;
            var stride = _work.RowBytes;
            var cx = (int)Math.Round(centre.X);
            var cy = (int)Math.Round(centre.Y);
            for (var dy = -radius; dy <= radius; dy++)
            {
                var y = cy + dy;
                if (y < 0 || y >= _height) continue;
                for (var dx = -radius; dx <= radius; dx++)
                {
                    var x = cx + dx;
                    if (x < 0 || x >= _width) continue;
                    var p = y * stride + x * 4;
                    var c = ((dy + radius) * side + dx + radius) * 4;
                    for (var k = 0; k < 4; k++) _carried[c + k] = pixels[p + k];
                }
            }
        }

        /// <summary>Drags the colour the brush carries over the colour under it, and picks up some of the mix.</summary>
        private void Smudge(SKPoint centre)
        {
            var radius = _radius;
            var side = radius * 2 + 1;
            if (_carried.Length == 0) PickUp(centre);
            var pixels = Pixels;
            var stride = _work.RowBytes;
            var cx = (int)Math.Round(centre.X);
            var cy = (int)Math.Round(centre.Y);
            var keep = (float)_strength;
            var inverseR = (float)(1 / (_diameter / 2));
            for (var dy = -radius; dy <= radius; dy++)
            {
                var y = cy + dy;
                if (y < 0 || y >= _height) continue;
                for (var dx = -radius; dx <= radius; dx++)
                {
                    var x = cx + dx;
                    if (x < 0 || x >= _width) continue;
                    var w = Weight((float)Math.Sqrt(dx * dx + dy * dy) * inverseR);
                    if (w <= 0) continue;
                    var p = y * stride + x * 4;
                    var c = ((dy + radius) * side + dx + radius) * 4;
                    for (var k = 0; k < 4; k++)
                    {
                        var under = (float)pixels[p + k];
                        var painted = under + (_carried[c + k] - under) * w;
                        pixels[p + k] = Bounded(painted);
                        // The brush picks up some of what it just left, more the weaker the smudge is set.
                        _carried[c + k] = painted + (_carried[c + k] - painted) * keep;
                    }
                }
            }
        }

        /// <summary>The pixels under the brush move with it, fading to none at the rim.</summary>
        private void Push(SKPoint a, SKPoint b)
        {
            var radius = _radius;
            var moveX = (b.X - a.X) * (float)_strength;
            var moveY = (b.Y - a.Y) * (float)_strength;
            var margin = (int)Math.Ceiling(Math.Max(Math.Abs(moveX), Math.Abs(moveY))) + 2;
            var cx = (int)Math.Round(b.X);
            var cy = (int)Math.Round(b.Y);
            // The area as it was before this dab, which the dab reads from.
            var x0 = Math.Max(0, cx - radius - margin);
            var x1 = Math.Min(_width - 1, cx + radius + margin);
            var y0 = Math.Max(0, cy - radius - margin);
            var y1 = Math.Min(_height - 1, cy + radius + margin);
            if (x0 > x1 || y0 > y1) return;
            var cw = x1 - x0 + 1;
            var ch = y1 - y0 + 1;
            if (_scratch.Length < cw * ch * 4) _scratch = new float[cw * ch * 4];
            var pixels = Pixels;
            var stride = _work.RowBytes;
            for (var y = 0; y < ch; y++)
            {
                for (var x = 0; x < cw; x++)
                {
                    var p = (y + y0) * stride + (x + x0) * 4;
                    var s = (y * cw + x) * 4;
                    for (var k = 0; k < 4; k++) _scratch[s + k] = pixels[p + k];
                }
            }
            var inverseR = (float)(1 / (_diameter / 2));
            for (var dy = -radius; dy <= radius; dy++)
            {
                var y = cy + dy;
                if (y < y0 || y > y1) continue;
                for (var dx = -radius; dx <= radius; dx++)
                {
                    var x = cx + dx;
                    if (x < x0 || x > x1) continue;
                    var w = Weight((float)Math.Sqrt(dx * dx + dy * dy) * inverseR);
                    if (w <= 0) continue;
                    // Bilinear read of the old pixels, from behind the brush's travel.
                    var sx = Math.Min(cw - 1f, Math.Max(0f, x - x0 - moveX * w));
                    var sy = Math.Min(ch - 1f, Math.Max(0f, y - y0 - moveY * w));
                    var ix = Math.Min(cw - 2, (int)sx);
                    var iy = Math.Min(ch - 2, (int)sy);
                    if (ix < 0 || iy < 0) continue;
                    var fx = sx - ix;
                    var fy = sy - iy;
                    var p = y * stride + x * 4;
                    var s00 = (iy * cw + ix) * 4;
                    var s10 = s00 + 4;
                    var s01 = s00 + cw * 4;
                    var s11 = s01 + 4;
                    for (var k = 0; k < 4; k++)
                    {
                        var top = _scratch[s00 + k] + (_scratch[s10 + k] - _scratch[s00 + k]) * fx;
                        var bottom = _scratch[s01 + k] + (_scratch[s11 + k] - _scratch[s01 + k]) * fx;
                        pixels[p + k] = Bounded(top + (bottom - top) * fy);
                    }
                }
            }
        }

        private static byte Bounded(float value) =>
            (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
    }
}
