using System.Globalization;
using System.Windows;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Desktop;

public partial class MainWindow
{
    private void ApplyCacheActionAvailability()
    {
        if (ClearLocalCacheButton is null) return;
        ClearLocalCacheButton.IsEnabled = !_operationBusy && !_shutdownStarted &&
            _telegramSession?.CurrentAuthorizationState == "authorizationStateReady" &&
            _storageChannel is not null && ManifestList?.SelectedItem is ManifestItem item &&
            item.Manifest.Committed && item.Manifest.AccountId == _storageChannel.AccountId &&
            !item.RemoteStatusUnknown && !HasActiveTransfer(item.Manifest.FileId);
    }

    private async void ClearLocalCache_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy || _telegramSession is null || _storageChannel is null ||
            ManifestList.SelectedItem is not ManifestItem item || !item.Manifest.Committed ||
            item.Manifest.AccountId != _storageChannel.AccountId || item.RemoteStatusUnknown ||
            HasActiveTransfer(item.Manifest.FileId)) return;

        var message = string.Format(CultureInfo.CurrentCulture,
            UiText.Instance.Get("dialog.clearLocalCache.confirm"), item.Manifest.FileName);
        if (MessageBox.Show(this, message, UiText.Instance.Get("action.clearLocalCache"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        SetBusy(true, UiText.Instance.Get("bulk.working"));
        try
        {
            var purger = new LocalCachePurger(_manifestStore, _transferQueueStore,
                _localCacheVerificationStore, CreateTransport(_telegramSession, _storageChannel));
            await purger.PurgeAsync(item.Manifest.FileId, _storageChannel.AccountId,
                _observedRemoteManifestIds, [_stagingRoot], OperationToken);
            StatusText.Text = string.Format(CultureInfo.CurrentCulture,
                UiText.Instance.Get("status.localCacheCleared"), item.Manifest.FileName);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = UiText.Instance.Get("status.localCacheClearCanceled");
        }
        catch (Exception ex)
        {
            StatusText.Text = UiText.Instance.Get("action.clearLocalCache") + ": " +
                UiText.Instance.LocalizeMessage(ex.Message);
        }
        finally
        {
            try { await RefreshManifestsAsync(); }
            catch (Exception ex) { StatusText.Text = UiText.Instance.LocalizeMessage(ex.Message); }
            finally { SetBusy(false); }
        }
    }
}
