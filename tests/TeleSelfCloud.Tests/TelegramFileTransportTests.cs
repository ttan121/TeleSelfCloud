using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class TelegramFileTransportTests
{
    [Fact]
    public async Task ProtectedFolderPublisherReplaysCiphertextAndPreservesTombstones()
    {
        var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.ProtectedFolders", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var key = VaultMetadataKey.Create("account-a", -100123);
            var folders = new SqliteLocalFolderStore(Path.Combine(root, "db.sqlite"));
            await folders.CreateAsync("account-a", "Private-finance", default);
            await folders.CreateAsync("account-a", "Deleted-secret", default);
            await folders.DeleteAsync("account-a", "Deleted-secret", default);
            var client = new FakeUpdateSource { SendUpdate = (_, caption) => SendSucceeded(-100123, 456, caption) };
            var transport = new TelegramFileTransport(client, -100123, Path.Combine(root, "downloads"));
            var publisher = new TelegramRemoteFolderStatePublisher(folders, transport, Path.Combine(root, "publish"), key);
            await publisher.PublishAsync("account-a", default);
            var first = client.SentDocumentContents!.ToArray(); var caption = client.SentCaption;
            Assert.StartsWith("TSC-FOLDERS|3|account-a|", caption);
            Assert.DoesNotContain("Private-finance", System.Text.Encoding.UTF8.GetString(first));
            Assert.DoesNotContain("Deleted-secret", System.Text.Encoding.UTF8.GetString(first));
            await publisher.PublishAsync("account-a", default);
            Assert.Equal(first, client.SentDocumentContents); Assert.Equal(caption, client.SentCaption);
            using var decoded = JsonDocument.Parse(key.Unprotect("folders", "account-a", first));
            Assert.Equal(2, decoded.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("Private-finance", decoded.RootElement.GetProperty("folders")[0].GetProperty("path").GetString());
            Assert.Equal("Deleted-secret", decoded.RootElement.GetProperty("tombstones")[0].GetProperty("path").GetString());
            Assert.Single(Directory.GetFiles(Path.Combine(root, "publish", "protected-outbox"), "*.json"));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "publish"), "folder-state-*.json"));
            using var foreign = VaultMetadataKey.Create("account-a", -100124);
            await Assert.ThrowsAsync<InvalidDataException>(() => new TelegramRemoteFolderStatePublisher(folders, transport, Path.Combine(root, "other"), foreign).PublishAsync("account-a", default));
            Assert.Equal(2, client.SendCount);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManifestPublisherRejectsCaptionDelimiterBeforeSending(bool encrypted)
    {
        var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.CaptionGuard", Guid.NewGuid().ToString("N"));
        try
        {
            using var key = VaultMetadataKey.Create("account-a", -100123);
            var hash = new string('A', 64);
            var manifest = new FileManifest(1, "file|ambiguous", "safe.bin", 1, hash, 1, [new(0, 0, 1, hash, "-100123/44", true)], true, "account-a");
            var client = new FakeUpdateSource();
            var transport = new TelegramFileTransport(client, -100123, Path.Combine(root, "downloads"));
            await Assert.ThrowsAsync<InvalidDataException>(() => new TelegramManifestPublisher(transport, Path.Combine(root, "publish"), encrypted ? key : null).PublishCommittedAsync(manifest, default));
            Assert.Equal(0, client.SendCount); Assert.False(Directory.Exists(Path.Combine(root, "publish")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ProtectedManifestPublisherKeepsExactCiphertextForReplayAndHidesOrganization()
    {
        var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.ProtectedPublisher", Guid.NewGuid().ToString("N"));
        try
        {
            using var key = VaultMetadataKey.Create("account-a", -100123);
            var hash = Convert.ToHexString(SHA256.HashData(new byte[] { 1 }));
            var manifest = new FileManifest(1, "file", "Secret-finance.bin", 1, hash, 1,
                [new(0, 0, 1, hash, "-100123/44", true, "C:/sensitive/cache")], true, "account-a", "Private/Finance");
            var client = new FakeUpdateSource { SendUpdate = (_, caption) => SendSucceeded(-100123, 456, caption) };
            var transport = new TelegramFileTransport(client, -100123, Path.Combine(root, "downloads"));
            var publisher = new TelegramManifestPublisher(transport, Path.Combine(root, "publish"), key);
            await publisher.PublishCommittedAsync(manifest, default);
            var first = client.SentDocumentContents!.ToArray(); var caption = client.SentCaption;
            Assert.StartsWith("TSC-MANIFEST|2|file|", caption);
            Assert.DoesNotContain("Secret-finance", System.Text.Encoding.UTF8.GetString(first));
            await publisher.PublishCommittedAsync(manifest, default);
            Assert.Equal(first, client.SentDocumentContents); Assert.Equal(caption, client.SentCaption);
            var decoded = JsonSerializer.Deserialize<FileManifest>(key.Unprotect("manifest", "file", first), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Equal("Secret-finance.bin", decoded.FileName); Assert.Equal("Private/Finance", decoded.FolderPath);
            Assert.Null(decoded.Parts[0].StagingPath);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "publish", "protected-outbox"), "*.json"));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "publish"), "*.manifest.json"));
            using var foreign = VaultMetadataKey.Create("account-a", -100124);
            await Assert.ThrowsAsync<InvalidDataException>(() => new TelegramManifestPublisher(transport, Path.Combine(root, "other"), foreign).PublishCommittedAsync(manifest, default));
            Assert.Equal(2, client.SendCount);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task ManifestPublisher_RemovesLocalEncryptedAndPartStagingPathsFromRemoteManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud-TelegramManifestPublisherTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var partPath = Path.Combine(root, "part.bin");
            var encryptedPath = Path.Combine(root, "encrypted.bin");
            var bytes = Enumerable.Range(0, 49).Select(value => (byte)value).ToArray();
            await File.WriteAllBytesAsync(partPath, bytes);
            await File.WriteAllBytesAsync(encryptedPath, bytes);
            var key = AesGcmFileCipher.CreateFileKey();
            var envelope = AesGcmFileCipher.WrapFileKey(key, "correct horse battery staple");
            CryptographicOperations.ZeroMemory(key);
            var fileHash = Convert.ToHexString(SHA256.HashData(new byte[] { 5 }));
            var manifest = new FileManifest(1, "encrypted-remote", "visible-name.bin", 1, fileHash, bytes.Length,
                new[] { new PartRecord(0, 0, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), "-100123/44", true, partPath) }, true,
                AccountId: "account-a", Encryption: new EncryptedPayloadDescriptor(1, bytes.Length,
                    Convert.ToHexString(SHA256.HashData(bytes)), envelope, encryptedPath));
            var client = new FakeUpdateSource
            {
                SendUpdate = (_, caption) => SendSucceeded(-100123, 456, caption)
            };
            var transport = new TelegramFileTransport(client, -100123, Path.Combine(root, "downloads"));

            await new TelegramManifestPublisher(transport, Path.Combine(root, "temp-manifests"))
                .PublishCommittedAsync(manifest, CancellationToken.None);

            var portable = JsonSerializer.Deserialize<FileManifest>(client.SentDocumentContents!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Null(portable.Parts.Single().StagingPath);
            Assert.Null(portable.Encryption!.StagingPath);
            Assert.Equal(envelope, portable.Encryption.RecoveryKey);
            Assert.DoesNotContain(partPath, System.Text.Encoding.UTF8.GetString(client.SentDocumentContents!));
            Assert.DoesNotContain(encryptedPath, System.Text.Encoding.UTF8.GetString(client.SentDocumentContents!));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task UploadPart_ConvertsSendFailedUpdateToBoundedFloodWaitRetry()
    {
        var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud-TelegramFileTransportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "part.bin");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var delays = new List<TimeSpan>();
        var client = new FakeUpdateSource();
        var transport = new TelegramFileTransport(client, -100123, Path.Combine(root, "downloads"));
        var retry = new TransferRetryPolicy((delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });

        try
        {
            client.SendUpdate = (attempt, caption) => attempt == 1
                ? SendFailed(-100123, -7, caption, 420, "FLOOD_WAIT_3")
                : SendSucceeded(-100123, 456, caption);

            var remoteId = await retry.ExecuteAsync(
                token => transport.UploadPartAsync(source, "file-1", 0, token), CancellationToken.None);

            Assert.Equal("-100123/456", remoteId);
            Assert.Equal(2, client.SendCount);
            Assert.Equal([TimeSpan.FromSeconds(3)], delays);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UploadPart_DoesNotRetryRejectedSendUpdate()
    {
        var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud-TelegramFileTransportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "part.bin");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var client = new FakeUpdateSource
        {
            SendUpdate = (_, caption) => SendFailed(-100123, -7, caption, 400, "FILE_PART_INVALID")
        };
        var transport = new TelegramFileTransport(client, -100123, Path.Combine(root, "downloads"));
        var retry = new TransferRetryPolicy((_, _) => Task.CompletedTask);

        try
        {
            var error = await Assert.ThrowsAsync<TelegramRequestException>(() => retry.ExecuteAsync(
                token => transport.UploadPartAsync(source, "file-1", 0, token), CancellationToken.None));

            Assert.Equal(400, error.ErrorCode);
            Assert.Equal(1, client.SendCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectedSend_FailsQueueWithCheckpointThenRetryCompletes()
    {
        var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud-QueueSendFailureTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "transfers.db");
        var source = Path.Combine(root, "payload.bin");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
        var manifests = new SqliteManifestStore(database);
        var queue = new SqliteTransferQueueStore(database);
        var client = new FakeUpdateSource
        {
            SendUpdate = (attempt, caption) => attempt == 2
                ? SendFailed(-100123, -7, caption, 400, "FILE_PART_INVALID")
                : SendSucceeded(-100123, 456 + attempt, caption)
        };
        var transport = new TelegramFileTransport(client, -100123, Path.Combine(root, "downloads"));
        var pipeline = new UploadPipeline(
            new FileTransferCoordinator(),
            new FixedCapabilityProvider(new UploadCapability("acct-A", 2, DateTimeOffset.UtcNow, "test")),
            transport,
            manifests,
            new NoOpManifestPublisher());

        try
        {
            var staged = await pipeline.StageForUploadAsync(source, Path.Combine(root, "staging"), 2, CancellationToken.None, forceChunking: true);
            await queue.EnqueueAsync(staged.FileId, staged.FileName, staged.LogicalSize, CancellationToken.None);
            var queued = Assert.Single(await queue.ListAsync(CancellationToken.None));
            var guard = new TransferQueueStartGuard(queue);

            var error = await Assert.ThrowsAsync<TelegramRequestException>(() => guard.RunAsync(
                queued.TaskId,
                () => Task.CompletedTask,
                async () => await new TransferRetryPolicy((_, _) => Task.CompletedTask).ExecuteAsync(
                    token => pipeline.ResumeAsync(staged.FileId, token, progress =>
                        queue.UpdateProgressAsync(queued.TaskId, progress.TransferredBytes, progress.TotalBytes, CancellationToken.None)),
                    CancellationToken.None)));

            Assert.Equal("FILE_PART_INVALID", error.Message);
            var failedQueueItem = Assert.Single(await queue.ListAsync(CancellationToken.None));
            var checkpointedManifest = (await manifests.LoadAsync(staged.FileId, CancellationToken.None))!;
            Assert.Equal(TransferQueueState.Failed, failedQueueItem.State);
            Assert.Equal(2, failedQueueItem.TransferredBytes);
            Assert.Contains("FILE_PART_INVALID", failedQueueItem.LastError);
            Assert.Equal(new[] { true, false }, checkpointedManifest.Parts.Select(part => part.Confirmed));

            await queue.RequeueForRetryAsync(queued.TaskId, CancellationToken.None);
            await guard.RunAsync(queued.TaskId, () => Task.CompletedTask, async () =>
                await new TransferRetryPolicy((_, _) => Task.CompletedTask).ExecuteAsync(
                    token => pipeline.ResumeAsync(staged.FileId, token, progress =>
                        queue.UpdateProgressAsync(queued.TaskId, progress.TransferredBytes, progress.TotalBytes, CancellationToken.None)),
                    CancellationToken.None));

            var completed = Assert.Single(await queue.ListAsync(CancellationToken.None));
            Assert.Equal(TransferQueueState.Completed, completed.State);
            Assert.Equal(4, completed.TransferredBytes);
            Assert.True((await manifests.LoadAsync(staged.FileId, CancellationToken.None))!.Committed);
            Assert.Equal(3, client.SendCount);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static JsonObject SendFailed(long chatId, long messageId, string caption, int code, string detail) => new()
    {
        ["@type"] = "updateMessageSendFailed",
        ["message"] = new JsonObject
        {
            ["id"] = messageId,
            ["chat_id"] = chatId,
            ["content"] = new JsonObject { ["caption"] = new JsonObject { ["text"] = caption } }
        },
        ["error"] = new JsonObject { ["code"] = code, ["message"] = detail }
    };

    private static JsonObject SendSucceeded(long chatId, long messageId, string caption) => new()
    {
        ["@type"] = "updateMessageSendSucceeded",
        ["old_message_id"] = -7L,
        ["message"] = new JsonObject
        {
            ["id"] = messageId,
            ["chat_id"] = chatId,
            ["content"] = new JsonObject { ["caption"] = new JsonObject { ["text"] = caption } }
        }
    };

    private sealed class FakeUpdateSource : ITelegramUpdateSource
    {
        public event EventHandler<JsonObject>? UpdateReceived;
        public Func<int, string, JsonObject>? SendUpdate { get; set; }
        public int SendCount { get; private set; }
        public byte[]? SentDocumentContents { get; private set; }
        public string? SentCaption { get; private set; }

        public Task<JsonObject> ExecuteAsync(JsonObject request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request["@type"]?.GetValue<string>() == "getChatHistory")
                return Task.FromResult(new JsonObject { ["@type"] = "messages", ["messages"] = new JsonArray() });
            if (request["@type"]?.GetValue<string>() != "sendMessage")
                throw new InvalidOperationException("Unexpected test request.");

            var attempt = ++SendCount;
            var caption = request["input_message_content"]?["caption"]?["text"]?.GetValue<string>()
                ?? throw new InvalidDataException("Test send has no caption.");
            SentCaption = caption;
            var documentPath = request["input_message_content"]?["document"]?["document"]?["path"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(documentPath)) SentDocumentContents = File.ReadAllBytes(documentPath);
            UpdateReceived?.Invoke(this, SendUpdate!(attempt, caption));
            return Task.FromResult(new JsonObject { ["@type"] = "message", ["id"] = -7L, ["chat_id"] = -100123L });
        }
    }

    private sealed class FixedCapabilityProvider(UploadCapability capability) : IUploadCapabilityProvider
    {
        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<UploadCapability?> GetCurrentAsync(CancellationToken cancellationToken) => Task.FromResult<UploadCapability?>(capability);
    }

    private sealed class NoOpManifestPublisher : IRemoteManifestPublisher
    {
        public Task PublishCommittedAsync(FileManifest manifest, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
