using System.Security.Cryptography;
using System.Text.Json.Nodes;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class TelegramManifestMediaByteSourceTests
{
    [Fact]
    public async Task ReadsBoundedRandomRangesAcrossCommittedMultipartPlaintext()
    {
        var session = new FakeSession(-100, new Dictionary<long, byte[]>
        {
            [11] = "ABC"u8.ToArray(),
            [12] = "DEFG"u8.ToArray()
        });
        var manifest = Manifest("ABCDEFG"u8.ToArray(), ["ABC"u8.ToArray(), "DEFG"u8.ToArray()]);
        await using var source = new TelegramManifestMediaByteSource(session, -100, "account", manifest);

        Assert.Equal(7, await source.GetSizeAsync(default));
        var first = await source.ReadAsync(2, 4, default);
        var second = await source.ReadAsync(3, 3, default);
        try
        {
            Assert.Equal("C", System.Text.Encoding.UTF8.GetString(first));
            Assert.Equal("DEF", System.Text.Encoding.UTF8.GetString(second));
            Assert.Equal(new[] { (100, 2L, 1), (101, 0L, 3) }, session.RangeRequests);
            Assert.Equal(new long[] { 11, 12 }, session.ResolvedMessageIds);
            Assert.DoesNotContain(session.RequestTypes, type => type == "downloadFile" && session.RangeRequests.Any(r => r.Limit == 0));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(first);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(second);
        }
    }

    [Fact]
    public void RejectsForeignChatAndInvalidOwnerBeforeRequestingBytes()
    {
        var session = new FakeSession(-100, new Dictionary<long, byte[]> { [11] = "ABC"u8.ToArray() });
        var manifest = Manifest("ABC"u8.ToArray(), ["ABC"u8.ToArray()]) with
        {
            Parts = [new PartRecord(0, 0, 3, Convert.ToHexString(SHA256.HashData("ABC"u8)), "-200/11", true)]
        };
        Assert.Throws<InvalidDataException>(() => new TelegramManifestMediaByteSource(session, -100, "account", manifest));
        Assert.Empty(session.RequestTypes);
    }

    [Fact]
    public async Task CancelsWaitWithoutReplacingAnExistingTdlibDownload()
    {
        var session = new FakeSession(-100, new Dictionary<long, byte[]> { [11] = "ABC"u8.ToArray() })
        {
            KeepDownloadActive = true
        };
        await using var source = new TelegramManifestMediaByteSource(session, -100, "account",
            Manifest("ABC"u8.ToArray(), ["ABC"u8.ToArray()]));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReadAsync(0, 2, cancellation.Token));
        Assert.DoesNotContain("downloadFile", session.RequestTypes);
    }

    [Fact]
    public async Task AbortingLoopbackRequestCancelsTdlibWaitAndReleasesUpdateSubscription()
    {
        var session = new FakeSession(-100, new Dictionary<long, byte[]> { [11] = "ABC"u8.ToArray() })
        {
            KeepDownloadActive = true
        };
        var tdlibSource = new TelegramManifestMediaByteSource(session, -100, "account",
            Manifest("ABC"u8.ToArray(), ["ABC"u8.ToArray()]));
        var source = new TrackingByteSource(tdlibSource);
        using var server = new MediaStreamServer(new ThrowingWorkflow(), Path.Combine(Path.GetTempPath(), "TeleSelfCloud.StreamCancellation", Guid.NewGuid().ToString("N")));
        using var client = new HttpClient();
        using var requestCancellation = new CancellationTokenSource();
        var url = server.StartServer("clip.mp4", _ => Task.FromResult<IMediaByteSource>(source));

        var request = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, requestCancellation.Token);
        await session.UpdateWaitSubscribed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        requestCancellation.Cancel();

        await source.ReadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(0, session.UpdateSubscriberCount);
        Assert.DoesNotContain("downloadFile", session.RequestTypes);
        Assert.False(source.Disposed.Task.IsCompleted, "A disconnected client must not end the preview's reusable byte-source lifetime.");
        server.StopServer();
        await source.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task MatchingUpdateFileWakesRangeWaitAndStateIsReRead()
    {
        var session = new FakeSession(-100, new Dictionary<long, byte[]> { [11] = "ABC"u8.ToArray() })
        {
            CompleteDownloadAfterUpdate = true
        };
        await using var source = new TelegramManifestMediaByteSource(session, -100, "account",
            Manifest("ABC"u8.ToArray(), ["ABC"u8.ToArray()]));

        var bytes = await source.ReadAsync(1, 2, default);
        try
        {
            Assert.Equal("BC", System.Text.Encoding.UTF8.GetString(bytes));
            Assert.Contains("downloadFile", session.RequestTypes);
            Assert.True(session.GetFileRequests >= 2);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [Fact]
    public async Task AuthenticatesEncryptedFrameAfterAssemblingCiphertextAcrossTelegramParts()
    {
        var logical = "abcdefghij"u8.ToArray();
        var key = AesGcmFileCipher.CreateFileKey();
        try
        {
            using var encrypted = new MemoryStream();
            await AesGcmFileCipher.EncryptAsync(new MemoryStream(logical), encrypted, key, frameSize: 4);
            var payload = encrypted.ToArray();
            var parts = Split(payload, 11);
            var manifest = EncryptedManifest(logical, payload, parts);
            var session = new FakeSession(-100, new Dictionary<long, byte[]>(parts.Select((part, index) => KeyValuePair.Create(11L + index, part))));
            await using var source = new TelegramManifestMediaByteSource(session, -100, "account", manifest, key);

            var first = await source.ReadAsync(2, 6, default);
            var second = await source.ReadAsync(4, 4, default);
            var third = await source.ReadAsync(8, 2, default);
            try
            {
                Assert.Equal("cd", System.Text.Encoding.UTF8.GetString(first));
                Assert.Equal("efgh", System.Text.Encoding.UTF8.GetString(second));
                Assert.Equal("ij", System.Text.Encoding.UTF8.GetString(third));
                Assert.Contains(session.RangeRequests, read => read.Limit > 0);
                Assert.Equal(10, await source.GetSizeAsync(default));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(first);
                CryptographicOperations.ZeroMemory(second);
                CryptographicOperations.ZeroMemory(third);
            }
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    [Fact]
    public async Task TamperedEncryptedFrameReturnsNoPlaintext()
    {
        var logical = "abcdefghij"u8.ToArray();
        var key = AesGcmFileCipher.CreateFileKey();
        try
        {
            using var encrypted = new MemoryStream();
            await AesGcmFileCipher.EncryptAsync(new MemoryStream(logical), encrypted, key, frameSize: 4);
            var payload = encrypted.ToArray();
            payload[33 + 4] ^= 0x40;
            var parts = Split(payload, 15);
            var manifest = EncryptedManifest(logical, payload, parts);
            var session = new FakeSession(-100, new Dictionary<long, byte[]>(parts.Select((part, index) => KeyValuePair.Create(11L + index, part))));
            await using var source = new TelegramManifestMediaByteSource(session, -100, "account", manifest, key);

            await Assert.ThrowsAnyAsync<CryptographicException>(() => source.ReadAsync(0, 2, default));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    [Fact]
    public void RefusesEncryptedAndForeignAccountManifests()
    {
        var session = new FakeSession(-100, new Dictionary<long, byte[]> { [11] = "ABC"u8.ToArray() });
        var manifest = Manifest("ABC"u8.ToArray(), ["ABC"u8.ToArray()]);
        Assert.Throws<InvalidOperationException>(() => new TelegramManifestMediaByteSource(session, -100, "account", manifest with
        {
            Encryption = new EncryptedPayloadDescriptor(1, 3, new string('0', 64),
                new PassphraseKeyEnvelope(1, "PBKDF2-SHA256", 600000, "AES-256-GCM", "AAAAAAAAAAAAAAAAAAAAAA==",
                    "AAAAAAAAAAAAAAAA", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "AAAAAAAAAAAAAAAAAAAAAA=="))
        }));
        Assert.Throws<InvalidDataException>(() => new TelegramManifestMediaByteSource(session, -100, "another-account", manifest));
        Assert.Empty(session.RequestTypes);
    }

    private static FileManifest Manifest(byte[] logicalBytes, IReadOnlyList<byte[]> parts)
    {
        var records = new List<PartRecord>();
        long offset = 0;
        for (var index = 0; index < parts.Count; index++)
        {
            var bytes = parts[index];
            records.Add(new PartRecord(index, offset, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), $"-100/{11 + index}", true));
            offset += bytes.Length;
        }
        return new FileManifest(1, "media-file", "clip.mp4", logicalBytes.Length,
            Convert.ToHexString(SHA256.HashData(logicalBytes)), Math.Max(1, parts.Max(part => part.Length)),
            records, true, "account");
    }

    private static FileManifest EncryptedManifest(byte[] logical, byte[] payload, IReadOnlyList<byte[]> parts)
    {
        var records = new List<PartRecord>();
        long offset = 0;
        for (var index = 0; index < parts.Count; index++)
        {
            var bytes = parts[index];
            records.Add(new PartRecord(index, offset, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), $"-100/{11 + index}", true));
            offset += bytes.Length;
        }
        var envelope = new PassphraseKeyEnvelope(1, "PBKDF2-SHA256", 600000, "AES-256-GCM",
            Convert.ToBase64String(new byte[16]), Convert.ToBase64String(new byte[12]),
            Convert.ToBase64String(new byte[32]), Convert.ToBase64String(new byte[16]));
        return new FileManifest(1, "media-file", "clip.mp4", logical.Length,
            Convert.ToHexString(SHA256.HashData(logical)), parts.Max(part => part.Length), records, true, "account",
            Encryption: new EncryptedPayloadDescriptor(1, payload.Length,
                Convert.ToHexString(SHA256.HashData(payload)), envelope));
    }

    private static IReadOnlyList<byte[]> Split(byte[] payload, int partSize) =>
        Enumerable.Range(0, (payload.Length + partSize - 1) / partSize)
            .Select(index => payload.AsSpan(index * partSize, Math.Min(partSize, payload.Length - index * partSize)).ToArray())
            .ToArray();

    private sealed class FakeSession(long chatId, IReadOnlyDictionary<long, byte[]> messageBytes) : ITelegramUpdateSource
    {
        private readonly Dictionary<int, (long MessageId, byte[] Bytes)> _files = messageBytes
            .Select((pair, index) => (FileId: 100 + index, pair.Key, pair.Value))
            .ToDictionary(item => item.FileId, item => (item.Key, item.Value));
        private readonly Dictionary<int, bool> _started = [];
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _completed = new();
        private EventHandler<JsonObject>? _updateReceived;
        public TaskCompletionSource UpdateWaitSubscribed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> RequestTypes { get; } = [];
        public List<long> ResolvedMessageIds { get; } = [];
        public List<(int FileId, long Offset, int Limit)> RangeRequests { get; } = [];
        public bool KeepDownloadActive { get; init; }
        public bool CompleteDownloadAfterUpdate { get; init; }
        public int GetFileRequests { get; private set; }
        public int UpdateSubscriberCount => _updateReceived?.GetInvocationList().Length ?? 0;
        public event EventHandler<JsonObject>? UpdateReceived
        {
            add { _updateReceived += value; UpdateWaitSubscribed.TrySetResult(); }
            remove => _updateReceived -= value;
        }

        public Task<JsonObject> ExecuteAsync(JsonObject request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = request["@type"]!.GetValue<string>();
            RequestTypes.Add(type);
            var fileId = request["file_id"]?.GetValue<int>() ?? 0;
            switch (type)
            {
                case "getMessage":
                {
                    var messageId = request["message_id"]!.GetValue<long>();
                    ResolvedMessageIds.Add(messageId);
                    var entry = _files.Single(pair => pair.Value.MessageId == messageId);
                    var caption = $"TSC-PART|1|media-file|{messageId - 11}";
                    return Task.FromResult(new JsonObject
                    {
                        ["@type"] = "message", ["chat_id"] = chatId, ["id"] = messageId,
                        ["content"] = new JsonObject
                        {
                            ["@type"] = "messageDocument", ["caption"] = new JsonObject { ["text"] = caption },
                            ["document"] = new JsonObject
                            {
                                ["document"] = new JsonObject
                                {
                                    ["id"] = entry.Key, ["size"] = entry.Value.Bytes.Length,
                                    ["remote"] = new JsonObject { ["unique_id"] = $"unique-{entry.Key}", ["is_uploading_completed"] = true }
                                }
                            }
                        }
                    });
                }
                case "getFile":
                    GetFileRequests++;
                    return Task.FromResult(FileState(fileId,
                        active: !_completed.ContainsKey(fileId) && (KeepDownloadActive || _started.GetValueOrDefault(fileId)),
                        completed: _completed.ContainsKey(fileId), 0, _completed.ContainsKey(fileId) ? _files[fileId].Bytes.Length : 0));
                case "downloadFile":
                {
                    var offset = request["offset"]!.GetValue<long>();
                    var limit = request["limit"]!.GetValue<int>();
                    RangeRequests.Add((fileId, offset, limit));
                    _started[fileId] = true;
                    if (CompleteDownloadAfterUpdate)
                    {
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(50);
                            _completed[fileId] = true;
                            _updateReceived?.Invoke(this, new JsonObject
                            {
                                ["@type"] = "updateFile", ["file"] = new JsonObject { ["id"] = fileId }
                            });
                        });
                        return Task.FromResult(FileState(fileId, active: true, completed: false, 0, 0));
                    }
                    _completed[fileId] = true;
                    return Task.FromResult(FileState(fileId, active: false, completed: true, 0, _files[fileId].Bytes.Length));
                }
                case "readFilePart":
                {
                    var offset = request["offset"]!.GetValue<long>();
                    var count = request["count"]!.GetValue<int>();
                    var bytes = _files[fileId].Bytes.AsSpan((int)offset, count).ToArray();
                    return Task.FromResult(new JsonObject { ["@type"] = "data", ["bytes"] = Convert.ToBase64String(bytes) });
                }
                default: throw new InvalidOperationException($"Unexpected request {type}.");
            }
        }

        private JsonObject FileState(int fileId, bool active, bool completed, long offset, long prefix) => new()
        {
            ["@type"] = "file", ["id"] = fileId, ["size"] = _files[fileId].Bytes.Length,
            ["local"] = new JsonObject
            {
                ["can_be_downloaded"] = true, ["is_downloading_active"] = active,
                ["is_downloading_completed"] = completed, ["download_offset"] = offset,
                ["downloaded_prefix_size"] = prefix
            }
        };
    }

    private sealed class TrackingByteSource(IMediaByteSource inner) : IMediaByteSource
    {
        public TaskCompletionSource ReadCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<long> GetSizeAsync(CancellationToken cancellationToken) => inner.GetSizeAsync(cancellationToken);

        public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken cancellationToken)
        {
            try { return await inner.ReadAsync(offset, count, cancellationToken); }
            catch (OperationCanceledException) { ReadCancelled.TrySetResult(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            Disposed.TrySetResult();
        }
    }

    private sealed class ThrowingWorkflow : ILocalFileWorkflow
    {
        public Task<FileManifest> PrepareAsync(string sourcePath, string stagingRoot, long partSizeBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<FileManifest>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RestoreAsync(string fileId, string destinationPath, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
