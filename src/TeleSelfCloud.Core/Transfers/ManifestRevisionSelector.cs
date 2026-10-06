using System.Security.Cryptography;
using System.Text.Json;

namespace TeleSelfCloud.Core.Transfers;

/// <summary>Selects the newest committed manifest when a remote scan finds historical revisions.</summary>
public static class ManifestRevisionSelector
{
    public static FileManifest PreferNewest(FileManifest? current, FileManifest candidate)
    {
        if (current is null) return candidate;
        if (!string.Equals(current.FileId, candidate.FileId, StringComparison.Ordinal))
            throw new ArgumentException("Manifest revisions must refer to the same file.", nameof(candidate));
        if (!string.IsNullOrWhiteSpace(current.AccountId) && !string.IsNullOrWhiteSpace(candidate.AccountId) && current.AccountId != candidate.AccountId)
            throw new InvalidDataException("Manifest revisions belong to different accounts. Local data was kept.");
        if (current.LogicalSize != candidate.LogicalSize || !string.Equals(current.TotalSha256, candidate.TotalSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A manifest revision changes immutable file content. Local data was kept; inspect the storage history before retrying.");
        if (current.Committed != candidate.Committed)
            return candidate.Committed ? PreserveLocalParts(candidate, current) : current;
        if (candidate.Revision != current.Revision)
            return candidate.Revision > current.Revision ? PreserveLocalParts(candidate, current) : current;
        if (candidate.UpdatedAtUtc is { } candidateTime && current.UpdatedAtUtc is { } currentTime)
        {
            if (candidateTime != currentTime) return candidateTime > currentTime ? PreserveLocalParts(candidate, current) : current;
        }
        if (candidate.UpdatedAtUtc is not null && current.UpdatedAtUtc is null) return PreserveLocalParts(candidate, current);
        if (candidate.UpdatedAtUtc is null && current.UpdatedAtUtc is not null) return current;
        // Timestamp/revision ties must converge independently of local state and history traversal order.
        return string.CompareOrdinal(PortableFingerprint(candidate), PortableFingerprint(current)) > 0
            ? PreserveLocalParts(candidate, current) : current;
    }

    public static string PortableFingerprint(FileManifest manifest) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest with
    {
        // Ownership is validated above and legacy manifests may be unbound locally.
        AccountId = null,
        TotalSha256 = manifest.TotalSha256.ToUpperInvariant(),
        UpdatedAtUtc = manifest.UpdatedAtUtc?.ToUniversalTime(),
        FileModifiedAtUtc = manifest.FileModifiedAtUtc?.ToUniversalTime(),
        Parts = manifest.Parts.OrderBy(part => part.Index).Select(part => part with { Sha256 = part.Sha256.ToUpperInvariant(), StagingPath = null }).ToArray(),
        Encryption = manifest.Encryption is null ? null : manifest.Encryption with { PayloadSha256 = manifest.Encryption.PayloadSha256.ToUpperInvariant(), StagingPath = null }
    })));

    private static FileManifest PreserveLocalParts(FileManifest newer, FileManifest older)
    {
        if (newer.LogicalSize != older.LogicalSize ||
            !string.Equals(newer.TotalSha256, older.TotalSha256, StringComparison.OrdinalIgnoreCase))
            return newer;

        var olderParts = older.Parts.ToDictionary(part => part.Index);
        var parts = newer.Parts.Select(part =>
        {
            if (!string.IsNullOrWhiteSpace(part.StagingPath) || !olderParts.TryGetValue(part.Index, out var localPart))
                return part;
            var sameRemotePart = part.Offset == localPart.Offset && part.Length == localPart.Length &&
                string.Equals(part.Sha256, localPart.Sha256, StringComparison.OrdinalIgnoreCase) &&
                part.Confirmed == localPart.Confirmed && string.Equals(part.RemoteId, localPart.RemoteId, StringComparison.Ordinal);
            return sameRemotePart && !string.IsNullOrWhiteSpace(localPart.StagingPath)
                ? part with { StagingPath = localPart.StagingPath }
                : part;
        }).ToArray();
        var encryption = newer.Encryption;
        if (encryption is { StagingPath: null } && older.Encryption is { } cached &&
            encryption.Version == cached.Version && encryption.PayloadSize == cached.PayloadSize &&
            string.Equals(encryption.PayloadSha256, cached.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            encryption = encryption with { StagingPath = cached.StagingPath };
        if (parts.SequenceEqual(newer.Parts) && encryption == newer.Encryption) return newer;
        return newer with { Parts = parts, Encryption = encryption };
    }
}
