using System.Globalization;
using System.IO;
using System.Windows;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Desktop;

public partial class MainWindow
{
    private bool _hasPendingFolderChange;
    private bool _restoreUploadAfterFolderReview;
    private long? _folderReviewChatId;

    private FolderOperationJournal CreateFolderOperationJournal(TelegramStorageChannelInfo channel)
    {
        var vaultRoot = GetVaultDataDirectory(channel);
        var journalDirectory = Path.Combine(vaultRoot, "folder-operations", channel.ChatId.ToString(CultureInfo.InvariantCulture));
        if (!LocalDatabaseProtection.IsConfigured(vaultRoot)) return new(journalDirectory);
        var lease = DatabaseProfileLease ?? throw new InvalidOperationException("The local profile must be open before folder recovery records can be accessed.");
        lease.RequireWithin(vaultRoot);
        return new(journalDirectory, identity => LocalDatabaseProtection.RecordCipher(vaultRoot,
            $"{channel.AccountId}:{channel.ChatId.ToString(CultureInfo.InvariantCulture)}:{identity}", "folder-journal"), lease);
    }

    private Task RefreshPendingFolderChangeAsync()
    {
        _hasPendingFolderChange = false;
        if (_storageChannel is not null && _activeTelegramAccountId == _storageChannel.AccountId)
            _hasPendingFolderChange = CreateFolderOperationJournal(_storageChannel).HasPending(_storageChannel.AccountId);
        ApplyFolderRecoveryActionAvailability();
        return Task.CompletedTask;
    }

    private void ApplyFolderRecoveryActionAvailability()
    {
        if (ResumeFolderChangeButton is null) return;
        var ready = _storageChannel is not null && _activeTelegramAccountId == _storageChannel.AccountId &&
            _telegramSession?.CurrentAuthorizationState == "authorizationStateReady";
        ResumeFolderChangeButton.Visibility = _hasPendingFolderChange && ready && !_isTrashPage ? Visibility.Visible : Visibility.Collapsed;
        ResumeFolderChangeButton.IsEnabled = _hasPendingFolderChange && ready && !_operationBusy && !_shutdownStarted;
        if (ReviewFolderChangeButton is not null && FolderRecoveryView is not null && FolderRecoveryHost is not null)
        {
            var scoped = _storageChannel is not null && _activeTelegramAccountId == _storageChannel.AccountId;
            ReviewFolderChangeButton.Visibility = _hasPendingFolderChange && scoped && !_isTrashPage ? Visibility.Visible : Visibility.Collapsed;
            ReviewFolderChangeButton.IsEnabled = _hasPendingFolderChange && scoped && !_operationBusy && !_shutdownStarted;
            FolderRecoveryView.SetCanAct(_hasPendingFolderChange && ready && !_operationBusy && !_shutdownStarted);
            if (!_hasPendingFolderChange || !scoped || _isTrashPage ||
                (FolderRecoveryHost.Visibility == Visibility.Visible &&
                    (FolderRecoveryView.Review?.Operation.AccountId != _storageChannel?.AccountId || _folderReviewChatId != _storageChannel?.ChatId))) CloseFolderReview();
        }
    }

    private async void ResumeFolderChange_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasPendingFolderChange || _operationBusy || _shutdownStarted || _telegramSession is null ||
            _storageChannel is null || _activeTelegramAccountId != _storageChannel.AccountId) return;
        var channel = _storageChannel;
        if (sender == FolderRecoveryView && (FolderRecoveryView.Review?.Operation.AccountId != channel.AccountId || _folderReviewChatId != channel.ChatId)) return;
        var service = CreateFolderManagementService(CreateTransport(_telegramSession, channel), channel);
        var expectedId = sender == FolderRecoveryView ? FolderRecoveryView.Review?.Operation.OperationId : null;
        await RunFolderOperationAsync(() => service.ResumePendingAsync(channel.AccountId, OperationToken, expectedId));
        await RefreshFolderReviewAfterActionAsync(channel);
    }

    private async void ReviewFolderChange_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy || _shutdownStarted || _storageChannel is null || _activeTelegramAccountId != _storageChannel.AccountId || !_hasPendingFolderChange) return;
        var channel = _storageChannel;
        SetBusy(true, UiText.Instance.Get("folder.review.loading"));
        try
        {
            var review = await CreateFolderReviewService(channel).ReviewPendingAsync(channel.AccountId, OperationToken);
            ManifestList.SelectedItems.Clear();
            _restoreUploadAfterFolderReview |= UploadToolbar.Visibility == Visibility.Visible;
            UploadToolbar.Visibility = Visibility.Collapsed;
            FolderRecoveryView.ShowReview(review);
            _folderReviewChatId = channel.ChatId;
            FolderRecoveryHost.Visibility = Visibility.Visible;
            FolderRecoveryView.RecoveryFiles.Focus();
        }
        catch (OperationCanceledException) { StatusText.Text = UiText.Instance.Get("folder.review.canceled"); }
        catch (Exception ex) { StatusText.Text = UiText.Instance.LocalizeMessage(ex.Message); }
        finally { SetBusy(false); }
    }

    private FolderManagementService CreateFolderReviewService(TelegramStorageChannelInfo channel) => new(
        _folderStore, _manifestStore, _transferQueueStore, (_, _) => throw new InvalidOperationException("Review cannot publish file metadata."),
        _ => throw new InvalidOperationException("Review cannot publish folder state."), CreateFolderOperationJournal(channel));

    private void CloseFolderReview_Click(object? sender, EventArgs e)
    {
        CloseFolderReview();
        ReviewFolderChangeButton.Focus();
    }

    private void CloseFolderReview()
    {
        if (FolderRecoveryHost is null) return;
        FolderRecoveryHost.Visibility = Visibility.Collapsed;
        if (_restoreUploadAfterFolderReview && FolderRecoveryView.Review?.Operation.AccountId == _activeTelegramAccountId)
            UploadToolbar.Visibility = Visibility.Visible;
        _restoreUploadAfterFolderReview = false;
        _folderReviewChatId = null;
    }

    private void RetryReviewedFolder_Click(object? sender, EventArgs e) => ResumeFolderChange_Click(sender ?? this, new RoutedEventArgs());

    private async void KeepFolderState_Click(object? sender, EventArgs e)
    {
        if (_operationBusy || _shutdownStarted || _telegramSession is null || _storageChannel is null ||
            _telegramSession.CurrentAuthorizationState != "authorizationStateReady" || _activeTelegramAccountId != _storageChannel.AccountId ||
            FolderRecoveryView.Review is not { } review) return;
        var channel = _storageChannel;
        if (review.Operation.AccountId != channel.AccountId || _folderReviewChatId != channel.ChatId) return;
        var confirmation = string.Format(CultureInfo.CurrentCulture, UiText.Instance.Get("folder.stop.confirm"),
            review.Operation.Source, review.Operation.AppliedFiles, review.Operation.Files.Count);
        if (MessageBox.Show(this, confirmation, UiText.Instance.Get("folder.stop.action"), MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var service = CreateFolderManagementService(CreateTransport(_telegramSession, channel), channel);
        await RunFolderOperationAsync(() => service.KeepCurrentStateAsync(channel.AccountId, review.Operation.OperationId, OperationToken));
        await RefreshFolderReviewAfterActionAsync(channel);
    }

    private async Task RefreshFolderReviewAfterActionAsync(TelegramStorageChannelInfo channel)
    {
        if (_shutdownStarted || _storageChannel != channel || FolderRecoveryHost.Visibility != Visibility.Visible) return;
        try
        {
            if (!CreateFolderOperationJournal(channel).HasPending(channel.AccountId)) { FolderRecoveryHost.Visibility = Visibility.Collapsed; return; }
            SetBusy(true, canCancel: false);
            FolderRecoveryView.ShowReview(await CreateFolderReviewService(channel).ReviewPendingAsync(channel.AccountId, CancellationToken.None));
        }
        catch (Exception ex) { StatusText.Text = UiText.Instance.LocalizeMessage(ex.Message); }
        finally { SetBusy(false); }
    }
}
