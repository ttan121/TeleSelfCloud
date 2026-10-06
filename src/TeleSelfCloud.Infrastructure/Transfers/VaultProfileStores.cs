using TeleSelfCloud.Infrastructure.Telegram;

namespace TeleSelfCloud.Infrastructure.Transfers;

/// <summary>One catalog/queue/folder/cache/staging workspace, never a TDLib session.</summary>
public sealed class VaultProfileStores
{
    public string Root { get; }
    public string StagingRoot => Path.Combine(Root, "staging");
    public SqliteManifestStore Manifests { get; }
    public SqliteTransferQueueStore Queue { get; }
    public SqliteRemoteSyncCheckpointStore Checkpoints { get; }
    public SqliteLocalFolderStore Folders { get; }
    public LocalCacheVerificationStore Cache { get; }
    public VaultProfileStores(string root, string? databaseKey = null, LocalCacheVerificationStore? cacheStore = null)
    {
        Root = Path.GetFullPath(root);
        var database = Path.Combine(Root, "manifests.db");
        Manifests = new(database, databaseKey); Queue = new(database, databaseKey); Checkpoints = new(database, databaseKey); Folders = new(database, databaseKey);
        Cache = cacheStore ?? new(Path.Combine(Root, "local-cache-verifications.json"));
    }
    public async Task PrepareAsync(string accountId, CancellationToken token, long? chatId = null)
    {
        var ownership = await InspectOwnershipAsync(accountId, chatId, token);
        if (ownership.HasForeignAccount || ownership.ForeignChatManifestCount > 0 || ownership.MalformedRemoteReferenceCount > 0)
            throw new InvalidDataException("The vault profile contains metadata from another account or storage chat. All local records were kept.");
        Directory.CreateDirectory(StagingRoot);
        await Queue.ListAsync(token);
        await Checkpoints.ListAccountAsync(accountId, token);
        await Folders.ListAsync(accountId, token);
        await Queue.RecoverInterruptedAsync(token);
    }

    public async Task<VaultProfileOwnershipReport> InspectOwnershipAsync(string accountId, long? chatId, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        var manifests = await Manifests.ListAsync(token);
        var foreignAccount = 0;
        var foreignAccountReferences = 0;
        var unboundAccount = 0;
        var foreignChatManifests = 0;
        var mixedChatManifests = 0;
        var foreignChatParts = 0;
        var malformedRemoteReferences = 0;
        foreach (var manifest in manifests)
        {
            if (string.IsNullOrWhiteSpace(manifest.AccountId)) unboundAccount++;
            else if (!string.Equals(manifest.AccountId, accountId, StringComparison.Ordinal)) foreignAccount++;

            var hasSelectedChat = false;
            var hasOtherChat = false;
            void InspectRemoteId(string? remoteId)
            {
                if (string.IsNullOrWhiteSpace(remoteId)) return;
                try
                {
                    if (chatId is null || TelegramRemoteMessageId.Parse(remoteId).ChatId == chatId.Value) hasSelectedChat = true;
                    else { hasOtherChat = true; foreignChatParts++; }
                }
                catch (Exception exception) when (exception is FormatException or InvalidDataException or ArgumentException)
                {
                    malformedRemoteReferences++;
                }
            }
            foreach (var part in manifest.Parts)
            {
                InspectRemoteId(part.RemoteId);
                if (part.CopySource is { } copySource)
                {
                    if (!string.Equals(copySource.AccountId, accountId, StringComparison.Ordinal)) foreignAccountReferences++;
                    InspectRemoteId(copySource.RemoteId);
                }
            }
            if (hasOtherChat) foreignChatManifests++;
            if (hasOtherChat && hasSelectedChat) mixedChatManifests++;
        }
        return new(manifests.Count, foreignAccount, unboundAccount, foreignAccountReferences, foreignChatManifests, mixedChatManifests,
            foreignChatParts, malformedRemoteReferences);
    }
}

public sealed record VaultProfileOwnershipReport(int ManifestCount, int ForeignAccountManifestCount,
    int UnboundAccountManifestCount, int ForeignAccountReferenceCount, int ForeignChatManifestCount, int MixedChatManifestCount,
    int ForeignChatPartCount, int MalformedRemoteReferenceCount)
{
    public bool HasForeignAccount => ForeignAccountManifestCount != 0 || ForeignAccountReferenceCount != 0;
}
