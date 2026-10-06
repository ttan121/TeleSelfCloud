using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class CatalogSyncUiTests
{
    [Theory]
    [InlineData("vi", 760, 650)]
    [InlineData("en", 1000, 760)]
    public void RealSyncActionsFitAndDescribeDurablePhases(string language, int width, int height)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.SyncUi", Guid.NewGuid().ToString("N"));
            MainWindow? window = null;
            try
            {
                Directory.CreateDirectory(root);
                UiText.Instance.SetLanguage(language, Path.Combine(root, "language.json"));
                var db = Path.Combine(root, "index.db");
                var manifests = new SqliteManifestStore(db);
                window = new MainWindow(new LocalFileWorkflow(new FileTransferCoordinator(), manifests, new StagedPartAssembler()),
                    Path.Combine(root, "staging"), manifests, new SqliteTransferQueueStore(db), new SqliteRemoteSyncCheckpointStore(db),
                    new SqliteLocalFolderStore(db), root);
                var actions = Assert.IsType<StackPanel>(window.FindName("CatalogSyncActions"));
                Assert.Equal(Visibility.Collapsed, actions.Visibility);
                foreach (var name in new[] { "CatalogSyncButton", "FullCatalogSyncButton", "RetryCatalogSyncButton" })
                    Assert.False(Assert.IsType<Button>(window.FindName(name)).IsEnabled);
                var text = Assert.IsAssignableFrom<TextBlock>(window.FindName("CatalogSyncReport"));
                var describe = typeof(MainWindow).GetMethod("DescribeSyncAttempt", BindingFlags.Static | BindingFlags.NonPublic)!;
                actions.Visibility = Visibility.Visible;
                var retry = Assert.IsType<Button>(window.FindName("RetryCatalogSyncButton"));
                retry.Visibility = Visibility.Visible;
                foreach (var outcome in Enum.GetValues<CatalogSyncOutcome>())
                {
                    var report = new CatalogSyncAttempt(1, "test", -100, true, false, outcome, DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow, 7, 4321, 9, 8, outcome is CatalogSyncOutcome.Completed or CatalogSyncOutcome.FolderPublicationPending ? DateTimeOffset.UtcNow : null, []);
                    var description = Assert.IsType<string>(describe.Invoke(null, [report]));
                    Assert.Contains("7", description);
                    Assert.Contains("4321", description);
                    Assert.Contains("8", description);
                    Assert.DoesNotContain("sync.report", description);
                    if (outcome == CatalogSyncOutcome.FolderPublicationPending)
                        Assert.Contains(language == "en" ? "without rescanning" : "không quét lại", description);
                    text.Text = description;
                    var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                    content.Measure(new Size(width, height));
                    content.Arrange(new Rect(0, 0, width, height));
                    content.UpdateLayout();
                    foreach (var name in new[] { "CatalogSyncButton", "FullCatalogSyncButton", "RetryCatalogSyncButton" })
                    {
                        var button = Assert.IsType<Button>(window.FindName(name));
                        var point = button.TransformToAncestor(content).Transform(new Point());
                        Assert.True(point.X >= 0 && point.X + button.ActualWidth <= width);
                        Assert.True(point.Y >= 0 && point.Y + button.ActualHeight <= height);
                    }
                    var capture = Environment.GetEnvironmentVariable("TSC_SYNC_CAPTURE_DIRECTORY");
                    if (!string.IsNullOrEmpty(capture))
                    {
                        Directory.CreateDirectory(capture);
                        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(content);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var stream = File.Create(Path.Combine(capture, $"sync-{language}-{outcome}.png"));
                        encoder.Save(stream);
                    }
                }
                typeof(MainWindow).GetMethod("ApplySyncActionAvailability", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                Assert.Equal(Visibility.Collapsed, actions.Visibility);
                Assert.False(retry.IsEnabled);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (window is not null)
                {
                    var frame = new DispatcherFrame();
                    window.Closed += (_, _) => frame.Continue = false;
                    window.Close();
                    if (frame.Continue) Dispatcher.PushFrame(frame);
                }
                dispatcher.InvokeShutdown();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Sync UI fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
