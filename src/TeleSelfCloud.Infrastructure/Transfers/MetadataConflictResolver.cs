using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

public sealed class MetadataConflictResolver(MetadataConflictArchive archive, IManifestStore manifests,
    ITransferQueueStore queue, Func<FileManifest, CancellationToken, Task> publish)
{
    public async Task<FileManifest> ApplyAsync(string fileId, string selectedFingerprint, CancellationToken token, bool prepareNewRevision = false)
    {
        using var lease = archive.AcquireLease(fileId);
        var history = await archive.LoadAsync(fileId, token) ?? throw new InvalidOperationException("No competing metadata versions were saved for this file.");
        var selected = history.Versions.SingleOrDefault(version => ManifestRevisionSelector.PortableFingerprint(version) == selectedFingerprint)
            ?? throw new InvalidOperationException("The selected metadata version is no longer available. Refresh the versions and retry.");
        var current = await manifests.LoadAsync(fileId, token) ?? throw new FileNotFoundException("The selected manifest does not exist.");
        archive.ValidateManifest(current, fileId);
        _ = ManifestRevisionSelector.PreferNewest(current, selected);
        if ((await queue.ListAsync(token)).Any(item => item.FileId == fileId && item.State is TransferQueueState.Pending or TransferQueueState.Running or TransferQueueState.Paused))
            throw new InvalidOperationException("This file has an unfinished transfer. Finish or cancel it before applying a metadata version.");
        var previousRevision = history.Pending?.After.Revision ?? 0;
        if (prepareNewRevision && history.Pending is { } superseded)
            history = history with { Pending = null, SupersededPlans = (history.SupersededPlans ?? []).Append(superseded).ToArray() };
        if (history.Pending is { } pending)
        {
            if (pending.SelectedFingerprint != selectedFingerprint)
                throw new InvalidOperationException("A metadata resolution is pending. Retry the saved selection before choosing another version.");
            var fingerprint = ManifestRevisionSelector.PortableFingerprint(current);
            if (fingerprint != ManifestRevisionSelector.PortableFingerprint(pending.Before) && fingerprint != ManifestRevisionSelector.PortableFingerprint(pending.After))
                throw new InvalidOperationException("The file changed after metadata resolution started. Its plan was kept; review the new metadata before retrying.");
        }
        else
        {
            var revised = current with
            {
                FileName = selected.FileName, FolderPath = selected.FolderPath, IsInTrash = selected.IsInTrash,
                IsFavorite = selected.IsFavorite, IsArchived = selected.IsArchived, IsHidden = selected.IsHidden,
                FileModifiedAtUtc = selected.FileModifiedAtUtc,
                Revision = checked(Math.Max(previousRevision, Math.Max(current.Revision,
                    Math.Max(history.SupersededPlans?.Select(plan => plan.After.Revision).DefaultIfEmpty(0).Max() ?? 0, history.Versions.Max(version => version.Revision)))) + 1),
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            history = history with { Pending = new(archive.Portable(current), archive.Portable(revised), selectedFingerprint) };
            await archive.SaveAsync(history, token);
        }
        var plan = history.Pending!;
        var after = ManifestRevisionSelector.PreferNewest(current, plan.After);
        token.ThrowIfCancellationRequested();
        if (ManifestRevisionSelector.PortableFingerprint(current) != ManifestRevisionSelector.PortableFingerprint(plan.After))
            await publish(plan.After, token);
        // Persist acceptance even if cancellation arrives after the publish adapter returns.
        await manifests.SaveAsync(after, CancellationToken.None);
        await archive.SaveAsync(history with { Pending = null }, CancellationToken.None);
        return after;
    }
}
