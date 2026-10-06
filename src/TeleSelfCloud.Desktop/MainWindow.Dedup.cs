using System;
using System.IO;
using System.Linq;
using System.Windows;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Telegram;

namespace TeleSelfCloud.Desktop;

public partial class MainWindow
{
    private void UploadEncryptionChanged(object sender, RoutedEventArgs e) => ApplyDedupAvailability();
    private void ApplyDedupAvailability()
    {
        if (DeduplicateUploadCheck is null) return;
        DeduplicateUploadCheck.IsEnabled = !_operationBusy && !_shutdownStarted;
        EncryptContentCheck.IsEnabled = !_operationBusy && !_shutdownStarted;
    }
    private IUploadDeduplication? CreateUploadDeduplication(ITelegramRequestClient session, TelegramFileTransport transport, string account, bool enabled) =>
        enabled ? new TelegramUploadDeduplication(_manifestStore, session, transport, account, PromptEncryptedCandidatePassphraseAsync) : null;
    private Task<string?> PromptEncryptedCandidatePassphraseAsync(FileManifest candidate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var name = candidate.FileName.Length <= 120 ? candidate.FileName : candidate.FileName[..120] + "…";
        var message = string.Format(System.Globalization.CultureInfo.CurrentCulture, UiText.Instance.Get("upload.dedupEncryptedPrompt"), name);
        return Task.FromResult(Prompt(message, UiText.Instance.Get("upload.dedupEncryptedTitle"), secret: true, allowEmpty: false,
            UiText.Instance.Get("upload.dedupEncryptedHelper")));
    }
    private bool UploadScopeMatches(ITelegramRequestClient session, TelegramStorageChannelInfo channel, IManifestStore store, string source, string staging) =>
        !_shutdownStarted && ReferenceEquals(_telegramSession, session) && ReferenceEquals(_manifestStore, store) &&
        _activeTelegramAccountId == channel.AccountId && _storageChannel?.ChatId == channel.ChatId &&
        _storageChannel.AccountId == channel.AccountId && UploadSelectionContains(source) && _stagingRoot == staging;

    private bool UploadSelectionContains(string source)
    {
        if (_chosenUploadPaths.Length == 0) return string.Equals(_chosenFilePath, source, StringComparison.OrdinalIgnoreCase);
        var fullSource = Path.GetFullPath(source);
        return _chosenUploadPaths.Any(selected =>
        {
            var fullSelected = Path.GetFullPath(selected);
            if (File.Exists(fullSelected)) return string.Equals(fullSelected, fullSource, StringComparison.OrdinalIgnoreCase);
            if (!Directory.Exists(fullSelected)) return false;
            var relative = Path.GetRelativePath(fullSelected, fullSource);
            return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        });
    }
    private static bool CopyFallbackTask(TransferQueueItem item) => item.Direction == TransferDirection.Upload &&
        item.State is TransferQueueState.Paused or TransferQueueState.Failed or TransferQueueState.Cancelled;
    private void ApplyCopyFallbackAvailability()
    {
        if (CopyFallbackButton is null) return;
        var item = (TransferList.SelectedItem as QueueItemView)?.Item;
        var manifest = item is null ? null : _manifestItems.FirstOrDefault(m => m.Manifest.FileId == item.FileId)?.Manifest;
        var visible = item is not null && CopyFallbackTask(item) && manifest is not null && !manifest.Committed && manifest.Parts.Any(p => p.CopySource is not null);
        CopyFallbackButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        CopyFallbackButton.IsEnabled = visible && !_operationBusy && !_shutdownStarted && DatabaseProfileLease is not null &&
            _telegramAccountLoaded && manifest!.AccountId == _activeTelegramAccountId && _telegramSession?.CurrentAuthorizationState == "authorizationStateReady";
    }
    private async void CopyFallback_Click(object sender, RoutedEventArgs e)
    {
        if (TransferList.SelectedItem is not QueueItemView view || !CopyFallbackTask(view.Item) || _operationBusy || _shutdownStarted ||
            DatabaseProfileLease is null || _telegramSession is not { CurrentAuthorizationState: "authorizationStateReady" } session ||
            _storageChannel is not { } channel || channel.AccountId != _activeTelegramAccountId) return;
        var store = _manifestStore;
        await SaveCopyFallbackAsync(view.Item, token =>
        {
            var transport = CreateTransport(session, channel);
            // Fallback only reconciles and saves a draft; it never publishes metadata.
            var publisher = new TelegramManifestPublisher(transport, System.IO.Path.Combine(_stagingRoot, "remote-manifests"));
            var pipeline = new TeleSelfCloud.Infrastructure.Transfers.UploadPipeline(new TeleSelfCloud.Infrastructure.Transfers.FileTransferCoordinator(),
                new TelegramUploadCapabilityProvider(session), transport, store, publisher, stagingContentStore: CreateStagingContentStore());
            return pipeline.SwitchPendingCopiesToUploadAsync(view.Item.FileId, token);
        });
    }
    private async Task SaveCopyFallbackAsync(TransferQueueItem item, Func<CancellationToken, Task<FileManifest>> action)
    {
        var store = _manifestStore; var queue = _transferQueueStore; var account = _activeTelegramAccountId; var channel = _storageChannel; var lease = DatabaseProfileLease;
        var session = _telegramSession; var root = LocalDatabaseRoot; var saved = false;
        bool SameScope() => ReferenceEquals(store, _manifestStore) && ReferenceEquals(queue, _transferQueueStore) && ReferenceEquals(lease, DatabaseProfileLease) &&
            ReferenceEquals(session, _telegramSession) && root == LocalDatabaseRoot && account == _activeTelegramAccountId &&
            channel?.ChatId == _storageChannel?.ChatId && _storageChannel?.AccountId == account && (TransferList.SelectedItem as QueueItemView)?.Item.TaskId == item.TaskId;
        if (_operationBusy || _shutdownStarted || lease is null || account is null || channel?.AccountId != account || !CopyFallbackTask(item) || !SameScope())
        { StatusText.Text = UiText.Instance.Get("dedup.fallbackScope"); return; }
        SetBusy(true, UiText.Instance.Get("dedup.fallbackWorking"), canCancel: true);
        try
        {
            lease.RequireWithin(root);
            var tasks = await queue.ListAsync(OperationToken);
            if (tasks.Any(t => t.State == TransferQueueState.Running) || !tasks.Any(t => t.TaskId == item.TaskId && CopyFallbackTask(t)) || !SameScope() || _shutdownStarted)
            { StatusText.Text = UiText.Instance.Get("dedup.fallbackScope"); return; }
            var manifest = await store.LoadAsync(item.FileId, OperationToken);
            if (manifest is null || manifest.AccountId != account || manifest.Committed || !manifest.Parts.Any(p => p.CopySource is not null) || !SameScope() || _shutdownStarted)
            { StatusText.Text = UiText.Instance.Get("dedup.fallbackScope"); return; }
            var updated = await action(OperationToken);
            saved = true;
            await queue.UpdateProgressAsync(item.TaskId, updated.Parts.Where(p => p.Confirmed).Sum(p => p.Length), updated.TransferSize, CancellationToken.None);
            if (!SameScope() || _shutdownStarted) { StatusText.Text = UiText.Instance.Get("dedup.fallbackOriginal"); return; }
            await RefreshManifestsAsync();
            if (!SameScope() || _shutdownStarted) { StatusText.Text = UiText.Instance.Get("dedup.fallbackOriginal"); return; }
            await RefreshQueueAsync(); StatusText.Text = UiText.Instance.Get("dedup.fallbackDone");
        }
        catch (Exception) { StatusText.Text = UiText.Instance.Get(saved ? "dedup.fallbackSavedRefresh" : "dedup.fallbackFailed"); }
        finally { SetBusy(false); ApplyActionAvailability(); }
    }
}
