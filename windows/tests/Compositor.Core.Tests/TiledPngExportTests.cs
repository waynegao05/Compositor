using System.Buffers.Binary;
using Compositor.Core.IO;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Tests;

public sealed class TiledPngExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CompositorExport-" + Guid.NewGuid().ToString("N"));

    public TiledPngExportTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(72)]
    [InlineData(144)]
    [InlineData(300)]
    [InlineData(600)]
    public void PngAndJpegExportsKeepTheDocumentResolution(int dpi)
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 3, 2, dpi);
        var png = Path.Combine(_root, "Image.png");
        var jpeg = Path.Combine(_root, "Image.jpg");
        Assert.True(ImageWriter.Write(document, png));
        Assert.True(ImageWriter.Write(document, jpeg));
        var pngBytes = File.ReadAllBytes(png);
        var density = PngChunk(pngBytes, "pHYs");
        Assert.Equal(9, density.Length);
        Assert.Equal(1, density[8]);
        Assert.Equal((uint)Math.Round(dpi / 0.0254), BinaryPrimitives.ReadUInt32BigEndian(density.AsSpan(0, 4)));
        Assert.Equal(density[..4], density[4..8]);
        var jpegBytes = File.ReadAllBytes(jpeg);
        var jfif = jpegBytes.AsSpan().IndexOf("JFIF\0"u8);
        Assert.True(jfif >= 0);
        Assert.Equal(1, jpegBytes[jfif + 7]);
        Assert.Equal(dpi, BinaryPrimitives.ReadUInt16BigEndian(jpegBytes.AsSpan(jfif + 8, 2)));
        Assert.Equal(dpi, BinaryPrimitives.ReadUInt16BigEndian(jpegBytes.AsSpan(jfif + 10, 2)));
        using var decodedPng = SKBitmap.Decode(png);
        using var decodedJpeg = SKBitmap.Decode(jpeg);
        Assert.NotNull(decodedPng);
        Assert.NotNull(decodedJpeg);
        Assert.Equal(3, decodedPng.Width);
        Assert.Equal(2, decodedJpeg.Height);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChangingJpegDensityKeepsIccAndCompressedData(bool hasJfif)
    {
        byte[] prefix = [0xFF, 0xD8];
        byte[] jfif = [0xFF, 0xE0, 0, 16, 74, 70, 73, 70, 0, 1, 2, 0, 0, 1, 0, 1, 0, 0];
        byte[] payload = [0xFF, 0xE2, 0, 6, 73, 67, 67, 0, 0xFF, 0xDA, 0, 2, 11, 0xFF, 0, 30, 0xFF, 0xD9];
        byte[] encoded = hasJfif ? [.. prefix, .. jfif, .. payload] : [.. prefix, .. payload];
        using var output = new MemoryStream();
        ImageWriter.WriteJpegWithResolution(output, encoded, 300);
        var result = output.ToArray();
        Assert.Equal(prefix, result[..2]);
        Assert.Equal(payload, result[20..]);
        Assert.Equal(1, result[13]);
        Assert.Equal(300, BinaryPrimitives.ReadUInt16BigEndian(result.AsSpan(14, 2)));
        Assert.Equal(300, BinaryPrimitives.ReadUInt16BigEndian(result.AsSpan(16, 2)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFailedWritePreservesTheTargetAndCleansItsTemporaryFile(bool existing)
    {
        var path = Path.Combine(_root, "Image.png");
        byte[] original = [10, 20, 30, 40];
        if (existing) File.WriteAllBytes(path, original);
        Assert.Throws<IOException>(() => AtomicImageWrite.Write(path, output =>
        {
            output.Write([1, 2, 3]);
            throw new IOException("Injected write failure.");
        }));
        Assert.Equal(existing, File.Exists(path));
        if (existing) Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void InvalidPngSettingsDoNotTruncateAnExistingExport()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 3, 2);
        var path = Path.Combine(_root, "Existing.png");
        byte[] original = [10, 20, 30];
        File.WriteAllBytes(path, original);
        Assert.Throws<ArgumentOutOfRangeException>(() => TiledPngWriter.Write(document, path, tileSize: 0));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void AFailedFinalMoveCleansItsTemporaryFile()
    {
        var path = Path.Combine(_root, "Directory.png");
        Directory.CreateDirectory(path);
        Assert.ThrowsAny<IOException>(() => AtomicImageWrite.Write(path, output => output.Write([1, 2, 3])));
        Assert.True(Directory.Exists(path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    public void SuccessfulExportReplacesTheWholeTarget(string extension)
    {
        var path = Path.Combine(_root, "Existing" + extension);
        File.WriteAllBytes(path, new byte[2048]);
        using var document = new CanvasDocument(Guid.NewGuid(), 3, 2, 300);
        Assert.True(ImageWriter.Write(document, path));
        using var decoded = SKBitmap.Decode(path);
        Assert.NotNull(decoded);
        Assert.Equal(3, decoded.Width);
        Assert.Equal(2, decoded.Height);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    private static byte[] PngChunk(byte[] bytes, string name)
    {
        for (var at = 8; at + 12 <= bytes.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(at, 4));
            Assert.InRange(length, 0, bytes.Length - at - 12);
            if (System.Text.Encoding.ASCII.GetString(bytes, at + 4, 4) == name)
                return bytes.AsSpan(at + 8, length).ToArray();
            at += 12 + length;
        }
        throw new InvalidDataException("Expected PNG chunk is missing: " + name);
    }
}
