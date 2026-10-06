using System.Security.Cryptography;
using System.Text;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

public sealed record AccountProfileMigrationResult(int MigratedManifestCount, int MigratedQueueItemCount, int MigratedCheckpointCount)
{
    public int RetainedManifestCount { get; init; }
}

/// <summary>Moves one account's local catalog and cache from the legacy shared profile into its account-owned stores.</summary>
public sealed class AccountProfileDataMigrator(
    SqliteManifestStore sharedManifestStore,
    SqliteManifestStore accountManifestStore,
    SqliteTransferQueueStore sharedQueueStore,
    SqliteTransferQueueStore accountQueueStore,
    SqliteRemoteSyncCheckpointStore sharedCheckpointStore,
    SqliteRemoteSyncCheckpointStore accountCheckpointStore,
    LocalCacheVerificationStore sharedCacheStore,
    string sharedStagingRoot,
    string accountStagingRoot,
    LocalStagingContentStore? sharedStagingContentStore = null,
    LocalStagingContentStore? accountStagingContentStore = null)
{
    public async Task<AccountProfileMigrationResult> MigrateAsync(string accountId, CancellationToken cancellationToken,
        long? storageChatId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (storageChatId == 0) throw new ArgumentOutOfRangeException(nameof(storageChatId));
        Directory.CreateDirectory(accountStagingRoot);
        var legacy = await sharedManifestStore.ListAsync(cancellationToken);
        var eligible = legacy.Where(manifest =>
            string.Equals(manifest.AccountId, accountId, StringComparison.Ordinal) ||
            (string.IsNullOrWhiteSpace(manifest.AccountId) && !manifest.Committed)).ToArray();
        var retainedCount = 0;
        if (storageChatId is { } targetChatId)
        {
            var target = await accountManifestStore.ListAsync(cancellationToken);
            var targetPlan = VaultLegacyPartitionPlanner.Create(target, accountId, targetChatId);
            if (target.Any(item => item.AccountId != accountId) || targetPlan.Manifests.Any(item =>
                    item.Disposition is not (VaultLegacyManifestDisposition.CurrentVault or VaultLegacyManifestDisposition.NoRemoteBinding)))
                throw new InvalidDataException("The migration destination contains another account or storage chat. Shared records were kept.");

            // Account-unbound drafts retain the established account assignment behavior, but remote/copy references
            // must still prove the primary chat. Ambiguous references remain recoverable in shared.
            var sourcePlan = VaultLegacyPartitionPlanner.Create(eligible.Select(item => item with { AccountId = accountId }), accountId, targetChatId);
            var drafts = eligible.Where(item => !item.Committed).Select(item => item.FileId).ToHashSet(StringComparer.Ordinal);
            var safeIds = sourcePlan.Manifests.Where(item => item.Disposition == VaultLegacyManifestDisposition.CurrentVault ||
                    (item.Disposition == VaultLegacyManifestDisposition.NoRemoteBinding && drafts.Contains(item.FileId)))
                .Select(item => item.FileId).ToHashSet(StringComparer.Ordinal);
            retainedCount = eligible.Length - safeIds.Count;
            eligible = eligible.Where(item => safeIds.Contains(item.FileId)).ToArray();
        }
        var migratedIds = new HashSet<string>(StringComparer.Ordinal);
        var copiedStagingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var manifest in eligible)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = await accountManifestStore.LoadAsync(manifest.FileId, cancellationToken);
            if (existing is not null &&
                (!string.Equals(existing.AccountId, accountId, StringComparison.Ordinal) ||
                 ManifestRevisionSelector.PortableFingerprint(existing) != ManifestRevisionSelector.PortableFingerprint(manifest)))
                throw new InvalidDataException("An existing file in the account profile differs from its shared copy. Both were kept; review the profile backup before retrying.");

            if (existing is null)
            {
                var parts = new List<PartRecord>(manifest.Parts.Count);
                foreach (var part in manifest.Parts)
                {
                    var destination = await CopyAndVerifyPartAsync(part, manifest.FileId, cancellationToken);
                    parts.Add(part with { StagingPath = destination });
                    if (destination is not null && !string.IsNullOrWhiteSpace(part.StagingPath))
                        copiedStagingPaths.Add(Path.GetFullPath(part.StagingPath));
                }
                var encryption = manifest.Encryption;
                if (encryption is not null)
                {
                    var encryptedPath = await CopyAndVerifyPayloadAsync(encryption, manifest.FileId, cancellationToken);
                    if (encryptedPath is not null && !string.IsNullOrWhiteSpace(encryption.StagingPath))
                        copiedStagingPaths.Add(Path.GetFullPath(encryption.StagingPath));
                    encryption = encryption with { StagingPath = encryptedPath };
                }
                var scoped = manifest with { AccountId = accountId, Parts = parts, Encryption = encryption };
                await accountManifestStore.SaveAsync(scoped, cancellationToken);
            }

            var durableCopy = await accountManifestStore.LoadAsync(manifest.FileId, cancellationToken);
            if (durableCopy is null || !string.Equals(durableCopy.AccountId, accountId, StringComparison.Ordinal) ||
                ManifestRevisionSelector.PortableFingerprint(durableCopy) != ManifestRevisionSelector.PortableFingerprint(manifest))
                throw new IOException("The account manifest copy could not be verified; its shared source was retained.");
            await VerifyRetainedStagingCoverageAsync(manifest, durableCopy, cancellationToken);
            migratedIds.Add(manifest.FileId);
        }

        var queueItems = (await sharedQueueStore.ListAsync(cancellationToken))
            .Where(item => migratedIds.Contains(item.FileId)).ToArray();
        await accountQueueStore.ImportItemsAsync(queueItems, cancellationToken);

        // A shared high-watermark does not prove a scoped destination has the complete catalog. Registered vaults
        // keep their own checkpoints, and shared ones stay intact so a later recovery can still rescan safely.
        var checkpoints = storageChatId is null
            ? await sharedCheckpointStore.ListAccountAsync(accountId, cancellationToken)
            : Array.Empty<(long ChatId, RemoteSyncCheckpoint Checkpoint)>();
        foreach (var (chatId, checkpoint) in checkpoints)
            await accountCheckpointStore.SaveAsync(accountId, chatId, checkpoint, cancellationToken);

        await accountQueueStore.RecoverInterruptedAsync(cancellationToken);
        await sharedCacheStore.RemoveManyAsync(migratedIds, cancellationToken);
        await sharedQueueStore.DeleteItemsForFilesAsync(migratedIds, cancellationToken);
        if (storageChatId is null) await sharedCheckpointStore.DeleteAccountAsync(accountId, cancellationToken);
        await sharedManifestStore.DeleteManyAsync(migratedIds, cancellationToken);
        await CleanupCopiedSourcePartsAsync(copiedStagingPaths, cancellationToken);

        return new AccountProfileMigrationResult(migratedIds.Count, queueItems.Length, checkpoints.Count)
            { RetainedManifestCount = retainedCount };
    }

    private async Task VerifyRetainedStagingCoverageAsync(FileManifest source, FileManifest target, CancellationToken token)
    {
        // A semantically identical destination is only a durable retry if it also retains every
        // staged byte still available in shared. Equal plaintext hashes alone prove neither fact.
        var targetParts = target.Parts.ToDictionary(part => part.Index);
        foreach (var part in source.Parts)
        {
            if (string.IsNullOrWhiteSpace(part.StagingPath) || !File.Exists(part.StagingPath)) continue;
            var destination = targetParts[part.Index];
            var identity = StagingFileIdentity.Part(source.FileId, part.Index);
            if (!await PartMatchesAsync(part.StagingPath, part, identity, sharedStagingContentStore, token) ||
                !IsOwnedDestinationStaging(destination.StagingPath) ||
                !await PartMatchesAsync(destination.StagingPath!, destination, identity, accountStagingContentStore, token))
                throw new InvalidDataException("The account manifest copy does not retain verified staging bytes; its shared source was kept.");
        }
        if (source.Encryption is { StagingPath: { } sourcePath } encryption && !string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
        {
            var destination = target.Encryption!;
            var identity = StagingFileIdentity.EncryptedPayload(source.FileId);
            if (!await PayloadMatchesAsync(sourcePath, encryption, identity, sharedStagingContentStore, token) ||
                !IsOwnedDestinationStaging(destination.StagingPath) ||
                !await PayloadMatchesAsync(destination.StagingPath!, destination, identity, accountStagingContentStore, token))
                throw new InvalidDataException("The account manifest copy does not retain the verified encrypted payload; its shared source was kept.");
        }
    }

    private bool IsOwnedDestinationStaging(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || LocalFileSystemPathGuard.ContainsReparsePoint(path)) return false;
        var targetRoot = Path.GetFullPath(accountStagingRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> CopyAndVerifyPartAsync(PartRecord part, string fileId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(part.StagingPath) || !File.Exists(part.StagingPath)) return null;
        var safeFileKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fileId)));
        var destinationBase = Path.Combine(accountStagingRoot, $"{safeFileKey}.{part.Index:D6}.part");
        var identity = StagingFileIdentity.Part(fileId, part.Index);
        Directory.CreateDirectory(accountStagingRoot);
        string destination = destinationBase;
        var suffix = 0;
        while (File.Exists(destination) && !await PartMatchesAsync(destination, part, identity, accountStagingContentStore, cancellationToken))
        {
            suffix++;
            destination = Path.Combine(accountStagingRoot, $"{safeFileKey}.{part.Index:D6}.{suffix}.part");
        }

        if (!File.Exists(destination) &&
            !string.Equals(Path.GetFullPath(part.StagingPath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            var temporary = destination + $".{Guid.NewGuid():N}.copying";
            try
            {
                await using var materialized = sharedStagingContentStore is null ? null : await sharedStagingContentStore.MaterializeAsync(
                    part.StagingPath, identity, cancellationToken);
                await using var direct = materialized is null
                    ? new FileStream(part.StagingPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true)
                    : null;
                var source = materialized?.Stream ?? direct!;
                await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                {
                    await source.CopyToAsync(target, cancellationToken);
                    await target.FlushAsync(cancellationToken); target.Flush(flushToDisk: true);
                }
                if (!await PartMatchesAsync(temporary, part, identity, null, cancellationToken))
                    throw new InvalidDataException($"A staged part failed its integrity check during account migration ({part.Index}). The shared source was retained.");
                if (accountStagingContentStore is not null)
                    await accountStagingContentStore.ProtectInPlaceAsync(temporary, identity, part.Length, part.Sha256, cancellationToken);
                File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        if (await PartMatchesAsync(destination, part, identity, accountStagingContentStore, cancellationToken)) return destination;
        throw new InvalidDataException($"A staged part failed its integrity check during account migration ({part.Index}). The shared source was retained.");
    }

    private async Task<string?> CopyAndVerifyPayloadAsync(
        EncryptedPayloadDescriptor descriptor, string fileId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(descriptor.StagingPath) || !File.Exists(descriptor.StagingPath)) return null;
        var safeFileKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fileId)));
        var destination = Path.Combine(accountStagingRoot, $"{safeFileKey}.encrypted-payload.bin");
        var identity = StagingFileIdentity.EncryptedPayload(fileId);
        Directory.CreateDirectory(accountStagingRoot);
        var suffix = 0;
        while (File.Exists(destination) && !await PayloadMatchesAsync(destination, descriptor, identity, accountStagingContentStore, cancellationToken))
            destination = Path.Combine(accountStagingRoot, $"{safeFileKey}.encrypted-payload.{++suffix}.bin");
        if (!File.Exists(destination) &&
            !string.Equals(Path.GetFullPath(descriptor.StagingPath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            var temporary = destination + $".{Guid.NewGuid():N}.copying";
            try
            {
                await using var materialized = sharedStagingContentStore is null ? null : await sharedStagingContentStore.MaterializeAsync(
                    descriptor.StagingPath, identity, cancellationToken);
                await using var direct = materialized is null
                    ? new FileStream(descriptor.StagingPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true)
                    : null;
                var source = materialized?.Stream ?? direct!;
                await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                {
                    await source.CopyToAsync(target, cancellationToken);
                    await target.FlushAsync(cancellationToken); target.Flush(flushToDisk: true);
                }
                if (!await PayloadMatchesAsync(temporary, descriptor, identity, null, cancellationToken))
                    throw new InvalidDataException("The encrypted staging payload failed integrity verification during account migration; its shared source was retained.");
                if (accountStagingContentStore is not null)
                    await accountStagingContentStore.ProtectInPlaceAsync(temporary, identity, descriptor.PayloadSize, descriptor.PayloadSha256, cancellationToken);
                File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        if (await PayloadMatchesAsync(destination, descriptor, identity, accountStagingContentStore, cancellationToken)) return destination;
        throw new InvalidDataException("The encrypted staging payload failed integrity verification during account migration; its shared source was retained.");
    }

    private static async Task<bool> PayloadMatchesAsync(
        string path, EncryptedPayloadDescriptor descriptor, string identity, LocalStagingContentStore? contentStore,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        try
        {
            await using var materialized = contentStore is null ? null : await contentStore.MaterializeAsync(path, identity, cancellationToken);
            await using var direct = materialized is null ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true) : null;
            var stream = materialized?.Stream ?? direct!;
            if (stream.Length != descriptor.PayloadSize) return false;
            return ManifestValidator.HashMatches(descriptor.PayloadSha256, await SHA256.HashDataAsync(stream, cancellationToken));
        }
        catch (IOException) { return false; }
        catch (InvalidDataException) { return false; }
    }

    private static async Task<bool> PartMatchesAsync(string path, PartRecord part, string identity,
        LocalStagingContentStore? contentStore, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        try
        {
            await using var materialized = contentStore is null ? null : await contentStore.MaterializeAsync(path, identity, cancellationToken);
            await using var direct = materialized is null ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true) : null;
            var stream = materialized?.Stream ?? direct!;
            return stream.Length == part.Length &&
                   string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)), part.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (InvalidDataException) { return false; }
    }

    private async Task CleanupCopiedSourcePartsAsync(HashSet<string> copiedPaths, CancellationToken cancellationToken)
    {
        if (copiedPaths.Count == 0) return;
        var remaining = await sharedManifestStore.ListAsync(cancellationToken);
        var stillReferenced = remaining.SelectMany(manifest =>
                manifest.Parts.Where(part => !string.IsNullOrWhiteSpace(part.StagingPath)).Select(part => Path.GetFullPath(part.StagingPath!))
                    .Append(manifest.Encryption?.StagingPath).Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Path.GetFullPath(path!)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sharedRoot = Path.GetFullPath(sharedStagingRoot) + Path.DirectorySeparatorChar;
        foreach (var path in copiedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stillReferenced.Contains(path) || !path.StartsWith(sharedRoot, StringComparison.OrdinalIgnoreCase) ||
                LocalFileSystemPathGuard.ContainsReparsePoint(path)) continue;
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
