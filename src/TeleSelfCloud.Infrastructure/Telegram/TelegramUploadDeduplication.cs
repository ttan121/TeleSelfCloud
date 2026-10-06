using System.Security.Cryptography;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Telegram;

/// <summary>Explicit same-vault plaintext planning and independently owned document copies.</summary>
public sealed class TelegramUploadDeduplication(IManifestStore store, ITelegramRequestClient session, TelegramFileTransport transport, string accountId,
    Func<FileManifest, CancellationToken, Task<string?>>? candidatePassphraseProvider = null) : IUploadLayoutDeduplication, IEncryptedUploadDeduplication
{
    public async Task<EncryptedUploadCopyPlan?> PlanEncryptedAsync(FileManifest staged, long maxPartBytes, string newRecoveryPassphrase, CancellationToken token)
    {
        if (!ReferenceEquals(session, transport.RequestClient)) throw new InvalidDataException("Duplicate verification and copy require the same Telegram session.");
        ManifestValidator.ValidateStructure(staged);
        if (staged.AccountId != accountId || maxPartBytes <= 0) throw new InvalidDataException("The encrypted duplicate plan belongs to another account or has an invalid limit.");
        if (staged.Encryption is null || candidatePassphraseProvider is null) return null;
        foreach (var stored in await store.ListAsync(token))
        {
            var candidate = stored with { Parts = stored.Parts.ToArray() };
            if (candidate.AccountId != accountId || candidate.FileId == staged.FileId || !candidate.Committed || candidate.IsInTrash || candidate.Encryption is null ||
                candidate.LogicalSize != staged.LogicalSize || !string.Equals(candidate.TotalSha256, staged.TotalSha256, StringComparison.OrdinalIgnoreCase) ||
                candidate.Parts.Any(p => p.Length > Math.Min(maxPartBytes, 2_000_000_000L))) continue;
            if (candidate.Parts.Any(p => p.RemoteId is null || TelegramRemoteMessageId.Parse(p.RemoteId).ChatId != transport.ChatId))
                throw new InvalidDataException("An encrypted duplicate candidate points outside the configured vault.");
            var passphrase = await candidatePassphraseProvider(candidate, token);
            token.ThrowIfCancellationRequested(); if (passphrase is null) return null;
            var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(staged.Parts[0].StagingPath ?? throw new InvalidDataException("Encrypted copy requires local staging.")))!, "encrypted-dedup");
            FileManifest? verified;
            try
            {
                verified = await new EncryptedContentDedupVerifier(session, transport, accountId, transport.ChatId, temporary)
                    .VerifyAndStageAsync(candidate, staged.TotalSha256, staged.LogicalSize, passphrase, newRecoveryPassphrase, token);
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                // An existing item's passphrase can differ from this upload's recovery passphrase.
                // That candidate is ineligible; the already-staged new ciphertext remains usable for ordinary upload.
                continue;
            }
            if (verified is null) continue;
            return new(verified.PartSizeBytes, verified.Parts.Select(p => p with { RemoteId = null, Confirmed = false,
                CopySource = new(accountId, candidate.FileId, p.Index, p.RemoteId!) }).ToArray(), verified.Encryption!);
        }
        return null;
    }
    public async Task<IReadOnlyList<PartCopySource>?> PlanAsync(FileManifest staged, CancellationToken token)
    {
        var candidate = await FindCandidateAsync(staged, long.MaxValue, true, token);
        return candidate?.Parts.Select(p => new PartCopySource(accountId, candidate.FileId, p.Index, p.RemoteId!)).ToArray();
    }
    public Task<FileManifest?> FindVerifiedLayoutAsync(FileManifest staged, long maxPartBytes, CancellationToken token) =>
        FindCandidateAsync(staged, maxPartBytes, false, token);
    private async Task<FileManifest?> FindCandidateAsync(FileManifest staged, long maxPartBytes, bool requireSameLayout, CancellationToken token)
    {
        if (maxPartBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxPartBytes));
        if (!ReferenceEquals(session, transport.RequestClient)) throw new InvalidDataException("Duplicate verification and copy require the same Telegram session.");
        ManifestValidator.ValidateStructure(staged);
        if (staged.AccountId != accountId) throw new InvalidDataException("The duplicate plan belongs to another account.");
        if (staged.Encryption is not null) return null;
        foreach (var stored in await store.ListAsync(token))
        {
            var candidate = stored with { Parts = stored.Parts.ToArray() };
            if (candidate.AccountId != accountId || candidate.FileId == staged.FileId || !candidate.Committed || candidate.IsInTrash || candidate.Encryption is not null ||
                candidate.LogicalSize != staged.LogicalSize || !string.Equals(candidate.TotalSha256, staged.TotalSha256, StringComparison.OrdinalIgnoreCase) ||
                candidate.Parts.Any(p => p.Length > Math.Min(maxPartBytes, 2_000_000_000L)) ||
                (requireSameLayout && (candidate.Parts.Count != staged.Parts.Count || candidate.Parts.Zip(staged.Parts).Any(p => p.First.Index != p.Second.Index || p.First.Offset != p.Second.Offset ||
                    p.First.Length != p.Second.Length || !string.Equals(p.First.Sha256, p.Second.Sha256, StringComparison.OrdinalIgnoreCase))))) continue;
            if (candidate.Parts.Any(p => p.RemoteId is null || TelegramRemoteMessageId.Parse(p.RemoteId).ChatId != transport.ChatId))
                throw new InvalidDataException("A duplicate candidate points outside the configured vault.");
            if (await new RemoteContentDedupVerifier(session, transport, accountId, transport.ChatId).VerifyAsync(candidate, staged.TotalSha256, staged.LogicalSize, token))
                return candidate;
        }
        return null;
    }
    public Task<string> CopyAsync(PartRecord stagedPart, PartCopySource source, string targetFileId, CancellationToken token)
    {
        if (!ReferenceEquals(session, transport.RequestClient)) throw new InvalidDataException("Duplicate verification and copy require the same Telegram session.");
        if (source.AccountId != accountId || TelegramRemoteMessageId.Parse(source.RemoteId).ChatId != transport.ChatId || source.Index != stagedPart.Index)
            throw new InvalidDataException("The saved duplicate-copy intent belongs to another scope.");
        return transport.CopyPartAsync(stagedPart with { Index = source.Index, RemoteId = source.RemoteId, Confirmed = true, CopySource = null }, source.OwnerFileId, targetFileId, stagedPart.Index, token);
    }
}
