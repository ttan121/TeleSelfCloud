using System.Security.Cryptography;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class LocalCacheMigrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.CacheMigration", Guid.NewGuid().ToString("N"));
    private readonly string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private readonly string scope = Guid.NewGuid().ToString("N");
    private string State => Path.Combine(root, "local-cache-verifications.json");
    private LocalRecordCipher Cipher(string identity) => new(key, scope, "cache-verification", identity);
    public LocalCacheMigrationTests() => Directory.CreateDirectory(root);
    [Theory]
    [InlineData("Prepared")]
    [InlineData("Verified")]
    [InlineData("Switching")]
    [InlineData("AfterReplace")]
    [InlineData("Switched")]
    [InlineData("OriginalProtected")]
    [InlineData("Ready")]
    public async Task DurableRestartPreservesExactSourceAndProtectsOriginalBeforeCleanup(string fault)
    {
        File.WriteAllText(State, "{ \n  }\n"); var original = File.ReadAllBytes(State); using var lease = LocalProfileLease.TryAcquire(root)!;
        using var state = Cipher("state"); using var journal = Cipher("migration");
        await Assert.ThrowsAsync<IOException>(() => LocalCacheStateMigration.MigrateAsync(root, lease, state, journal, default, stage => { if (stage == fault) throw new IOException(); }));
        await LocalCacheStateMigration.MigrateAsync(root, lease, state, journal, default);
        Assert.Equal(original, state.Unprotect(File.ReadAllBytes(State))); Assert.Empty(await new LocalCacheVerificationStore(State, state).LoadAllAsync(default));
        var work = Assert.Single(Directory.GetDirectories(Path.Combine(root, "cache-record-migrations")));
        Assert.Equal(original, state.Unprotect(File.ReadAllBytes(Path.Combine(work, "original.tsc")))); Assert.False(File.Exists(Path.Combine(work, "old-raw")));
        await new LocalCacheVerificationStore(State, state).RemoveManyAsync(["absent"], default);
        var updated = File.ReadAllBytes(State); await LocalCacheStateMigration.MigrateAsync(root, lease, state, journal, default); Assert.Equal(updated, File.ReadAllBytes(State));
    }
    [Theory]
    [InlineData("Verified", false)]
    [InlineData("Switching", true)]
    public async Task CancellationPreservesSourceBeforeIntentAndCompletesAfterIntent(string stage, bool encrypted)
    {
        File.WriteAllText(State, "{}"); using var lease = LocalProfileLease.TryAcquire(root)!; using var state = Cipher("state"); using var journal = Cipher("migration"); using var stop = new CancellationTokenSource();
        var work = LocalCacheStateMigration.MigrateAsync(root, lease, state, journal, stop.Token, current => { if (current == stage) stop.Cancel(); });
        if (encrypted) { await work; Assert.Equal("{}"u8.ToArray(), state.Unprotect(File.ReadAllBytes(State))); }
        else { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work); Assert.Equal("{}", File.ReadAllText(State)); }
        await LocalCacheStateMigration.MigrateAsync(root, lease, state, journal, default);
    }
    [Fact]
    public async Task ChangedSourceOrBadArchiveKeepsAllRecoveryCopies()
    {
        File.WriteAllText(State, "{}"); using var lease = LocalProfileLease.TryAcquire(root)!; using var state = Cipher("state"); using var journal = Cipher("migration");
        await Assert.ThrowsAsync<IOException>(() => LocalCacheStateMigration.MigrateAsync(root, lease, state, journal, default, stage => { if (stage == "Verified") throw new IOException(); }));
        File.WriteAllText(State, "{ }"); await Assert.ThrowsAsync<InvalidDataException>(() => LocalCacheStateMigration.MigrateAsync(root, lease, state, journal, default)); Assert.Equal("{ }", File.ReadAllText(State));
        File.WriteAllText(State, "{}");
        await Assert.ThrowsAsync<IOException>(() => LocalCacheStateMigration.MigrateAsync(root, lease, state, journal, default, stage => { if (stage == "Switched") throw new IOException(); }));
        var work = Assert.Single(Directory.GetDirectories(Path.Combine(root, "cache-record-migrations"))); var archive = Path.Combine(work, "original.tsc"); var originalArchive = File.ReadAllBytes(archive);
        var bad = originalArchive.ToArray(); bad[^1] ^= 1; File.WriteAllBytes(archive, bad);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => LocalCacheStateMigration.MigrateAsync(root, lease, state, journal, default)); Assert.True(File.Exists(Path.Combine(work, "old-raw")));
        File.WriteAllBytes(archive, originalArchive); await LocalCacheStateMigration.MigrateAsync(root, lease, state, journal, default); Assert.False(File.Exists(Path.Combine(work, "old-raw")));
    }
    [Fact]
    public async Task MissingStateCreatesAuthenticatedEmptyStateAndWrongJournalKeyNeverFallsBack()
    {
        using var lease = LocalProfileLease.TryAcquire(root)!; using var state = Cipher("state"); using var journal = Cipher("migration");
        await LocalCacheStateMigration.MigrateAsync(root, lease, state, journal, default); Assert.Equal("{}"u8.ToArray(), state.Unprotect(File.ReadAllBytes(State)));
        var before = File.ReadAllBytes(State); using var wrong = new LocalRecordCipher(key, Guid.NewGuid().ToString("N"), "cache-verification", "migration");
        await Assert.ThrowsAnyAsync<CryptographicException>(() => LocalCacheStateMigration.MigrateAsync(root, lease, state, wrong, default)); Assert.Equal(before, File.ReadAllBytes(State));
        File.Delete(State); await Assert.ThrowsAsync<FileNotFoundException>(() => LocalCacheStateMigration.MigrateAsync(root, lease, state, journal, default)); Assert.False(File.Exists(State));
    }
    [Fact]
    public async Task RealVerificationSnapshotsAndPrivatePathsSurviveByteExactMigration()
    {
        var record = new LocalCacheVerification("Private-id", new string('A', 64), 3, LocalCacheIntegrityState.Missing, 0, 1, DateTimeOffset.UnixEpoch,
            [new("Private/folder/part.bin", false, null, null)]);
        var original = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, LocalCacheVerification> { [record.FileId] = record });
        File.WriteAllBytes(State, original); using var lease = LocalProfileLease.TryAcquire(root)!; using var state = Cipher("state"); using var journal = Cipher("migration");
        await LocalCacheStateMigration.MigrateAsync(root, lease, state, journal, default);
        Assert.Equal(original, state.Unprotect(File.ReadAllBytes(State)));
        var recovered = Assert.Single(await new LocalCacheVerificationStore(State, state).LoadAllAsync(default)).Value;
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(record), System.Text.Json.JsonSerializer.Serialize(recovered));
        Assert.DoesNotContain("Private", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(State)));
    }
    [Fact]
    public async Task ForeignLeaseAndOpenWriterCannotPrepareMigration()
    {
        File.WriteAllText(State, "{}"); using var foreign = LocalProfileLease.TryAcquire(Path.Combine(root, "foreign"))!;
        using var state = Cipher("state"); using var journal = Cipher("migration");
        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalCacheStateMigration.MigrateAsync(root, foreign, state, journal, default));
        using var lease = LocalProfileLease.TryAcquire(root)!;
        using var writer = new FileStream(State, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        await Assert.ThrowsAnyAsync<IOException>(() => LocalCacheStateMigration.MigrateAsync(root, lease, state, journal, default));
        Assert.False(File.Exists(Path.Combine(root, "cache-record-migration.tsc")));
    }
    [Fact]
    public async Task InvalidLegacyStateIsPreservedWithoutPreparedPlan()
    {
        File.WriteAllText(State, "{\"id\":null}"); using var lease = LocalProfileLease.TryAcquire(root)!; using var state = Cipher("state"); using var journal = Cipher("migration");
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalCacheStateMigration.MigrateAsync(root, lease, state, journal, default)); Assert.Equal("{\"id\":null}", File.ReadAllText(State)); Assert.False(File.Exists(Path.Combine(root, "cache-record-migration.tsc")));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
