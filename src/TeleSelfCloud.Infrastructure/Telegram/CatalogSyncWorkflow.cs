using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Telegram;

public sealed record CatalogSyncResult(CatalogSyncAttempt Report, IReadOnlyList<FileManifest> Manifests, Exception? Error);

public sealed class CatalogSyncWorkflow(TelegramRemoteManifestCatalog catalog, CatalogSyncAttemptStore store,
    string accountId, long chatId, Func<CancellationToken, Task> publishFolders)
{
    public async Task<CatalogSyncResult> RunAsync(bool fullRescan, CancellationToken token)
    {
        using var lease = store.AcquireLease();
        CatalogSyncAttempt? previous;
        try { previous = await store.LoadAsync(token); }
        catch (InvalidDataException) when (fullRescan) { token.ThrowIfCancellationRequested(); store.PreserveInvalidReport(); previous = null; }
        var started = DateTimeOffset.UtcNow;
        var report = new CatalogSyncAttempt(1, accountId, chatId, fullRescan, false, CatalogSyncOutcome.Running,
            started, started, 0, 0, 0, 0, null, previous?.ObservedFileIds ?? []);
        await store.SaveAsync(report, token);
        IReadOnlyList<FileManifest> imported;
        try { imported = await catalog.ImportRecentAsync(token, fullRescan); }
        catch (Exception ex)
        {
            if (catalog.LastSuccessfulSyncUtc is not null)
            {
                report = Snapshot(report, CatalogSyncOutcome.FolderPublicationPending, !catalog.WasIncrementalSync);
                await store.SaveAsync(report, CancellationToken.None);
                return new(report, [], ex);
            }
            report = Snapshot(report, ex is OperationCanceledException ? CatalogSyncOutcome.Canceled : CatalogSyncOutcome.Failed, false);
            await store.SaveAsync(report, CancellationToken.None);
            return new(report, [], ex);
        }
        report = Snapshot(report, CatalogSyncOutcome.FolderPublicationPending, !catalog.WasIncrementalSync);
        // Catalog checkpoint is already committed; persist this boundary before outbound folder publication.
        await store.SaveAsync(report, CancellationToken.None);
        return await FinishPublicationAsync(report, imported, token);
    }

    public async Task<CatalogSyncResult> RetryFolderPublicationAsync(CancellationToken token)
    {
        using var lease = store.AcquireLease();
        var report = await store.LoadAsync(token);
        if (report?.Outcome != CatalogSyncOutcome.FolderPublicationPending)
            throw new InvalidOperationException("There is no pending folder publication. Sync the catalog again.");
        return await FinishPublicationAsync(report, [], token);
    }

    private async Task<CatalogSyncResult> FinishPublicationAsync(CatalogSyncAttempt report, IReadOnlyList<FileManifest> imported, CancellationToken token)
    {
        Exception? failure = null;
        try { token.ThrowIfCancellationRequested(); await publishFolders(token); report = report with { Outcome = CatalogSyncOutcome.Completed }; }
        catch (Exception ex) { failure = ex; }
        report = report with { UpdatedAtUtc = DateTimeOffset.UtcNow };
        await store.SaveAsync(report, CancellationToken.None);
        return new(report, imported, failure);
    }

    private CatalogSyncAttempt Snapshot(CatalogSyncAttempt previous, CatalogSyncOutcome outcome, bool replaceObservations) => previous with
    {
        WasIncremental = catalog.WasIncrementalSync, Outcome = outcome, UpdatedAtUtc = DateTimeOffset.UtcNow,
        Pages = catalog.PagesRead, Messages = catalog.MessagesRead, ManifestsFound = catalog.ManifestCaptionsFound,
        FilesIndexed = catalog.FilesIndexed, CatalogCompletedAtUtc = catalog.LastSuccessfulSyncUtc,
        ObservedFileIds = (replaceObservations ? catalog.ObservedFileIds : previous.ObservedFileIds.Concat(catalog.ObservedFileIds))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
    };
}
