using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Telegram;

namespace TeleSelfCloud.Infrastructure.Transfers;

public sealed class PermanentFileDeletion(
    IManifestStore manifestStore,
    ITransferQueueStore queueStore,
    TelegramRemoteFileDeleter remoteDeleter) : IPermanentFileDeletion
{
    public async Task<IReadOnlyList<PermanentFileDeletionResult>> ExecuteAsync(
        IEnumerable<string> fileIds, string accountId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        var ids = fileIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length is 0 or > 100) throw new ArgumentOutOfRangeException(nameof(fileIds), "Select between 1 and 100 Trash items.");
        var results = new List<PermanentFileDeletionResult>(ids.Length);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remoteDeleted = false;
            try
            {
                var manifest = await manifestStore.LoadAsync(id, cancellationToken)
                    ?? throw new FileNotFoundException("The selected manifest does not exist.", id);
                if (!manifest.Committed || !manifest.IsInTrash || !string.Equals(manifest.FileId, id, StringComparison.Ordinal) ||
                    !string.Equals(manifest.AccountId, accountId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Only a committed Trash item owned by the active account can be deleted.");
                var activeTransfers = (await queueStore.ListAsync(cancellationToken))
                    .Where(item => item.FileId == id && item.State is TransferQueueState.Pending or TransferQueueState.Running or TransferQueueState.Paused)
                    .ToArray();
                if (activeTransfers.Length > 0)
                    throw new InvalidOperationException("Cancel or pause and remove this file's queued transfers before deleting it.");

                await remoteDeleter.DeleteTrashedFileAsync(manifest, cancellationToken);
                remoteDeleted = true;
                await queueStore.DeleteItemsForFilesAsync(new[] { id }, cancellationToken);
                await manifestStore.DeleteManyAsync(new[] { id }, cancellationToken);
                results.Add(new PermanentFileDeletionResult(id, true, true, null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { results.Add(new PermanentFileDeletionResult(id, false, remoteDeleted, ex.Message)); }
        }
        return results;
    }
}
