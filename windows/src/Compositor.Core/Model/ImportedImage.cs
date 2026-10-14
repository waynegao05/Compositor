using SkiaSharp;

namespace Compositor.Core.Model;

/// <summary>
/// Pixels a layer or a mask holds, with the small copy the layer panel draws. Pixels are immutable while
/// they stand for a saved asset: anything that changes them makes a new instance, so reference equality
/// means "the same pixels", as CGImage identity does in the Swift build.
/// </summary>
public sealed class ImportedImage : IDisposable
{
    public ImportedImage(SKBitmap image, SKBitmap thumbnail, string name, double? resolution = null)
    {
        Image = image;
        Thumbnail = thumbnail;
        Name = name;
        Resolution = resolution is >= 1 and <= 9600 ? resolution : null;
    }

    public SKBitmap Image { get; private set; }
    public SKBitmap Thumbnail { get; private set; }
    public string Name { get; private set; }
    /// <summary>Source pixels per inch, when the file supplies a valid physical density.</summary>
    public double? Resolution { get; }

    public int Width => Image.Width;
    public int Height => Image.Height;

    /// <summary>Thumbnail size: at most 96 pixels on the longer side, as the Mac build uses.</summary>
    public static (int Width, int Height) ThumbnailSize(int width, int height)
    {
        var factor = Math.Min(1, 96.0 / Math.Max(width, height));
        return (Math.Max(1, (int)(width * factor)), Math.Max(1, (int)(height * factor)));
    }

    public static ImportedImage Create(SKBitmap image, string name, double? resolution = null) =>
        new(image, Bitmaps.Thumbnail(image), name, resolution);

    public void Dispose()
    {
        Image.Dispose();
        Thumbnail.Dispose();
    }
}
