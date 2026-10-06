using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Telegram;

namespace TeleSelfCloud.Infrastructure.Transfers;

public enum VaultLegacyManifestDisposition
{
    CurrentVault,
    OtherChat,
    MixedChats,
    UnboundAccount,
    NoRemoteBinding,
    ForeignAccount,
    ForeignAccountReference,
    MalformedRemoteReference
}

public sealed record VaultLegacyManifestPartition(string FileId, VaultLegacyManifestDisposition Disposition,
    IReadOnlyList<long> ReferencedChatIds);

public sealed record VaultLegacyPartitionPlan(string AccountId, long CurrentChatId,
    IReadOnlyList<VaultLegacyManifestPartition> Manifests)
{
    // Create validates every manifest before classification. NoRemoteBinding therefore means an
    // account-owned draft without remote or copy references, which can stay in the primary catalog.
    public bool RequiresIsolation => Manifests.Any(entry => entry.Disposition is not
        (VaultLegacyManifestDisposition.CurrentVault or VaultLegacyManifestDisposition.NoRemoteBinding));

    public IReadOnlyList<string> CurrentVaultFileIds => Manifests
        .Where(entry => entry.Disposition == VaultLegacyManifestDisposition.CurrentVault)
        .Select(entry => entry.FileId).ToArray();

    public IReadOnlyList<string> LocalRecoveryFileIds => Manifests
        .Where(entry => entry.Disposition is VaultLegacyManifestDisposition.OtherChat or
            VaultLegacyManifestDisposition.MixedChats or VaultLegacyManifestDisposition.UnboundAccount or
            VaultLegacyManifestDisposition.NoRemoteBinding or VaultLegacyManifestDisposition.ForeignAccountReference or
            VaultLegacyManifestDisposition.MalformedRemoteReference)
        .Select(entry => entry.FileId).ToArray();

    public IReadOnlyList<string> ForeignAccountFileIds => Manifests
        .Where(entry => entry.Disposition == VaultLegacyManifestDisposition.ForeignAccount)
        .Select(entry => entry.FileId).ToArray();
}

/// <summary>Classifies whole legacy manifests without rewriting account/chat ownership or staging paths.</summary>
public static class VaultLegacyPartitionPlanner
{
    public static VaultLegacyPartitionPlan Create(IEnumerable<FileManifest> manifests, string accountId, long currentChatId)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (currentChatId == 0) throw new ArgumentOutOfRangeException(nameof(currentChatId));

        var entries = new List<VaultLegacyManifestPartition>();
        var fileIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var manifest in manifests)
        {
            ManifestValidator.ValidateStructure(manifest);
            if (!fileIds.Add(manifest.FileId))
                throw new InvalidDataException("The legacy partition source contains duplicate manifest IDs.");

            if (!string.IsNullOrWhiteSpace(manifest.AccountId) &&
                !string.Equals(manifest.AccountId, accountId, StringComparison.Ordinal))
            {
                entries.Add(new(manifest.FileId, VaultLegacyManifestDisposition.ForeignAccount, []));
                continue;
            }
            if (string.IsNullOrWhiteSpace(manifest.AccountId))
            {
                entries.Add(new(manifest.FileId, VaultLegacyManifestDisposition.UnboundAccount, []));
                continue;
            }

            if (manifest.Parts.Any(part => part.CopySource is { } source &&
                    !string.Equals(source.AccountId, accountId, StringComparison.Ordinal)))
            {
                entries.Add(new(manifest.FileId, VaultLegacyManifestDisposition.ForeignAccountReference, []));
                continue;
            }

            IReadOnlyList<long> chatIds;
            try { chatIds = GetChatIds(manifest); }
            catch (InvalidDataException)
            {
                entries.Add(new(manifest.FileId, VaultLegacyManifestDisposition.MalformedRemoteReference, []));
                continue;
            }

            if (chatIds.Count == 0)
                entries.Add(new(manifest.FileId, VaultLegacyManifestDisposition.NoRemoteBinding, chatIds));
            else if (chatIds.Count > 1)
                entries.Add(new(manifest.FileId, VaultLegacyManifestDisposition.MixedChats, chatIds));
            else if (chatIds[0] != currentChatId)
                entries.Add(new(manifest.FileId, VaultLegacyManifestDisposition.OtherChat, chatIds));
            else
                entries.Add(new(manifest.FileId, VaultLegacyManifestDisposition.CurrentVault, chatIds));
        }

        return new(accountId, currentChatId, entries);
    }

    private static IReadOnlyList<long> GetChatIds(FileManifest manifest)
    {
        var chatIds = new HashSet<long>();
        foreach (var part in manifest.Parts)
        {
            Add(part.RemoteId);
            if (part.CopySource is { } source) Add(source.RemoteId);
        }
        return chatIds.Order().ToArray();

        void Add(string? remoteId)
        {
            if (string.IsNullOrWhiteSpace(remoteId)) return;
            try { chatIds.Add(TelegramRemoteMessageId.Parse(remoteId).ChatId); }
            catch (InvalidDataException exception)
            { throw new InvalidDataException("The legacy manifest has a malformed remote reference.", exception); }
        }
    }
}
