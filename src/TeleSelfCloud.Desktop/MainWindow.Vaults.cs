using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Desktop;

public partial class MainWindow
{
    private string? GetStorageConnectionBlockReason()
    {
        if (_storageConnectionInProgress) return "storage.connection.progress";
        if (_shutdownStarted || _operationBusy) return "storage.connection.busy";
        if (_telegramSession?.CurrentAuthorizationState != "authorizationStateReady") return "storage.connection.signIn";
        if (!_telegramAccountLoaded || _activeTelegramAccountId is null)
            return _vaultProfileSetupFailed ? "vault.profileUnavailable" : "storage.connection.profileLoading";
        return null;
    }

    private long? GetSelectedStorageConnectionChatId(bool useSelectedVault) =>
        useSelectedVault && VaultPicker.SelectedItem is VaultChoice choice && choice.Channel.AccountId == _activeTelegramAccountId
            ? choice.Channel.ChatId : null;

    private void ShowStorageConnectionFeedback(string? message)
    {
        StorageConnectionFeedback.Text = message ?? string.Empty;
        StorageConnectionFeedback.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        if (!string.IsNullOrEmpty(message)) StatusText.Text = message;
    }

    private sealed record VaultChoice(TelegramStorageChannelInfo Channel, string StatusLabel)
    {
        public string DisplayLabel => Channel.DisplayLabel;
    }

    private void UpdateVaultPickerPresentation(IEnumerable<TelegramStorageChannelInfo> channels, long? selectedChatId, bool canInteract)
    {
        var registeredIds = _vaultRegistry is not null && _vaultRegistry.AccountId == _activeTelegramAccountId
            ? _vaultRegistry.Vaults.Select(v => v.ChatId).ToHashSet() : [];
        var choices = channels.Select(channel => new VaultChoice(channel, UiText.Instance.Get(
            channel.ChatId == _storageChannel?.ChatId ? "vault.choice.active" :
            registeredIds.Contains(channel.ChatId) ? "vault.choice.registered" : "vault.choice.discovered"))).ToArray();
        VaultPicker.ItemsSource = choices;
        VaultPicker.SelectedItem = choices.FirstOrDefault(v => v.Channel.ChatId == selectedChatId) ?? choices.FirstOrDefault();
        VaultPicker.IsEnabled = canInteract && choices.Length > 0;
        SwitchVaultButton.IsEnabled = VaultPicker.IsEnabled;
        VaultPickerState.Visibility = choices.Length == 0 || _pendingLegacyIsolationChannel is not null || _vaultProfileSetupFailed ? Visibility.Visible : Visibility.Collapsed;
        VaultPickerState.Text = UiText.Instance.Get(_vaultProfileSetupFailed ? "vault.profileUnavailable" : _pendingLegacyIsolationChannel is not null
            ? "vault.empty.catalogError" : _storageConnectionInProgress ? "vault.empty.loading" : "vault.empty.help");
        System.Windows.Automation.AutomationProperties.SetHelpText(VaultPicker, VaultPickerState.Visibility == Visibility.Visible
            ? VaultPickerState.Text : UiText.Instance.Get("vault.choice.help"));
    }

    private TelegramVaultRegistryState? _vaultRegistry;
    private bool _vaultProfileSetupFailed;
    private TelegramStorageChannelInfo? _pendingLegacyIsolationChannel;
    private bool _legacyLocalDataView;
    private TelegramStorageChannelInfo? _legacyViewReturnChannel;
    private IReadOnlyList<TelegramStorageChannelInfo> _discoveredVaults = [];
    private string? _discoveryAccountId;
    private readonly Dictionary<string, TelegramVaultRegistry> protectedRegistries = new(StringComparer.OrdinalIgnoreCase);
    private TelegramVaultRegistry CreateVaultRegistry(string account)
    {
        var root = GetAccountDataDirectory(account);
        if (!LocalVaultRegistryProtection.IsConfigured(root)) return new(root, account);
        LocalVaultRegistryProtection.RequireReady(root, account);
        if (!protectedRegistries.TryGetValue(root, out var registry)) { registry = LocalVaultRegistryProtection.OpenRegistry(root, account); protectedRegistries.Add(root, registry); }
        return registry;
    }
    private VaultCreationJournal CreateVaultCreationJournal(string account)
    {
        var root = GetAccountDataDirectory(account);
        if (!LocalDatabaseProtection.IsConfigured(root)) return new(root, account);
        var lease = DatabaseProfileLease ?? throw new InvalidOperationException("The local profile must be open before vault creation recovery can be accessed.");
        lease.RequireWithin(root);
        return new(root, account, identity => LocalDatabaseProtection.RecordCipher(root, $"account:{account}:{identity}", "vault-creation"), lease);
    }
    private string GetVaultDataDirectory(TelegramStorageChannelInfo channel) => _vaultRegistry?.AccountId == channel.AccountId
        ? CreateVaultRegistry(channel.AccountId).GetDataDirectory(_vaultRegistry, channel.ChatId)
        : throw new InvalidOperationException("The selected vault is not registered for this account.");

    private void ApplyVaultActionAvailability()
    {
        if (VaultActions is null) return;
        var authorized = _telegramSession?.CurrentAuthorizationState == "authorizationStateReady";
        var ready = _telegramAccountLoaded && _activeTelegramAccountId is not null && authorized;
        VaultActions.Visibility = authorized && (ready || _vaultProfileSetupFailed) ? Visibility.Visible : Visibility.Collapsed;
        VaultPicker.IsEnabled = ready && !_operationBusy && !_shutdownStarted;
        SwitchVaultButton.IsEnabled = VaultPicker.IsEnabled;
        AddVaultButton.IsEnabled = VaultPicker.IsEnabled;
        CreateVaultButton.IsEnabled = VaultPicker.IsEnabled;
        DiscoverVaultsButton.IsEnabled = VaultPicker.IsEnabled;
        var pending = ready && CreateVaultCreationJournal(_activeTelegramAccountId!).HasPending;
        RecoverVaultCreationButton.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        RecoverVaultCreationButton.IsEnabled = VaultPicker.IsEnabled;
        AbandonVaultCreationButton.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        AbandonVaultCreationButton.IsEnabled = VaultPicker.IsEnabled;
        var canRecoverLegacy = ready && !_operationBusy && !_shutdownStarted &&
            _pendingLegacyIsolationChannel?.AccountId == _activeTelegramAccountId;
        RecoverLegacyVaultButton.Visibility = canRecoverLegacy ? Visibility.Visible : Visibility.Collapsed;
        RecoverLegacyVaultButton.IsEnabled = canRecoverLegacy;
        var hasIsolatedPrimary = ready && _vaultRegistry is { PrimaryVaultUsesIsolatedDirectory: true } &&
            _vaultRegistry.AccountId == _activeTelegramAccountId;
        var canViewLegacy = hasIsolatedPrimary && !_operationBusy && !_shutdownStarted && !_legacyLocalDataView && _storageChannel is not null;
        ViewLegacyLocalDataButton.Visibility = canViewLegacy ? Visibility.Visible : Visibility.Collapsed;
        ViewLegacyLocalDataButton.IsEnabled = canViewLegacy;
        var canReturn = ready && !_operationBusy && !_shutdownStarted && _legacyLocalDataView &&
            _legacyViewReturnChannel?.AccountId == _activeTelegramAccountId;
        ReturnToVaultButton.Visibility = canReturn ? Visibility.Visible : Visibility.Collapsed;
        ReturnToVaultButton.IsEnabled = canReturn;
        CreateVaultButton.IsEnabled &= !pending;
        if (_operationBusy) return;
        var selected = (VaultPicker.SelectedItem as VaultChoice)?.Channel.ChatId ?? _vaultRegistry?.ActiveChatId;
        var registered = _vaultRegistry is not null && _vaultRegistry.AccountId == _activeTelegramAccountId ? _vaultRegistry.Vaults : [];
        var discovered = _discoveryAccountId == _activeTelegramAccountId ? _discoveredVaults : [];
        var choices = registered.Concat(discovered).GroupBy(v => v.ChatId).Select(group => group.Last()).OrderBy(v => v.Title).ThenBy(v => v.ChatId).ToArray();
        UpdateVaultPickerPresentation(choices, selected, ready && !_shutdownStarted);
    }

    private void UseVaultStores(VaultProfileStores stores)
    {
        CloseMetadataHistory(); CloseFolderReview();
        foreach (var preview in OwnedWindows.OfType<FilePreviewWindow>().ToArray()) preview.Close();
        _manifestStore = stores.Manifests; _transferQueueStore = stores.Queue;
        _syncCheckpointStore = stores.Checkpoints; _folderStore = stores.Folders;
        _localCacheVerificationStore = stores.Cache; _stagingRoot = stores.StagingRoot;
        var stagingContent = DatabaseProfileLease is null ? null : CreateStagingContentStore(stores.Root);
        _workflow = new LocalFileWorkflow(new FileTransferCoordinator(), stores.Manifests,
            new StagedPartAssembler(stagingContent), stagingContent);
        _queueViews.Clear(); _manifestItems = []; _localFolderPaths = [];
        ManifestList.ItemsSource = null; TrashManifestList.ItemsSource = null; TransferList.ItemsSource = null;
        _observedRemoteManifestIds.Clear(); _currentFolderPath = string.Empty;
        _lastCatalogAttempt = null; _hasPendingFolderChange = false;
        _chosenFilePath = string.Empty; _chosenUploadPaths = Array.Empty<string>(); ChosenFileName.Text = UiText.Instance.Get("upload.choose");
        UpdateLastSyncText(null);
    }

    private async Task OpenRegisteredVaultAsync(TelegramStorageChannelInfo verified, CancellationToken token)
    {
        var registry = CreateVaultRegistry(verified.AccountId);
        if (await registry.LoadAsync(token) is null)
        {
            var primary = CreateProtectedVaultStores(GetAccountDataDirectory(verified.AccountId));
            var manifests = await primary.Manifests.ListAsync(token);
            var legacyPlan = VaultLegacyPartitionPlanner.Create(manifests, verified.AccountId, verified.ChatId);
            if (legacyPlan.RequiresIsolation)
            {
                _pendingLegacyIsolationChannel = verified;
                throw new InvalidDataException(UiText.Instance.Get("vault.legacyNeedsIsolation"));
            }
            await primary.PrepareAsync(verified.AccountId, token, verified.ChatId);
        }
        var registered = await registry.RegisterAsync(verified, select: false, token);
        var stores = CreateProtectedVaultStores(registry.GetDataDirectory(registered, verified.ChatId));
        await stores.PrepareAsync(verified.AccountId, token, verified.ChatId);
        var externalStagingCount = 0;
        if (LocalDatabaseProtection.IsConfigured(stores.Root))
        {
            var stagingMigration = await LocalStagingProtectionMigrator.MigrateAsync(stores.StagingRoot, stores.Manifests,
                CreateStagingContentStore(stores.Root), token, (_, _) => StatusText.Text = UiText.Instance.Get("localdb.stage.StagingEncrypting"));
            externalStagingCount = stagingMigration.OutsideCatalogCount;
        }
        if (DatabaseProfileLease is { } lease)
            await StagingOrphanReconciler.ReconcileAsync(LocalDataRoot, stores.StagingRoot, stores.Manifests, lease, DateTimeOffset.UtcNow, token);
        // Prepare first; once selection is durably committed, swap every store together without another await.
        var selected = await registry.SelectAsync(verified.ChatId, token);
        _vaultRegistry = selected;
        UseVaultStores(stores);
        _storageChannel = verified;
        ShowStorageConnectionFeedback(null);
        _legacyLocalDataView = false;
        _legacyViewReturnChannel = null;
        LoadMetadataKey(stores.Root, verified.AccountId, verified.ChatId);
        ReportExternalStagingPaths(externalStagingCount);
    }

    private async Task OpenLegacyLocalDataViewAsync(string account)
    {
        if (_storageChannel is null || _vaultRegistry is not { PrimaryVaultUsesIsolatedDirectory: true } ||
            _vaultRegistry.AccountId != account || _activeTelegramAccountId != account)
            throw new InvalidOperationException(UiText.Instance.Get("vault.legacyChanged"));
        var accountRoot = GetAccountDataDirectory(account);
        if (LocalDatabaseProtection.IsConfigured(accountRoot) || LocalCacheProtection.IsConfigured(accountRoot))
            throw new InvalidOperationException(UiText.Instance.Get("vault.legacyIsolationProtected"));
        var legacyStores = CreateProtectedVaultStores(accountRoot);
        _ = await legacyStores.Manifests.ListAsync(OperationToken);
        _legacyViewReturnChannel = _storageChannel;
        _legacyLocalDataView = true;
        _storageChannel = null;
        UseVaultStores(legacyStores);
        await RefreshManifestsReadOnlyAsync();
        StatusText.Text = UiText.Instance.Get("vault.viewLegacyOpened");
        ApplyActionAvailability();
    }

    private async void VaultAction_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy || _shutdownStarted || !_telegramAccountLoaded || _activeTelegramAccountId is null ||
            _telegramSession?.CurrentAuthorizationState != "authorizationStateReady" ||
            (_vaultRegistry is not null && _vaultRegistry.AccountId != _activeTelegramAccountId)) return;
        var account = _activeTelegramAccountId;
        var session = _telegramSession;
        var selected = (VaultPicker.SelectedItem as VaultChoice)?.Channel;
        string? input = null;
        if (sender == AddVaultButton)
            input = Prompt(UiText.Instance.Get("vault.attachPrompt"), UiText.Instance.Get("vault.attach"), allowEmpty: false);
        else if (sender == CreateVaultButton)
            input = Prompt(UiText.Instance.Get("vault.createPrompt"), UiText.Instance.Get("vault.create"), allowEmpty: false);
        if ((sender == AddVaultButton || sender == CreateVaultButton) && input is null) return;
        if (sender == SwitchVaultButton && selected is null) return;
        if (sender == SwitchVaultButton && selected!.ChatId == _storageChannel?.ChatId) return;
        SetBusy(true, UiText.Instance.Get("vault.opening"), canCancel: false);
        TelegramStorageChannelInfo? newlyCreated = null;
        try
        {
            if (sender == ViewLegacyLocalDataButton)
            {
                await OpenLegacyLocalDataViewAsync(account);
                return;
            }
            var service = CreateStorageChannelService(session);
            if (sender == ReturnToVaultButton)
            {
                var returnChannel = _legacyViewReturnChannel;
                if (!_legacyLocalDataView || returnChannel?.AccountId != account) return;
                var verifiedReturn = await service.VerifyExistingAsync(returnChannel.ChatId, account, OperationToken);
                await OpenRegisteredVaultAsync(verifiedReturn, OperationToken);
                await RefreshManifestsAsync();
                StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("vault.opened"), verifiedReturn.Title);
                ApplyActionAvailability();
                return;
            }
            if (sender == RecoverLegacyVaultButton)
            {
                var pending = _pendingLegacyIsolationChannel;
                if (pending is null || pending.AccountId != account) return;
                var recoveredChannel = await service.VerifyExistingAsync(pending.ChatId, account, OperationToken);
                var accountRoot = GetAccountDataDirectory(account);
                if (LocalDatabaseProtection.IsConfigured(accountRoot) || LocalCacheProtection.IsConfigured(accountRoot))
                    throw new InvalidOperationException(UiText.Instance.Get("vault.legacyIsolationProtected"));

                var legacyStores = CreateProtectedVaultStores(accountRoot);
                var manifests = await legacyStores.Manifests.ListAsync(OperationToken);
                var plan = VaultLegacyPartitionPlanner.Create(manifests, account, recoveredChannel.ChatId);
                if (!plan.RequiresIsolation)
                    throw new InvalidOperationException(UiText.Instance.Get("vault.legacyChanged"));
                var confirmation = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("vault.legacyIsolationConfirm"),
                    recoveredChannel.Title, plan.LocalRecoveryFileIds.Count);
                if (MessageBox.Show(this, confirmation, UiText.Instance.Get("vault.isolateLegacy"), MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

                var isolatedRegistry = CreateVaultRegistry(account);
                var isolatedStores = CreateProtectedVaultStores(isolatedRegistry.GetIsolatedPrimaryDirectory(recoveredChannel.ChatId));
                var result = await VaultLegacyPrimaryIsolationWorkflow.IsolateAsync(legacyStores, isolatedStores,
                    isolatedRegistry, recoveredChannel, async (path, cancellation) =>
                    {
                        CopyValidatedMetadataKey(accountRoot, path, account, recoveredChannel.ChatId);
                        await isolatedStores.PrepareAsync(account, cancellation, recoveredChannel.ChatId);
                    }, OperationToken);
                _vaultRegistry = result.Registry;
                UseVaultStores(isolatedStores);
                _storageChannel = recoveredChannel;
                _legacyLocalDataView = false;
                _legacyViewReturnChannel = null;
                _pendingLegacyIsolationChannel = null;
                LoadMetadataKey(isolatedStores.Root, recoveredChannel.AccountId, recoveredChannel.ChatId);
                await RefreshManifestsAsync();
                StatusText.Text = UiText.Instance.Get("vault.legacyIsolationDone");
                SetBusy(false);
                await RunCatalogSyncAsync(fullRescan: true);
                return;
            }
            if (sender == AbandonVaultCreationButton)
            {
                var journal = CreateVaultCreationJournal(account);
                var attempt = await journal.LoadAsync(OperationToken);
                if (attempt is null) return;
                if (MessageBox.Show(this, UiText.Instance.Get("vault.abandonConfirm"), UiText.Instance.Get("vault.abandon"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                await journal.AbandonAsync(attempt.RequestId, OperationToken);
                StatusText.Text = UiText.Instance.Get("vault.abandoned");
                return;
            }
            if (sender == DiscoverVaultsButton)
            {
                EnableOperationCancellation();
                var discovered = await service.DiscoverAsync(account, OperationToken);
                _discoveredVaults = discovered; _discoveryAccountId = account;
                StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("vault.discovered"), discovered.Count);
                return;
            }
            TelegramStorageChannelInfo verified;
            if (sender == CreateVaultButton || sender == RecoverVaultCreationButton)
            {
                EnableOperationCancellation();
                verified = newlyCreated = await new VaultCreationWorkflow(service, CreateVaultCreationJournal(account), account)
                    .ExecuteAsync(input, OpenRegisteredVaultAsync, OperationToken);
            }
            else
            {
                var chatId = selected?.ChatId ?? 0;
                if (sender == AddVaultButton && (!long.TryParse(input, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out chatId) || chatId == 0))
                    throw new InvalidOperationException(UiText.Instance.Get("vault.invalidId"));
                verified = await service.VerifyExistingAsync(chatId, account, OperationToken);
            }
            if (sender != CreateVaultButton && sender != RecoverVaultCreationButton) await OpenRegisteredVaultAsync(verified, CancellationToken.None);
            await RefreshManifestsAsync();
            StatusText.Text = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("vault.opened"), verified.Title);
        }
        catch (Exception ex)
        {
            StatusText.Text = UiText.Instance.LocalizeMessage(ex.Message) + (newlyCreated is null ? "" : " " +
                string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("vault.createdRecovery"), newlyCreated.ChatId));
        }
        finally { SetBusy(false); ApplyActionAvailability(); }
    }

    private static void CopyValidatedMetadataKey(string sourceRoot, string targetRoot, string account, long chatId)
    {
        using var key = VaultMetadataKeyStore.LoadForIsolatedVault(sourceRoot, account, chatId);
        if (key is null) return;
        var records = new List<(string Destination, byte[] Bytes)>();
        try
        {
            // Check both records before writing either, including a partial copy left by an interrupted attempt.
            foreach (var name in new[] { "metadata-key-policy.json", "metadata-key.dpapi.json" })
            {
                var source = Path.Combine(sourceRoot, name);
                var destination = Path.Combine(targetRoot, name);
                if (!File.Exists(source) || LocalFileSystemPathGuard.ContainsReparsePoint(source) ||
                    LocalFileSystemPathGuard.ContainsReparsePoint(destination) || Directory.Exists(destination))
                    throw new InvalidDataException("The legacy metadata key records are incomplete or linked; both were kept.");
                var bytes = File.ReadAllBytes(source);
                records.Add((destination, bytes));
                if (File.Exists(destination) && !File.ReadAllBytes(destination).AsSpan().SequenceEqual(bytes))
                    throw new InvalidDataException("The isolated vault contains a different metadata key record; both records were kept.");
            }
            Directory.CreateDirectory(targetRoot);
            foreach (var (destination, bytes) in records)
            {
                if (File.Exists(destination)) continue;
                var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    { output.Write(bytes); output.Flush(flushToDisk: true); }
                    File.Move(temporary, destination, false);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            using var copiedKey = VaultMetadataKeyStore.Load(targetRoot, account, chatId);
            if (copiedKey is null || copiedKey.KeyId != key.KeyId || copiedKey.ScopeProof() != key.ScopeProof())
                throw new CryptographicException("The isolated vault metadata key copy did not verify; the original records were kept.");
        }
        finally { foreach (var record in records) CryptographicOperations.ZeroMemory(record.Bytes); }
    }
}
