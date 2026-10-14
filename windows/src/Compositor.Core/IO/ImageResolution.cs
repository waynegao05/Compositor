using System.Buffers.Binary;

namespace Compositor.Core.IO;

/// <summary>Physical density from small image headers, without decoding or allocating another image.</summary>
internal static class ImageResolution
{
    private const int MaximumHeaderBytes = 1024 * 1024;

    internal static double? Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumHeaderBytes) bytes = bytes[..MaximumHeaderBytes];
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return Png(bytes);
        if (bytes.StartsWith(new byte[] { 255, 216 })) return Jpeg(bytes);
        return null;
    }

    private static double? Valid(double value) => value is >= 1 and <= 9600 ? value : null;

    private static double? Png(ReadOnlySpan<byte> bytes)
    {
        var at = 8;
        while (at + 12 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(at, 4));
            if (length > bytes.Length - at - 12) return null;
            var type = bytes.Slice(at + 4, 4);
            if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8)) return null;
            if (type.SequenceEqual("pHYs"u8) && length == 9 && bytes[at + 16] == 1)
                return Valid(BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(at + 8, 4)) * 0.0254);
            at += (int)length + 12;
        }
        return null;
    }

    private static double? Jpeg(ReadOnlySpan<byte> bytes)
    {
        double? jfif = null;
        var at = 2;
        while (at + 4 <= bytes.Length && bytes[at] == 255)
        {
            while (at + 1 < bytes.Length && bytes[at + 1] == 255) at++;
            if (at + 4 > bytes.Length) break;
            var marker = bytes[at + 1];
            if (marker is 0xDA or 0xD9) break;
            if (marker is 0x01 or >= 0xD0 and <= 0xD7) { at += 2; continue; }
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(at + 2, 2));
            if (length < 2 || length > bytes.Length - at - 2) break;
            var data = bytes.Slice(at + 4, length - 2);
            if (marker == 0xE0 && data.Length >= 12 && data.StartsWith("JFIF\0"u8))
            {
                var density = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(8, 2));
                jfif = data[7] switch { 1 => Valid(density), 2 => Valid(density * 2.54), _ => null };
            }
            if (marker == 0xE1 && data.StartsWith("Exif\0\0"u8)
                && Exif(data[6..]) is { } resolution) return resolution;
            at += length + 2;
        }
        return jfif;
    }

    /// <summary>Only the first TIFF directory's X/Y density and unit; no recursion or offset chasing.</summary>
    private static double? Exif(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8) return null;
        var little = bytes.StartsWith("II"u8);
        if (!little && !bytes.StartsWith("MM"u8)) return null;
        if (U16(bytes[2..], little) != 42) return null;
        var offset = U32(bytes[4..], little);
        if (offset > bytes.Length - 2) return null;
        var at = (int)offset;
        var count = U16(bytes[at..], little);
        at += 2;
        if (count > (bytes.Length - at) / 12) return null;
        double? x = null, y = null;
        var unit = 2;
        for (var i = 0; i < count; i++, at += 12)
        {
            var entry = bytes.Slice(at, 12);
            if (U32(entry[4..], little) != 1) continue;
            var tag = U16(entry, little);
            var type = U16(entry[2..], little);
            if (tag == 0x128 && type == 3) unit = U16(entry[8..], little);
            if (tag is not (0x11A or 0x11B) || type != 5) continue;
            var valueAt = U32(entry[8..], little);
            if (valueAt > bytes.Length - 8) continue;
            var rational = bytes.Slice((int)valueAt, 8);
            var denominator = U32(rational[4..], little);
            if (denominator == 0) continue;
            var density = U32(rational, little) / (double)denominator;
            if (tag == 0x11A) x = density; else y = density;
        }
        if ((x ?? y) is not { } value) return null;
        return unit switch { 2 => Valid(value), 3 => Valid(value * 2.54), _ => null };
    }

    private static ushort U16(ReadOnlySpan<byte> value, bool little) => little
        ? BinaryPrimitives.ReadUInt16LittleEndian(value) : BinaryPrimitives.ReadUInt16BigEndian(value);

    private static uint U32(ReadOnlySpan<byte> value, bool little) => little
        ? BinaryPrimitives.ReadUInt32LittleEndian(value) : BinaryPrimitives.ReadUInt32BigEndian(value);
}
