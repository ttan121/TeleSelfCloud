using System.IO;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace TeleSelfCloud.Desktop;

internal static class DocumentThumbnailRenderer
{
    public static BitmapSource? RenderPdf(byte[] bytes, CancellationToken token)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var random = input.AsRandomAccessStream();
        var document = PdfDocument.LoadFromStreamAsync(random).AsTask(token).GetAwaiter().GetResult();
        if (document.PageCount == 0) return null;
        using var page = document.GetPage(0);
        using var output = new InMemoryRandomAccessStream();
        var height = (uint)Math.Clamp(192 * page.Size.Height / Math.Max(1, page.Size.Width), 1, 1024);
        page.RenderToStreamAsync(output, new PdfPageRenderOptions { DestinationWidth = 192, DestinationHeight = height })
            .AsTask(token).GetAwaiter().GetResult();
        output.Seek(0);
        using var stream = output.AsStreamForRead();
        return ReadBitmap(stream);
    }

    public static BitmapSource? RenderWindowsThumbnail(byte[] bytes, string extension, CancellationToken token)
    {
        // Shell providers choose the decoder from the extension. Never expose .part bytes as a document.
        var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.Thumbnails", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "preview" + extension);
        try
        {
            File.WriteAllBytes(path, bytes);
            token.ThrowIfCancellationRequested();
            var file = StorageFile.GetFileFromPathAsync(path).AsTask(token).GetAwaiter().GetResult();
            using var thumbnail = file.GetThumbnailAsync(ThumbnailMode.SingleItem, 192, ThumbnailOptions.None)
                .AsTask(token).GetAwaiter().GetResult();
            if (thumbnail is null || thumbnail.Type != ThumbnailType.Image) return null;
            using var stream = thumbnail.AsStreamForRead();
            return ReadBitmap(stream);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static BitmapSource ReadBitmap(Stream stream)
    {
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 192; image.StreamSource = stream; image.EndInit(); image.Freeze();
        return image;
    }
}
