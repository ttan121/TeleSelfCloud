using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Infrastructure.Telegram;

public sealed class TelegramRemoteFileDeleter(
    ITelegramRequestClient session,
    IPartTransport transport,
    long chatId,
    string accountId,
    VaultMetadataKey? metadataKey = null)
{
    private const int PageSize = 100;
    private const int DeleteBatchSize = 100;
    private const int MaxManifestBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<int> DeleteTrashedFileAsync(FileManifest localManifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localManifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (metadataKey is not null && (metadataKey.AccountId != accountId || metadataKey.ChatId != chatId))
            throw new InvalidDataException("The deletion metadata key belongs to another vault.");
        ManifestValidator.ValidateStructure(localManifest);
        if (!localManifest.Committed || !localManifest.IsInTrash ||
            !string.Equals(localManifest.AccountId, accountId, StringComparison.Ordinal))
            throw new InvalidOperationException("Only a committed Trash item owned by the active Telegram account can be deleted.");

        var remoteManifestIds = new HashSet<long>();
        var revisions = new List<FileManifest>();
        await ScanManifestRevisionsAsync(localManifest.FileId, remoteManifestIds, revisions, cancellationToken);
        if (revisions.Count == 0) revisions.Add(localManifest);

        FileManifest? newest = null;
        foreach (var revision in revisions)
        {
            if (!string.IsNullOrWhiteSpace(revision.AccountId) && !string.Equals(revision.AccountId, accountId, StringComparison.Ordinal))
                throw new InvalidDataException("A remote manifest revision belongs to a different Telegram account.");
            newest = ManifestRevisionSelector.PreferNewest(newest, revision);
        }
        if (newest is null || newest.Revision != localManifest.Revision || !newest.IsInTrash ||
            !SameRemoteParts(newest, localManifest))
            throw new InvalidOperationException("The remote file changed or left Trash. Sync again before deleting it.");

        var partsById = new Dictionary<string, (string FileId, int Index)>(StringComparer.Ordinal);
        foreach (var revision in revisions.Append(localManifest))
        foreach (var part in revision.Parts)
        {
            var remoteId = part.RemoteId ?? throw new InvalidDataException("A remote manifest contains a part without a Telegram reference.");
            var reference = TelegramRemoteMessageId.Parse(remoteId);
            if (reference.ChatId != chatId)
                throw new InvalidDataException("A part reference points outside the active Telegram storage channel.");
            var expected = (revision.FileId, part.Index);
            if (partsById.TryGetValue(remoteId, out var prior) && prior != expected)
                throw new InvalidDataException("A Telegram message is referenced as two different file parts.");
            partsById[remoteId] = expected;
        }

        foreach (var (remoteId, expected) in partsById)
            await ValidatePartMessageAsync(remoteId, expected.FileId, expected.Index, cancellationToken);

        var allMessageIds = remoteManifestIds.Concat(partsById.Keys.Select(id => TelegramRemoteMessageId.Parse(id).MessageId))
            .Distinct().Order().ToArray();
        for (var offset = 0; offset < allMessageIds.Length; offset += DeleteBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = allMessageIds.Skip(offset).Take(DeleteBatchSize);
            var response = await session.ExecuteAsync(new JsonObject
            {
                ["@type"] = "deleteMessages",
                ["chat_id"] = chatId,
                ["message_ids"] = new JsonArray(batch.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["revoke"] = true
            }, cancellationToken);
            if (response["@type"]?.GetValue<string>() != "ok")
                throw new IOException("TDLib did not confirm permanent deletion of every Telegram message.");
        }
        return allMessageIds.Length;
    }

    private async Task ScanManifestRevisionsAsync(
        string fileId, HashSet<long> messageIds, List<FileManifest> revisions, CancellationToken cancellationToken)
    {
        long fromMessageId = 0;
        while (true)
        {
            var response = await session.ExecuteAsync(new JsonObject
            {
                ["@type"] = "getChatHistory", ["chat_id"] = chatId,
                ["from_message_id"] = fromMessageId, ["offset"] = 0,
                ["limit"] = PageSize, ["only_local"] = false
            }, cancellationToken);
            if (response["messages"] is not JsonArray messages)
                throw new InvalidDataException("Telegram returned an invalid history page; no messages were deleted.");
            if (messages.Count == 0) return;
            if (messages.OfType<JsonObject>().Count() != messages.Count)
                throw new InvalidDataException("Telegram returned a malformed history page; no messages were deleted.");
            long? priorId = null;
            foreach (var message in messages.OfType<JsonObject>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!long.TryParse(message["id"]?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var messageId) || messageId <= 0 ||
                    !long.TryParse(message["chat_id"]?.ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var messageChatId) || messageChatId != chatId ||
                    priorId is { } previous && messageId >= previous || fromMessageId != 0 && messageId > fromMessageId)
                    throw new InvalidDataException("Telegram history had an invalid message identity or order; no messages were deleted.");
                priorId = messageId;
                if (messageId == fromMessageId) continue; // Already validated/imported on the prior page.
                var fields = message["content"]?["caption"]?["text"]?.GetValue<string>()?.Split('|');
                if (fields is not { Length: >= 3 } || fields[0] != "TSC-MANIFEST" || fields[2] != fileId) continue;
                if (fields.Length != 4 || fields[1] is not ("1" or "2"))
                    throw new InvalidDataException("A target manifest uses an invalid or unsupported caption. No messages were deleted.");
                if (fields[1] == "2" && metadataKey is null)
                    throw new InvalidDataException("The existing vault metadata key is required before deleting this file.");
                await using var stream = await transport.DownloadPartAsync($"{chatId}/{messageId}", cancellationToken);
                var bytes = await ReadBoundedAsync(stream, MaxManifestBytes, cancellationToken);
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), fields[3], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A remote manifest failed its SHA-256 check; no messages were deleted.");
                var plain = fields[1] == "2" ? metadataKey!.Unprotect("manifest", fileId, bytes) : bytes;
                FileManifest manifest;
                try { manifest = JsonSerializer.Deserialize<FileManifest>(plain, JsonOptions) ?? throw new InvalidDataException("A remote manifest was empty; no messages were deleted."); }
                finally { if (fields[1] == "2") CryptographicOperations.ZeroMemory(plain); }
                ManifestValidator.ValidateStructure(manifest);
                if (!manifest.Committed || !string.Equals(manifest.FileId, fileId, StringComparison.Ordinal))
                    throw new InvalidDataException("A remote manifest did not match the requested file; no messages were deleted.");
                messageIds.Add(messageId);
                revisions.Add(manifest);
            }
            if (!long.TryParse(messages.LastOrDefault()?["id"]?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var oldestId) || oldestId <= 0 ||
                fromMessageId != 0 && oldestId > fromMessageId)
                throw new InvalidDataException("Telegram history stopped advancing; no messages were deleted.");
            if (oldestId == fromMessageId) return; // Strict ordering proves this page contains only the inclusive anchor.
            fromMessageId = oldestId;
        }
    }

    private async Task ValidatePartMessageAsync(string remoteId, string fileId, int index, CancellationToken cancellationToken)
    {
        var reference = TelegramRemoteMessageId.Parse(remoteId);
        if (reference.ChatId != chatId) throw new InvalidDataException("A part reference points outside the active storage channel.");
        JsonObject response;
        try
        {
            response = await session.ExecuteAsync(new JsonObject
            {
                ["@type"] = "getMessage", ["chat_id"] = chatId, ["message_id"] = reference.MessageId
            }, cancellationToken);
        }
        catch (TelegramRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("MESSAGE_ID_INVALID", StringComparison.Ordinal))
        {
            return; // Previously deleted during an interrupted retry of this same confirmed Trash operation.
        }
        if (ReadInt64(response["chat_id"]) != chatId || ReadInt64(response["id"]) != reference.MessageId ||
            response["content"]?["caption"]?["text"]?.GetValue<string>() != $"TSC-PART|1|{fileId}|{index.ToString(CultureInfo.InvariantCulture)}" ||
            response["content"]?["document"]?["document"]?["id"] is null)
            throw new InvalidDataException("A Telegram part reference failed file, index, or channel validation; no messages were deleted.");
    }

    private static long? ReadInt64(JsonNode? value)
    {
        if (value is not JsonValue jsonValue) return null;
        if (jsonValue.TryGetValue<long>(out var longValue)) return longValue;
        if (jsonValue.TryGetValue<int>(out var intValue)) return intValue;
        return null;
    }

    private static bool SameRemoteParts(FileManifest first, FileManifest second) =>
        first.Parts.Count == second.Parts.Count && first.Parts.Zip(second.Parts).All(pair =>
            pair.First.Index == pair.Second.Index && pair.First.Offset == pair.Second.Offset && pair.First.Length == pair.Second.Length &&
            string.Equals(pair.First.Sha256, pair.Second.Sha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(pair.First.RemoteId, pair.Second.RemoteId, StringComparison.Ordinal));

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maximumBytes) throw new InvalidDataException("A remote manifest exceeded the safe parsing limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return buffer.ToArray();
    }
}
