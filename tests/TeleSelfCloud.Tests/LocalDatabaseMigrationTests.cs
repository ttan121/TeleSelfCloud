using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class LocalDatabaseMigrationTests : IDisposable
{
    private readonly string parent = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.LocalMigration", Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(parent, "profile");
    private string Backup => Path.Combine(parent, "recovery.tsc-db-key.json");
    private string Db => Path.Combine(Root, "manifests.db");
    private const string Passphrase = "local catalog recovery passphrase";
    public LocalDatabaseMigrationTests() => Directory.CreateDirectory(Root);
    private async Task Seed()
    {
        var hash = new string('A', 64);
        await new SqliteManifestStore(Db).SaveAsync(new FileManifest(1, "file", "Private-catalog.bin", 100, hash, 100, [new(0, 0, 100, hash, "-100/44", true)], true, "42", "Private/folder"), default);
        var queue = new SqliteTransferQueueStore(Db); await queue.EnqueueAsync("file", "Private-catalog.bin", 100, default);
        await queue.SetStateAsync("file", TransferQueueState.Running, null, default); await queue.UpdateProgressAsync("file", 40, 100, default); await queue.SetStateAsync("file", TransferQueueState.Paused, null, default);
        await new SqliteLocalFolderStore(Db).CreateAsync("42", "Private/folder", default);
        await new SqliteRemoteSyncCheckpointStore(Db).SaveAsync("42", -100, new(123, DateTimeOffset.UnixEpoch), default);
        SqliteConnection.ClearAllPools();
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db, Pooling = false }.ToString()); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = "CREATE VIRTUAL TABLE Search USING fts5(Name); INSERT INTO Search VALUES('Synthetic secret'); PRAGMA wal_checkpoint(TRUNCATE);"; cmd.ExecuteNonQuery();
    }
    private async Task Verify()
    {
        Assert.Equal("Ready", LocalDatabaseProtection.Status(Root).Stage);
        var key = LocalDatabaseProtection.DatabaseKey(Root);
        Assert.Equal("Private-catalog.bin", (await new SqliteManifestStore(Db, key).LoadAsync("file", default))!.FileName);
        var queue = Assert.Single(await new SqliteTransferQueueStore(Db, key).ListAsync(default)); Assert.Equal(40, queue.TransferredBytes); Assert.Equal(TransferQueueState.Paused, queue.State);
        Assert.Equal(123, (await new SqliteRemoteSyncCheckpointStore(Db, key).LoadAsync("42", -100, default))!.HighestMessageId);
        Assert.Contains(await new SqliteLocalFolderStore(Db, key).ListAsync("42", default), f => f.Path == "Private/folder");
        Assert.False(File.ReadAllBytes(Db).AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8));
        await using var c = await SqliteDatabase.OpenAsync(Db, key, default); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM Search WHERE Search MATCH 'secret'"; Assert.Equal(1L, cmd.ExecuteScalar());
    }
    [Theory]
    [InlineData("Prepared")]
    [InlineData("Verified")]
    [InlineData("Switching")]
    [InlineData("SidecarsMoved")]
    [InlineData("AfterReplace")]
    [InlineData("Switched")]
    [InlineData("ArchivesProtected")]
    [InlineData("Ready")]
    public async Task RestartAtEachDurableBoundaryPreservesAllStoreAndFtsData(string fault)
    {
        await Seed(); using var lease = LocalProfileLease.TryAcquire(Root)!;
        LocalDatabaseProtection.Request(Root, Backup, Passphrase, lease);
        await Assert.ThrowsAsync<IOException>(() => LocalDatabaseProtection.MigrateOfflineAsync(Root, lease, default, stage => { if (stage == fault) throw new IOException("simulated crash"); }));
        await LocalDatabaseProtection.MigrateOfflineAsync(Root, lease, default); await Verify();
        var work = Assert.Single(Directory.GetDirectories(Path.Combine(Root, "local-db-migrations")));
        Assert.NotEmpty(Directory.GetFiles(work, "*.tscenc")); Assert.DoesNotContain(Directory.GetFiles(work, "old-*"), p => !p.EndsWith(".tscenc"));
        var before = File.ReadAllBytes(Db); await LocalDatabaseProtection.MigrateOfflineAsync(Root, lease, default); Assert.Equal(before, File.ReadAllBytes(Db));
    }
    [Fact]
    public async Task CommittedWalFramesSurviveOfflineSnapshotAndInterruptedSidecarMoves()
    {
        await Seed();
        byte[] main, wal;
        using (var origin = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db, Pooling = false }.ToString()))
        {
            origin.Open(); using var cmd = origin.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; INSERT INTO Search VALUES('walunique');"; cmd.ExecuteNonQuery();
            static byte[] ReadShared(string path) { using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); }
            main = ReadShared(Db); wal = ReadShared(Db + "-wal");
        }
        Assert.NotEmpty(wal);
        // Materialize the committed main+WAL snapshot after closing the fixture writer.
        // This models an offline profile left by abrupt termination, without any live profile.
        File.WriteAllBytes(Db, main); File.WriteAllBytes(Db + "-wal", wal);
        using var lease = LocalProfileLease.TryAcquire(Root)!; LocalDatabaseProtection.Request(Root, Backup, Passphrase, lease);
        await Assert.ThrowsAsync<IOException>(() => LocalDatabaseProtection.MigrateOfflineAsync(Root, lease, default, stage => { if (stage == "SidecarsMoved") throw new IOException(); }));
        await LocalDatabaseProtection.MigrateOfflineAsync(Root, lease, default); await Verify();
        await using var c = await SqliteDatabase.OpenAsync(Db, LocalDatabaseProtection.DatabaseKey(Root), default);
        using var query = c.CreateCommand(); query.CommandText = "SELECT count(*) FROM Search WHERE Search MATCH 'walunique'"; Assert.Equal(1L, query.ExecuteScalar());
        Assert.Contains(Directory.GetFiles(Path.Combine(Root, "local-db-migrations"), "old-wal.tscenc", SearchOption.AllDirectories), p => new FileInfo(p).Length > 0);
    }
    [Theory]
    [InlineData("Prepared", false)]
    [InlineData("Verified", false)]
    [InlineData("Switching", true)]
    public async Task CancellationBeforeSwitchPreservesSourceAndAfterIntentCompletes(string stage, bool completes)
    {
        await Seed(); var before = File.ReadAllBytes(Db); using var lease = LocalProfileLease.TryAcquire(Root)!;
        LocalDatabaseProtection.Request(Root, Backup, Passphrase, lease); using var cancel = new CancellationTokenSource();
        var migration = LocalDatabaseProtection.MigrateOfflineAsync(Root, lease, cancel.Token, value => { if (value == stage) cancel.Cancel(); });
        if (completes) await migration;
        else { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => migration); Assert.Equal(before, File.ReadAllBytes(Db)); }
        await LocalDatabaseProtection.MigrateOfflineAsync(Root, lease, default); await Verify();
    }
    [Fact]
    public async Task DamagedProtectedOriginalKeepsRawRecoveryCopyUntilArchiveCanBeVerified()
    {
        await Seed(); using var lease = LocalProfileLease.TryAcquire(Root)!; LocalDatabaseProtection.Request(Root, Backup, Passphrase, lease);
        await Assert.ThrowsAsync<IOException>(() => LocalDatabaseProtection.MigrateOfflineAsync(Root, lease, default, stage => { if (stage == "Switched") throw new IOException(); }));
        var work = Assert.Single(Directory.GetDirectories(Path.Combine(Root, "local-db-migrations")));
        var raw = Path.Combine(work, "old-main"); var original = File.ReadAllBytes(raw);
        var bad = Path.Combine(work, "old-main.tscenc"); File.WriteAllBytes(bad, [1, 2, 3]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => LocalDatabaseProtection.MigrateOfflineAsync(Root, lease, default));
        Assert.Equal(original, File.ReadAllBytes(raw)); Assert.Equal("Switched", LocalDatabaseProtection.Status(Root).Stage);
        File.Delete(bad); await LocalDatabaseProtection.MigrateOfflineAsync(Root, lease, default); await Verify(); Assert.False(File.Exists(raw));
    }
    [Fact]
    public async Task PortableExportRecoversReadyCatalogWithExactExistingKeyAndRejectsOtherProfile()
    {
        await Seed(); using var lease = LocalProfileLease.TryAcquire(Root)!; LocalDatabaseProtection.Request(Root, Backup, Passphrase, lease);
        await LocalDatabaseProtection.MigrateOfflineAsync(Root, lease, default);
        var expected = LocalDatabaseProtection.DatabaseKey(Root); var bytes = File.ReadAllBytes(Db);
        var exported = Path.Combine(parent, "export.json"); LocalDatabaseProtection.Export(Root, exported, Passphrase, lease);
        Assert.Throws<InvalidOperationException>(() => LocalDatabaseProtection.Export(Root, Path.Combine(Root, "bad.json"), Passphrase, lease));
        var wrong = JsonNode.Parse(File.ReadAllText(exported))!; wrong["protectionId"] = Guid.NewGuid().ToString("N");
        var wrongPath = Path.Combine(parent, "wrong.json"); File.WriteAllText(wrongPath, wrong.ToJsonString());
        Assert.Throws<InvalidDataException>(() => LocalDatabaseProtection.Recover(Root, wrongPath, Passphrase, lease));
        File.Delete(Path.Combine(Root, "local-db-key.dpapi"));
        Assert.Throws<FileNotFoundException>(() => LocalDatabaseProtection.DatabaseKey(Root));
        LocalDatabaseProtection.Recover(Root, exported, Passphrase, lease);
        Assert.Equal(expected, LocalDatabaseProtection.DatabaseKey(Root)); Assert.Equal(bytes, File.ReadAllBytes(Db)); await Verify();
    }
    [Fact]
    public async Task DamagedCandidateCannotReplaceSource()
    {
        await Seed(); using var lease = LocalProfileLease.TryAcquire(Root)!; LocalDatabaseProtection.Request(Root, Backup, Passphrase, lease);
        await Assert.ThrowsAsync<IOException>(() => LocalDatabaseProtection.MigrateOfflineAsync(Root, lease, default, stage => { if (stage == "Verified") throw new IOException(); }));
        var before = File.ReadAllBytes(Db); var work = Assert.Single(Directory.GetDirectories(Path.Combine(Root, "local-db-migrations")));
        var path = Path.Combine(work, "candidate.db"); var damaged = File.ReadAllBytes(path); damaged[^1] ^= 1; File.WriteAllBytes(path, damaged);
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalDatabaseProtection.MigrateOfflineAsync(Root, lease, default));
        Assert.Equal(before, File.ReadAllBytes(Db)); Assert.Equal(damaged, File.ReadAllBytes(path)); Assert.Equal("Verified", LocalDatabaseProtection.Status(Root).Stage);
    }
    [Fact]
    public async Task WrongRecoveryKeyAndDamagedDpapiCannotCreateReplacementOrLoseCatalog()
    {
        await Seed(); using var lease = LocalProfileLease.TryAcquire(Root)!; var before = File.ReadAllBytes(Db);
        LocalDatabaseProtection.Request(Root, Backup, Passphrase, lease);
        File.WriteAllText(Path.Combine(Root, "local-db-key.dpapi"), "damaged");
        Assert.Throws<CryptographicException>(() => LocalDatabaseProtection.Status(Root));
        Assert.Throws<InvalidOperationException>(() => LocalDatabaseProtection.Request(Root, Path.Combine(parent, "other.json"), Passphrase, lease));
        Assert.ThrowsAny<CryptographicException>(() => LocalDatabaseProtection.Recover(Root, Backup, "incorrect passphrase", lease));
        Assert.Equal(before, File.ReadAllBytes(Db));
        LocalDatabaseProtection.Recover(Root, Backup, Passphrase, lease);
        await LocalDatabaseProtection.MigrateOfflineAsync(Root, lease, default); await Verify();
    }
    [Fact]
    public async Task ChangedSourceOrCorruptJournalKeepsSourceAndPreparedCopies()
    {
        await Seed(); using var lease = LocalProfileLease.TryAcquire(Root)!;
        LocalDatabaseProtection.Request(Root, Backup, Passphrase, lease);
        await Assert.ThrowsAsync<IOException>(() => LocalDatabaseProtection.MigrateOfflineAsync(Root, lease, default, stage => { if (stage == "Verified") throw new IOException(); }));
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db, Pooling = false }.ToString())) { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE Manifests SET FileName='Changed'"; cmd.ExecuteNonQuery(); }
        var changed = File.ReadAllBytes(Db);
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalDatabaseProtection.MigrateOfflineAsync(Root, lease, default));
        Assert.Equal(changed, File.ReadAllBytes(Db));
        var path = Path.Combine(Root, "local-db-migration.json"); var journal = JsonNode.Parse(File.ReadAllText(path))!;
        var data = Convert.FromBase64String(journal["data"]!.GetValue<string>()); data[0] ^= 1; journal["data"] = Convert.ToBase64String(data); File.WriteAllText(path, journal.ToJsonString());
        await Assert.ThrowsAnyAsync<CryptographicException>(() => LocalDatabaseProtection.MigrateOfflineAsync(Root, lease, default));
        Assert.Equal(changed, File.ReadAllBytes(Db));
    }
    [Fact]
    public async Task ForeignLeaseAndActiveWriterAreRejectedBeforeSnapshot()
    {
        await Seed(); using var foreign = LocalProfileLease.TryAcquire(Path.Combine(parent, "foreign"))!;
        Assert.Throws<InvalidOperationException>(() => LocalDatabaseProtection.Request(Root, Backup, Passphrase, foreign));
        using var lease = LocalProfileLease.TryAcquire(Root)!; LocalDatabaseProtection.Request(Root, Backup, Passphrase, lease);
        using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db, Pooling = false }.ToString()); writer.Open();
        await Assert.ThrowsAnyAsync<IOException>(() => LocalDatabaseProtection.MigrateOfflineAsync(Root, lease, default));
        Assert.False(File.Exists(Path.Combine(Root, "local-db-migration.json")));
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(parent, true); }
}
