using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Compositor.Core.IO;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Desktop;

/// <summary>A detached image returned by the system clipboard. The caller owns its pixels.</summary>
internal sealed class ClipboardBitmap(SKBitmap image, Guid? token) : IDisposable
{
    private SKBitmap? _image = image;
    internal SKBitmap Image => _image ?? throw new ObjectDisposedException(nameof(ClipboardBitmap));
    internal Guid? Token { get; } = token;
    internal SKBitmap Take()
    {
        var image = Image;
        _image = null;
        return image;
    }
    public void Dispose() => Interlocked.Exchange(ref _image, null)?.Dispose();
}

/// <summary>Injectable boundary; reads always observe system content, never an application cache.</summary>
internal interface ISystemImageClipboard
{
    Task PublishAsync(byte[] png, Guid token);
    Task<ClipboardBitmap?> ReadAsync(int remainingPixels);
}

/// <summary>
/// Avalonia 12 clipboard adapter. Win32 maps DataFormat.Bitmap to PNG/DIB representations, retaining alpha
/// where the receiving application supports it. Native interoperability still needs Windows verification.
/// </summary>
internal sealed class SystemImageClipboard(IClipboard? clipboard) : ISystemImageClipboard
{
    private static readonly DataFormat<byte[]> CopyToken =
        DataFormat.CreateBytesApplicationFormat("compositor-image-copy-v1");

    public async Task PublishAsync(byte[] png, Guid token)
    {
        var system = clipboard ?? throw new InvalidOperationException(L10n.T("The system clipboard is unavailable."));
        using var stream = new MemoryStream(png, writable: false);
        var transfer = new BitmapTransfer(new Bitmap(stream), token);
        var handedOver = false;
        try
        {
            await system.SetDataAsync(transfer);
            handedOver = true;
            // Keep the image available after Compositor closes, where the platform supports flushing.
            await system.FlushAsync();
        }
        finally
        {
            // Successfully published transfer objects belong to Avalonia, including their deferred bitmap.
            if (!handedOver) transfer.Dispose();
        }
    }

    public async Task<ClipboardBitmap?> ReadAsync(int remainingPixels)
    {
        var system = clipboard ?? throw new InvalidOperationException(L10n.T("The system clipboard is unavailable."));
        // The read transfer is a short-lived snapshot and must be disposed by its caller.
        using var transfer = await system.TryGetDataAsync();
        if (transfer is null || !transfer.Formats.Contains(DataFormat.Bitmap)) return null;
        using var bitmap = await transfer.TryGetBitmapAsync();
        if (bitmap is null) return null;
        CheckSize(bitmap.PixelSize.Width, bitmap.PixelSize.Height, remainingPixels);
        var marker = await transfer.TryGetValueAsync(CopyToken);
        var token = marker is { Length: 16 } ? new Guid(marker) : (Guid?)null;
        using var encoded = new MemoryStream();
        bitmap.Save(encoded, PngBitmapEncoderOptions.Default);
        if (encoded.Length > DocumentLimits.MaxAssetBytes) throw new ImportException(ImportError.TooLarge);
        return DecodePng(encoded.ToArray(), token, remainingPixels);
    }

    internal static ClipboardBitmap DecodePng(byte[] png, Guid? token, int remainingPixels)
    {
        if (png.LongLength > DocumentLimits.MaxAssetBytes) throw new ImportException(ImportError.TooLarge);
        using var encoded = new SKMemoryStream(png);
        using var codec = SKCodec.Create(encoded) ?? throw new ImportException(ImportError.Unreadable);
        CheckSize(codec.Info.Width, codec.Info.Height, remainingPixels);
        var image = SKBitmap.Decode(codec) ?? throw new ImportException(ImportError.Unreadable);
        return new ClipboardBitmap(image, token);
    }

    internal static byte[] EncodePng(SKBitmap bitmap)
    {
        CheckSize(bitmap.Width, bitmap.Height, DocumentLimits.DocumentPixelBudget);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new ImportException(ImportError.Unreadable);
        if (encoded.Size > DocumentLimits.MaxAssetBytes) throw new ImportException(ImportError.TooLarge);
        return encoded.ToArray();
    }

    internal static void CheckSize(int width, int height, int remainingPixels)
    {
        if (width < 1 || height < 1 || width > DocumentLimits.MaxSide || height > DocumentLimits.MaxSide
            || (long)width * height > DocumentLimits.MaxSurfacePixels || (long)width * height > remainingPixels)
            throw new ImportException(ImportError.TooLarge);
    }

    /// <summary>
    /// DataTransfer itself does not dispose its Bitmap values in Avalonia 12.1.3. This wrapper owns the
    /// detached bitmap until the clipboard releases it; both sync and async access are needed by Win32.
    /// </summary>
    private sealed class BitmapTransfer : IDataTransfer, IAsyncDataTransfer
    {
        private readonly DataTransfer _data = new();
        private Bitmap? _bitmap;
        internal BitmapTransfer(Bitmap bitmap, Guid token)
        {
            _bitmap = bitmap;
            var item = new DataTransferItem();
            item.SetBitmap(bitmap);
            item.Set(CopyToken, token.ToByteArray());
            _data.Add(item);
        }
        public IReadOnlyList<DataFormat> Formats => _data.Formats;
        IReadOnlyList<IDataTransferItem> IDataTransfer.Items => _data.Items;
        IReadOnlyList<IAsyncDataTransferItem> IAsyncDataTransfer.Items => _data.Items;
        public void Dispose() => Interlocked.Exchange(ref _bitmap, null)?.Dispose();
    }
}
