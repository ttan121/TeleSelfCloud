using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class MediaPreviewUiTests
{
    [Theory]
    [InlineData("vi", 760, 650)]
    [InlineData("en", 1100, 800)]
    public void StreamingPreviewProvidesAccessibleDefaultPlayerHandoff(string language, int width, int height)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.MediaPreviewUi", Guid.NewGuid().ToString("N"));
            FilePreviewWindow? window = null;
            MediaStreamServer? server = null;
            try
            {
                Directory.CreateDirectory(root);
                UiText.Instance.SetLanguage(language, Path.Combine(root, "language.json"));
                var workflow = new EmptyWorkflow();
                var source = new EmptyByteSource();
                server = new MediaStreamServer(workflow, Path.Combine(root, "stream"));
                var url = server.StartServer("fixture.mp4", source);
                window = FilePreviewWindow.CreateStreaming("fixture.mp4", url, server, darkMode: true);
                var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                content.Measure(new Size(width, height));
                content.Arrange(new Rect(0, 0, width, height));
                content.UpdateLayout();
                var launch = FindButton(content);
                Assert.True(launch.IsEnabled);
                Assert.Equal(UiText.Instance.Get("preview.media.open"), AutomationProperties.GetName(launch));
                var buttonBounds = launch.TransformToAncestor(content).TransformBounds(new Rect(0, 0, launch.ActualWidth, launch.ActualHeight));
                Assert.True(buttonBounds.Bottom <= height - 16, $"The player action is clipped by the preview edge: {buttonBounds}.");
                Assert.Contains(language == "vi" ? "Đóng cửa sổ này" : "Close this window", UiText.Instance.Get("preview.media.help"));

                var capture = Environment.GetEnvironmentVariable("TSC_MEDIA_PREVIEW_CAPTURE");
                if (!string.IsNullOrWhiteSpace(capture))
                {
                    Directory.CreateDirectory(capture);
                    var drawing = new DrawingVisual();
                    using (var context = drawing.RenderOpen()) context.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, width, height));
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(drawing);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(capture, $"media-preview-{language}.png"));
                    encoder.Save(output);
                }
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                if (window is not null) window.Close();
                server?.Dispose();
                dispatcher.InvokeShutdown();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Media preview UI fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static Button FindButton(DependencyObject root)
    {
        if (root is Button button) return button;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            try { return FindButton(VisualTreeHelper.GetChild(root, index)); }
            catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException("The streaming preview has no player action.");
    }

    private sealed class EmptyWorkflow : ILocalFileWorkflow
    {
        public Task<FileManifest> PrepareAsync(string sourcePath, string stagingRoot, long partSizeBytes, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<FileManifest>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RestoreAsync(string fileId, string destinationPath, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class EmptyByteSource : IMediaByteSource
    {
        public Task<long> GetSizeAsync(CancellationToken cancellationToken) => Task.FromResult(0L);
        public Task<byte[]> ReadAsync(long offset, int count, CancellationToken cancellationToken) => Task.FromResult(Array.Empty<byte>());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
