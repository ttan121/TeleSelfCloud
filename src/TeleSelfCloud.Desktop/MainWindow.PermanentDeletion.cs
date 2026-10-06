using System.Text.Json;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Desktop;

public partial class MainWindow
{
    private sealed record PermanentDeletionScope(IManifestStore Store, ITransferQueueStore Queue,
        TelegramAuthSession? Session, TelegramStorageChannelInfo Channel, LocalProfileLease Lease,
        string Root, long KeyGeneration, VaultMetadataKey? Key, FileManifest[] Manifests) : IDisposable
    {
        public void Dispose() => Key?.Dispose();
    }

    private PermanentDeletionScope? CapturePermanentDeletionScope(FileManifest[] selected)
    {
        if (_operationBusy || _shutdownStarted || DatabaseProfileLease is not { } lease ||
            _storageChannel is not { } channel || channel.AccountId != _activeTelegramAccountId || selected.Length is 0 or > 100 ||
            selected.Any(m => !m.Committed || !m.IsInTrash || m.AccountId != channel.AccountId) ||
            selected.Select(m => m.FileId).Distinct(StringComparer.Ordinal).Count() != selected.Length) return null;
        lease.RequireWithin(LocalDatabaseRoot);
        return new(_manifestStore, _transferQueueStore, _telegramSession, channel, lease, LocalDatabaseRoot,
            _metadataKeyGeneration, MetadataKeyForPublishing()?.Clone(), selected);
    }

    private bool PermanentDeletionScopeMatches(PermanentDeletionScope scope) => !_shutdownStarted &&
        ReferenceEquals(scope.Store, _manifestStore) && ReferenceEquals(scope.Queue, _transferQueueStore) &&
        ReferenceEquals(scope.Session, _telegramSession) && ReferenceEquals(scope.Lease, DatabaseProfileLease) &&
        (scope.Session is null || scope.Session.CurrentAuthorizationState == "authorizationStateReady") &&
        scope.Root == LocalDatabaseRoot && scope.Channel.AccountId == _activeTelegramAccountId &&
        _storageChannel?.AccountId == scope.Channel.AccountId && _storageChannel.ChatId == scope.Channel.ChatId &&
        scope.KeyGeneration == _metadataKeyGeneration;

    // Called only after the user confirms. All writes and requests remain bound to the captured vault.
    private async Task<IReadOnlyList<PermanentFileDeletionResult>?> RunPermanentDeletionAsync(PermanentDeletionScope scope,
        Func<VaultMetadataKey?, CancellationToken, Task<IReadOnlyList<PermanentFileDeletionResult>>> action)
    {
        if (_operationBusy || !PermanentDeletionScopeMatches(scope))
        { StatusText.Text = UiText.Instance.Get("trash.deleteForever.scopeChanged"); return null; }
        SetBusy(true, UiText.Instance.Get("bulk.working"), canCancel: false);
        try
        {
            scope.Lease.RequireWithin(scope.Root);
            var tasks = await scope.Queue.ListAsync(OperationToken);
            if (tasks.Any(t => t.State == TransferQueueState.Running) || !PermanentDeletionScopeMatches(scope))
            { StatusText.Text = UiText.Instance.Get("trash.deleteForever.scopeChanged"); return null; }
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            foreach (var selected in scope.Manifests)
            {
                var current = await scope.Store.LoadAsync(selected.FileId, OperationToken);
                if (current is null || !JsonSerializer.SerializeToUtf8Bytes(selected, options).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(current, options)) ||
                    !PermanentDeletionScopeMatches(scope))
                { StatusText.Text = UiText.Instance.Get("trash.deleteForever.scopeChanged"); return null; }
            }
            var results = await action(scope.Key, OperationToken);
            if (!PermanentDeletionScopeMatches(scope))
            { StatusText.Text = UiText.Instance.Get("trash.deleteForever.original"); return null; }
            await RefreshManifestsAsync();
            if (!PermanentDeletionScopeMatches(scope))
            { StatusText.Text = UiText.Instance.Get("trash.deleteForever.original"); return null; }
            StatusText.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, UiText.Instance.Get("trash.deleteForever.done"), results.Count(r => r.Succeeded), results.Count);
            return results;
        }
        catch (Exception)
        {
            StatusText.Text = UiText.Instance.Get("trash.deleteForever.failed"); return null;
        }
        finally { SetBusy(false); }
    }
}
