using System.Globalization;
using System.IO;
using System.Windows;
using TeleSelfCloud.Infrastructure.Telegram;

namespace TeleSelfCloud.Desktop;

public partial class MainWindow
{
    private CatalogSyncAttempt? _lastCatalogAttempt;
    private bool _catalogSyncRunning;
    private CatalogSyncAttemptStore CreateSyncAttemptStore(TelegramStorageChannelInfo channel)
    {
        var vaultRoot = GetVaultDataDirectory(channel);
        var directory = Path.Combine(vaultRoot, "sync-attempts");
        if (!LocalDatabaseProtection.IsConfigured(vaultRoot)) return new(directory, channel.AccountId, channel.ChatId);
        var lease = DatabaseProfileLease ?? throw new InvalidOperationException("The local profile must be open before sync reports can be accessed.");
        lease.RequireWithin(vaultRoot);
        return new(directory, channel.AccountId, channel.ChatId,
            identity => LocalDatabaseProtection.RecordCipher(vaultRoot, identity, "sync-report"), lease);
    }

    private void ApplySyncActionAvailability()
    {
        if (CatalogSyncActions is null) return;
        var channel = _storageChannel;
        var scoped = channel is not null && _activeTelegramAccountId == channel.AccountId;
        if (!scoped || (_lastCatalogAttempt is not null &&
            (_lastCatalogAttempt.AccountId != channel!.AccountId || _lastCatalogAttempt.ChatId != channel.ChatId))) _lastCatalogAttempt = null;
        CatalogSyncActions.Visibility = scoped ? Visibility.Visible : Visibility.Collapsed;
        var ready = scoped && !_operationBusy && !_shutdownStarted && _telegramSession?.CurrentAuthorizationState == "authorizationStateReady";
        CatalogSyncButton.IsEnabled = ready;
        FullCatalogSyncButton.IsEnabled = ready;
        RetryCatalogSyncButton.IsEnabled = ready;
        RetryCatalogSyncButton.Visibility = _lastCatalogAttempt is not null && _lastCatalogAttempt.Outcome != CatalogSyncOutcome.Completed ? Visibility.Visible : Visibility.Collapsed;
        if (!_catalogSyncRunning) CatalogSyncReport.Text = DescribeSyncAttempt(_lastCatalogAttempt);
    }

    internal static string DescribeSyncAttempt(CatalogSyncAttempt? report)
    {
        if (report is null) return UiText.Instance.Get("sync.report.none");
        var key = report.Outcome switch
        {
            CatalogSyncOutcome.Running => "sync.report.interrupted",
            CatalogSyncOutcome.Completed => "sync.report.completed",
            CatalogSyncOutcome.FolderPublicationPending => "sync.report.folderPending",
            _ => "sync.report.partial"
        };
        return string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get(key), report.Pages, report.Messages, report.FilesIndexed);
    }

    private async Task RunCatalogSyncAsync(bool fullRescan, bool retry = false)
    {
        if (_shutdownStarted || _telegramSession is null || _storageChannel is null ||
            _activeTelegramAccountId != _storageChannel.AccountId || _telegramSession.CurrentAuthorizationState != "authorizationStateReady" ||
            (_operationBusy && !_storageConnectionInProgress)) return;
        var channel = _storageChannel;
        var session = _telegramSession;
        var store = CreateSyncAttemptStore(channel);
        TelegramRemoteManifestCatalog? catalog = null;
        EventHandler<string>? progress = null;
        _catalogSyncRunning = true;
        SetBusy(true, UiText.Instance.Get("status.scanningPrivateChannel"));
        CatalogSyncReport.Text = UiText.Instance.Get("sync.report.running");
        try
        {
            CatalogSyncAttempt? previous = null;
            try { previous = await store.LoadAsync(OperationToken); }
            catch (InvalidDataException) when (fullRescan) { /* Workflow preserves the invalid report before replacing it. */ }
            if (previous is not null) _observedRemoteManifestIds.UnionWith(previous.ObservedFileIds);
            var token = OperationToken;
            UpdateLastSyncText((await _syncCheckpointStore.LoadAsync(channel.AccountId, channel.ChatId, token))?.LastSuccessfulSyncUtc);
            var transport = CreateTransport(session, channel);
            catalog = new TelegramRemoteManifestCatalog(session, transport, _manifestStore, channel.ChatId, channel.AccountId,
                _syncCheckpointStore, _folderStore, CreateMetadataConflictArchive(channel), _metadataKey);
            progress = (_, status) => Dispatcher.BeginInvoke((Action)(() =>
            {
                if (_catalogSyncRunning && _storageChannel == channel)
                { StatusText.Text = UiText.Instance.LocalizeMessage(status); CatalogSyncReport.Text = UiText.Instance.LocalizeMessage(status); }
            }));
            catalog.ScanStatusChanged += progress;
            var workflow = new CatalogSyncWorkflow(catalog, store, channel.AccountId, channel.ChatId,
                cancellation => PublishFolderStateAsync(transport, channel.AccountId, cancellation));
            var result = retry && previous?.Outcome == CatalogSyncOutcome.FolderPublicationPending
                ? await workflow.RetryFolderPublicationAsync(token)
                : await workflow.RunAsync(fullRescan || (retry && previous?.RequestedFull == true), token);
            _lastCatalogAttempt = result.Report;
            if (result.Report.CatalogCompletedAtUtc is not null && !result.Report.WasIncremental)
                _observedRemoteManifestIds.Clear();
            _observedRemoteManifestIds.UnionWith(result.Report.ObservedFileIds);
            UpdateLastSyncText((await _syncCheckpointStore.LoadAsync(channel.AccountId, channel.ChatId, CancellationToken.None))?.LastSuccessfulSyncUtc);
            StatusText.Text = DescribeSyncAttempt(result.Report) + (result.Error is null ? "" : " " + UiText.Instance.LocalizeMessage(result.Error.Message));
        }
        catch (OperationCanceledException) { StatusText.Text = UiText.Instance.Get("status.remoteSyncCanceled"); }
        catch (Exception ex) { StatusText.Text = UiText.Instance.LocalizeMessage(ex.Message); }
        finally
        {
            _catalogSyncRunning = false;
            if (catalog is not null && progress is not null) catalog.ScanStatusChanged -= progress;
            if (catalog is not null)
            {
                _observedRemoteManifestIds.UnionWith(catalog.ObservedFileIds);
                if (catalog.LastSuccessfulSyncUtc is not null) UpdateLastSyncText(catalog.LastSuccessfulSyncUtc);
            }
            try { await RefreshManifestsAsync(); }
            catch (Exception ex) { StatusText.Text = UiText.Instance.LocalizeMessage(ex.Message); }
            finally { SetBusy(false); ApplySyncActionAvailability(); }
        }
    }
}
