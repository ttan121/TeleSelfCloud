using System.Text.Json;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Infrastructure.Telegram;

public sealed record VaultLegacyIsolationResult(
    TelegramVaultRegistryState Registry,
    VaultLegacyPartitionPlan Partition,
    string IsolatedDirectory);

/// <summary>
/// Prepares a clean primary catalog in the vault-specific directory when the legacy account catalog
/// cannot safely be assigned to one chat. The source catalog and its files are never changed.
/// </summary>
public static class VaultLegacyPrimaryIsolationWorkflow
{
    private const string MarkerName = ".tsc-legacy-primary-isolation.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private sealed record Intent(int SchemaVersion, string AccountId, long ChatId, string OperationId);

    public static async Task<VaultLegacyIsolationResult> IsolateAsync(
        VaultProfileStores legacyStores,
        VaultProfileStores isolatedStores,
        TelegramVaultRegistry registry,
        TelegramStorageChannelInfo verifiedChannel,
        Func<string, CancellationToken, Task>? prepareIsolatedRoot,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(legacyStores);
        ArgumentNullException.ThrowIfNull(isolatedStores);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(verifiedChannel);
        ArgumentNullException.ThrowIfNull(prepareIsolatedRoot);
        if (verifiedChannel.ChatId == 0 || string.IsNullOrWhiteSpace(verifiedChannel.AccountId))
            throw new ArgumentException("A verified account and storage chat are required.", nameof(verifiedChannel));
        if (await registry.LoadAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("Legacy isolation only applies before a vault registry has been created.");

        var sourceRoot = Path.GetFullPath(legacyStores.Root);
        var destinationRoot = Path.GetFullPath(registry.GetIsolatedPrimaryDirectory(verifiedChannel.ChatId));
        if (string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The isolated vault directory cannot be the legacy account directory.");

        var sourceManifests = await legacyStores.Manifests.ListAsync(token).ConfigureAwait(false);
        var plan = VaultLegacyPartitionPlanner.Create(sourceManifests, verifiedChannel.AccountId, verifiedChannel.ChatId);
        if (!plan.RequiresIsolation)
            throw new InvalidOperationException("The legacy catalog does not require chat-scope isolation.");

        if (!string.Equals(destinationRoot, Path.GetFullPath(isolatedStores.Root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The isolated catalog store does not match the registry directory.");

        PrepareOrResumeIntent(destinationRoot, verifiedChannel.AccountId, verifiedChannel.ChatId);
        EnsureOnlyOwnedEmptyCatalogArtifacts(destinationRoot);
        var existing = await isolatedStores.Manifests.ListAsync(token).ConfigureAwait(false);
        if (existing.Count != 0)
            throw new InvalidDataException("The isolated vault already contains records; they were kept and were not merged with the legacy catalog.");

        await prepareIsolatedRoot(destinationRoot, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        EnsureOnlyOwnedEmptyCatalogArtifacts(destinationRoot);
        if ((await isolatedStores.Manifests.ListAsync(token).ConfigureAwait(false)).Count != 0 ||
            (await isolatedStores.Queue.ListAsync(token).ConfigureAwait(false)).Count != 0 ||
            (await isolatedStores.Checkpoints.ListAccountAsync(verifiedChannel.AccountId, token).ConfigureAwait(false)).Count != 0 ||
            (await isolatedStores.Folders.ListAsync(verifiedChannel.AccountId, token).ConfigureAwait(false)).Count != 0 ||
            (await isolatedStores.Folders.ListTombstonesAsync(verifiedChannel.AccountId, token).ConfigureAwait(false)).Count != 0 ||
            (await isolatedStores.Cache.LoadAllAsync(token).ConfigureAwait(false)).Count != 0)
            throw new InvalidDataException("The isolated vault changed during recovery preparation. Its contents were kept.");

        token.ThrowIfCancellationRequested();
        var state = await registry.RegisterIsolatedPrimaryAsync(verifiedChannel, token).ConfigureAwait(false);
        return new(state, plan, destinationRoot);
    }

    private static void PrepareOrResumeIntent(string root, string accountId, long chatId)
    {
        if (LocalFileSystemPathGuard.ContainsReparsePoint(root))
            throw new InvalidDataException("The isolated vault path contains a filesystem link; no recovery data was changed.");
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, MarkerName);
        if (File.Exists(marker))
        {
            if (LocalFileSystemPathGuard.ContainsReparsePoint(marker))
                throw new InvalidDataException("The isolated vault recovery marker contains a filesystem link.");
            using var input = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 16 * 1024) throw new InvalidDataException("The isolated vault recovery marker is invalid.");
            var intent = JsonSerializer.Deserialize<Intent>(input, JsonOptions);
            if (intent is null || intent.SchemaVersion != 1 || intent.AccountId != accountId || intent.ChatId != chatId ||
                !Guid.TryParseExact(intent.OperationId, "N", out _))
                throw new InvalidDataException("The isolated vault recovery marker belongs to another operation.");
            return;
        }

        if (Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidDataException("The isolated vault directory already contains unrecognized data; it was kept.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Intent(1, accountId, chatId, Guid.NewGuid().ToString("N")), JsonOptions);
        var temporary = marker + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(bytes); output.Flush(flushToDisk: true); }
            File.Move(temporary, marker, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void EnsureOnlyOwnedEmptyCatalogArtifacts(string root)
    {
        var allowedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            MarkerName, "manifests.db", "manifests.db-wal", "manifests.db-shm",
            "metadata-key-policy.json", "metadata-key.dpapi.json", "metadata-key.lock"
        };
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (Directory.Exists(entry))
            {
                if (!string.Equals(Path.GetFileName(entry), "staging", StringComparison.OrdinalIgnoreCase) ||
                    LocalFileSystemPathGuard.ContainsReparsePoint(entry) || Directory.EnumerateFileSystemEntries(entry).Any())
                    throw new InvalidDataException("The isolated vault contains unrecognized or non-empty data; it was kept.");
                continue;
            }
            if (!allowedFiles.Contains(Path.GetFileName(entry)) || LocalFileSystemPathGuard.ContainsReparsePoint(entry))
                throw new InvalidDataException("The isolated vault contains unrecognized data; it was kept.");
        }
    }
}
