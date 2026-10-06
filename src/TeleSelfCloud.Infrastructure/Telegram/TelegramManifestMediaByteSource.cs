using System.Text.Json.Nodes;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Infrastructure.Telegram;

/// <summary>Bounded random reads of a committed plaintext manifest through TDLib's file cache API.</summary>
public sealed class TelegramManifestMediaByteSource : IMediaByteSource
{
    public const int MaximumReadBytes = 64 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UpdateFallbackInterval = TimeSpan.FromMilliseconds(500);
    private readonly ITelegramUpdateSource _session;
    private readonly long _chatId;
    private readonly FileManifest _manifest;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly Dictionary<int, int> _nativeFileIds = [];
    private readonly CancellationTokenSource _lifetime = new();
    private byte[]? _fileKey;
    private byte[]? _encryptedHeader;
    private AesGcmFileCipher.EncryptedFrameLayout? _frameLayout;
    private byte[]? _cachedPlainFrame;
    private uint? _cachedFrameIndex;
    private bool _disposed;

    public TelegramManifestMediaByteSource(
        ITelegramUpdateSource session, long chatId, string accountId, FileManifest manifest, byte[]? fileKey = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(manifest);
        if (chatId == 0) throw new ArgumentOutOfRangeException(nameof(chatId));
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (manifest.AccountId is not null && !string.Equals(manifest.AccountId, accountId, StringComparison.Ordinal))
            throw new InvalidDataException("The media manifest belongs to a different account.");
        ManifestValidator.ValidateStructure(manifest);
        if (!manifest.Committed || manifest.IsInTrash ||
            manifest.Parts.Any(part => !part.Confirmed || string.IsNullOrWhiteSpace(part.RemoteId)))
            throw new InvalidOperationException("Only committed, available files can be streamed by this source.");
        if (manifest.Encryption is null && fileKey is not null)
            throw new ArgumentException("A content key was supplied for an unencrypted manifest.", nameof(fileKey));
        if (manifest.Encryption is not null && (fileKey is null || fileKey.Length != 32))
            throw new InvalidOperationException("Unlock this encrypted file before creating its streaming source.");
        foreach (var part in manifest.Parts)
        {
            if (TelegramRemoteMessageId.Parse(part.RemoteId!).ChatId != chatId)
                throw new InvalidDataException("The media manifest contains a part outside the active storage chat.");
        }

        _session = session;
        _chatId = chatId;
        _manifest = manifest with { Parts = manifest.Parts.ToArray() };
        _fileKey = fileKey?.ToArray();
    }

    public Task<long> GetSizeAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_manifest.LogicalSize);
    }

    public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count is < 1 or > MaximumReadBytes) throw new ArgumentOutOfRangeException(nameof(count));
        if (offset >= _manifest.LogicalSize) return [];

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _readGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            return _manifest.Encryption is null
                ? await ReadPlaintextAsync(offset, count, linked.Token).ConfigureAwait(false)
                : await ReadEncryptedAsync(offset, count, linked.Token).ConfigureAwait(false);
        }
        finally { _readGate.Release(); }
    }

    private async Task<byte[]> ReadPlaintextAsync(long offset, int count, CancellationToken cancellationToken)
    {
        var part = FindPayloadPart(offset);
        var expected = (int)Math.Min(count, part.Length - (offset - part.Offset));
        if (expected <= 0) throw new InvalidDataException("The committed media manifest contains a gap.");
        return await ReadPayloadRangeAsync(offset, expected, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> ReadEncryptedAsync(long offset, int count, CancellationToken cancellationToken)
    {
        var descriptor = _manifest.Encryption!;
        var header = await GetEncryptedHeaderAsync(cancellationToken).ConfigureAwait(false);
        var layout = _frameLayout!.Value;
        var frameIndex = checked((uint)(offset / layout.FrameSize));
        var plaintextFrameStart = (long)frameIndex * layout.FrameSize;
        var insideFrame = (int)(offset - plaintextFrameStart);
        var frameLength = AesGcmFileCipher.GetFramePlaintextLength(layout, frameIndex);
        var wanted = Math.Min(count, frameLength - insideFrame);
        if (_cachedFrameIndex != frameIndex)
        {
            if (_cachedPlainFrame is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(_cachedPlainFrame);
            var storedLength = checked(frameLength + 16);
            var encrypted = await ReadPayloadRangeAsync(AesGcmFileCipher.GetFrameOffset(layout, frameIndex), storedLength, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                _cachedPlainFrame = AesGcmFileCipher.DecryptFrame(header, frameIndex, encrypted, _fileKey!);
                _cachedFrameIndex = frameIndex;
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(encrypted); }
        }
        return _cachedPlainFrame!.AsSpan(insideFrame, wanted).ToArray();
    }

    private async Task<byte[]> GetEncryptedHeaderAsync(CancellationToken cancellationToken)
    {
        if (_encryptedHeader is not null) return _encryptedHeader;
        var descriptor = _manifest.Encryption!;
        var header = await ReadPayloadRangeAsync(0, 33, cancellationToken).ConfigureAwait(false);
        try
        {
            var layout = AesGcmFileCipher.ParseFrameLayout(header, _manifest.LogicalSize, descriptor.PayloadSize);
            _encryptedHeader = header;
            _frameLayout = layout;
            return header;
        }
        catch
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(header);
            throw;
        }
    }

    private PartRecord FindPayloadPart(long offset)
    {
        var payloadSize = _manifest.Encryption?.PayloadSize ?? _manifest.LogicalSize;
        if (offset < 0 || offset >= payloadSize) throw new ArgumentOutOfRangeException(nameof(offset));
        var low = 0;
        var high = _manifest.Parts.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var part = _manifest.Parts[middle];
            if (offset < part.Offset) high = middle - 1;
            else if (offset >= part.Offset + part.Length) low = middle + 1;
            else return part;
        }
        throw new InvalidDataException("The committed media manifest does not cover the requested byte.");
    }

    private async Task<byte[]> ReadPayloadRangeAsync(long offset, int count, CancellationToken cancellationToken)
    {
        var payloadSize = _manifest.Encryption?.PayloadSize ?? _manifest.LogicalSize;
        if (offset < 0 || count < 1 || offset > payloadSize - count)
            throw new InvalidDataException("The requested media byte range is outside the committed payload.");
        var result = new byte[count];
        var written = 0;
        try
        {
            while (written < count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentOffset = offset + written;
                var part = FindPayloadPart(currentOffset);
                var insidePart = currentOffset - part.Offset;
                var wanted = (int)Math.Min(Math.Min(count - written, part.Length - insidePart), MaximumReadBytes);
                if (wanted <= 0) throw new InvalidDataException("The committed media manifest contains a part gap.");
                var fileId = await ResolvePartFileIdAsync(part, cancellationToken).ConfigureAwait(false);
                await EnsureRangeAvailableAsync(fileId, part, insidePart, wanted, cancellationToken).ConfigureAwait(false);
                var response = await _session.ExecuteAsync(new JsonObject
                {
                    ["@type"] = "readFilePart", ["file_id"] = fileId, ["offset"] = insidePart, ["count"] = wanted
                }, cancellationToken).ConfigureAwait(false);
                if (response["@type"]?.GetValue<string>() != "data" || response["bytes"] is not JsonValue encoded ||
                    !encoded.TryGetValue<string>(out var base64))
                    throw new InvalidDataException("TDLib returned an invalid media byte range.");
                byte[] bytes;
                try { bytes = Convert.FromBase64String(base64); }
                catch (FormatException ex) { throw new InvalidDataException("TDLib returned malformed media bytes.", ex); }
                try
                {
                    if (bytes.Length != wanted)
                        throw new EndOfStreamException("TDLib returned fewer bytes than the committed part requires.");
                    bytes.CopyTo(result, written);
                }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
                written += wanted;
            }
            return result;
        }
        catch
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(result);
            throw;
        }
    }

    private async Task<int> ResolvePartFileIdAsync(PartRecord part, CancellationToken cancellationToken)
    {
        if (_nativeFileIds.TryGetValue(part.Index, out var cached)) return cached;
        var locator = TelegramRemoteMessageId.Parse(part.RemoteId!);
        if (locator.ChatId != _chatId)
            throw new InvalidDataException("The media part locator is outside the active storage chat.");

        var message = await _session.ExecuteAsync(new JsonObject
        {
            ["@type"] = "getMessage",
            ["chat_id"] = _chatId,
            ["message_id"] = locator.MessageId
        }, cancellationToken).ConfigureAwait(false);
        var document = TelegramDedupDocument.Read(message, _chatId, locator.MessageId,
            _manifest.FileId, part.Index, part.Length);
        _nativeFileIds.Add(part.Index, document.FileId);
        return document.FileId;
    }

    private async Task EnsureRangeAvailableAsync(int fileId, PartRecord part, long offset, int count, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);
        var token = timeout.Token;
        var state = await GetFileStateAsync(fileId, token).ConfigureAwait(false);
        ValidateKnownSize(state, part);
        if (Covers(state, offset, count)) return;

        // Do not replace another queue/download owner's active range. Let it finish,
        // then request only the bytes needed for this player read.
        while (IsDownloading(state) && !Covers(state, offset, count))
        {
            state = await WaitForFileStateAsync(fileId, token).ConfigureAwait(false);
            ValidateKnownSize(state, part);
            if (Covers(state, offset, count)) return;
        }

        if (state.Completed) return;
        if (!state.CanDownload) throw new IOException("TDLib cannot download this media part in the active session.");
        var startResponse = await _session.ExecuteAsync(new JsonObject
        {
            ["@type"] = "downloadFile",
            ["file_id"] = fileId,
            ["priority"] = 32,
            ["offset"] = offset,
            ["limit"] = count,
            ["synchronous"] = false
        }, token).ConfigureAwait(false);
        state = ParseFileState(startResponse, fileId);

        while (!Covers(state, offset, count))
        {
            token.ThrowIfCancellationRequested();
            if (!IsDownloading(state))
                throw new IOException("TDLib stopped before the requested media range became available.");
            state = await WaitForFileStateAsync(fileId, token).ConfigureAwait(false);
            ValidateKnownSize(state, part);
        }
    }

    private async Task<FileState> WaitForFileStateAsync(int fileId, CancellationToken cancellationToken)
    {
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<JsonObject> handler = (_, update) =>
        {
            if (update["@type"]?.GetValue<string>() == "updateFile" && ReadInt(update["file"]?["id"]) == fileId)
                updated.TrySetResult();
        };
        _session.UpdateReceived += handler;
        try
        {
            var fallback = Task.Delay(UpdateFallbackInterval, cancellationToken);
            await Task.WhenAny(updated.Task, fallback).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await GetFileStateAsync(fileId, cancellationToken).ConfigureAwait(false);
        }
        finally { _session.UpdateReceived -= handler; }
    }

    private async Task<FileState> GetFileStateAsync(int fileId, CancellationToken cancellationToken)
    {
        var response = await _session.ExecuteAsync(new JsonObject { ["@type"] = "getFile", ["file_id"] = fileId }, cancellationToken)
            .ConfigureAwait(false);
        return ParseFileState(response, fileId);
    }

    private static FileState ParseFileState(JsonObject response, int expectedId)
    {
        if (response["@type"]?.GetValue<string>() != "file" || ReadInt(response["id"]) != expectedId ||
            response["local"] is not JsonObject local)
            throw new InvalidDataException("TDLib returned invalid media file state.");
        return new FileState(
            ReadLong(response["size"]),
            ReadBool(local["can_be_downloaded"]),
            ReadBool(local["is_downloading_active"]),
            ReadBool(local["is_downloading_completed"]),
            ReadLong(local["download_offset"]),
            ReadLong(local["downloaded_prefix_size"]));
    }

    private static void ValidateKnownSize(FileState state, PartRecord part)
    {
        if (state.Size > 0 && state.Size != part.Length)
            throw new InvalidDataException("TDLib part size no longer matches the committed manifest.");
    }

    private static bool Covers(FileState state, long offset, int count) => state.Completed ||
        (offset >= state.DownloadOffset && offset + count <= state.DownloadOffset + state.DownloadedPrefixSize);

    private static bool IsDownloading(FileState state) => state.Active;

    private static int ReadInt(JsonNode? node) => int.TryParse(node?.ToString(), out var value) ? value : 0;
    private static long ReadLong(JsonNode? node) => long.TryParse(node?.ToString(), out var value) ? value : 0;
    private static bool ReadBool(JsonNode? node) => node?.GetValue<bool>() == true;

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TelegramManifestMediaByteSource));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        await _readGate.WaitAsync().ConfigureAwait(false);
        if (_fileKey is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(_fileKey);
        if (_encryptedHeader is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(_encryptedHeader);
        if (_cachedPlainFrame is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(_cachedPlainFrame);
        _readGate.Release();
        _readGate.Dispose();
        _lifetime.Dispose();
    }

    private sealed record FileState(long Size, bool CanDownload, bool Active, bool Completed,
        long DownloadOffset, long DownloadedPrefixSize);
}
