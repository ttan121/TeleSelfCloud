using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class DesktopStartupTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MainWindowLoadsRealXamlAndWaitsForActiveOperationWhenClosing(bool canCancel)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.StartupTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            MainWindow? window = null;
            try
            {
                var database = Path.Combine(root, "manifests.db");
                var manifests = new SqliteManifestStore(database);
                var workflow = new LocalFileWorkflow(new FileTransferCoordinator(), manifests, new StagedPartAssembler());
                window = new MainWindow(workflow, Path.Combine(root, "staging"), manifests,
                    new SqliteTransferQueueStore(database), new SqliteRemoteSyncCheckpointStore(database),
                    new SqliteLocalFolderStore(database), root);

                // Construct and lay out the real WPF tree without firing Loaded or restoring Telegram sign-in.
                window.Measure(new Size(1000, 760));
                window.Arrange(new Rect(0, 0, 1000, 760));
                Assert.NotNull(window.FindName("ManifestList"));
                Assert.NotNull(window.FindName("RelinkUploadSourceButton"));
                Assert.False(Assert.IsType<Button>(window.FindName("ClearLocalCacheButton")).IsEnabled);
                var resumeFolder = Assert.IsType<Button>(window.FindName("ResumeFolderChangeButton"));
                Assert.False(resumeFolder.IsEnabled);
                Assert.Equal(Visibility.Collapsed, resumeFolder.Visibility);

                UiText.Instance.SetLanguage("vi", Path.Combine(root, "ui-language.json"));
                var confirmation = string.Format(UiText.Instance.Get("dialog.clearLocalCache.confirm"), "movie.mp4");
                Assert.Contains("movie.mp4", confirmation);
                var transfers = Assert.IsType<RadioButton>(window.FindName("NavTransfers"));
                transfers.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(UiText.Instance.Get("nav.transfers"), Assert.IsType<TextBlock>(window.FindName("PageTitle")).Text);
                Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(window.FindName("TransfersPage")).Visibility);

                var setBusy = typeof(MainWindow).GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!;
                setBusy.Invoke(window, [true, "Fixture transfer in progress", canCancel, "status.cancel"]);
                var operationToken = (CancellationToken)typeof(MainWindow).GetProperty("OperationToken", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var closed = false;
                window.Closed += (_, _) => closed = true;
                window.Close();
                Assert.False(closed);
                Assert.Equal(canCancel, operationToken.IsCancellationRequested);
                Assert.False(Assert.IsType<Button>(window.FindName("UploadButton")).IsEnabled);
                // The controller must wait for the transfer's durable checkpoint/finally to finish.
                setBusy.Invoke(window, [false, null, true, "status.cancel"]);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (window is not null)
                {
                    var shutdownFrame = new System.Windows.Threading.DispatcherFrame();
                    window.Closed += (_, _) => shutdownFrame.Continue = false;
                    window.Close();
                    if (shutdownFrame.Continue) System.Windows.Threading.Dispatcher.PushFrame(shutdownFrame);
                }
                dispatcher.InvokeShutdown();
                Directory.Delete(root, recursive: true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF startup probe timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
