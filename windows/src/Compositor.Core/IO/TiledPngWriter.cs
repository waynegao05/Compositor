using System.IO.Compression;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.IO;

/// <summary>
/// Writes a document as an 8-bit RGBA PNG without ever holding the whole canvas. The canvas is rendered a
/// band of tiles at a time and the rows are streamed out as they are ready; the compressed bytes become
/// IDAT chunks as they arrive, so neither the picture nor its compression builds up in memory.
/// </summary>
public static class TiledPngWriter
{
    /// <summary>Wide enough that most documents are one band, small enough to stay well under a buffer.</summary>
    public const int DefaultTileSize = 1024;

    public static void Write(CanvasDocument document, string path, int tileSize = DefaultTileSize)
    {
        AtomicImageWrite.Write(path, output => Write(document, output, tileSize));
    }

    public static void Write(CanvasDocument document, Stream output, int tileSize = DefaultTileSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tileSize, 1);
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> header = stackalloc byte[13];
        WriteBigEndian(header[..4], document.Width);
        WriteBigEndian(header.Slice(4, 4), document.Height);
        header[8] = 8;  // bits per channel
        header[9] = 6;  // red, green, blue and alpha
        header[10] = 0; // deflate
        header[11] = 0; // the adaptive filter set
        header[12] = 0; // not interlaced
        WriteChunk(output, "IHDR", header);
        var resolution = double.IsFinite(document.Resolution) && document.Resolution > 0
            ? document.Resolution : 72;
        var pixelsPerMeter = (int)Math.Clamp(Math.Round(resolution / 0.0254), 1, int.MaxValue);
        Span<byte> density = stackalloc byte[9];
        WriteBigEndian(density[..4], pixelsPerMeter);
        WriteBigEndian(density.Slice(4, 4), pixelsPerMeter);
        density[8] = 1; // meters
        WriteChunk(output, "pHYs", density);

        using (var chunks = new ChunkStream(output))
        using (var deflate = new ZLibStream(chunks, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[document.Width * 4];
            for (var top = 0; top < document.Height; top += tileSize)
            {
                var band = Math.Min(tileSize, document.Height - top);
                var tiles = new List<SKBitmap>();
                try
                {
                    for (var left = 0; left < document.Width; left += tileSize)
                    {
                        var width = Math.Min(tileSize, document.Width - left);
                        tiles.Add(DocumentRenderer.RenderRegion(document, SKRectI.Create(left, top, width, band)));
                    }
                    for (var y = 0; y < band; y++)
                    {
                        var at = 0;
                        foreach (var tile in tiles)
                        {
                            var pixels = tile.GetPixelSpan();
                            var start = y * tile.Width * 4;
                            for (var x = 0; x < tile.Width; x++, at += 4)
                            {
                                var p = start + x * 4;
                                var alpha = pixels[p + 3];
                                if (alpha == 0)
                                {
                                    row[at] = row[at + 1] = row[at + 2] = row[at + 3] = 0;
                                    continue;
                                }
                                // The compositor works premultiplied; PNG stores straight alpha.
                                for (var channel = 0; channel < 3; channel++)
                                {
                                    var value = (pixels[p + channel] * 255 + alpha / 2) / alpha;
                                    row[at + channel] = (byte)(value > 255 ? 255 : value);
                                }
                                row[at + 3] = alpha;
                            }
                        }
                        deflate.WriteByte(0); // no filter
                        deflate.Write(row);
                    }
                }
                finally
                {
                    foreach (var tile in tiles) tile.Dispose();
                }
            }
        }
        WriteChunk(output, "IEND", []);
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[4];
        WriteBigEndian(header, data.Length);
        output.Write(header);
        Span<byte> name = stackalloc byte[4];
        for (var index = 0; index < 4; index++) name[index] = (byte)type[index];
        output.Write(name);
        output.Write(data);
        WriteBigEndian(header, (int)Crc32(name, data));
        output.Write(header);
    }

    private static void WriteBigEndian(Span<byte> destination, int value)
    {
        destination[0] = (byte)(value >> 24);
        destination[1] = (byte)(value >> 16);
        destination[2] = (byte)(value >> 8);
        destination[3] = (byte)value;
    }

    private static uint Crc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in first) crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        foreach (var value in second) crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint entry = 0; entry < 256; entry++)
        {
            var value = entry;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
            table[entry] = value;
        }
        return table;
    }

    /// <summary>Collects compressed bytes and emits them as IDAT chunks of a sane size.</summary>
    private sealed class ChunkStream : Stream
    {
        private const int ChunkBytes = 64 * 1024;
        private readonly Stream _output;
        private readonly byte[] _buffer = new byte[ChunkBytes];
        private int _count;

        public ChunkStream(Stream output) => _output = output;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                var take = Math.Min(ChunkBytes - _count, buffer.Length);
                buffer[..take].CopyTo(_buffer.AsSpan(_count));
                _count += take;
                buffer = buffer[take..];
                if (_count == ChunkBytes) Emit();
            }
        }

        public override void WriteByte(byte value)
        {
            _buffer[_count++] = value;
            if (_count == ChunkBytes) Emit();
        }

        public override void Flush()
        {
            Emit();
            _output.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Emit();
                // leftOpen: the caller owns the file they handed in.
            }
            base.Dispose(disposing);
        }

        private void Emit()
        {
            if (_count == 0) return;
            WriteChunk(_output, "IDAT", _buffer.AsSpan(0, _count));
            _count = 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
