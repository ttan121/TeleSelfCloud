using System.Security.Cryptography;
using System.Text.Json.Nodes;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class TelegramDocumentCopyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.DocumentCopy", Guid.NewGuid().ToString("N"));
    private PartRecord Source => new(0, 0, 3, Convert.ToHexString(SHA256.HashData("ABC"u8)), "-100/10", true);
    public TelegramDocumentCopyTests() => Directory.CreateDirectory(root);
    private sealed class Remote(string path, string? damage) : ITelegramUpdateSource
    {
        public event EventHandler<JsonObject>? UpdateReceived;
        public int Reads, Sends; public JsonObject? Input; public string? Caption;
        public Task<JsonObject> ExecuteAsync(JsonObject request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (request["@type"]!.GetValue<string>())
            {
                case "getMessage":
                    Reads++; return Task.FromResult(new JsonObject { ["@type"] = "message", ["chat_id"] = -100L, ["id"] = 10L,
                        ["content"] = new JsonObject { ["@type"] = "messageDocument", ["caption"] = new JsonObject { ["text"] = "TSC-PART|1|owner|0" },
                            ["document"] = new JsonObject { ["document"] = new JsonObject { ["id"] = 7, ["size"] = 3L,
                                ["remote"] = new JsonObject { ["is_uploading_completed"] = true, ["unique_id"] = damage == "changed" && Reads >= 3 ? "changed" : "stable" } } } } });
                case "downloadFile":
                    return Task.FromResult(new JsonObject { ["id"] = 7, ["local"] = new JsonObject { ["is_downloading_completed"] = true, ["path"] = path } });
                case "sendMessage":
                    Sends++; Input = (JsonObject)request["input_message_content"]!["document"]!["document"]!.DeepClone();
                    Caption = request["input_message_content"]!["caption"]!["text"]!.GetValue<string>();
                    var message = new JsonObject { ["@type"] = "message", ["chat_id"] = -100L, ["id"] = damage == "same-message" ? 10L : 20L,
                        ["content"] = new JsonObject { ["@type"] = "messageDocument", ["caption"] = new JsonObject { ["text"] = Caption },
                            ["document"] = new JsonObject { ["document"] = new JsonObject { ["id"] = 8, ["size"] = 3L,
                                ["remote"] = new JsonObject { ["is_uploading_completed"] = true, ["unique_id"] = damage == "bad-copy" ? "foreign" : "stable" } } } } };
                    UpdateReceived?.Invoke(this, new JsonObject { ["@type"] = "updateMessageSendSucceeded", ["old_message_id"] = -7L, ["message"] = message });
                    return Task.FromResult(new JsonObject { ["@type"] = "message", ["id"] = -7L, ["chat_id"] = -100L });
                default: throw new InvalidOperationException("Unexpected request.");
            }
        }
    }
    [Fact]
    public async Task VerifiedRemoteBytesSendAsInputFileIdWithIndependentCaptionAndMessage()
    {
        var path = Path.Combine(root, "source"); File.WriteAllBytes(path, "ABC"u8.ToArray()); var remote = new Remote(path, null);
        var transport = new TelegramFileTransport(remote, -100, Path.Combine(root, "download"));
        Assert.Equal("-100/20", await transport.CopyPartAsync(Source, "owner", "new-owner", 7, default));
        Assert.Equal("inputFileId", remote.Input!["@type"]!.GetValue<string>()); Assert.Equal(7, remote.Input["id"]!.GetValue<int>()); Assert.Null(remote.Input["path"]);
        Assert.Equal("TSC-PART|1|new-owner|7", remote.Caption); Assert.Equal(1, remote.Sends); Assert.Empty(Directory.GetFiles(Path.Combine(root, "download")));
    }
    [Theory]
    [InlineData("hash")]
    [InlineData("changed")]
    public async Task ChangedContentOrIdentityNeverSendsCopy(string damage)
    {
        var path = Path.Combine(root, "source"); File.WriteAllBytes(path, damage == "hash" ? "BAD"u8.ToArray() : "ABC"u8.ToArray()); var remote = new Remote(path, damage);
        var transport = new TelegramFileTransport(remote, -100, Path.Combine(root, "download"));
        await Assert.ThrowsAsync<InvalidDataException>(() => transport.CopyPartAsync(Source, "owner", "new-owner", 0, default)); Assert.Equal(0, remote.Sends); Assert.Empty(Directory.GetFiles(Path.Combine(root, "download")));
    }
    [Fact]
    public async Task ForeignVaultOrSameOwnerCannotRequestSourceAndSameMessageCannotEstablishOwnership()
    {
        var path = Path.Combine(root, "source"); File.WriteAllBytes(path, "ABC"u8.ToArray()); var remote = new Remote(path, "same-message");
        var transport = new TelegramFileTransport(remote, -100, Path.Combine(root, "download"));
        await Assert.ThrowsAsync<InvalidDataException>(() => transport.CopyPartAsync(Source with { RemoteId = "-200/10" }, "owner", "new-owner", 0, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => transport.CopyPartAsync(Source, "owner", "owner", 0, default)); Assert.Equal(0, remote.Reads);
        await Assert.ThrowsAsync<InvalidDataException>(() => transport.CopyPartAsync(Source, "owner", "new-owner", 0, default)); Assert.Equal(1, remote.Sends);
    }
    [Fact]
    public async Task CopyAcknowledgmentMustRetainVerifiedPhysicalFileIdentity()
    {
        var path = Path.Combine(root, "source"); File.WriteAllBytes(path, "ABC"u8.ToArray()); var remote = new Remote(path, "bad-copy");
        var transport = new TelegramFileTransport(remote, -100, Path.Combine(root, "download"));
        await Assert.ThrowsAsync<InvalidDataException>(() => transport.CopyPartAsync(Source, "owner", "new-owner", 0, default)); Assert.Equal(1, remote.Sends);
    }
    private sealed class Capability : IUploadCapabilityProvider
    {
        public Task RefreshAsync(CancellationToken token) => Task.CompletedTask;
        public Task<UploadCapability?> GetCurrentAsync(CancellationToken token) => Task.FromResult<UploadCapability?>(new("42", 1000, DateTimeOffset.UtcNow, "fixture"));
    }
    private sealed class Publisher : IRemoteManifestPublisher
    {
        public Task PublishCommittedAsync(FileManifest manifest, CancellationToken token) { Assert.All(manifest.Parts, p => Assert.Null(p.CopySource)); return Task.CompletedTask; }
    }
    [Fact]
    public async Task ActualTelegramPlannerAndPipelineReuseValidatedCandidateWithNewOwnership()
    {
        var path = Path.Combine(root, "source"); File.WriteAllBytes(path, "ABC"u8.ToArray()); var remote = new Remote(path, null);
        var transport = new TelegramFileTransport(remote, -100, Path.Combine(root, "download")); var store = new SqliteManifestStore(Path.Combine(root, "db"));
        var original = new FileManifest(1, "owner", "original.bin", 3, Source.Sha256, 3, [Source], true, "42"); await store.SaveAsync(original, default);
        var dedup = new TelegramUploadDeduplication(store, remote, transport, "42");
        var pipeline = new UploadPipeline(new FileTransferCoordinator(), new Capability(), transport, store, new Publisher(), dedup);
        var copied = await pipeline.UploadAsync(path, Path.Combine(root, "stage"), 3, default);
        Assert.True(copied.Committed); Assert.NotEqual(original.FileId, copied.FileId); Assert.Equal("-100/20", Assert.Single(copied.Parts).RemoteId);
        Assert.Equal("-100/10", (await store.LoadAsync("owner", default))!.Parts[0].RemoteId); Assert.Equal("inputFileId", remote.Input!["@type"]!.GetValue<string>());
        Assert.Equal($"TSC-PART|1|{copied.FileId}|0", remote.Caption); Assert.Equal(1, remote.Sends);
    }
    [Fact]
    public async Task PlannerRejectsDifferentVerifierSessionBeforeRemoteReads()
    {
        var path = Path.Combine(root, "source"); File.WriteAllBytes(path, "ABC"u8.ToArray()); var remote = new Remote(path, null); var other = new Remote(path, null);
        var transport = new TelegramFileTransport(remote, -100, Path.Combine(root, "download")); var store = new SqliteManifestStore(Path.Combine(root, "db"));
        var staged = new FileManifest(1, "new", "new.bin", 3, Source.Sha256, 3, [Source with { Confirmed = false, RemoteId = null }], false, "42");
        await Assert.ThrowsAsync<InvalidDataException>(() => new TelegramUploadDeduplication(store, other, transport, "42").PlanAsync(staged, default)); Assert.Equal(0, remote.Reads); Assert.Equal(0, other.Reads);
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
