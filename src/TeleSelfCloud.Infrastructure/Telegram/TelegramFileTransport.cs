using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Telegram;

public sealed class TelegramFileTransport : IPartTransport, IAcceptedPartRecovery
{
    private const long ConservativeFileLimitBytes = TelegramUploadCapabilityProvider.ConservativePerFileLimitBytes;
    private readonly ITelegramUpdateSource _session;
    private readonly long _chatId;
    public long ChatId => _chatId;
    internal ITelegramRequestClient RequestClient => _session;
    private readonly string _downloadDirectory;

    public event EventHandler<string>? TransferStatusChanged;

    public TelegramFileTransport(ITelegramUpdateSource session, long chatId, string downloadDirectory)
    {
        _session = session;
        _chatId = chatId;
        _downloadDirectory = Path.GetFullPath(downloadDirectory);
        Directory.CreateDirectory(_downloadDirectory);
    }

    public async Task<string> UploadPartAsync(string path, string fileId, int index, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Staged Telegram part was not found.", path);
        if (info.Length > ConservativeFileLimitBytes)
            throw new InvalidOperationException("The part exceeds TeleSelfCloud's conservative 2,000,000,000-byte per-file ceiling.");

        var caption = $"TSC-PART|1|{fileId}|{index.ToString(CultureInfo.InvariantCulture)}";
        var message = await SendDocumentAsync(path, caption, cancellationToken);
        return CreateRemoteId(message["chat_id"]!.GetValue<long>(), message["id"]!.GetValue<long>());
    }

    public async Task<string?> FindAcceptedPartAsync(string path, string fileId, int index, CancellationToken cancellationToken)
    {
        var caption = $"TSC-PART|1|{fileId}|{index.ToString(CultureInfo.InvariantCulture)}";
        return await FindExistingDocumentAsync(path, caption, cancellationToken);
    }

    public async Task<string?> FindExistingDocumentAsync(string path, string caption, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("The staged Telegram document was not found.", path);
        if (info.Length > ConservativeFileLimitBytes)
            throw new InvalidOperationException("The document exceeds TeleSelfCloud's conservative 2,000,000,000-byte per-file ceiling.");

        await using var local = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        var expectedLength = local.Length;
        if (expectedLength > ConservativeFileLimitBytes)
            throw new InvalidOperationException("The document exceeds TeleSelfCloud's conservative 2,000,000,000-byte per-file ceiling.");
        var expectedHash = await SHA256.HashDataAsync(local, cancellationToken);

        long fromMessageId = 0;
        while (true)
        {
            var response = await _session.ExecuteAsync(new JsonObject
            {
                ["@type"] = "getChatHistory",
                ["chat_id"] = _chatId,
                ["from_message_id"] = fromMessageId,
                ["offset"] = 0,
                ["limit"] = 100,
                ["only_local"] = false
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (response["@type"]?.ToString() != "messages" || response["messages"] is not JsonArray messages || messages.Any(item => item is not JsonObject))
                throw new InvalidDataException("Telegram recovery history is malformed. The saved transfer was kept; no new document was sent.");
            if (messages.Count == 0) return null;
            long previousId = long.MaxValue;
            foreach (var message in messages.OfType<JsonObject>())
            {
                if (!long.TryParse(message["id"]?.ToString(), out var id) || id <= 0 || id >= previousId ||
                    (fromMessageId != 0 && id > fromMessageId) ||
                    !long.TryParse(message["chat_id"]?.ToString(), out var chat) || chat != _chatId)
                    throw new InvalidDataException("Telegram recovery history has invalid order, IDs, or storage scope. The saved transfer was kept; no new document was sent.");
                previousId = id;
            }
            // offset=0 includes its anchor. The anchor alone is the valid oldest boundary.
            if (fromMessageId != 0 && messages.Count == 1 && previousId == fromMessageId) return null;

            foreach (var message in messages.OfType<JsonObject>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (message["id"]!.GetValue<long>() == fromMessageId) continue;
                var sentCaption = message["content"]?["caption"]?["text"]?.GetValue<string>();
                if (!string.Equals(sentCaption, caption, StringComparison.Ordinal)) continue;
                if (message["sending_state"] is { } sendingState)
                {
                    if (sendingState["@type"]?.ToString() == "messageSendingStateFailed") continue;
                    throw new IOException("A matching Telegram document is still being sent. Wait for confirmation before retrying.");
                }
                if (!long.TryParse(message["chat_id"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var messageChatId) ||
                    messageChatId != _chatId ||
                    !long.TryParse(message["id"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var messageId))
                    continue;

                var remoteId = CreateRemoteId(messageChatId, messageId);
                await using var remote = await DownloadPartAsync(remoteId, cancellationToken);
                if (await MatchesContentAsync(remote, expectedLength, expectedHash, cancellationToken))
                    return remoteId;
            }

            var lastMessage = messages.OfType<JsonObject>().LastOrDefault();
            if (!long.TryParse(lastMessage?["id"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var oldestMessageId) ||
                oldestMessageId <= 0 || (fromMessageId != 0 && oldestMessageId >= fromMessageId))
                throw new InvalidDataException("Telegram recovery history did not advance. The saved transfer was kept; no new document was sent.");
            fromMessageId = oldestMessageId;
        }

    }

    public async Task<string> UploadManifestAsync(string path, string fileId, string sha256, CancellationToken cancellationToken)
    {
        return await UploadStorageDocumentAsync(path, $"TSC-MANIFEST|1|{fileId}|{sha256}", cancellationToken);
    }

    public async Task<string> UploadStorageDocumentAsync(string path, string caption, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > ConservativeFileLimitBytes)
            throw new InvalidOperationException("The storage document is missing or exceeds the conservative Telegram file ceiling.");
        if (string.IsNullOrWhiteSpace(caption) || !caption.StartsWith("TSC-", StringComparison.Ordinal) || caption.Length > 1024)
            throw new ArgumentException("Storage document caption is invalid or exceeds Telegram's caption limit.", nameof(caption));
        var existingId = await FindExistingDocumentAsync(path, caption, cancellationToken);
        if (existingId is not null) return existingId;

        var message = await SendDocumentAsync(path, caption, cancellationToken);
        return CreateRemoteId(message["chat_id"]!.GetValue<long>(), message["id"]!.GetValue<long>());
    }

    public async Task<Stream> DownloadPartAsync(string remoteId, CancellationToken cancellationToken)
    {
        var (chatId, messageId) = ParseRemoteId(remoteId);
        if (chatId != _chatId) throw new InvalidOperationException("The remote part is not in the configured TeleSelfCloud storage channel.");

        var message = await _session.ExecuteAsync(new JsonObject
        {
            ["@type"] = "getMessage",
            ["chat_id"] = chatId,
            ["message_id"] = messageId
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (message["chat_id"]?.ToString() != chatId.ToString(CultureInfo.InvariantCulture) ||
            message["id"]?.ToString() != messageId.ToString(CultureInfo.InvariantCulture))
            throw new InvalidDataException("Telegram returned a different storage message. The saved transfer was kept.");
        if (message["sending_state"] is not null)
            throw new InvalidDataException("The Telegram storage document is not confirmed. Wait for sending to finish before retrying.");
        var fileId = message["content"]?["document"]?["document"]?["id"]?.GetValue<int>()
            ?? throw new InvalidDataException("The Telegram storage message does not contain a document.");
        var file = await _session.ExecuteAsync(new JsonObject
        {
            ["@type"] = "downloadFile",
            ["file_id"] = fileId,
            ["priority"] = 32,
            ["offset"] = 0,
            ["limit"] = 0,
            ["synchronous"] = true
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (file["id"]?.ToString() != fileId.ToString(CultureInfo.InvariantCulture))
            throw new InvalidDataException("Telegram returned a different document file. The saved transfer was kept.");
        if (file["local"]?["is_downloading_completed"]?.GetValue<bool>() != true)
            throw new IOException("TDLib did not complete the remote part download.");
        var sourcePath = file["local"]?["path"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("TDLib completed the download without a readable local file.");

        var destinationPath = Path.Combine(_downloadDirectory, $"{Guid.NewGuid():N}.part");
        var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
            await source.CopyToAsync(destination, cancellationToken);
            await destination.FlushAsync(cancellationToken);
            destination.Position = 0;
            return destination;
        }
        catch { await destination.DisposeAsync(); throw; }
    }

    public async Task<string> CopyPartAsync(PartRecord source, string ownerFileId, string targetFileId, int targetIndex, CancellationToken token)
    {
        if (!source.Confirmed || source.RemoteId is null || source.Length < 0 || source.Length > ConservativeFileLimitBytes ||
            source.Sha256.Length != 64 || !source.Sha256.All(Uri.IsHexDigit) || targetIndex < 0 || source.Index < 0 ||
            string.IsNullOrWhiteSpace(ownerFileId) || string.IsNullOrWhiteSpace(targetFileId) || ownerFileId == targetFileId ||
            ownerFileId.Length > 200 || targetFileId.Length > 200 || ownerFileId.IndexOfAny(['|', '\r', '\n']) >= 0 || targetFileId.IndexOfAny(['|', '\r', '\n']) >= 0)
            throw new InvalidDataException("The document copy source or target ownership is invalid.");
        var reference = TelegramRemoteMessageId.Parse(source.RemoteId);
        if (reference.ChatId != _chatId) throw new InvalidDataException("The document copy source belongs to another vault.");
        async Task<TelegramDedupDocument> ReadIdentity()
        {
            var message = await _session.ExecuteAsync(new JsonObject { ["@type"] = "getMessage", ["chat_id"] = _chatId, ["message_id"] = reference.MessageId }, token);
            return TelegramDedupDocument.Read(message, _chatId, reference.MessageId, ownerFileId, source.Index, source.Length);
        }
        var identity = await ReadIdentity();
        await using (var input = await DownloadPartAsync(source.RemoteId, token))
            if (!await MatchesContentAsync(input, source.Length, Convert.FromHexString(source.Sha256), token))
                throw new InvalidDataException("The document copy source failed its content check.");
        if (await ReadIdentity() != identity) throw new InvalidDataException("The document copy source changed during verification.");
        var caption = $"TSC-PART|1|{targetFileId}|{targetIndex.ToString(CultureInfo.InvariantCulture)}";
        var sent = await SendDocumentAsync(new JsonObject { ["@type"] = "inputFileId", ["id"] = identity.FileId }, source.Length, caption, token);
        var remote = CreateRemoteId(sent["chat_id"]!.GetValue<long>(), sent["id"]!.GetValue<long>());
        if (remote == source.RemoteId) throw new InvalidDataException("The copied document did not receive independent message ownership.");
        var copied = TelegramDedupDocument.Read(sent, _chatId, sent["id"]!.GetValue<long>(), targetFileId, targetIndex, source.Length);
        if (copied.UniqueId != identity.UniqueId) throw new InvalidDataException("The copy confirmation did not identify the verified remote file.");
        return remote;
    }
    private async Task<JsonObject> SendDocumentAsync(string path, string caption, CancellationToken cancellationToken)
    {
        await using var stagedLease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await SendDocumentAsync(new JsonObject { ["@type"] = "inputFileLocal", ["path"] = Path.GetFullPath(path) }, stagedLease.Length, caption, cancellationToken);
    }
    private async Task<JsonObject> SendDocumentAsync(JsonObject inputFile, long fileLength, string caption, CancellationToken cancellationToken)
    {
        TransferStatusChanged?.Invoke(this, $"Sending a {fileLength:N0}-byte Telegram document...");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromHours(2));
        var sentUpdate = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingSendUpdates = new List<JsonObject>();
        var updateGate = new object();
        long? temporaryMessageId = null;
        EventHandler<JsonObject> observeSendUpdate = (_, update) =>
        {
            var type = update["@type"]?.GetValue<string>();
            if (type is not ("updateMessageSendSucceeded" or "updateMessageSendFailed") ||
                update["message"]?["chat_id"]?.GetValue<long>() != _chatId)
                return;

            lock (updateGate)
            {
                if (temporaryMessageId is not null && IsMatchingSendUpdate(update, caption, temporaryMessageId))
                {
                    TransferStatusChanged?.Invoke(this, "Telegram sent an upload result update for this document.");
                    sentUpdate.TrySetResult((JsonObject)update.DeepClone());
                }
                else if (temporaryMessageId is null)
                {
                    if (pendingSendUpdates.Count >= 128)
                        sentUpdate.TrySetException(new InvalidDataException("Telegram send updates exceeded the recovery buffer. The saved transfer was kept; retry to verify the accepted document."));
                    else pendingSendUpdates.Add((JsonObject)update.DeepClone());
                }
            }
        };
        _session.UpdateReceived += observeSendUpdate;
        try
        {
            var response = await _session.ExecuteAsync(new JsonObject
            {
                ["@type"] = "sendMessage",
                ["chat_id"] = _chatId,
                ["topic_id"] = null,
                ["reply_to"] = null,
                ["options"] = null,
                ["reply_markup"] = null,
                ["input_message_content"] = new JsonObject
                {
                    ["@type"] = "inputMessageDocument",
                    ["document"] = new JsonObject
                    {
                        ["@type"] = "inputDocument",
                        ["document"] = inputFile,
                        ["thumbnail"] = null,
                        ["disable_content_type_detection"] = false
                    },
                    ["caption"] = new JsonObject
                    {
                        ["@type"] = "formattedText",
                        ["text"] = caption,
                        ["entities"] = new JsonArray()
                    }
                }
            }, timeout.Token);
            if (response["@type"] == null || response["@type"]?.GetValue<string>() != "message")
                throw new InvalidDataException("TDLib did not return a sent message.");

            temporaryMessageId = response["id"]?.GetValue<long>();
            if (temporaryMessageId is null or 0 || response["chat_id"]?.ToString() != _chatId.ToString(CultureInfo.InvariantCulture))
                throw new InvalidDataException("Telegram returned an invalid send identity. The saved transfer was kept.");
            lock (updateGate)
            {
                foreach (var pending in pendingSendUpdates)
                {
                    if (IsMatchingSendUpdate(pending, caption, temporaryMessageId))
                    {
                        TransferStatusChanged?.Invoke(this, "Telegram sent an upload result update for this document.");
                        sentUpdate.TrySetResult((JsonObject)pending.DeepClone());
                        break;
                    }
                }
                pendingSendUpdates.Clear();
            }

            TransferStatusChanged?.Invoke(this, "Telegram accepted the send request; waiting for upload confirmation...");
            var update = await sentUpdate.Task.WaitAsync(timeout.Token);
            if (update["@type"]?.GetValue<string>() == "updateMessageSendFailed")
            {
                var error = update["error"];
                var code = int.TryParse(error?["code"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCode)
                    ? parsedCode
                    : (int?)null;
                throw TelegramRequestException.From(code, error?["message"]?.GetValue<string>() ?? "unknown error");
            }
            TransferStatusChanged?.Invoke(this, "Telegram confirmed the document upload.");
            var confirmed = update["message"] as JsonObject
                ?? throw new InvalidDataException("Telegram returned an invalid upload confirmation. The saved transfer was kept.");
            if (!long.TryParse(confirmed["id"]?.ToString(), out var confirmedId) || confirmedId <= 0 || confirmed["sending_state"] is not null ||
                confirmed["content"]?["caption"]?["text"]?.GetValue<string>() != caption)
                throw new InvalidDataException("Telegram returned an invalid upload confirmation. The saved transfer was kept.");
            return confirmed;
        }
        catch
        {
            timeout.Cancel();
            throw;
        }
        finally
        {
            _session.UpdateReceived -= observeSendUpdate;
        }
    }

    private bool IsMatchingSendUpdate(JsonObject update, string caption, long? temporaryMessageId)
    {
        var type = update["@type"]?.GetValue<string>();
        if (type is not ("updateMessageSendSucceeded" or "updateMessageSendFailed")) return false;
        var message = update["message"];
        if (message?["chat_id"]?.GetValue<long>() != _chatId) return false;
        if (temporaryMessageId is not { } expectedId) return false;
        return type == "updateMessageSendSucceeded"
            ? update["old_message_id"]?.GetValue<long>() == expectedId
            : message["id"]?.GetValue<long>() == expectedId || update["old_message_id"]?.GetValue<long>() == expectedId;
    }

    private static string CreateRemoteId(long chatId, long messageId) =>
        $"{chatId.ToString(CultureInfo.InvariantCulture)}/{messageId.ToString(CultureInfo.InvariantCulture)}";

    private static (long ChatId, long MessageId) ParseRemoteId(string remoteId)
    {
        var parsed = TelegramRemoteMessageId.Parse(remoteId);
        return (parsed.ChatId, parsed.MessageId);
    }

    private static async Task<bool> MatchesContentAsync(Stream remote, long expectedLength, byte[] expectedHash, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long length = 0;
        int read;
        while ((read = await remote.ReadAsync(buffer, cancellationToken)) > 0)
        {
            length += read;
            if (length > expectedLength) return false;
            hash.AppendData(buffer, 0, read);
        }

        return length == expectedLength && CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expectedHash);
    }
}
