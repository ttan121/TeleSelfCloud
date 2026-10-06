using System.Security.Cryptography;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

/// <summary>Removes a committed file's local staged parts only after every remote part is reverified.</summary>
public sealed class LocalCachePurger(
    IManifestStore manifestStore,
    ITransferQueueStore queueStore,
    LocalCacheVerificationStore cacheVerificationStore,
    IPartTransport transport)
{
    public async Task PurgeAsync(
        string fileId, string accountId, IReadOnlySet<string> observedRemoteFileIds,
        IEnumerable<string> allowedStagingRoots, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(observedRemoteFileIds);
        ArgumentNullException.ThrowIfNull(allowedStagingRoots);

        var manifest = await manifestStore.LoadAsync(fileId, cancellationToken)
            ?? throw new FileNotFoundException("The selected manifest does not exist.", fileId);
        ManifestValidator.ValidateStructure(manifest);
        if (!manifest.Committed || !string.Equals(manifest.AccountId, accountId, StringComparison.Ordinal))
            throw new InvalidOperationException("Only a committed manifest owned by this account can have its local cache cleared.");
        if (!observedRemoteFileIds.Contains(fileId))
            throw new InvalidOperationException("Sync the storage channel and confirm this file exists remotely before clearing its local cache.");

        var active = (await queueStore.ListAsync(cancellationToken)).Any(item =>
            item.FileId == fileId && item.State is TransferQueueState.Pending or TransferQueueState.Running or TransferQueueState.Paused);
        if (active) throw new InvalidOperationException("Pause or remove this file's active transfers before clearing its local cache.");

        var roots = allowedStagingRoots.Select(Path.GetFullPath)
            .Select(path => Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar).ToArray();
        if (roots.Length == 0) throw new ArgumentException("At least one approved staging root is required.", nameof(allowedStagingRoots));
        foreach (var root in roots) RejectReparsePoints(root);

        var partPaths = manifest.Parts.Where(part => !string.IsNullOrWhiteSpace(part.StagingPath))
            .Select(part => Path.GetFullPath(part.StagingPath!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var encryptedPath = string.IsNullOrWhiteSpace(manifest.Encryption?.StagingPath)
            ? null : Path.GetFullPath(manifest.Encryption.StagingPath);
        foreach (var path in encryptedPath is null ? partPaths : partPaths.Append(encryptedPath))
        {
            if (!roots.Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A local cache path is outside the approved staging directories; no files were removed.");
            RejectReparsePoints(path);
        }

        foreach (var part in manifest.Parts.OrderBy(part => part.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!part.Confirmed || string.IsNullOrWhiteSpace(part.RemoteId))
                throw new InvalidDataException("A committed remote part reference is missing; local cache was kept.");
            await using var remote = await transport.DownloadPartAsync(part.RemoteId, cancellationToken);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long length = 0;
            while (true)
            {
                var read = await remote.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                length = checked(length + read);
                if (length > part.Length) throw new InvalidDataException("A remote part exceeds the manifest size; local cache was kept.");
                hash.AppendData(buffer, 0, read);
            }
            if (length != part.Length || !ManifestValidator.HashMatches(part.Sha256, hash.GetHashAndReset()))
                throw new InvalidDataException("A remote part failed size or SHA-256 verification; local cache was kept.");
        }

        var allManifests = await manifestStore.ListAsync(cancellationToken);
        var referencedElsewhere = allManifests.Where(item => item.FileId != fileId)
            .SelectMany(item => item.Parts.Select(part => part.StagingPath).Append(item.Encryption?.StagingPath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in partPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePoints(path);
            if (!referencedElsewhere.Contains(path) && File.Exists(path)) File.Delete(path);
            var parts = manifest.Parts.Select(part =>
                !string.IsNullOrWhiteSpace(part.StagingPath) &&
                string.Equals(Path.GetFullPath(part.StagingPath), path, StringComparison.OrdinalIgnoreCase)
                    ? part with { StagingPath = null } : part).ToArray();
            manifest = manifest with { Parts = parts };
            await manifestStore.SaveAsync(manifest, CancellationToken.None);
        }

        if (encryptedPath is not null)
        {
            RejectReparsePoints(encryptedPath);
            if (!referencedElsewhere.Contains(encryptedPath) && File.Exists(encryptedPath)) File.Delete(encryptedPath);
        }
        if (manifest.Encryption is not null)
        {
            manifest = manifest with { Encryption = manifest.Encryption with { StagingPath = null } };
            await manifestStore.SaveAsync(manifest, CancellationToken.None);
        }
        await cacheVerificationStore.RemoveManyAsync([fileId], CancellationToken.None);
    }

    private static void RejectReparsePoints(string path)
    {
        if (LocalFileSystemPathGuard.ContainsReparsePoint(path))
            throw new InvalidOperationException("A local cache path contains a filesystem link; no files were removed.");
    }
}
