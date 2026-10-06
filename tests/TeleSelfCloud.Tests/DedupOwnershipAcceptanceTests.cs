using System.Security.Cryptography;
using System.Text.Json.Nodes;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class DedupOwnershipAcceptanceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.DedupOwnership", Guid.NewGuid().ToString("N"));
    public DedupOwnershipAcceptanceTests() => Directory.CreateDirectory(root);
    internal sealed class World(string root) : ITelegramUpdateSource
    {
        private sealed record Document(int FileId, string Caption);
        private readonly Dictionary<long, Document> messages = []; private readonly Dictionary<int, byte[]> files = [];
        private long nextMessage = 1000; private int nextFile;
        public int LocalSends, IdSends, Deletes, Downloads; public event EventHandler<JsonObject>? UpdateReceived;
        private JsonObject Message(long id, Document document) => new()
        {
            ["@type"] = "message", ["id"] = id, ["chat_id"] = -100L,
            ["content"] = new JsonObject { ["@type"] = "messageDocument", ["caption"] = new JsonObject { ["text"] = document.Caption },
                ["document"] = new JsonObject { ["document"] = new JsonObject { ["id"] = document.FileId, ["size"] = (long)files[document.FileId].Length,
                    ["remote"] = new JsonObject { ["is_uploading_completed"] = true, ["unique_id"] = "physical-" + document.FileId } } } }
        };
        public Task<JsonObject> ExecuteAsync(JsonObject request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); JsonObject response;
            switch (request["@type"]!.GetValue<string>())
            {
                case "sendMessage":
                    var input = request["input_message_content"]!["document"]!["document"]!; int file;
                    if (input["@type"]!.GetValue<string>() == "inputFileLocal") { LocalSends++; file = ++nextFile; files[file] = File.ReadAllBytes(input["path"]!.GetValue<string>()); }
                    else { IdSends++; Assert.Equal("inputFileId", input["@type"]!.GetValue<string>()); file = input["id"]!.GetValue<int>(); Assert.True(files.ContainsKey(file)); }
                    var id = ++nextMessage; var document = new Document(file, request["input_message_content"]!["caption"]!["text"]!.GetValue<string>()); messages[id] = document;
                    UpdateReceived?.Invoke(this, new JsonObject { ["@type"] = "updateMessageSendSucceeded", ["old_message_id"] = -7L, ["message"] = Message(id, document) });
                    response = new() { ["@type"] = "message", ["id"] = -7L, ["chat_id"] = -100L }; break;
                case "getMessage":
                    var requested = request["message_id"]!.GetValue<long>();
                    if (!messages.TryGetValue(requested, out var found)) throw TelegramRequestException.From(400, "MESSAGE_ID_INVALID");
                    response = Message(requested, found); break;
                case "downloadFile":
                    Downloads++;
                    var nativeId = request["file_id"]!.GetValue<int>(); var path = Path.Combine(root, "native-" + nativeId); File.WriteAllBytes(path, files[nativeId]);
                    response = new() { ["id"] = nativeId, ["local"] = new JsonObject { ["is_downloading_completed"] = true, ["path"] = path } }; break;
                case "getChatHistory":
                    var cursor = request["from_message_id"]!.GetValue<long>();
                    response = new() { ["@type"] = "messages", ["messages"] = new JsonArray(messages.Where(p => cursor == 0 || p.Key <= cursor).OrderByDescending(p => p.Key).Take(2).Select(p => (JsonNode)Message(p.Key, p.Value)).ToArray()) }; break;
                case "deleteMessages":
                    Deletes++; foreach (var item in request["message_ids"]!.AsArray()) messages.Remove(item!.GetValue<long>()); response = new() { ["@type"] = "ok" }; break;
                default: throw new InvalidOperationException("Unexpected request.");
            }
            return Task.FromResult(response);
        }
    }
    private sealed class Capability : IUploadCapabilityProvider
    {
        public Task RefreshAsync(CancellationToken token) => Task.CompletedTask;
        public Task<UploadCapability?> GetCurrentAsync(CancellationToken token) => Task.FromResult<UploadCapability?>(new("42", 1000, DateTimeOffset.UtcNow, "fixture"));
    }
    [Theory]
    [InlineData(false, false, 3)]
    [InlineData(false, true, 3)]
    [InlineData(true, false, 3)]
    [InlineData(true, true, 3)]
    [InlineData(false, false, 2)]
    [InlineData(false, true, 2)]
    [InlineData(true, false, 2)]
    [InlineData(true, true, 2)]
    public async Task CopyResyncRestoreAndEitherDeletionKeepOtherIndependentOwner(bool protectedMetadata, bool deleteOriginal, int requestedCopyPartSize)
    {
        var source = Path.Combine(root, "source.bin"); var bytes = "ABCDEF"u8.ToArray(); File.WriteAllBytes(source, bytes);
        var world = new World(root); var transport = new TelegramFileTransport(world, -100, Path.Combine(root, "downloads"));
        using var key = protectedMetadata ? VaultMetadataKey.Create("42", -100) : null;
        var store = new SqliteManifestStore(Path.Combine(root, "db")); var publisher = new TelegramManifestPublisher(transport, Path.Combine(root, "publication"), key);
        var original = await new UploadPipeline(new FileTransferCoordinator(), new Capability(), transport, store, publisher).UploadAsync(source, Path.Combine(root, "stage"), 3, default, true);
        var copy = await new UploadPipeline(new FileTransferCoordinator(), new Capability(), transport, store, publisher, new TelegramUploadDeduplication(store, world, transport, "42"))
            .UploadAsync(source, Path.Combine(root, "stage"), requestedCopyPartSize, default, true);
        Assert.Equal(original.PartSizeBytes, copy.PartSizeBytes); Assert.Equal(original.Parts.Count, copy.Parts.Count);
        Assert.Equal(2, world.IdSends); Assert.Equal(4, world.LocalSends); Assert.Empty(original.Parts.Select(p => p.RemoteId).Intersect(copy.Parts.Select(p => p.RemoteId)));
        var fresh = new SqliteManifestStore(Path.Combine(root, "fresh"));
        await new TelegramRemoteManifestCatalog(world, transport, fresh, -100, "42", metadataKey: key).ImportRecentAsync(default, true);
        Assert.Equal(2, (await fresh.ListAsync(default)).Count);
        var target = deleteOriginal ? original : copy; var survivor = deleteOriginal ? copy : original;
        var restored = Path.Combine(root, "before.bin"); await new FileTransferCoordinator(transport).ReassembleAsync((await fresh.LoadAsync(survivor.FileId, default))!, restored, default); Assert.Equal(bytes, File.ReadAllBytes(restored));
        target = target with { Revision = 1, UpdatedAtUtc = DateTimeOffset.UtcNow, IsInTrash = true }; await publisher.PublishCommittedAsync(target, default);
        if (protectedMetadata)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => new TelegramRemoteFileDeleter(world, transport, -100, "42").DeleteTrashedFileAsync(target, default)); Assert.Equal(0, world.Deletes);
        }
        Assert.Equal(4, await new TelegramRemoteFileDeleter(world, transport, -100, "42", key).DeleteTrashedFileAsync(target, default));
        var after = new SqliteManifestStore(Path.Combine(root, "after")); await new TelegramRemoteManifestCatalog(world, transport, after, -100, "42", metadataKey: key).ImportRecentAsync(default, true);
        var remaining = Assert.Single(await after.ListAsync(default)); Assert.Equal(survivor.FileId, remaining.FileId);
        var final = Path.Combine(root, "after.bin"); await new FileTransferCoordinator(transport).ReassembleAsync(remaining, final, default); Assert.Equal(bytes, File.ReadAllBytes(final)); Assert.Equal(survivor.TotalSha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(final))));
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
