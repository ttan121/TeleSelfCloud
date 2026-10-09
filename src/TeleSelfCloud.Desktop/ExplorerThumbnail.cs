using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Desktop;

/// <summary>Viewport-owned thumbnails; no Telegram access or full-file assembly.</summary>
public sealed class ExplorerThumbnail : Image
{
    public static readonly DependencyProperty ManifestProperty = DependencyProperty.Register(
        nameof(Manifest), typeof(FileManifest), typeof(ExplorerThumbnail), new PropertyMetadata(null, OnManifestChanged));
    private static readonly SemaphoreSlim DecodeSlots = new(2);
    private CancellationTokenSource? _request;
    public FileManifest? Manifest { get => (FileManifest?)GetValue(ManifestProperty); set => SetValue(ManifestProperty, value); }

    public ExplorerThumbnail()
    {
        Loaded += (_, _) => Reload();
        Unloaded += (_, _) => Stop();
        IsHitTestVisible = false;
    }

    private static void OnManifestChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((ExplorerThumbnail)sender).Reload();
    private void Stop()
    {
        _request?.Cancel();
        _request = null;
        Source = null;
    }

    private async void Reload()
    {
        Stop();
        if (!IsLoaded || Manifest is not { } manifest || !CanDecode(manifest)) return;
        SetCurrentValue(StretchProperty, Path.GetExtension(manifest.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            ? System.Windows.Media.Stretch.Uniform : System.Windows.Media.Stretch.UniformToFill);
        var request = _request = new CancellationTokenSource();
        var token = request.Token;
        try
        {
            await DecodeSlots.WaitAsync(token);
            BitmapSource? thumbnail;
            try { thumbnail = await Task.Run(() => Decode(manifest, token), token); }
            finally { DecodeSlots.Release(); }
            if (!token.IsCancellationRequested && ReferenceEquals(_request, request)) Source = thumbnail;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException or FormatException)
        {
            // Unavailable/corrupt/protected local bytes keep the underlying file icon.
        }
        finally
        {
            if (ReferenceEquals(_request, request)) _request = null;
            request.Dispose();
        }
    }

    private static bool CanDecode(FileManifest manifest) => manifest.Encryption is null && !manifest.IsInTrash &&
        manifest.Parts.Count > 0 && manifest.LogicalSize is > 0 and <= 32 * 1024 * 1024 &&
        manifest.Parts.All(part => !string.IsNullOrEmpty(part.StagingPath)) &&
        !string.IsNullOrEmpty(Path.GetExtension(manifest.FileName));

    private static BitmapSource? Decode(FileManifest manifest, CancellationToken token)
    {
        // A missing/stale staging path is common for cloud-only files. Check it before
        // allocating a payload buffer so scrolling cannot allocate 32 MiB per missing tile.
        foreach (var part in manifest.Parts)
        {
            token.ThrowIfCancellationRequested();
            if (new FileInfo(part.StagingPath!).Length != part.Length) return null;
        }
        var bytes = new byte[(int)manifest.LogicalSize];
        var offset = 0;
        foreach (var part in manifest.Parts)
        {
            token.ThrowIfCancellationRequested();
            if (part.Offset != offset || part.Length < 0 || part.Length > bytes.Length - offset) return null;
            using var file = new FileStream(part.StagingPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            if (file.Length != part.Length) return null;
            var slice = bytes.AsSpan(offset, (int)part.Length);
            file.ReadExactly(slice);
            if (!Convert.ToHexString(SHA256.HashData(slice)).Equals(part.Sha256, StringComparison.OrdinalIgnoreCase)) return null;
            offset += (int)part.Length;
        }
        if (offset != bytes.Length) return null;
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!hash.Equals(manifest.TotalSha256, StringComparison.OrdinalIgnoreCase)) return null;
        var extension = Path.GetExtension(manifest.FileName).ToLowerInvariant();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        if (extension == ".pdf") return DocumentThumbnailRenderer.RenderPdf(bytes, timeout.Token);
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".tif" or ".tiff"))
            return DocumentThumbnailRenderer.RenderWindowsThumbnail(bytes, extension, timeout.Token);
        using var stream = new MemoryStream(bytes, writable: false);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelWidth > 32000 || frame.PixelHeight > 32000 ||
            frame.PixelHeight > frame.PixelWidth * 10L) return null;
        stream.Position = 0;
        token.ThrowIfCancellationRequested();
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = Math.Min(192, frame.PixelWidth);
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        token.ThrowIfCancellationRequested();
        return image;
    }
}
