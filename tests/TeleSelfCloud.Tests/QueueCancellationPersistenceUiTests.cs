using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class QueueCancellationPersistenceUiTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    [Theory]
    [InlineData(TransferQueueState.Pending)]
    [InlineData(TransferQueueState.Paused)]
    [InlineData(TransferQueueState.Failed)]
    public void CancelClearRefreshAndReopenDoNotRecreateUpload(TransferQueueState initialState)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.CancelPersistence", Guid.NewGuid().ToString("N"));
            MainWindow? window = null;
            try
            {
                Directory.CreateDirectory(root);
                UiText.Instance.SetLanguage("vi", Path.Combine(root, "ui-language.json"));
                var db = Path.Combine(root, "catalog.db");
                var manifests = new SqliteManifestStore(db);
                var queue = new SqliteTransferQueueStore(db);
                var part = Path.Combine(root, "kept-part.bin");
                File.WriteAllBytes(part, [42]);
                var hash = Convert.ToHexString(SHA256.HashData(new byte[] { 42 }));
                var draft = new FileManifest(1, "cancel-target", "cancel.txt", 1, hash, 1,
                    [new PartRecord(0, 0, 1, hash, null, false, part)], false, "42");
                Pump(manifests.SaveAsync(draft, default));
                Pump(manifests.SaveAsync(draft with { FileId = "other-upload", FileName = "other.txt" }, default));
                Pump(queue.EnsureAsync(draft.FileId, draft.FileName, 1, default));
                Pump(queue.EnsureAsync("other-upload", "other.txt", 1, default));
                if (initialState != TransferQueueState.Pending)
                {
                    Pump(queue.SetStateAsync(draft.FileId, TransferQueueState.Running, null, default));
                    Pump(queue.SetStateAsync(draft.FileId, initialState, null, default));
                }
                MainWindow CreateWindow()
                {
                    var result = new MainWindow(new LocalFileWorkflow(new FileTransferCoordinator(), manifests, new StagedPartAssembler()),
                        Path.Combine(root, "staging"), manifests, queue, new SqliteRemoteSyncCheckpointStore(db), new SqliteLocalFolderStore(db), root);
                    typeof(MainWindow).GetField("_activeTelegramAccountId", Private)!.SetValue(result, "42");
                    return result;
                }
                window = CreateWindow();
                Refresh(window);
                var list = (ListView)window.FindName("TransferList");
                var view = list.Items.Cast<object>().Single(item =>
                    ((TransferQueueItem)item.GetType().GetProperty("Item")!.GetValue(item)!).FileId == draft.FileId);
                Invoke(window, "CancelQueueItem_Click", new Button { DataContext = view }, new RoutedEventArgs());
                Pump((Task)Invoke(window, "RefreshQueueAsync")!);
                Assert.Equal(TransferQueueState.Cancelled, Items(queue).Single(item => item.FileId == draft.FileId).State);
                Refresh(window);
                Assert.Equal(TransferQueueState.Cancelled, Items(queue).Single(item => item.FileId == draft.FileId).State);
                Invoke(window, "ClearDoneQueue_Click", window.FindName("QueueClearDoneButton"), new RoutedEventArgs());
                Pump((Task)Invoke(window, "RefreshQueueAsync")!);
                Assert.DoesNotContain(Items(queue), item => item.FileId == draft.FileId);
                Refresh(window);
                Assert.DoesNotContain(Items(queue), item => item.FileId == draft.FileId);
                Assert.Equal(TransferQueueState.Pending, Items(queue).Single(item => item.FileId == "other-upload").State);
                Assert.NotNull(Pump(manifests.LoadAsync(draft.FileId, default)));
                Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(part));
                Close(window); window = null;
                manifests = new SqliteManifestStore(db);
                queue = new SqliteTransferQueueStore(db);
                window = CreateWindow();
                Refresh(window);
                foreach (var page in new[] { "files", "transfers", "settings", "transfers" })
                    Invoke(window, "NavigateToPage", page);
                Refresh(window);
                Assert.DoesNotContain(Items(queue), item => item.FileId == draft.FileId);
                Assert.Single(Items(queue));
                // A deliberate enqueue can add it again; refresh alone cannot.
                Pump(queue.EnqueueAsync(draft.FileId, draft.FileName, 1, default));
                Refresh(window);
                Assert.Equal(TransferQueueState.Pending, Items(queue).Single(item => item.FileId == draft.FileId).State);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (window is not null) Close(window);
                dispatcher.InvokeShutdown(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Cancellation fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static object? Invoke(MainWindow window, string method, params object?[] args) => typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, args);
    private static void Refresh(MainWindow window) => Pump((Task)Invoke(window, "RefreshManifestsAsync")!);
    private static IReadOnlyList<TransferQueueItem> Items(SqliteTransferQueueStore queue) => Pump(queue.ListAsync(default));
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke((Action)(() => frame.Continue = false)), TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
    private static void Close(MainWindow window)
    {
        var frame = new DispatcherFrame(); window.Closed += (_, _) => frame.Continue = false;
        window.Close(); if (frame.Continue) Dispatcher.PushFrame(frame);
    }
}
