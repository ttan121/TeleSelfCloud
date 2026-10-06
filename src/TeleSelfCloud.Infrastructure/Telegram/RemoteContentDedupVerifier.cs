using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Infrastructure.Telegram;

/// <summary>Reads actual remote bytes before considering reuse. Does not publish, skip uploads or transfer ownership.</summary>
public sealed class RemoteContentDedupVerifier(ITelegramRequestClient session, IPartTransport transport, string accountId, long chatId)
{
    public async Task<bool> VerifyAsync(FileManifest candidate, string sourceSha256, long sourceSize, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Encryption is not null)
            throw new NotSupportedException("Encrypted dedup requires authenticated plaintext verification and recovery-key ownership.");
        return await VerifyPayloadAsync(candidate, sourceSha256, sourceSize, null, token);
    }
    internal async Task<bool> VerifyPayloadAsync(FileManifest candidate, string sourceSha256, long sourceSize, Stream? verifiedOutput, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(candidate); ArgumentNullException.ThrowIfNull(sourceSha256); ManifestValidator.ValidateStructure(candidate);
        candidate = candidate with { Parts = candidate.Parts.ToArray() }; ManifestValidator.ValidateStructure(candidate);
        if (string.IsNullOrWhiteSpace(accountId) || chatId == 0 || candidate.AccountId != accountId)
            throw new InvalidDataException("The duplicate candidate belongs to another storage scope.");
        if (sourceSize < 0 || sourceSha256.Length != 64 || !sourceSha256.All(Uri.IsHexDigit)) throw new ArgumentException("The source content identity is invalid.");
        if (!candidate.Committed || candidate.IsInTrash || candidate.TransferSize != sourceSize || !string.Equals(candidate.Encryption?.PayloadSha256 ?? candidate.TotalSha256, sourceSha256, StringComparison.OrdinalIgnoreCase)) return false;
        // Check every locator before fetching bytes; never send a request to a foreign vault.
        foreach (var part in candidate.Parts)
            if (part.RemoteId is null || TelegramRemoteMessageId.Parse(part.RemoteId).ChatId != chatId)
                throw new InvalidDataException("A duplicate part points outside the active storage channel.");
        using var total = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        try
        {
            foreach (var part in candidate.Parts.OrderBy(p => p.Index))
            {
                token.ThrowIfCancellationRequested(); var reference = TelegramRemoteMessageId.Parse(part.RemoteId!);
                var message = await session.ExecuteAsync(new JsonObject { ["@type"] = "getMessage", ["chat_id"] = chatId, ["message_id"] = reference.MessageId }, token);
                var identity = TelegramDedupDocument.Read(message, chatId, reference.MessageId, candidate.FileId, part.Index, part.Length);
                await using var input = await transport.DownloadPartAsync(part.RemoteId!, token);
                using var partHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long count = 0;
                while (true)
                {
                    // Read at most remaining bytes + one: excessive responses cannot consume unbounded bandwidth.
                    var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, part.Length - count + (part.Length - count < buffer.Length ? 1 : 0))), token);
                    if (read == 0) break;
                    if (read > part.Length - count) return false;
                    count += read; partHash.AppendData(buffer, 0, read); total.AppendData(buffer, 0, read);
                    if (verifiedOutput is not null) await verifiedOutput.WriteAsync(buffer.AsMemory(0, read), token);
                }
                if (count != part.Length || !CryptographicOperations.FixedTimeEquals(partHash.GetHashAndReset(), Convert.FromHexString(part.Sha256))) return false;
                var current = await session.ExecuteAsync(new JsonObject { ["@type"] = "getMessage", ["chat_id"] = chatId, ["message_id"] = reference.MessageId }, token);
                if (TelegramDedupDocument.Read(current, chatId, reference.MessageId, candidate.FileId, part.Index, part.Length) != identity)
                    throw new InvalidDataException("The duplicate document changed during verification.");
            }
            return CryptographicOperations.FixedTimeEquals(total.GetHashAndReset(), Convert.FromHexString(sourceSha256));
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
    private static long? ReadNumber(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var number)) return number;
        return value.TryGetValue<int>(out var integer) ? integer : null;
    }
}
