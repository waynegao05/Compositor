using System.Buffers.Binary;
using BitMiracle.LibTiff.Classic;
using Compositor.Core.IO;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Tests;

public sealed class ImageResolutionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("Compositor-resolution-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(".png", 300)]
    [InlineData(".jpg", 300)]
    [InlineData(".png", 600)]
    [InlineData(".jpg", 600)]
    public void ExportedDensityBecomesTheImportedDocumentDensity(string extension, int resolution)
    {
        using var source = new CanvasDocument(Guid.NewGuid(), 6, 4, resolution);
        var path = Path.Combine(_root, "image" + extension);
        Assert.True(ImageWriter.Write(source, path));
        using var imported = ImageImporter.Decode(path);
        using var document = ImageImporter.NewDocument(imported);
        Assert.InRange(document.Resolution, resolution - 0.02, resolution + 0.02);
        Assert.Equal(6, document.Width);
        Assert.Equal(4, document.Height);
        using var explicitResolution = ImageImporter.NewDocument(imported, 144);
        Assert.Equal(144, explicitResolution.Resolution);
    }

    [Theory]
    [InlineData(2, 300, 300)]
    [InlineData(3, 100, 254)]
    [InlineData(1, 300, 72)]
    public void TiffResolutionUsesItsDeclaredUnit(int unit, int density, int expected)
    {
        var path = Path.Combine(_root, "density.tiff");
        using (var tiff = Tiff.Open(path, "w"))
        {
            Assert.NotNull(tiff);
            tiff.SetField(TiffTag.IMAGEWIDTH, 2);
            tiff.SetField(TiffTag.IMAGELENGTH, 1);
            tiff.SetField(TiffTag.SAMPLESPERPIXEL, 3);
            tiff.SetField(TiffTag.BITSPERSAMPLE, 8);
            tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
            tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
            tiff.SetField(TiffTag.ROWSPERSTRIP, 1);
            tiff.SetField(TiffTag.RESOLUTIONUNIT, unit);
            tiff.SetField(TiffTag.XRESOLUTION, (double)density);
            tiff.SetField(TiffTag.YRESOLUTION, (double)density);
            Assert.True(tiff.WriteScanline([255, 0, 0, 0, 255, 0], 0));
        }
        using var imported = ImageImporter.Decode(path);
        using var document = ImageImporter.NewDocument(imported);
        Assert.Equal(expected, document.Resolution, 3);
        Assert.Equal(SKColors.Red, imported.Image.GetPixel(0, 0));
        Assert.Equal(SKColors.Lime, imported.Image.GetPixel(1, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(9601)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidOrMissingDensityFallsBackTo72(double value)
    {
        using var pixels = new SKBitmap(Bitmaps.ColorInfo(2, 1));
        using var imported = ImportedImage.Create(pixels, "image", value);
        Assert.Null(imported.Resolution);
        using var document = ImageImporter.NewDocument(imported);
        Assert.Equal(72, document.Resolution);
    }

    [Theory]
    [InlineData(1, 300, 300d)]
    [InlineData(2, 100, 254d)]
    [InlineData(0, 300, null)]
    [InlineData(1, 0, null)]
    [InlineData(1, 9601, null)]
    public void JfifPhysicalUnitsAndInvalidDensityAreHandled(int unit, int density, double? expected)
    {
        Assert.Equal(expected, ImageResolution.Read(Jfif(unit, density)));
    }

    [Theory]
    [InlineData(true, 2, 300)]
    [InlineData(false, 2, 300)]
    [InlineData(true, 3, 762)]
    public void ExifDensityTakesPrecedenceOverJfif(bool little, int unit, double expected)
    {
        var jpeg = Jfif(1, 72);
        var exif = Exif(little, unit);
        var bytes = jpeg[..^2].Concat(exif).Concat(new byte[] { 255, 217 }).ToArray();
        Assert.Equal(expected, ImageResolution.Read(bytes));
    }

    [Fact]
    public void TruncatedAndOutOfBoundsMetadataCannotBreakImageImport()
    {
        var exif = Exif(true, 2);
        var complete = new byte[] { 255, 216 }.Concat(exif).Concat(new byte[] { 255, 217 }).ToArray();
        for (var length = 0; length < complete.Length; length++)
            _ = ImageResolution.Read(complete.AsSpan(0, length));
        // The TIFF IFD pointer and density value pointer must both stay inside the APP1 segment.
        complete.AsSpan(16, 4).Fill(255);
        Assert.Null(ImageResolution.Read(complete));
        var random = new byte[4096];
        new Random(17).NextBytes(random);
        for (var length = 0; length < random.Length; length++)
            Assert.Null(ImageResolution.Read(random.AsSpan(0, length)));
    }

    private static byte[] Jfif(int unit, int density)
    {
        byte[] bytes = [255, 216, 255, 224, 0, 16, 74, 70, 73, 70, 0, 1, 2,
            (byte)unit, 0, 0, 0, 0, 0, 0, 255, 217];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(14, 2), (ushort)density);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(16, 2), (ushort)density);
        return bytes;
    }

    private static byte[] Exif(bool little, int unit)
    {
        var tiff = new byte[46];
        tiff[0] = tiff[1] = little ? (byte)'I' : (byte)'M';
        Write16(2, 42);
        Write32(4, 8);
        Write16(8, 2);
        Write16(10, 0x11A);
        Write16(12, 5);
        Write32(14, 1);
        Write32(18, 38);
        Write16(22, 0x128);
        Write16(24, 3);
        Write32(26, 1);
        Write16(30, (ushort)unit);
        Write32(38, 300);
        Write32(42, 1);
        byte[] data = [.. "Exif\0\0"u8, .. tiff];
        byte[] header = [255, 225, 0, 0];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)(data.Length + 2));
        return [.. header, .. data];

        void Write16(int at, ushort value)
        {
            if (little) BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at), value);
            else BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(at), value);
        }
        void Write32(int at, uint value)
        {
            if (little) BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at), value);
            else BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(at), value);
        }
    }
}
