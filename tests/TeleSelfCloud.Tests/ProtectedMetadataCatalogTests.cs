using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class ProtectedMetadataCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.ProtectedCatalog", Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private const string Account = "42";
    private const long Chat = -100;
    [Fact]
    public async Task MixedHistoryDecryptsNewManifestAndFoldersWhileRetainingLegacyCompatibility()
    {
        using var key = VaultMetadataKey.Create(Account, Chat);
        var hash = new string('A', 64);
        var manifest = new FileManifest(1, "file", "private.bin", 1, hash, 1, [new(0, 0, 1, hash, "-100/10", true)], true, Account, "Secret", Revision: 2);
        var folder = new { SchemaVersion = 2, AccountId = Account, Folders = new[] { new LocalFolder(Account, "Secret", DateTimeOffset.UtcNow) }, Tombstones = Array.Empty<FolderTombstone>() };
        var transport = new Parts();
        transport.Bytes["-100/30"] = key.Protect("manifest", "file", JsonSerializer.SerializeToUtf8Bytes(manifest, Options));
        transport.Bytes["-100/25"] = key.Protect("folders", Account, JsonSerializer.SerializeToUtf8Bytes(folder, Options));
        transport.Bytes["-100/20"] = JsonSerializer.SerializeToUtf8Bytes(manifest with { FileName = "legacy.bin", Revision = 1 }, Options);
        var page = Page((30, Caption("TSC-MANIFEST|2|file|", transport.Bytes["-100/30"])),
            (25, Caption("TSC-FOLDERS|3|42|", transport.Bytes["-100/25"])), (20, Caption("TSC-MANIFEST|1|file|", transport.Bytes["-100/20"])));
        var db = Path.Combine(root, "db.sqlite"); Directory.CreateDirectory(root);
        var manifests = new SqliteManifestStore(db); var folders = new SqliteLocalFolderStore(db); var checkpoints = new SqliteRemoteSyncCheckpointStore(db);
        var catalog = new TelegramRemoteManifestCatalog(new History(page), transport, manifests, Chat, Account, checkpoints, folders, metadataKey: key);
        await catalog.ImportRecentAsync(default, true);
        Assert.Equal("private.bin", (await manifests.LoadAsync("file", default))!.FileName);
        Assert.Equal("Secret", (await folders.ListAsync(Account, default)).Single().Path);
        Assert.Equal(30, (await checkpoints.LoadAsync(Account, Chat, default))!.HighestMessageId);
        Assert.Equal(1, catalog.FilesIndexed); Assert.Equal(1, catalog.FolderSnapshotsFound);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrWrongMetadataKeyCannotAdvanceCheckpointOrReplaceLocalRows(bool wrong)
    {
        using var key = VaultMetadataKey.Create(Account, Chat); using var other = VaultMetadataKey.Create(Account, Chat);
        var hash = new string('A', 64);
        var manifest = new FileManifest(1, "file", "private.bin", 1, hash, 1, [new(0, 0, 1, hash, "-100/10", true)], true, Account);
        var transport = new Parts(); transport.Bytes["-100/30"] = key.Protect("manifest", "file", JsonSerializer.SerializeToUtf8Bytes(manifest, Options));
        var db = Path.Combine(root, "db.sqlite"); Directory.CreateDirectory(root);
        var manifests = new SqliteManifestStore(db); var checkpoints = new SqliteRemoteSyncCheckpointStore(db);
        await manifests.SaveAsync(manifest with { FileName = "retained.bin" }, default);
        var previous = new RemoteSyncCheckpoint(5, DateTimeOffset.UtcNow); await checkpoints.SaveAsync(Account, Chat, previous, default);
        var catalog = new TelegramRemoteManifestCatalog(new History(Page((30, Caption("TSC-MANIFEST|2|file|", transport.Bytes["-100/30"])))), transport, manifests, Chat, Account, checkpoints, metadataKey: wrong ? other : null);
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.ImportRecentAsync(default));
        Assert.Equal("retained.bin", (await manifests.LoadAsync("file", default))!.FileName);
        Assert.Equal(previous, await checkpoints.LoadAsync(Account, Chat, default));
        Assert.Null(catalog.LastSuccessfulSyncUtc); Assert.Empty(catalog.ObservedFileIds);
    }
    private static string Caption(string prefix, byte[] bytes) => prefix + Convert.ToHexString(SHA256.HashData(bytes));
    private static JsonObject Page(params (long Id, string Caption)[] entries) => new() { ["messages"] = new JsonArray(entries.Select(e => (JsonNode?)new JsonObject
    { ["id"] = e.Id, ["chat_id"] = Chat, ["content"] = new JsonObject { ["@type"] = "messageDocument", ["caption"] = new JsonObject { ["text"] = e.Caption } } }).ToArray()) };
    private sealed class History(JsonObject page) : ITelegramRequestClient
    {
        private int calls;
        public Task<JsonObject> ExecuteAsync(JsonObject request, CancellationToken token) => Task.FromResult(++calls == 1 ? page : Page());
    }
    private sealed class Parts : IPartTransport
    {
        public Dictionary<string, byte[]> Bytes { get; } = new();
        public Task<string> UploadPartAsync(string path, string id, int index, CancellationToken token) => throw new NotSupportedException();
        public Task<Stream> DownloadPartAsync(string id, CancellationToken token) => Task.FromResult<Stream>(new MemoryStream(Bytes[id]));
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
