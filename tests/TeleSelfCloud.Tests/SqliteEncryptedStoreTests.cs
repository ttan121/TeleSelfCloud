using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class SqliteEncryptedStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.EncryptedStores", Guid.NewGuid().ToString("N"));
    private readonly string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public SqliteEncryptedStoreTests() => Directory.CreateDirectory(root);
    [Fact]
    public async Task AllFourStoresReopenWithExactManifestQueueFolderAndCheckpointState()
    {
        var db = Path.Combine(root, "catalog.db"); var hash = new string('A', 64);
        var manifest = new FileManifest(1, "file", "Synthetic-private-record.bin", 100, hash, 100,
            [new(0, 0, 100, hash, "-100/44", true, "C:/Synthetic/private-staging")], true, "42", "Synthetic-private-folder");
        var manifests = new SqliteManifestStore(db, key); var queue = new SqliteTransferQueueStore(db, key);
        var folders = new SqliteLocalFolderStore(db, key); var checkpoints = new SqliteRemoteSyncCheckpointStore(db, key);
        await manifests.SaveAsync(manifest, default);
        await queue.EnqueueAsync("file", manifest.FileName, 100, default);
        await queue.SetStateAsync("file", TransferQueueState.Running, null, default);
        await queue.UpdateProgressAsync("file", 40, 100, default);
        await queue.SetStateAsync("file", TransferQueueState.Paused, null, default);
        await folders.CreateAsync("42", "Synthetic-private-folder", default);
        await folders.CreateAsync("42", "Synthetic-deleted-folder", default);
        await folders.DeleteAsync("42", "Synthetic-deleted-folder", default);
        var checkpoint = new RemoteSyncCheckpoint(123, DateTimeOffset.UtcNow);
        await checkpoints.SaveAsync("42", -100, checkpoint, default);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(manifest), System.Text.Json.JsonSerializer.Serialize(await new SqliteManifestStore(db, key).LoadAsync("file", default)));
        var saved = Assert.Single(await new SqliteTransferQueueStore(db, key).ListAsync(default));
        Assert.Equal(TransferQueueState.Paused, saved.State); Assert.Equal(40, saved.TransferredBytes); Assert.Equal(1, saved.AttemptCount);
        Assert.Equal("Synthetic-private-folder", Assert.Single(await new SqliteLocalFolderStore(db, key).ListAsync("42", default)).Path);
        Assert.Equal("Synthetic-deleted-folder", Assert.Single(await new SqliteLocalFolderStore(db, key).ListTombstonesAsync("42", default)).Path);
        Assert.Equal(checkpoint, await new SqliteRemoteSyncCheckpointStore(db, key).LoadAsync("42", -100, default));
        var bytes = File.ReadAllBytes(db);
        foreach (var text in new[] { manifest.FileName, manifest.FolderPath, manifest.Parts[0].StagingPath! }) Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) < 0);
        Assert.False(bytes.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrWrongKeyCannotReuseAnOpenCorrectKeyConnectionOrReplaceDatabase(bool wrong)
    {
        var db = Path.Combine(root, "catalog.db");
        var hash = new string('A', 64);
        await new SqliteManifestStore(db, key).SaveAsync(new FileManifest(1, "file", "safe.bin", 1, hash, 1, [new(0, 0, 1, hash, "-100/44", true)], true, "42"), default);
        var before = SHA256.HashData(File.ReadAllBytes(db));
        await using (var held = await SqliteDatabase.OpenAsync(db, key, default))
        {
            var options = new SqliteConnectionStringBuilder(held.ConnectionString);
            Assert.False(options.Pooling); Assert.Equal(SqliteCacheMode.Private, options.Cache);
            await Assert.ThrowsAsync<SqliteException>(() => new SqliteManifestStore(db, wrong ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) : null).LoadAsync("file", default));
        }
        SqliteConnection.ClearAllPools();
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(db)));
        Assert.NotNull(await new SqliteManifestStore(db, key).LoadAsync("file", default));
    }
    [Theory]
    [InlineData("")]
    [InlineData("not a key")]
    [InlineData("AQ==")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\n")]
    public async Task InvalidExplicitKeyCannotCreatePlaintextDatabase(string invalid)
    {
        var db = Path.Combine(root, "absent.db");
        await Assert.ThrowsAsync<InvalidDataException>(() => new SqliteManifestStore(db, invalid).ListAsync(default));
        Assert.False(File.Exists(db));
    }
    [Fact]
    public async Task PlainDatabaseIsNotSilentlyRekeyedByOpeningItWithAKey()
    {
        var db = Path.Combine(root, "legacy.db");
        await new SqliteLocalFolderStore(db).CreateAsync("42", "Legacy", default);
        SqliteConnection.ClearAllPools(); var before = File.ReadAllBytes(db);
        await Assert.ThrowsAsync<SqliteException>(() => new SqliteLocalFolderStore(db, key).ListAsync("42", default));
        Assert.Equal(before, File.ReadAllBytes(db));
        Assert.Equal("Legacy", Assert.Single(await new SqliteLocalFolderStore(db).ListAsync("42", default)).Path);
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
}
