using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class UploadDragDropUiTests
{
    [Fact]
    public void FilesPageAdvertisesDropAndRejectsDropsOnTrashOrBusyStates()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.UploadDropUi", Guid.NewGuid().ToString("N"));
            MainWindow? window = null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                Directory.CreateDirectory(root);
                UiText.Instance.SetLanguage("en", Path.Combine(root, "language.json"));
                var database = Path.Combine(root, "catalog.db");
                var manifests = new SqliteManifestStore(database);
                Task.Run(() => manifests.ListAsync(default)).GetAwaiter().GetResult();
                window = new MainWindow(new LocalFileWorkflow(new FileTransferCoordinator(), manifests, new StagedPartAssembler()),
                    Path.Combine(root, "staging"), manifests, new SqliteTransferQueueStore(database),
                    new SqliteRemoteSyncCheckpointStore(database), new SqliteLocalFolderStore(database), root);

                var page = Assert.IsType<Grid>(window.FindName("FilesPage"));
                var overlay = Assert.IsType<Border>(window.FindName("FilesDropOverlay"));
                Assert.True(page.AllowDrop);
                Assert.Equal(UiText.Instance.Get("files.dropHint"), AutomationProperties.GetHelpText(page));
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
                var uploadFiles = Assert.IsType<Button>(window.FindName("UploadButton"));
                Assert.Equal(UiText.Instance.Get("upload.choose"), AutomationProperties.GetName(uploadFiles));
                var uploadMenu = Assert.IsType<ContextMenu>(uploadFiles.ContextMenu);
                Assert.Equal(2, uploadMenu.Items.Count);
                Assert.Equal(UiText.Instance.Get("upload.chooseFiles"), AutomationProperties.GetName(Assert.IsType<MenuItem>(uploadMenu.Items[0])));
                Assert.Equal(UiText.Instance.Get("upload.chooseFolder"), AutomationProperties.GetName(Assert.IsType<MenuItem>(uploadMenu.Items[1])));

                var payloadFile = Path.Combine(root, "payload.bin"); File.WriteAllText(payloadFile, "payload");
                var data = new DataObject(DataFormats.FileDrop, new[] { payloadFile });
                var canAccept = typeof(MainWindow).GetMethod("CanAcceptUploadDrop", flags)!;
                Assert.True((bool)canAccept.Invoke(window, [data])!);

                typeof(MainWindow).GetField("_isTrashPage", flags)!.SetValue(window, true);
                Assert.False((bool)canAccept.Invoke(window, [data])!);
                typeof(MainWindow).GetField("_isTrashPage", flags)!.SetValue(window, false);
                typeof(MainWindow).GetField("_operationBusy", flags)!.SetValue(window, true);
                Assert.False((bool)canAccept.Invoke(window, [data])!);
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                if (window is not null)
                {
                    // The window has no active session here; bypass async shutdown so test teardown never closes it twice.
                    typeof(MainWindow).GetField("_allowClose", flags)!.SetValue(window, true);
                    var shutdownFrame = new DispatcherFrame();
                    window.Closed += (_, _) => shutdownFrame.Continue = false;
                    window.Close();
                    if (shutdownFrame.Continue) Dispatcher.PushFrame(shutdownFrame);
                }
                dispatcher.InvokeShutdown();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
