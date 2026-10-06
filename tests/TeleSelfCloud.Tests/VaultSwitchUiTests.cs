using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class VaultSwitchUiTests
{
    [Fact]
    public void LegacyLocalBrowseIsReadOnlyAndDoesNotCreateUploadQueueRows()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.LegacyBrowseUi", Guid.NewGuid().ToString("N"));
            MainWindow? window = null;
            try
            {
                Directory.CreateDirectory(root);
                UiText.Instance.SetLanguage("en", Path.Combine(root, "language.json"));
                var accountRoot = TelegramAccountProfileStore.GetDirectory(root, "42");
                var legacy = new VaultProfileStores(accountRoot);
                var hash = new string('A', 64);
                var draft = new FileManifest(1, "kept-draft", "draft.bin", 1, hash, 1,
                    [new(0, 0, 1, hash, null, false)], false, "42");
                Task.Run(() => legacy.Manifests.SaveAsync(draft, default)).GetAwaiter().GetResult();
                using var registry = new TelegramVaultRegistry(accountRoot, "42");
                var registered = Task.Run(() => registry.RegisterIsolatedPrimaryAsync(new(-101, "42", "Verified"), default)).GetAwaiter().GetResult();
                var uiDb = Path.Combine(root, "ui.db");
                var uiManifests = new SqliteManifestStore(uiDb);
                window = new MainWindow(new LocalFileWorkflow(new FileTransferCoordinator(), uiManifests, new StagedPartAssembler()),
                    Path.Combine(root, "ui-staging"), uiManifests, new SqliteTransferQueueStore(uiDb),
                    new SqliteRemoteSyncCheckpointStore(uiDb), new SqliteLocalFolderStore(uiDb), root);
                var type = typeof(MainWindow);
                void Write(string name, object? value) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
                Write("_activeTelegramAccountId", "42"); Write("_telegramAccountLoaded", true);
                Write("_storageChannel", new TelegramStorageChannelInfo(-101, "42", "Verified")); Write("_vaultRegistry", registered);
                var browse = (Task)type.GetMethod("OpenLegacyLocalDataViewAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, ["42"])!;
                var frame = new DispatcherFrame();
                browse.ContinueWith(_ => dispatcher.BeginInvoke((Action)(() => frame.Continue = false)), TaskScheduler.Default);
                if (!browse.IsCompleted) Dispatcher.PushFrame(frame);
                browse.GetAwaiter().GetResult();

                Assert.True((bool)type.GetField("_legacyLocalDataView", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
                Assert.Null(type.GetField("_storageChannel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
                Assert.Empty(Task.Run(() => ((SqliteTransferQueueStore)type.GetField("_transferQueueStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).ListAsync(default)).GetAwaiter().GetResult());
                Assert.Equal("draft.bin", Task.Run(() => ((SqliteManifestStore)type.GetField("_manifestStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).LoadAsync("kept-draft", default)).GetAwaiter().GetResult()!.FileName);
                Assert.False(Assert.IsType<Button>(window.FindName("CreateFolderButton")).IsEnabled);
                Assert.False(Assert.IsType<Button>(window.FindName("RemoveLocalEntryButton")).IsEnabled);
                Assert.Equal(UiText.Instance.Get("vault.viewLegacyOpened"), Assert.IsAssignableFrom<TextBlock>(window.FindName("StatusText")).Text);

                var open = (Task)type.GetMethod("OpenRegisteredVaultAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [new TelegramStorageChannelInfo(-101, "42", "Verified"), CancellationToken.None])!;
                var returnFrame = new DispatcherFrame();
                open.ContinueWith(_ => dispatcher.BeginInvoke((Action)(() => returnFrame.Continue = false)), TaskScheduler.Default);
                if (!open.IsCompleted) Dispatcher.PushFrame(returnFrame);
                open.GetAwaiter().GetResult();
                type.GetMethod("ApplyActionAvailability", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                Assert.False((bool)type.GetField("_legacyLocalDataView", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
                Assert.True(Assert.IsType<Button>(window.FindName("CreateFolderButton")).IsEnabled);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Legacy browse UI fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void MixedLegacyCatalogIsRetainedAndOffersTheIsolatedVaultRecoveryAction()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.LegacyVaultUi", Guid.NewGuid().ToString("N"));
            MainWindow? window = null;
            try
            {
                Directory.CreateDirectory(root);
                UiText.Instance.SetLanguage("vi", Path.Combine(root, "language.json"));
                var accountRoot = TelegramAccountProfileStore.GetDirectory(root, "42");
                var legacy = new VaultProfileStores(accountRoot);
                var hash = new string('A', 64);
                var expected = new FileManifest(1, "mixed-legacy", "kept.bin", 2, hash, 1,
                    [new(0, 0, 1, hash, "-101/10", true), new(1, 1, 1, hash, "-102/20", true)], true, "42");
                Task.Run(() => legacy.Manifests.SaveAsync(expected, default)).GetAwaiter().GetResult();
                var uiDatabase = Path.Combine(root, "ui.db");
                var uiManifests = new SqliteManifestStore(uiDatabase);
                window = new MainWindow(new LocalFileWorkflow(new FileTransferCoordinator(), uiManifests, new StagedPartAssembler()),
                    Path.Combine(root, "ui-staging"), uiManifests, new SqliteTransferQueueStore(uiDatabase),
                    new SqliteRemoteSyncCheckpointStore(uiDatabase), new SqliteLocalFolderStore(uiDatabase), root);
                var type = typeof(MainWindow);
                type.GetField("_activeTelegramAccountId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, "42");
                var task = (Task)type.GetMethod("OpenRegisteredVaultAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [new TelegramStorageChannelInfo(-101, "42", "Verified"), CancellationToken.None])!;
                var frame = new DispatcherFrame();
                task.ContinueWith(_ => dispatcher.BeginInvoke((Action)(() => frame.Continue = false)), TaskScheduler.Default);
                if (!task.IsCompleted) Dispatcher.PushFrame(frame);
                Assert.IsType<InvalidDataException>(Record.Exception(() => task.GetAwaiter().GetResult()));
                var pending = Assert.IsType<TelegramStorageChannelInfo>(type.GetField("_pendingLegacyIsolationChannel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
                Assert.Equal(-101, pending.ChatId);
                Assert.Null(Task.Run(() => new TelegramVaultRegistry(accountRoot, "42").LoadAsync(default)).GetAwaiter().GetResult());
                Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(Task.Run(() => legacy.Manifests.LoadAsync("mixed-legacy", default)).GetAwaiter().GetResult()));
                Assert.False(Directory.Exists(Path.Combine(accountRoot, "vaults")));
                var action = Assert.IsType<Button>(window.FindName("RecoverLegacyVaultButton"));
                Assert.Equal(UiText.Instance.Get("vault.isolateLegacy"), AutomationProperties.GetName(action));
                Assert.Equal(UiText.Instance.Get("vault.isolateLegacyTip"), AutomationProperties.GetHelpText(action));
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Legacy vault UI fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Theory]
    [InlineData("vi", 760, 650)]
    [InlineData("en", 1000, 760)]
    public void ActualWindowSwapsAllStoresAndRestoresTheOriginalVault(string language, int width, int height)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.VaultUi", Guid.NewGuid().ToString("N"));
            MainWindow? window = null;
            try
            {
                Directory.CreateDirectory(root);
                UiText.Instance.SetLanguage(language, Path.Combine(root, "language.json"));
                var db = Path.Combine(root, "shared.db");
                var manifests = new SqliteManifestStore(db);
                window = new MainWindow(new LocalFileWorkflow(new FileTransferCoordinator(), manifests, new StagedPartAssembler()),
                    Path.Combine(root, "staging"), manifests, new SqliteTransferQueueStore(db), new SqliteRemoteSyncCheckpointStore(db),
                    new SqliteLocalFolderStore(db), root);
                var type = typeof(MainWindow);
                object? Read(string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                void Write(string name, object? value) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
                void Open(long chat)
                {
                    var task = (Task)type.GetMethod("OpenRegisteredVaultAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(window, [new TelegramStorageChannelInfo(chat, "42", chat == -101 ? "Kho gốc / Primary" : "Kho bổ sung / Additional"), CancellationToken.None])!;
                    var frame = new DispatcherFrame();
                    task.ContinueWith(_ => dispatcher.BeginInvoke((Action)(() => frame.Continue = false)), TaskScheduler.Default);
                    if (!task.IsCompleted) Dispatcher.PushFrame(frame);
                    task.GetAwaiter().GetResult();
                }
                var actions = Assert.IsType<StackPanel>(window.FindName("VaultActions"));
                Assert.Equal(Visibility.Collapsed, actions.Visibility);
                foreach (var name in new[] { "SwitchVaultButton", "AddVaultButton", "CreateVaultButton", "DiscoverVaultsButton", "RecoverVaultCreationButton", "RecoverLegacyVaultButton" })
                    Assert.False(Assert.IsType<Button>(window.FindName(name)).IsEnabled);
                var legacyRecovery = Assert.IsType<Button>(window.FindName("RecoverLegacyVaultButton"));
                Assert.Equal(Visibility.Collapsed, legacyRecovery.Visibility);
                Assert.Equal(UiText.Instance.Get("vault.isolateLegacy"), AutomationProperties.GetName(legacyRecovery));
                Assert.Equal(UiText.Instance.Get("vault.isolateLegacyTip"), AutomationProperties.GetHelpText(legacyRecovery));
                var viewLegacy = Assert.IsType<Button>(window.FindName("ViewLegacyLocalDataButton"));
                var returnToVault = Assert.IsType<Button>(window.FindName("ReturnToVaultButton"));
                Assert.Equal(UiText.Instance.Get("vault.viewLegacy"), AutomationProperties.GetName(viewLegacy));
                Assert.Equal(UiText.Instance.Get("vault.viewLegacyTip"), AutomationProperties.GetHelpText(viewLegacy));
                Write("_activeTelegramAccountId", "42");
                Open(-101);
                var first = Assert.IsType<SqliteManifestStore>(Read("_manifestStore"));
                var queue = Assert.IsType<SqliteTransferQueueStore>(Read("_transferQueueStore"));
                var folders = Read("_folderStore"); var cache = Read("_localCacheVerificationStore"); var checkpoint = Read("_syncCheckpointStore");
                var firstStaging = Assert.IsType<string>(Read("_stagingRoot"));
                var manifest = new FileManifest(1, "same", "primary.bin", 1, new string('A', 64), 1,
                    [new(0, 0, 1, new string('A', 64), "-101/10", true)], true, "42");
                Task.Run(async () => { await first.SaveAsync(manifest, default); await queue.EnsureAsync("same", "primary.bin", 1, default); }).GetAwaiter().GetResult();
                Write("_chosenFilePath", "must-not-follow-to-second-vault.bin");
                Open(-102);
                Assert.NotSame(first, Read("_manifestStore")); Assert.NotSame(queue, Read("_transferQueueStore"));
                Assert.NotSame(folders, Read("_folderStore")); Assert.NotSame(cache, Read("_localCacheVerificationStore"));
                Assert.NotSame(checkpoint, Read("_syncCheckpointStore"));
                Assert.NotEqual(firstStaging, Read("_stagingRoot"));
                Assert.Equal(string.Empty, Read("_chosenFilePath"));
                var second = Assert.IsType<SqliteManifestStore>(Read("_manifestStore"));
                Assert.Null(Task.Run(() => second.LoadAsync("same", default)).GetAwaiter().GetResult());
                Task.Run(() => second.SaveAsync(manifest with { FileName = "second.bin", Parts = [manifest.Parts[0] with { RemoteId = "-102/10" }] }, default)).GetAwaiter().GetResult();
                Open(-101);
                var restored = Assert.IsType<SqliteManifestStore>(Read("_manifestStore"));
                Assert.Equal("primary.bin", Task.Run(() => restored.LoadAsync("same", default)).GetAwaiter().GetResult()!.FileName);
                Assert.Single(Task.Run(() => ((SqliteTransferQueueStore)Read("_transferQueueStore")!).ListAsync(default)).GetAwaiter().GetResult());
                Assert.Equal(firstStaging, Read("_stagingRoot"));
                Assert.Equal(-101, ((TelegramVaultRegistryState)Read("_vaultRegistry")!).ActiveChatId);
                actions.Visibility = Visibility.Visible;
                var picker = Assert.IsType<ComboBox>(window.FindName("VaultPicker"));
                picker.ItemsSource = ((TelegramVaultRegistryState)Read("_vaultRegistry")!).Vaults;
                picker.SelectedIndex = 0;
                Assert.IsType<StackPanel>(window.FindName("CatalogSyncActions")).Visibility = Visibility.Visible;
                Assert.IsAssignableFrom<TextBlock>(window.FindName("CatalogSyncReport")).Text = string.Format(UiText.Instance.Get("sync.report.folderPending"), 7, 4321, 8);
                Assert.IsType<Button>(window.FindName("RetryCatalogSyncButton")).Visibility = Visibility.Visible;
                Assert.IsType<Button>(window.FindName("RecoverVaultCreationButton")).Visibility = Visibility.Visible;
                Assert.IsType<Button>(window.FindName("AbandonVaultCreationButton")).Visibility = Visibility.Visible;
                var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                legacyRecovery.Visibility = Visibility.Visible;
                legacyRecovery.IsEnabled = true;
                viewLegacy.Visibility = Visibility.Visible; viewLegacy.IsEnabled = true;
                returnToVault.Visibility = Visibility.Visible; returnToVault.IsEnabled = true;
                content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
                foreach (var name in new[] { "SwitchVaultButton", "AddVaultButton", "CreateVaultButton", "RetryCatalogSyncButton", "DiscoverVaultsButton", "RecoverVaultCreationButton", "AbandonVaultCreationButton", "RecoverLegacyVaultButton", "ViewLegacyLocalDataButton", "ReturnToVaultButton" })
                {
                    var button = Assert.IsType<Button>(window.FindName(name));
                    var point = button.TransformToAncestor(content).Transform(new Point());
                    Assert.True(point.X + button.ActualWidth <= width && point.Y + button.ActualHeight <= height);
                }
                var cryptoHost = Assert.IsType<Border>(window.FindName("MetadataEncryptionActions"));
                type.GetMethod("ApplyMetadataEncryptionAvailability", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                Assert.Equal(Visibility.Visible, cryptoHost.Visibility);
                var enable = Assert.IsType<Button>(window.FindName("EnableMetadataEncryptionButton"));
                var export = Assert.IsType<Button>(window.FindName("ExportMetadataKeyButton"));
                var recoverKey = Assert.IsType<Button>(window.FindName("RecoverMetadataKeyButton"));
                Assert.True(enable.IsEnabled); Assert.False(export.IsEnabled); Assert.True(recoverKey.IsEnabled);
                var profileRoot = Path.GetDirectoryName(firstStaging)!;
                using var metadataKey = VaultMetadataKey.Create("42", -101);
                TeleSelfCloud.Desktop.VaultMetadataKeyStore.Save(profileRoot, metadataKey);
                type.GetMethod("LoadMetadataKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [profileRoot, "42", -101L]);
                type.GetMethod("ApplyMetadataEncryptionAvailability", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                Assert.False(enable.IsEnabled); Assert.True(export.IsEnabled); Assert.True(recoverKey.IsEnabled);
                type.GetMethod("NavigateToPage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, ["settings"]);
                content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
                foreach (var button in new[] { enable, export, recoverKey })
                {
                    // Settings grows with protection features; each action must remain
                    // reachable through the actual ScrollViewer, including at 760x650.
                    button.BringIntoView(); content.UpdateLayout();
                    var p = button.TransformToAncestor(content).Transform(new Point());
                    Assert.True(p.X >= 0 && p.Y >= 0 && p.X + button.ActualWidth <= width && p.Y + button.ActualHeight <= height);
                }
                var keyPath = Path.Combine(profileRoot, "metadata-key.dpapi.json");
                var validKeyRecord = File.ReadAllText(keyPath);
                foreach (var invalidRecord in new[] { "corrupted", validKeyRecord.Replace("\"version\":1", "\"version\":999"), validKeyRecord.Replace("\"protectedKey\":\"", "\"protectedKey\":null,\"ignored\":\"") })
                {
                    File.WriteAllText(keyPath, invalidRecord);
                    type.GetMethod("LoadMetadataKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [profileRoot, "42", -101L]);
                    type.GetMethod("ApplyMetadataEncryptionAvailability", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                    Assert.False(enable.IsEnabled); Assert.False(export.IsEnabled); Assert.True(recoverKey.IsEnabled);
                    Assert.Equal(UiText.Instance.Get("metadata.crypto.keyUnavailable"), Assert.IsType<TextBlock>(window.FindName("MetadataEncryptionReport")).Text);
                }
                File.WriteAllText(keyPath, "corrupted");
                type.GetMethod("LoadMetadataKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [profileRoot, "42", -101L]);
                type.GetMethod("ApplyMetadataEncryptionAvailability", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                Assert.False(enable.IsEnabled); Assert.False(export.IsEnabled); Assert.True(recoverKey.IsEnabled);
                Assert.Equal(UiText.Instance.Get("metadata.crypto.keyUnavailable"), Assert.IsType<TextBlock>(window.FindName("MetadataEncryptionReport")).Text);
                content.UpdateLayout();
                var failureToPublish = Assert.Throws<TargetInvocationException>(() => type.GetMethod("MetadataKeyForPublishing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null));
                Assert.IsType<InvalidDataException>(failureToPublish.InnerException);
                Assert.Equal("corrupted", File.ReadAllText(Path.Combine(profileRoot, "metadata-key.dpapi.json")));
                var capture = Environment.GetEnvironmentVariable("TSC_VAULT_CAPTURE_DIRECTORY");
                if (!string.IsNullOrEmpty(capture))
                {
                    Directory.CreateDirectory(capture);
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(capture, $"vault-{language}.png")); encoder.Save(stream);
                }
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Vault UI fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
