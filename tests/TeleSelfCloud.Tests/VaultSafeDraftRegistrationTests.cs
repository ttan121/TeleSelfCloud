using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class VaultSafeDraftRegistrationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshAccountDraftRegistersInTheOriginalPrimaryCatalog(bool includeCurrentRemoteManifest)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.SafeDraftUi", Guid.NewGuid().ToString("N"));
            MainWindow? window = null;
            try
            {
                Directory.CreateDirectory(root); UiText.Instance.SetLanguage("en", Path.Combine(root, "language.json"));
                var accountRoot = TelegramAccountProfileStore.GetDirectory(root, "42");
                var stores = new VaultProfileStores(accountRoot);
                Directory.CreateDirectory(stores.StagingRoot);
                var stagingPath = Path.Combine(stores.StagingRoot, "draft.part");
                File.WriteAllBytes(stagingPath, [1, 2, 3]);
                var draft = Draft(stagingPath);
                Pump(stores.Manifests.SaveAsync(draft, default));
                if (includeCurrentRemoteManifest) Pump(stores.Manifests.SaveAsync(CurrentManifest(), default));
                Pump(stores.Queue.EnsureAsync(draft.FileId, draft.FileName, draft.TransferSize, default));
                var before = JsonSerializer.Serialize(Pump(stores.Manifests.ListAsync(default)));
                var queueBefore = JsonSerializer.Serialize(Pump(stores.Queue.ListAsync(default)));
                var uiDb = Path.Combine(root, "ui.db"); var uiStore = new SqliteManifestStore(uiDb);
                window = new MainWindow(new LocalFileWorkflow(new FileTransferCoordinator(), uiStore, new StagedPartAssembler()),
                    Path.Combine(root, "ui-staging"), uiStore, new SqliteTransferQueueStore(uiDb),
                    new SqliteRemoteSyncCheckpointStore(uiDb), new SqliteLocalFolderStore(uiDb), root);
                typeof(MainWindow).GetField("_activeTelegramAccountId", Private)!.SetValue(window, "42");
                Pump((Task)typeof(MainWindow).GetMethod("OpenRegisteredVaultAsync", Private)!.Invoke(window,
                    [new TelegramStorageChannelInfo(-101, "42", "Fixture"), CancellationToken.None])!);

                using var registry = new TelegramVaultRegistry(accountRoot, "42");
                var registered = Pump(registry.LoadAsync(default))!;
                Assert.False(registered.PrimaryVaultUsesIsolatedDirectory);
                Assert.Equal(accountRoot, registry.GetDataDirectory(registered, -101));
                Assert.Null(typeof(MainWindow).GetField("_pendingLegacyIsolationChannel", Private)!.GetValue(window));
                var activeStore = Assert.IsType<SqliteManifestStore>(typeof(MainWindow).GetField("_manifestStore", Private)!.GetValue(window));
                Assert.NotSame(uiStore, activeStore);
                Assert.Equal(before, JsonSerializer.Serialize(Pump(activeStore.ListAsync(default))));
                Assert.Equal(queueBefore, JsonSerializer.Serialize(Pump(stores.Queue.ListAsync(default))));
                Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(stagingPath));
                Assert.False(Directory.Exists(registry.GetIsolatedPrimaryDirectory(-101)));
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                if (window is not null) { typeof(MainWindow).GetField("_allowClose", Private)!.SetValue(window, true); window.Close(); }
                dispatcher.InvokeShutdown(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Safe draft registration fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SafeDraftDoesNotCreateAnIsolationIntentOrInvokePreparation(bool includeCurrentRemoteManifest)
    {
        var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.SafeDraftIsolation", Guid.NewGuid().ToString("N"));
        try
        {
            var stores = new VaultProfileStores(root);
            await stores.Manifests.SaveAsync(Draft(null), default);
            if (includeCurrentRemoteManifest) await stores.Manifests.SaveAsync(CurrentManifest(), default);
            var before = JsonSerializer.Serialize(await stores.Manifests.ListAsync(default));
            var plan = VaultLegacyPartitionPlanner.Create(await stores.Manifests.ListAsync(default), "42", -101);
            Assert.False(plan.RequiresIsolation);
            Assert.Equal(VaultLegacyManifestDisposition.NoRemoteBinding, Assert.Single(plan.Manifests, item => item.FileId == "draft").Disposition);
            Assert.Contains("draft", plan.LocalRecoveryFileIds); // Recovery taxonomy remains unchanged.
            using var registry = new TelegramVaultRegistry(root, "42");
            var target = new VaultProfileStores(registry.GetIsolatedPrimaryDirectory(-101));
            var called = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => VaultLegacyPrimaryIsolationWorkflow.IsolateAsync(stores,
                target, registry, new(-101, "42", "Fixture"), (_, _) => { called = true; return Task.CompletedTask; }, default));

            Assert.False(called);
            Assert.Null(await registry.LoadAsync(default));
            Assert.False(Directory.Exists(target.Root));
            Assert.Equal(before, JsonSerializer.Serialize(await stores.Manifests.ListAsync(default)));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static FileManifest Draft(string? stagingPath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 }));
        return new(1, "draft", "draft.bin", 3, hash, 3, [new(0, 0, 3, hash, null, false, stagingPath)], false, "42");
    }
    private static FileManifest CurrentManifest() => Draft(null) with
    {
        FileId = "current", FileName = "current.bin", Committed = true,
        Parts = [Draft(null).Parts[0] with { RemoteId = "-101/20", Confirmed = true }]
    };
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Pump(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame();
        task.ContinueWith(_ => dispatcher.BeginInvoke((Action)(() => frame.Continue = false)), TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}
