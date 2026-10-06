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

public sealed class AccountProfileVaultActivationUiTests
{
    [Fact]
    public void RestartImportsSharedIntoIsolatedPrimaryAndOpensActiveVaultWithoutChangingRetainedLegacyRoot()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.AccountVaultActivationUi", Guid.NewGuid().ToString("N"));
            MainWindow? window = null;
            try
            {
                Directory.CreateDirectory(root);
                UiText.Instance.SetLanguage("en", Path.Combine(root, "language.json"));
                var shared = new VaultProfileStores(root);
                var accountRoot = TelegramAccountProfileStore.GetDirectory(root, "42");
                var legacy = new VaultProfileStores(accountRoot);
                using var registry = new TelegramVaultRegistry(accountRoot, "42");
                var state = Task.Run(async () =>
                {
                    await registry.RegisterIsolatedPrimaryAsync(new(-101, "42", "Primary"), default);
                    return await registry.RegisterAsync(new(-102, "42", "Active"), select: true, default);
                }).GetAwaiter().GetResult();
                var primary = new VaultProfileStores(registry.GetDataDirectory(state, -101));
                var active = new VaultProfileStores(registry.GetDataDirectory(state, -102));
                var bytes = new byte[] { 9 };
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                Directory.CreateDirectory(shared.StagingRoot);
                Directory.CreateDirectory(legacy.StagingRoot);
                var sharedPart = Path.Combine(shared.StagingRoot, "source.part");
                var legacyPart = Path.Combine(legacy.StagingRoot, "legacy.part");
                File.WriteAllBytes(sharedPart, bytes); File.WriteAllBytes(legacyPart, bytes);
                var source = new FileManifest(1, "same-id", "primary.bin", 1, hash, 1,
                    [new(0, 0, 1, hash, "-101/10", true, sharedPart)], true, "42");
                var retained = source with { FileId = "legacy", FileName = "legacy.bin", Parts = [source.Parts[0] with { RemoteId = "-1009/20", StagingPath = legacyPart }] };
                var activeFile = source with { FileName = "active.bin", Parts = [source.Parts[0] with { RemoteId = "-102/30", StagingPath = null }] };
                var otherShared = source with { FileId = "other-chat", FileName = "other.bin", Parts = [source.Parts[0] with { RemoteId = "-1009/40", StagingPath = null }] };
                Task.Run(async () =>
                {
                    await shared.Manifests.SaveAsync(source, default); await shared.Manifests.SaveAsync(otherShared, default);
                    await legacy.Manifests.SaveAsync(retained, default); await active.Manifests.SaveAsync(activeFile, default);
                    await shared.Queue.EnsureAsync(source.FileId, source.FileName, 1, default);
                    await shared.Queue.EnsureAsync(otherShared.FileId, otherShared.FileName, 1, default);
                    await legacy.Queue.EnsureAsync(retained.FileId, retained.FileName, 1, default);
                    await active.Queue.EnsureAsync(activeFile.FileId, activeFile.FileName, 1, default);
                    await legacy.Checkpoints.SaveAsync("42", -1009, new(900, DateTimeOffset.UnixEpoch), default);
                    await shared.Checkpoints.SaveAsync("42", -101, new(110, DateTimeOffset.UnixEpoch), default);
                    await active.Checkpoints.SaveAsync("42", -102, new(220, DateTimeOffset.UnixEpoch), default);
                }).GetAwaiter().GetResult();
                var legacyManifestsBefore = JsonSerializer.Serialize(Task.Run(() => legacy.Manifests.ListAsync(default)).GetAwaiter().GetResult());
                var legacyQueueBefore = JsonSerializer.Serialize(Task.Run(() => legacy.Queue.ListAsync(default)).GetAwaiter().GetResult());
                window = new MainWindow(new LocalFileWorkflow(new FileTransferCoordinator(), shared.Manifests, new StagedPartAssembler()),
                    shared.StagingRoot, shared.Manifests, shared.Queue, shared.Checkpoints, shared.Folders, root);
                var type = typeof(MainWindow);
                type.GetField("_activeTelegramAccountId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, "42");
                var task = (Task)type.GetMethod("ActivateAccountProfileAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, ["42"])!;
                var frame = new DispatcherFrame();
                task.ContinueWith(_ => dispatcher.BeginInvoke((Action)(() => frame.Continue = false)), TaskScheduler.Default);
                if (!task.IsCompleted) Dispatcher.PushFrame(frame);
                task.GetAwaiter().GetResult();

                Assert.Equal(legacyManifestsBefore, JsonSerializer.Serialize(Task.Run(() => legacy.Manifests.ListAsync(default)).GetAwaiter().GetResult()));
                Assert.Equal(legacyQueueBefore, JsonSerializer.Serialize(Task.Run(() => legacy.Queue.ListAsync(default)).GetAwaiter().GetResult()));
                Assert.Equal(bytes, File.ReadAllBytes(legacyPart));
                Assert.Equal(900, Task.Run(() => legacy.Checkpoints.LoadAsync("42", -1009, default)).GetAwaiter().GetResult()!.HighestMessageId);
                Assert.Equal("primary.bin", Task.Run(() => primary.Manifests.LoadAsync("same-id", default)).GetAwaiter().GetResult()!.FileName);
                Assert.Equal("active.bin", Task.Run(() => active.Manifests.LoadAsync("same-id", default)).GetAwaiter().GetResult()!.FileName);
                Assert.Null(Task.Run(() => shared.Manifests.LoadAsync("same-id", default)).GetAwaiter().GetResult());
                Assert.NotNull(Task.Run(() => shared.Manifests.LoadAsync("other-chat", default)).GetAwaiter().GetResult());
                Assert.Equal(110, Task.Run(() => shared.Checkpoints.LoadAsync("42", -101, default)).GetAwaiter().GetResult()!.HighestMessageId);
                Assert.Null(Task.Run(() => primary.Checkpoints.LoadAsync("42", -101, default)).GetAwaiter().GetResult());
                Assert.Equal(active.StagingRoot, type.GetField("_stagingRoot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
                Assert.Equal("active.bin", Task.Run(() => ((SqliteManifestStore)type.GetField("_manifestStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).LoadAsync("same-id", default)).GetAwaiter().GetResult()!.FileName);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (window is not null)
                {
                    var frame = new DispatcherFrame(); window.Closed += (_, _) => frame.Continue = false;
                    window.Close(); if (frame.Continue) Dispatcher.PushFrame(frame);
                }
                dispatcher.InvokeShutdown(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Account activation fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
