using System.Globalization;
using System.IO;
using System.Windows;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Desktop;

public partial class MainWindow
{
    private string? _reviewedMetadataFileId;
    private MetadataConflictArchive CreateMetadataConflictArchive(TelegramStorageChannelInfo channel)
    {
        var vaultRoot = GetVaultDataDirectory(channel);
        var directory = Path.Combine(vaultRoot, "metadata-history", channel.ChatId.ToString(CultureInfo.InvariantCulture));
        if (!LocalDatabaseProtection.IsConfigured(vaultRoot)) return new(directory, channel.AccountId, channel.ChatId);
        var lease = DatabaseProfileLease ?? throw new InvalidOperationException("The local profile must be open before metadata history can be accessed.");
        lease.RequireWithin(vaultRoot);
        return new(directory, channel.AccountId, channel.ChatId, identity => LocalDatabaseProtection.RecordCipher(vaultRoot,
            $"{channel.AccountId}:{channel.ChatId.ToString(CultureInfo.InvariantCulture)}:{identity}", "metadata-history"), lease);
    }

    private void ApplyMetadataActionAvailability()
    {
        if (ReviewMetadataButton is null || MetadataHistoryPanel is null) return;
        var item = SelectedManifestItem;
        var scoped = _storageChannel is not null && _activeTelegramAccountId == _storageChannel.AccountId &&
            item is not null && item.Manifest.Committed && item.Manifest.AccountId == _storageChannel.AccountId;
        ReviewMetadataButton.IsEnabled = scoped && !_operationBusy && !_shutdownStarted;
        if (_reviewedMetadataFileId is not null && (!scoped || _reviewedMetadataFileId != item!.Manifest.FileId)) CloseMetadataHistory();
        MetadataHistoryPanel.SetCanApply(scoped && !_operationBusy && !_shutdownStarted &&
            _telegramSession?.CurrentAuthorizationState == "authorizationStateReady" && !HasActiveTransfer(item!.Manifest.FileId));
    }

    private void CloseMetadataHistory()
    {
        _reviewedMetadataFileId = null;
        MetadataHistoryPanel.Visibility = Visibility.Collapsed;
    }

    private void CloseMetadataHistory_Click(object? sender, EventArgs e)
    {
        CloseMetadataHistory();
        ReviewMetadataButton.Focus();
    }

    private async void ReviewMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (!ReviewMetadataButton.IsEnabled || _storageChannel is null || SelectedManifestItem is not { } item) return;
        var channel = _storageChannel;
        SetBusy(true, UiText.Instance.Get("metadata.loading"));
        try
        {
            var history = await CreateMetadataConflictArchive(channel).LoadAsync(item.Manifest.FileId, OperationToken);
            _reviewedMetadataFileId = item.Manifest.FileId;
            MetadataHistoryPanel.ShowHistory(history, item.Manifest);
            MetadataHistoryPanel.Visibility = Visibility.Visible;
            MetadataHistoryPanel.VersionsList.Focus();
        }
        catch (OperationCanceledException) { StatusText.Text = UiText.Instance.Get("metadata.canceled"); }
        catch (Exception ex) { StatusText.Text = UiText.Instance.LocalizeMessage(ex.Message); }
        finally { SetBusy(false); }
    }

    private async void ApplyMetadataVersion_Click(object? sender, EventArgs e) => await ApplyMetadataVersionAsync(false);
    private async void PrepareMetadataVersion_Click(object? sender, EventArgs e) => await ApplyMetadataVersionAsync(true);

    private async Task ApplyMetadataVersionAsync(bool prepareNewRevision)
    {
        if (_operationBusy || _shutdownStarted || _telegramSession is null || _storageChannel is null ||
            _telegramSession.CurrentAuthorizationState != "authorizationStateReady" || _activeTelegramAccountId != _storageChannel.AccountId ||
            SelectedManifestItem is not { } item || item.Manifest.FileId != _reviewedMetadataFileId ||
            item.Manifest.AccountId != _storageChannel.AccountId || HasActiveTransfer(item.Manifest.FileId) ||
            MetadataHistoryPanel.SelectedFingerprint is not { } fingerprint) return;
        var channel = _storageChannel;
        var transport = CreateTransport(_telegramSession, channel);
        var resolver = new MetadataConflictResolver(CreateMetadataConflictArchive(channel), _manifestStore, _transferQueueStore,
            (manifest, token) => CreateManifestPublisher(transport, channel.AccountId).PublishCommittedAsync(manifest, token));
        SetBusy(true, UiText.Instance.Get("status.publishingManifestRevision"));
        try
        {
            var revised = await resolver.ApplyAsync(item.Manifest.FileId, fingerprint, OperationToken, prepareNewRevision);
            _observedRemoteManifestIds.Add(revised.FileId);
            await RefreshManifestsAsync();
            CloseMetadataHistory();
            StatusText.Text = UiText.Instance.Get("metadata.applied");
        }
        catch (OperationCanceledException) { StatusText.Text = UiText.Instance.Get("metadata.canceled"); }
        catch (Exception ex) { StatusText.Text = UiText.Instance.LocalizeMessage(ex.Message); }
        finally
        {
            try
            {
                if (_reviewedMetadataFileId == item.Manifest.FileId && !_shutdownStarted)
                {
                    var history = await CreateMetadataConflictArchive(channel).LoadAsync(item.Manifest.FileId, CancellationToken.None);
                    var current = await _manifestStore.LoadAsync(item.Manifest.FileId, CancellationToken.None);
                    if (current is not null) MetadataHistoryPanel.ShowHistory(history, current);
                }
            }
            catch (Exception ex) { StatusText.Text = UiText.Instance.LocalizeMessage(ex.Message); }
            finally { SetBusy(false); }
        }
    }
}
