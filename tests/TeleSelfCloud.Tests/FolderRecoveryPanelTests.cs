using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class FolderRecoveryPanelTests
{
    [Theory]
    [InlineData("en", 1000, 760)]
    [InlineData("vi", 760, 650)]
    public void RealFolderPanelShowsActualStatesAndCannotRequestMovesAfterStop(string language, int width, int height)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.FolderPanel", Guid.NewGuid().ToString("N"));
            MainWindow? window = null;
            FolderRecoveryPanel? panel = null;
            try
            {
                Directory.CreateDirectory(root);
                UiText.Instance.SetLanguage(language, Path.Combine(root, "language.json"));
                var db = Path.Combine(root, "manifest.db");
                var manifests = new SqliteManifestStore(db);
                window = new MainWindow(new LocalFileWorkflow(new FileTransferCoordinator(), manifests, new StagedPartAssembler()),
                    Path.Combine(root, "staging"), manifests, new SqliteTransferQueueStore(db), new SqliteRemoteSyncCheckpointStore(db),
                    new SqliteLocalFolderStore(db), root);
                var reviewButton = Assert.IsType<Button>(window.FindName("ReviewFolderChangeButton"));
                Assert.False(reviewButton.IsEnabled);
                Assert.Equal(Visibility.Collapsed, reviewButton.Visibility);
                panel = Assert.IsType<FolderRecoveryPanel>(window.FindName("FolderRecoveryView"));
                panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                var before = new FileManifest(1, "a", "Báo cáo tổng hợp.bin", 1, new string('A', 64), 1,
                    [new(0, 0, 1, new string('A', 64), "-100/123", true)], true, "account-a", "Old/Nested");
                var after = FileManifestMetadata.Move(before, "New/Nested");
                var beforeB = before with { FileId = "b", FileName = "Tệp có thay đổi từ thiết bị khác.bin" };
                var plan = new PendingFolderOperation(1, Guid.NewGuid().ToString("N"), "account-a", "Old", "New", false,
                    ["Old", "Old/Nested"], [new(before, after), new(beforeB, FileManifestMetadata.Move(beforeB, "New/Nested"))], 1, false, DateTimeOffset.UtcNow);
                var review = new FolderOperationReview(plan,
                    [new("a", before.FileName, FolderRecoveryFileState.After, "New/Nested", 1),
                     new("b", beforeB.FileName, FolderRecoveryFileState.Changed, "Thư mục khác/Đã sửa", 5)], 1);
                panel.ShowReview(review);
                panel.SetCanAct(true);
                Assert.Equal(2, panel.RecoveryFiles.Items.Count);
                Assert.True(panel.KeepStateButton.IsEnabled);
                Assert.Contains(UiText.Instance.Get("folder.review.state.Changed"), panel.RecoveryFiles.Items.Cast<FolderRecoveryPanel.RecoveryRow>().Last().Summary);
                var stops = 0;
                panel.KeepStateRequested += (_, _) => stops++;
                panel.KeepStateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(1, stops);
                panel.SetCanAct(false);
                panel.KeepStateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(1, stops);
                Assert.Equal(UiText.Instance.Get("folder.stop.action"), AutomationProperties.GetName(panel.KeepStateButton));
                Assert.IsType<Border>(window.FindName("FolderRecoveryHost")).Visibility = Visibility.Visible;
                reviewButton.Visibility = Visibility.Visible;
                Assert.IsType<Button>(window.FindName("ResumeFolderChangeButton")).Visibility = Visibility.Visible;
                var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                content.Measure(new Size(width, height));
                content.Arrange(new Rect(0, 0, width, height));
                content.UpdateLayout();
                Assert.True(panel.ActualHeight <= 280);
                var point = panel.TransformToAncestor(content).Transform(new Point());
                Assert.True(point.Y + panel.ActualHeight <= height);
                var topActions = Assert.IsType<WrapPanel>(window.FindName("TopActions"));
                Assert.True(topActions.TransformToAncestor(content).Transform(new Point()).X + topActions.ActualWidth <= width,
                    "Recovery actions extend past the minimum window.");
                var capture = Environment.GetEnvironmentVariable("TSC_FOLDER_CAPTURE_DIRECTORY");
                if (!string.IsNullOrEmpty(capture))
                {
                    Directory.CreateDirectory(capture);
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(capture, $"folder-review-{language}-{width}x{height}.png"));
                    encoder.Save(output);
                }
                panel.ShowReview(review with { Operation = plan with { StopRequestedAtUtc = DateTimeOffset.UtcNow } });
                panel.SetCanAct(true);
                Assert.False(panel.KeepStateButton.IsEnabled);
                Assert.True(panel.RetryPlanButton.IsEnabled);
                Assert.Equal(UiText.Instance.Get("folder.stop.finish"), panel.RetryPlanButton.Content);
                Assert.Contains(UiText.Instance.Get("folder.stop.pending"), panel.PlanSummary.Text);
                panel.KeepStateButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(1, stops);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                panel?.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Folder panel fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
