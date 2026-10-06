using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class MetadataKeyScopeUiTests
{
    [Theory]
    [InlineData("stale-root")]
    [InlineData("stale-generation")]
    [InlineData("during-preflight-vault")]
    [InlineData("during-preflight-key")]
    [InlineData("success")]
    [InlineData("export-changed")]
    [InlineData("recover-changed")]
    [InlineData("foreign-publishing")]
    [InlineData("foreign-active-account")]
    public void KeyActionsCannotInstallIntoChangedVaultOrKeyContext(string mode)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var parent = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.MetadataScope", Guid.NewGuid().ToString("N")); var root = Path.Combine(parent, "profile"); MainWindow? window = null;
            try
            {
                Directory.CreateDirectory(root); var db = Path.Combine(root, "manifests.db"); var manifests = new SqliteManifestStore(db);
                window = new MainWindow(new LocalFileWorkflow(new FileTransferCoordinator(), manifests, new StagedPartAssembler()), Path.Combine(root, "staging"), manifests, new SqliteTransferQueueStore(db), new SqliteRemoteSyncCheckpointStore(db), new SqliteLocalFolderStore(db), root);
                var type = typeof(MainWindow);
                object? Read(string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                void Write(string name, object? value) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
                void Pump(Task task) { var frame = new DispatcherFrame(); task.ContinueWith(_ => dispatcher.BeginInvoke((Action)(() => frame.Continue = false)), TaskScheduler.Default); if (!task.IsCompleted) Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult(); }
                void Open(long chat) => Pump((Task)type.GetMethod("OpenRegisteredVaultAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [new TelegramStorageChannelInfo(chat, "42", "Fixture vault"), CancellationToken.None])!);
                Write("_activeTelegramAccountId", "42"); Open(-101);
                var channel = (TelegramStorageChannelInfo)Read("_storageChannel")!;
                var vaultRoot = Path.GetDirectoryName((string)Read("_stagingRoot")!)!; var generation = (long)Read("_metadataKeyGeneration")!;
                var destination = Path.Combine(parent, "backup.tsc-key.json"); var checks = 0;
                if (mode is "foreign-publishing" or "foreign-active-account")
                {
                    if (mode == "foreign-publishing") Write("_metadataKey", VaultMetadataKey.Create("42", -102));
                    else Write("_activeTelegramAccountId", "99");
                    var error = Assert.Throws<TargetInvocationException>(() => type.GetMethod("MetadataKeyForPublishing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null));
                    Assert.IsType<InvalidDataException>(error.InnerException); Assert.False(File.Exists(destination)); return;
                }
                byte[]? originalRecord = null;
                if (mode.EndsWith("-changed"))
                {
                    using var original = VaultMetadataKey.Create("42", -101); VaultMetadataKeyStore.Save(vaultRoot, original);
                    type.GetMethod("LoadMetadataKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [vaultRoot, "42", -101L]); generation = (long)Read("_metadataKeyGeneration")!;
                    originalRecord = File.ReadAllBytes(Path.Combine(vaultRoot, "metadata-key.dpapi.json"));
                    if (mode == "recover-changed") VaultMetadataKeyStore.WriteBackup(destination, original.ExportBackup("fixture metadata passphrase"));
                    // Force the vault switch during the next awaited KDF operation; Background
                    // can run after the Normal-priority continuation has already saved the backup.
                    dispatcher.BeginInvoke((Action)(() => Open(-102)), DispatcherPriority.Send);
                }
                if (mode == "stale-root") Open(-102);
                if (mode == "stale-generation") type.GetMethod("LoadMetadataKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [vaultRoot, "42", -101L]);
                Func<CancellationToken, Task> preflight = _ =>
                {
                    checks++;
                    if (mode == "during-preflight-key") type.GetMethod("LoadMetadataKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [vaultRoot, "42", -101L]);
                    return mode == "during-preflight-vault" ? (Task)type.GetMethod("OpenRegisteredVaultAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [new TelegramStorageChannelInfo(-102, "42", "Changed vault"), CancellationToken.None])! : Task.CompletedTask;
                };
                Pump((Task)type.GetMethod("SaveMetadataKeyActionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [vaultRoot, channel, generation, !mode.EndsWith("-changed"), mode == "recover-changed", destination, "fixture metadata passphrase", preflight])!);
                if (mode == "success")
                {
                    Assert.True(File.Exists(destination)); using var saved = VaultMetadataKeyStore.Load(vaultRoot, "42", -101);
                    Assert.NotNull(saved); Assert.Equal(saved.KeyId, ((VaultMetadataKey)Read("_metadataKey")!).KeyId); Assert.Equal(1, checks);
                    using var backup = VaultMetadataKey.Recover(VaultMetadataKeyStore.ReadBackup(destination), "42", -101, "fixture metadata passphrase"); Assert.Equal(saved.ScopeProof(), backup.ScopeProof());
                }
                else
                {
                    Assert.Equal(mode == "recover-changed", File.Exists(destination)); Assert.Equal(mode.EndsWith("-changed"), VaultMetadataKeyStore.IsConfigured(vaultRoot)); Assert.Null(Read("_metadataKey"));
                    Assert.Equal(mode.StartsWith("stale-") || mode.EndsWith("-changed") ? 0 : 1, checks);
                    if (originalRecord is not null) { Assert.Equal(originalRecord, File.ReadAllBytes(Path.Combine(vaultRoot, "metadata-key.dpapi.json"))); Assert.Empty(Directory.GetFiles(vaultRoot, "metadata-key.dpapi.json.*.backup")); }
                    Assert.Equal(UiText.Instance.Get("metadata.crypto.scopeChanged"), Assert.IsAssignableFrom<TextBlock>(window.FindName("StatusText")).Text);
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (window is not null) { var frame = new DispatcherFrame(); window.Closed += (_, _) => frame.Continue = false; window.Close(); if (frame.Continue) Dispatcher.PushFrame(frame); }
                dispatcher.InvokeShutdown(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(parent)) Directory.Delete(parent, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(40)), "Metadata key context fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
