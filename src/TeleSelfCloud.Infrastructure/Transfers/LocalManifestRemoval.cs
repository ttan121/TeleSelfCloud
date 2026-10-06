using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

public sealed class LocalManifestRemoval(
    IManifestStore manifestStore,
    ITransferQueueStore queueStore,
    LocalCacheVerificationStore cacheVerificationStore)
{
    public async Task RemoveAsync(IEnumerable<string> fileIds, string accountId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        var ids = fileIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length is 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(fileIds), "Select between 1 and 100 local entries.");

        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = await manifestStore.LoadAsync(id, cancellationToken)
                ?? throw new FileNotFoundException("The selected local manifest does not exist.", id);
            if (!manifest.Committed || !string.Equals(manifest.FileId, id, StringComparison.Ordinal) ||
                !string.Equals(manifest.AccountId, accountId, StringComparison.Ordinal))
                throw new InvalidOperationException("Only committed entries owned by the selected account can be removed locally.");
        }

        var activeFileIds = (await queueStore.ListAsync(cancellationToken))
            .Where(item => ids.Contains(item.FileId, StringComparer.Ordinal) &&
                item.State is TransferQueueState.Pending or TransferQueueState.Running or TransferQueueState.Paused)
            .Select(item => item.FileId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (activeFileIds.Length > 0)
            throw new InvalidOperationException("Cancel or pause and remove this file's queued transfers before removing it locally.");

        await queueStore.DeleteItemsForFilesAsync(ids, cancellationToken);
        await cacheVerificationStore.RemoveManyAsync(ids, cancellationToken);
        await manifestStore.DeleteManyAsync(ids, cancellationToken);
    }
}
