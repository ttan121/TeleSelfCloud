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

public sealed class LocalRestoreActionUiTests
{
    [Fact]
    public void LocalRestoreIsAvailableOnlyWhenAllPartsArePresentAndItemIsNotInTrash()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.LocalRestoreAction", Guid.NewGuid().ToString("N"));
            MainWindow? window = null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                Directory.CreateDirectory(root);
                UiText.Instance.SetLanguage("en", Path.Combine(root, "language.json"));
                var db = Path.Combine(root, "catalog.db");
                var manifests = new SqliteManifestStore(db);
                Task.Run(() => manifests.ListAsync(default)).GetAwaiter().GetResult();
                window = new MainWindow(new LocalFileWorkflow(new FileTransferCoordinator(), manifests, new StagedPartAssembler()),
                    Path.Combine(root, "staging"), manifests, new SqliteTransferQueueStore(db),
                    new SqliteRemoteSyncCheckpointStore(db), new SqliteLocalFolderStore(db), root);

                var staged = Path.Combine(root, "staged-part.bin");
                var bytes = new byte[] { 1, 3, 5, 7 };
                File.WriteAllBytes(staged, bytes);
                var manifest = new FileManifest(1, "offline", "offline.bin", bytes.Length,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), bytes.Length,
                    [new PartRecord(0, 0, bytes.Length, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), null, false, staged)], false);
                var itemType = typeof(MainWindow).GetNestedType("ManifestItem", flags)!;
                var item = Activator.CreateInstance(itemType, flags, binder: null, args: [manifest, null, null, false], culture: null)!;
                var list = Assert.IsType<ListView>(window.FindName("ManifestList"));
                list.ItemsSource = new[] { item };
                list.SelectedItem = item;
                typeof(MainWindow).GetMethod("ApplyActionAvailability", flags)!.Invoke(window, null);

                var restore = Assert.IsType<Button>(window.FindName("RestoreLocalButton"));
                Assert.Equal(UiText.Instance.Get("action.restoreLocal"), AutomationProperties.GetName(restore));
                Assert.Equal(UiText.Instance.Get("action.restoreLocalTip"), AutomationProperties.GetHelpText(restore));
                Assert.True(restore.IsEnabled);
                Assert.Equal(Visibility.Visible, restore.Visibility);
                Assert.IsType<Border>(window.FindName("FileDetailPanel")).Visibility = Visibility.Visible;
                var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                content.Measure(new Size(760, 650));
                content.Arrange(new Rect(0, 0, 760, 650));
                content.UpdateLayout();
                Assert.True(restore.ActualWidth > 0 && restore.ActualHeight >= 28);
                var bounds = restore.TransformToAncestor(content).TransformBounds(new Rect(0, 0, restore.ActualWidth, restore.ActualHeight));
                Assert.True(bounds.Left >= 0 && bounds.Right <= 760 && bounds.Top >= 0 && bounds.Bottom <= 650,
                    $"Local restore action is outside the minimum viewport: {bounds}.");

                var missing = manifest with
                {
                    FileId = "missing",
                    Parts = [manifest.Parts[0] with { StagingPath = Path.Combine(root, "missing.bin") }]
                };
                var missingItem = Activator.CreateInstance(itemType, flags, binder: null, args: [missing, null, null, false], culture: null)!;
                list.ItemsSource = new[] { missingItem };
                list.SelectedItem = missingItem;
                typeof(MainWindow).GetMethod("ApplyActionAvailability", flags)!.Invoke(window, null);
                Assert.False(restore.IsEnabled);
                Assert.Equal(Visibility.Collapsed, restore.Visibility);

                var trashed = manifest with { FileId = "trashed", IsInTrash = true };
                var trashedItem = Activator.CreateInstance(itemType, flags, binder: null, args: [trashed, null, null, false], culture: null)!;
                list.ItemsSource = new[] { trashedItem };
                list.SelectedItem = trashedItem;
                typeof(MainWindow).GetMethod("ApplyActionAvailability", flags)!.Invoke(window, null);
                Assert.False(restore.IsEnabled);
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                if (window is not null)
                {
                    typeof(MainWindow).GetField("_shutdownStarted", flags)!.SetValue(window, false);
                    window.WindowStyle = WindowStyle.None;
                    window.ShowInTaskbar = false;
                    window.ShowActivated = false;
                    window.Opacity = 0;
                    var frame = new DispatcherFrame();
                    window.Closed += (_, _) => frame.Continue = false;
                    window.Show();
                    window.Close();
                    if (frame.Continue) Dispatcher.PushFrame(frame);
                }
                dispatcher.InvokeShutdown(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Local restore UI fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
