using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Collections.Concurrent;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class QueueItemControlsUiTests
{
    [Fact]
    public void RealQueueTemplateShowsAccessiblePauseAndCancelActionsOnlyForAvailableStates()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.QueueControls", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            MainWindow? window = null;
            try
            {
                var database = Path.Combine(root, "manifest.db");
                var manifests = new SqliteManifestStore(database);
                window = new MainWindow(new LocalFileWorkflow(new FileTransferCoordinator(), manifests, new StagedPartAssembler()),
                    Path.Combine(root, "staging"), manifests, new SqliteTransferQueueStore(database),
                    new SqliteRemoteSyncCheckpointStore(database), new SqliteLocalFolderStore(database), root);
                var list = Assert.IsType<ListView>(window.FindName("TransferList"));
                Assert.NotNull(list.ItemTemplate);
                var viewType = typeof(MainWindow).GetNestedType("QueueItemView", BindingFlags.NonPublic)!;
                var constructor = viewType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null, [typeof(TransferQueueItem)], modifiers: null)!;
                var pauseName = UiText.Instance.Get("queue.pauseItem");
                var cancelName = UiText.Instance.Get("queue.cancelItem");
                Assert.False(string.IsNullOrWhiteSpace(pauseName));
                Assert.False(string.IsNullOrWhiteSpace(cancelName));

                foreach (var state in new[] { TransferQueueState.Pending, TransferQueueState.Running, TransferQueueState.Paused,
                             TransferQueueState.Failed, TransferQueueState.Completed, TransferQueueState.Cancelled })
                {
                    var now = DateTimeOffset.UtcNow;
                    var item = new TransferQueueItem("task-" + state, "file-" + state, "song.mp3", TransferDirection.Upload,
                        null, state, 0, 0, 100, now, now, null);
                    var view = constructor.Invoke([item]);
                    var row = Assert.IsAssignableFrom<FrameworkElement>(list.ItemTemplate.LoadContent());
                    row.DataContext = view;
                    row.Width = 720;
                    row.Measure(new Size(760, 650));
                    row.Arrange(new Rect(0, 0, 760, row.DesiredSize.Height));
                    row.UpdateLayout();
                    var buttons = Descendants(row).OfType<Button>().ToArray();
                    var pause = Assert.Single(buttons, button => AutomationProperties.GetName(button) == pauseName);
                    var cancel = Assert.Single(buttons, button => AutomationProperties.GetName(button) == cancelName);
                    Assert.Equal(state == TransferQueueState.Running ? Visibility.Visible : Visibility.Collapsed, pause.Visibility);
                    var canCancel = state is TransferQueueState.Pending or TransferQueueState.Running or TransferQueueState.Paused or TransferQueueState.Failed;
                    Assert.Equal(canCancel ? Visibility.Visible : Visibility.Collapsed, cancel.Visibility);
                    foreach (var button in buttons.Where(button => button.Visibility == Visibility.Visible))
                    {
                        var point = button.TransformToAncestor(row).Transform(new Point());
                        Assert.True(point.X >= 0 && point.X + button.ActualWidth <= row.ActualWidth + 1);
                        Assert.True(point.Y >= 0 && point.Y + button.ActualHeight <= row.ActualHeight + 1);
                    }
                    if (state == TransferQueueState.Running)
                    {
                        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                        var active = (ConcurrentDictionary<string, CancellationTokenSource>)typeof(MainWindow)
                            .GetField("_activeQueueItemCancellation", flags)!.GetValue(window)!;
                        var cancelRequests = (ConcurrentDictionary<string, byte>)typeof(MainWindow)
                            .GetField("_cancelQueueItemOnStop", flags)!.GetValue(window)!;
                        using var pauseCancellation = new CancellationTokenSource();
                        active[item.TaskId] = pauseCancellation;
                        pause.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, pause));
                        Assert.True(pauseCancellation.IsCancellationRequested);
                        active.TryRemove(item.TaskId, out _);

                        using var cancelCancellation = new CancellationTokenSource();
                        active[item.TaskId] = cancelCancellation;
                        cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, cancel));
                        Assert.True(cancelCancellation.IsCancellationRequested);
                        Assert.True(cancelRequests.TryRemove(item.TaskId, out _));
                        active.TryRemove(item.TaskId, out _);
                    }
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (window is not null)
                {
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    window.Closed += (_, _) => frame.Continue = false;
                    window.Close();
                    if (frame.Continue) System.Windows.Threading.Dispatcher.PushFrame(frame);
                }
                dispatcher.InvokeShutdown();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Queue item UI fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(System.Windows.Media.VisualTreeHelper.GetChild(root, index)))
                yield return child;
    }
}
