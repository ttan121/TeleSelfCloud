using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

public sealed record LocalStagingProtectionMigrationResult(int ProtectedFileCount, int AlreadyProtectedFileCount,
    int MissingFileCount, int OutsideCatalogCount);

/// <summary>Idempotently protects catalog-owned staging files without changing manifest paths or schemas.</summary>
public static class LocalStagingProtectionMigrator
{
    private sealed record Candidate(string Path, string Identity, long Length, string Sha256);

    public static async Task<LocalStagingProtectionMigrationResult> MigrateAsync(string catalogStagingRoot,
        IManifestStore manifests, LocalStagingContentStore contentStore, CancellationToken token,
        Action<int, int>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogStagingRoot);
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(contentStore);
        var root = Path.GetFullPath(catalogStagingRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        if (LocalFileSystemPathGuard.ContainsReparsePoint(root))
            throw new InvalidDataException("The catalog staging root contains a filesystem link; migration stopped and kept its data.");

        var candidates = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        var outsidePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = 0;
        foreach (var manifest in await manifests.ListAsync(token))
        {
            token.ThrowIfCancellationRequested();
            ManifestValidator.ValidateStructure(manifest);
            foreach (var part in manifest.Parts)
            {
                if (string.IsNullOrWhiteSpace(part.StagingPath)) continue;
                Add(part.StagingPath, StagingFileIdentity.Part(manifest.FileId, part.Index), part.Length, part.Sha256);
            }
            if (manifest.Encryption is { StagingPath: { } payloadPath } payload)
                Add(payloadPath, StagingFileIdentity.EncryptedPayload(manifest.FileId), payload.PayloadSize, payload.PayloadSha256);
        }

        var protectedCount = 0;
        var alreadyProtected = 0;
        var index = 0;
        foreach (var candidate in candidates.Values)
        {
            token.ThrowIfCancellationRequested();
            var wasProtected = await HasProtectedHeaderAsync(candidate.Path, token);
            await contentStore.ProtectInPlaceAsync(candidate.Path, candidate.Identity, candidate.Length, candidate.Sha256, token);
            if (!await HasProtectedHeaderAsync(candidate.Path, token))
                throw new InvalidOperationException("The selected catalog has no ready staging-protection key; migration stopped without claiming success.");
            if (wasProtected) alreadyProtected++;
            else protectedCount++;
            progress?.Invoke(++index, candidates.Count);
            token.ThrowIfCancellationRequested();
        }

        return new(protectedCount, alreadyProtected, missing, outsidePaths.Count);

        void Add(string rawPath, string identity, long length, string sha256)
        {
            var fullPath = Path.GetFullPath(rawPath);
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                outsidePaths.Add(fullPath);
                return;
            }
            if (!File.Exists(fullPath)) { missing++; return; }
            if (LocalFileSystemPathGuard.ContainsReparsePoint(fullPath))
                throw new InvalidDataException("A catalog staging file contains a filesystem link; migration stopped and kept its data.");
            var candidate = new Candidate(fullPath, identity, length, sha256);
            if (candidates.TryGetValue(fullPath, out var prior) && prior != candidate)
                throw new InvalidDataException("A staging file is claimed by conflicting catalog identities; migration stopped and kept its data.");
            candidates[fullPath] = candidate;
        }
    }

    private static async Task<bool> HasProtectedHeaderAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var prefix = new byte[8];
        var count = 0;
        while (count < prefix.Length)
        {
            var read = await stream.ReadAsync(prefix.AsMemory(count), token);
            if (read == 0) break;
            count += read;
        }
        return LocalStagingCipher.HasProtectedHeader(prefix.AsSpan(0, count));
    }
}
