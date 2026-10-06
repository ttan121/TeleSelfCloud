using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

public enum FolderRecoveryFileState { Before, After, Changed, Missing, OtherAccount }
public sealed record FolderRecoveryFile(string FileId, string FileName, FolderRecoveryFileState State, string? CurrentFolder, long? CurrentRevision);
public sealed record FolderOperationReview(PendingFolderOperation Operation, IReadOnlyList<FolderRecoveryFile> Files, int NewFiles);

public sealed class FolderManagementService(
    ILocalFolderStore folderStore, IManifestStore manifestStore, ITransferQueueStore queueStore,
    Func<FileManifest, CancellationToken, Task> publishManifest,
    Func<CancellationToken, Task> publishFolderState, FolderOperationJournal journal)
{
    public Task<FolderOperationResult> RenameAsync(string accountId, string path, string newName, CancellationToken token)
    {
        var source = FileManifestMetadata.NormalizeFolderPath(path);
        var name = ValidateName(newName);
        var parent = source.Contains('/') ? source[..source.LastIndexOf('/')] : string.Empty;
        return StartAsync(accountId, source, parent.Length == 0 ? name : parent + "/" + name, false, token);
    }

    public Task<FolderOperationResult> DeleteAsync(string accountId, string path, CancellationToken token)
    {
        var source = FileManifestMetadata.NormalizeFolderPath(path);
        var parent = source.Contains('/') ? source[..source.LastIndexOf('/')] : string.Empty;
        return StartAsync(accountId, source, parent, true, token);
    }

    public async Task<FolderOperationResult> ResumePendingAsync(string accountId, CancellationToken token, string? expectedOperationId = null)
    {
        using var lease = journal.AcquireLease(accountId);
        var pending = await journal.LoadAsync(accountId, token)
            ?? throw new InvalidOperationException("There is no pending folder change for this account and vault.");
        if (expectedOperationId is not null && pending.OperationId != expectedOperationId)
            throw new InvalidOperationException("The pending folder plan changed. Review it again before continuing.");
        return await ContinueAsync(pending, token);
    }

    public async Task<FolderOperationReview> ReviewPendingAsync(string accountId, CancellationToken token)
    {
        using var lease = journal.AcquireLease(accountId);
        var pending = await journal.LoadAsync(accountId, token)
            ?? throw new InvalidOperationException("There is no pending folder change for this account and vault.");
        var files = new List<FolderRecoveryFile>();
        foreach (var change in pending.Files)
        {
            var current = await manifestStore.LoadAsync(change.Before.FileId, token);
            var state = current is null ? FolderRecoveryFileState.Missing : current.AccountId != accountId ? FolderRecoveryFileState.OtherAccount :
                FolderOperationJournal.SamePortableManifest(current, change.After) ? FolderRecoveryFileState.After :
                FolderOperationJournal.SamePortableManifest(current, change.Before) ? FolderRecoveryFileState.Before : FolderRecoveryFileState.Changed;
            files.Add(new(change.Before.FileId, current?.AccountId == accountId ? current.FileName : change.Before.FileName, state,
                current?.AccountId == accountId ? current.FolderPath : null, current?.AccountId == accountId ? current.Revision : null));
        }
        var ids = pending.Files.Select(change => change.Before.FileId).ToHashSet(StringComparer.Ordinal);
        var newFiles = (await manifestStore.ListAsync(token)).Count(file => file.AccountId == accountId && !ids.Contains(file.FileId) &&
            (FolderOperationJournal.IsSubpath(file.FolderPath, pending.Source) ||
                (!pending.Delete && FolderOperationJournal.IsSubpath(file.FolderPath, pending.Destination))));
        return new(pending, files, newFiles);
    }

    public async Task<FolderOperationResult> KeepCurrentStateAsync(string accountId, string expectedOperationId, CancellationToken token)
    {
        using var lease = journal.AcquireLease(accountId);
        var pending = await journal.LoadAsync(accountId, token)
            ?? throw new InvalidOperationException("There is no pending folder change for this account and vault.");
        if (pending.OperationId != expectedOperationId)
            throw new InvalidOperationException("The pending folder plan changed. Review it again before stopping remaining steps.");
        if (pending.StopRequestedAtUtc is null)
        {
            pending = pending with { StopRequestedAtUtc = DateTimeOffset.UtcNow };
            await journal.SaveAsync(pending, token);
        }
        return await FinishStopAsync(pending, token);
    }

    private async Task<FolderOperationResult> FinishStopAsync(PendingFolderOperation pending, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            await publishFolderState(token);
            // Once publication was acknowledged, persist the decision despite late cancellation.
            await journal.ArchiveStoppedAsync(pending, CancellationToken.None);
            journal.Complete(pending.AccountId);
            return new(pending.Source, pending.AppliedFiles, true, null, KeptCurrentState: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new(pending.Source, pending.AppliedFiles, false, ex.Message, KeptCurrentState: true); }
    }

    private async Task<FolderOperationResult> StartAsync(string accountId, string source, string destination, bool delete, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (source.Length == 0) throw new InvalidOperationException("The storage root cannot be changed.");
        if (!delete && destination.StartsWith(source + "/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A folder cannot be moved into its own subtree.");
        using var lease = journal.AcquireLease(accountId);
        var pending = await journal.LoadAsync(accountId, token);
        if (pending is not null)
        {
            if (pending.Source != source || pending.Destination != destination || pending.Delete != delete)
                throw new InvalidOperationException("Finish the pending folder change before starting another one.");
            return await ContinueAsync(pending, token);
        }
        var folders = await folderStore.ListAsync(accountId, token);
        var allFiles = await manifestStore.ListAsync(token);
        var files = allFiles.Where(file => file.AccountId == accountId && FolderOperationJournal.IsSubpath(file.FolderPath, source)).ToArray();
        if (!folders.Any(folder => string.Equals(folder.Path, source, StringComparison.OrdinalIgnoreCase)) && files.Length == 0)
            throw new KeyNotFoundException("The selected folder no longer exists.");
        if (!delete && source == destination) return new FolderOperationResult(destination, 0, true, null);
        if (!delete && (folders.Any(folder => FolderOperationJournal.IsSubpath(folder.Path, destination) && !FolderOperationJournal.IsSubpath(folder.Path, source)) ||
            allFiles.Any(file => file.AccountId == accountId && FolderOperationJournal.IsSubpath(file.FolderPath, destination) && !FolderOperationJournal.IsSubpath(file.FolderPath, source))))
            throw new InvalidOperationException("A folder or file already exists at the destination.");
        await EnsureNoActiveTransfersAsync(files, token);
        var changes = files.OrderBy(file => file.FileId, StringComparer.Ordinal).Select(file =>
        {
            var target = delete ? destination : destination + file.FolderPath[source.Length..];
            return new FolderFileChange(file, file.Committed ? FileManifestMetadata.Move(file, target) : file with { FolderPath = target });
        }).ToArray();
        pending = new PendingFolderOperation(1, Guid.NewGuid().ToString("N"), accountId, source, destination, delete,
            folders.Where(folder => FolderOperationJournal.IsSubpath(folder.Path, source)).Select(folder => folder.Path)
                .Append(source).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), changes, 0, false, DateTimeOffset.UtcNow,
            !folders.Any(folder => string.Equals(folder.Path, source, StringComparison.OrdinalIgnoreCase)));
        // Store the immutable plan before publishing or changing local metadata.
        await journal.SaveAsync(pending, token);
        return await ContinueAsync(pending, token);
    }

    private async Task<FolderOperationResult> ContinueAsync(PendingFolderOperation pending, CancellationToken token)
    {
        if (pending.StopRequestedAtUtc is not null) return await FinishStopAsync(pending, token);
        var currentFiles = new List<FileManifest>();
        foreach (var change in pending.Files)
        {
            var current = await manifestStore.LoadAsync(change.Before.FileId, token)
                ?? throw new InvalidOperationException("A file in the pending folder change is missing. Its recovery plan was kept.");
            if (!FolderOperationJournal.SamePortableManifest(current, change.Before) && !FolderOperationJournal.SamePortableManifest(current, change.After))
                throw new InvalidOperationException("A file changed while its folder operation was pending. Sync and resolve the conflict before retrying.");
            currentFiles.Add(current);
        }
        if (currentFiles.Take(pending.AppliedFiles).Where((file, index) =>
            !FolderOperationJournal.SamePortableManifest(file, pending.Files[index].After)).Any())
            throw new InvalidOperationException("A file changed while its folder operation was pending. Sync and resolve the conflict before retrying.");
        var plannedIds = pending.Files.Select(change => change.Before.FileId).ToHashSet(StringComparer.Ordinal);
        if ((await manifestStore.ListAsync(token)).Any(file => file.AccountId == pending.AccountId && !plannedIds.Contains(file.FileId) &&
            (FolderOperationJournal.IsSubpath(file.FolderPath, pending.Source) ||
             (!pending.Delete && FolderOperationJournal.IsSubpath(file.FolderPath, pending.Destination)))))
            throw new InvalidOperationException("The folder contents changed while its operation was pending. Sync and resolve the conflict before retrying.");
        await EnsureNoActiveTransfersAsync(currentFiles, token);
        var folders = await folderStore.ListAsync(pending.AccountId, token);
        var hasSource = folders.Any(folder => string.Equals(folder.Path, pending.Source, StringComparison.OrdinalIgnoreCase));
        var tombstones = pending.Delete ? await folderStore.ListTombstonesAsync(pending.AccountId, token) : [];
        var alreadyApplied = !hasSource && (pending.Delete
            ? pending.FolderPaths.All(path => tombstones.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
            : pending.FolderPaths.Select(path => pending.Destination + path[pending.Source.Length..])
                .All(path => folders.Any(folder => string.Equals(folder.Path, path, StringComparison.OrdinalIgnoreCase))));
        if ((pending.FolderApplied && !alreadyApplied) || (!alreadyApplied &&
            ((!pending.Delete && folders.Any(folder => FolderOperationJournal.IsSubpath(folder.Path, pending.Destination) &&
                !FolderOperationJournal.IsSubpath(folder.Path, pending.Source))) ||
             folders.Any(folder => FolderOperationJournal.IsSubpath(folder.Path, pending.Source) &&
                 !pending.FolderPaths.Contains(folder.Path, StringComparer.OrdinalIgnoreCase)) ||
             (!pending.SourceInferred && pending.FolderPaths.Any(path => !folders.Any(folder => string.Equals(folder.Path, path, StringComparison.OrdinalIgnoreCase)))))))
            throw new InvalidOperationException("The folder contents changed while its operation was pending. Sync and resolve the conflict before retrying.");
        var published = pending.Files.Take(pending.AppliedFiles).Count(change => change.After.Committed);
        try
        {
            for (var i = pending.AppliedFiles; i < pending.Files.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var change = pending.Files[i];
                var current = currentFiles[i];
                if (!FolderOperationJournal.SamePortableManifest(current, change.After))
                {
                    var after = change.After with { Parts = current.Parts, Encryption = current.Encryption };
                    if (after.Committed) { await publishManifest(after, token); published++; }
                    await manifestStore.SaveAsync(after, CancellationToken.None);
                }
                pending = pending with { AppliedFiles = i + 1 };
                await journal.SaveAsync(pending, CancellationToken.None);
            }
            token.ThrowIfCancellationRequested();
            if (!pending.FolderApplied)
            {
                if (!alreadyApplied && !hasSource && pending.SourceInferred)
                { await folderStore.CreateAsync(pending.AccountId, pending.Source, token); hasSource = true; }
                if (hasSource)
                {
                    if (pending.Delete) await folderStore.DeleteAsync(pending.AccountId, pending.Source, token);
                    else await folderStore.RenameAsync(pending.AccountId, pending.Source, pending.Destination, token);
                }
                pending = pending with { FolderApplied = true };
                await journal.SaveAsync(pending, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw new FolderOperationException("The folder change is incomplete. Its saved recovery plan can be resumed.", published, ex); }
        try
        {
            await publishFolderState(token);
            journal.Complete(pending.AccountId);
            return new FolderOperationResult(pending.Delete ? pending.Source : pending.Destination, pending.Files.Count, true, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new FolderOperationResult(pending.Delete ? pending.Source : pending.Destination, pending.Files.Count, false, ex.Message); }
    }

    private async Task EnsureNoActiveTransfersAsync(IReadOnlyList<FileManifest> files, CancellationToken token)
    {
        var ids = files.Select(file => file.FileId).ToHashSet(StringComparer.Ordinal);
        if ((await queueStore.ListAsync(token)).Any(item => ids.Contains(item.FileId) &&
            item.State is TransferQueueState.Pending or TransferQueueState.Running or TransferQueueState.Paused))
            throw new InvalidOperationException("Cancel and remove active transfers before changing their folder.");
    }

    private static string ValidateName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var name = value.Trim();
        if (name.Length == 0 || name is "." or ".." || name.Contains('/') || name.Contains('\\') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Enter a valid folder name without a path.", nameof(value));
        return name;
    }
}
