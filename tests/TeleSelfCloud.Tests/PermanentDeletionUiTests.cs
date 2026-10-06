using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using System.Windows.Controls;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class PermanentDeletionUiTests
{
    private sealed class Capability : IUploadCapabilityProvider
    {
        public Task RefreshAsync(CancellationToken token) => Task.CompletedTask;
        public Task<UploadCapability?> GetCurrentAsync(CancellationToken token) => Task.FromResult<UploadCapability?>(new("42", 1000, DateTimeOffset.UtcNow, "fixture"));
    }
    [Theory]
    [InlineData("success", false)]
    [InlineData("success", true)]
    [InlineData("account-before", true)]
    [InlineData("key-before", true)]
    [InlineData("manifest-before", true)]
    [InlineData("running", true)]
    [InlineData("busy", true)]
    [InlineData("account-after", true)]
    [InlineData("key-missing", true)]
    [InlineData("store-before", true)]
    [InlineData("shutdown-before", true)]
    [InlineData("store-after", true)]
    public void ConfirmedDeletionUsesOwnedKeyAndCapturedVaultAndPreservesDedupSurvivor(string mode, bool encryptedMetadata)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.DeletionUi", Guid.NewGuid().ToString("N"));
            MainWindow? window = null; LocalProfileLease? lease = null; VaultMetadataKey? key = null; IDisposable? captured = null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic; var type = typeof(MainWindow);
            void Pump(Task task) { var frame = new DispatcherFrame(); task.ContinueWith(_ => dispatcher.BeginInvoke((Action)(() => frame.Continue = false)), TaskScheduler.Default); if (!task.IsCompleted) Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult(); }
            try
            {
                Directory.CreateDirectory(root); UiText.Instance.SetLanguage("vi", Path.Combine(root, "language.json"));
                var db = Path.Combine(root, "db"); var store = new SqliteManifestStore(db); var queue = new SqliteTransferQueueStore(db);
                window = new MainWindow(new LocalFileWorkflow(new FileTransferCoordinator(), store, new StagedPartAssembler()), Path.Combine(root, "staging"), store,
                    queue, new SqliteRemoteSyncCheckpointStore(db), new SqliteLocalFolderStore(db), root);
                lease = LocalProfileLease.TryAcquire(root)!; window.DatabaseProfileLease = lease;
                void Set(string name, object? value) => type.GetField(name, flags)!.SetValue(window, value);
                Set("_activeTelegramAccountId", "42");
                Pump((Task)type.GetMethod("OpenRegisteredVaultAsync", flags)!.Invoke(window, [new TelegramStorageChannelInfo(-100, "42", "Fixture"), CancellationToken.None])!);
                store = Assert.IsType<SqliteManifestStore>(type.GetField("_manifestStore", flags)!.GetValue(window));
                queue = Assert.IsType<SqliteTransferQueueStore>(type.GetField("_transferQueueStore", flags)!.GetValue(window));
                key = encryptedMetadata ? VaultMetadataKey.Create("42", -100) : null; Set("_metadataKey", key);
                var world = new DedupOwnershipAcceptanceTests.World(root); var transport = new TelegramFileTransport(world, -100, Path.Combine(root, "downloads"));
                var publisher = new TelegramManifestPublisher(transport, Path.Combine(root, "publication"), key);
                var source = Path.Combine(root, "source.bin"); var bytes = "ABCDEF"u8.ToArray(); File.WriteAllBytes(source, bytes);
                FileManifest? original = null, target = null;
                Pump(Task.Run(async () =>
                {
                    original = await new UploadPipeline(new FileTransferCoordinator(), new Capability(), transport, store, publisher).UploadAsync(source, Path.Combine(root, "stage"), 3, default, true);
                    target = await new UploadPipeline(new FileTransferCoordinator(), new Capability(), transport, store, publisher, new TelegramUploadDeduplication(store, world, transport, "42")).UploadAsync(source, Path.Combine(root, "stage"), 3, default, true);
                    target = target with { IsInTrash = true, Revision = 1, UpdatedAtUtc = DateTimeOffset.UtcNow };
                    await publisher.PublishCommittedAsync(target, default); await store.SaveAsync(target, default);
                }));
                Assert.Equal(2, world.IdSends);
                if (mode == "key-missing") Set("_metadataKeyReadFailed", true);
                var capture = type.GetMethod("CapturePermanentDeletionScope", flags)!;
                if (mode == "key-missing")
                {
                    var error = Assert.Throws<TargetInvocationException>(() => capture.Invoke(window, [new[] { target! }]));
                    Assert.IsType<InvalidDataException>(error.InnerException); Assert.Equal(0, world.Deletes); return;
                }
                captured = Assert.IsAssignableFrom<IDisposable>(capture.Invoke(window, [new[] { target! }]));
                var snapshotKey = (VaultMetadataKey?)captured.GetType().GetProperty("Key")!.GetValue(captured);
                if (key is not null) { Assert.NotSame(key, snapshotKey); Assert.Equal(key.KeyId, snapshotKey!.KeyId); }
                if (mode == "account-before") Set("_activeTelegramAccountId", "99");
                if (mode == "key-before") Set("_metadataKeyGeneration", 99L);
                if (mode == "busy") Set("_operationBusy", true);
                if (mode == "store-before") Set("_manifestStore", new SqliteManifestStore(Path.Combine(root, "other.db")));
                if (mode == "shutdown-before") Set("_shutdownStarted", true);
                if (mode == "manifest-before") Pump(store.SaveAsync(target! with { FileName = "changed.bin", Revision = 2 }, default));
                if (mode == "running") Pump(Task.Run(async () => { await queue.EnsureAsync("other", "other.bin", 1, default); await queue.SetStateAsync("other", TransferQueueState.Running, null, default); }));
                var called = false;
                async Task<IReadOnlyList<PermanentFileDeletionResult>> Action(VaultMetadataKey? ownedKey, CancellationToken token)
                {
                    called = true; key?.Dispose(); // UI-owned key can disappear; the operation owns a clone.
                    var results = await new PermanentFileDeletion(store, queue, new TelegramRemoteFileDeleter(world, transport, -100, "42", ownedKey)).ExecuteAsync([target!.FileId], "42", token);
                    if (mode == "account-after") Set("_activeTelegramAccountId", "99");
                    if (mode == "store-after") Set("_manifestStore", new SqliteManifestStore(Path.Combine(root, "other.db")));
                    return results;
                }
                var task = (Task<IReadOnlyList<PermanentFileDeletionResult>?>)type.GetMethod("RunPermanentDeletionAsync", flags)!.Invoke(window,
                    [captured, (Func<VaultMetadataKey?, CancellationToken, Task<IReadOnlyList<PermanentFileDeletionResult>>>)Action])!;
                Pump(task);
                var allowed = mode is "success" or "account-after" or "store-after";
                Assert.Equal(allowed, called); Assert.Equal(allowed ? 1 : 0, world.Deletes);
                Assert.Equal(!allowed, Task.Run(() => store.LoadAsync(target!.FileId, default)).GetAwaiter().GetResult() is not null);
                Assert.NotNull(Task.Run(() => store.LoadAsync(original!.FileId, default)).GetAwaiter().GetResult());
                if (mode == "success") Assert.True(Assert.Single(task.Result!).Succeeded); else Assert.Null(task.Result);
                var status = Assert.IsAssignableFrom<TextBlock>(window.FindName("StatusText")).Text;
                Assert.Equal(mode == "success" ? string.Format(UiText.Instance.Get("trash.deleteForever.done"), 1, 1) : UiText.Instance.Get(mode is "account-after" or "store-after" ? "trash.deleteForever.original" : "trash.deleteForever.scopeChanged"), status);
                if (mode == "store-after") Assert.Empty(Task.Run(() => ((IManifestStore)type.GetField("_manifestStore", flags)!.GetValue(window)!).ListAsync(default)).GetAwaiter().GetResult());
                var output = Path.Combine(root, "survivor.bin"); Pump(new FileTransferCoordinator(transport).ReassembleAsync(original!, output, default)); Assert.Equal(bytes, File.ReadAllBytes(output));
                captured.Dispose(); if (snapshotKey is not null) Assert.Throws<ObjectDisposedException>(() => snapshotKey.ExportKeyBytes()); captured = null;
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                captured?.Dispose();
                if (window is not null) { type.GetField("_operationBusy", flags)!.SetValue(window, false); type.GetField("_shutdownStarted", flags)!.SetValue(window, false); var frame = new DispatcherFrame(); window.Closed += (_, _) => frame.Continue = false; window.Close(); if (frame.Continue) Dispatcher.PushFrame(frame); }
                key?.Dispose(); lease?.Dispose(); dispatcher.InvokeShutdown(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Deletion UI fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
