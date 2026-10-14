using Compositor.Core.Document;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.IO;

/// <summary>
/// Writing the flattened document out. The extension chooses the format: PNG is streamed a band of tiles at
/// a time and works at any canvas size; JPEG has to be made whole, so it is refused for a canvas too big to
/// hold rather than quietly failing.
/// </summary>
public static class ImageWriter
{
    public const int DefaultQuality = 90;

    public static bool Write(CanvasDocument document, string path, int quality = DefaultQuality)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            TiledPngWriter.Write(document, path);
            return true;
        }
        if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return WriteJpeg(document, path, quality);
        }
        return false;
    }

    /// <summary>Whether the format named by the extension is one this writes.</summary>
    public static bool Knows(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool WriteJpeg(CanvasDocument document, string path, int quality)
    {
        // Encoding is done on the whole picture at once, so a canvas that will not fit cannot be written.
        if ((long)document.Width * document.Height > DocumentLimits.MaxSurfacePixels) return false;
        using var flattened = DocumentRenderer.Render(document);
        using var image = SKImage.FromBitmap(flattened);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100));
        if (encoded is null) return false;
        AtomicImageWrite.Write(path, output => WriteJpegWithResolution(output, encoded.AsSpan(), document.Resolution));
        return true;
    }

    /// <summary>Set JFIF density without re-encoding pixels or disturbing ICC and other JPEG segments.</summary>
    internal static void WriteJpegWithResolution(Stream output, ReadOnlySpan<byte> encoded, double resolution)
    {
        if (encoded.Length < 2 || encoded[0] != 0xFF || encoded[1] != 0xD8)
            throw new InvalidDataException("The JPEG encoder returned an invalid image.");
        var density = (int)Math.Clamp(Math.Round(double.IsFinite(resolution) && resolution > 0 ? resolution : 72),
            1, ushort.MaxValue);
        var at = 2;
        while (at + 4 <= encoded.Length && encoded[at] == 0xFF)
        {
            var marker = encoded[at + 1];
            if (marker is 0xDA or 0xD9) break;
            var length = (encoded[at + 2] << 8) | encoded[at + 3];
            if (length < 2 || at + 2L + length > encoded.Length) break;
            if (marker == 0xE0 && length >= 16 && encoded.Slice(at + 4, 5).SequenceEqual("JFIF\0"u8))
            {
                // Units and both densities are the only bytes that change.
                output.Write(encoded[..(at + 11)]);
                Span<byte> values = stackalloc byte[5];
                values[0] = 1; // dots per inch
                values[1] = values[3] = (byte)(density >> 8);
                values[2] = values[4] = (byte)density;
                output.Write(values);
                output.Write(encoded[(at + 16)..]);
                return;
            }
            at += length + 2;
        }
        Span<byte> jfif = stackalloc byte[18];
        jfif.Clear();
        jfif[0] = 0xFF;
        jfif[1] = 0xE0;
        jfif[3] = 16;
        "JFIF\0"u8.CopyTo(jfif[4..]);
        jfif[9] = 1;
        jfif[10] = 2;
        jfif[11] = 1;
        jfif[12] = jfif[14] = (byte)(density >> 8);
        jfif[13] = jfif[15] = (byte)density;
        output.Write(encoded[..2]);
        output.Write(jfif);
        output.Write(encoded[2..]);
    }
}
