using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class LocalCacheProtectionTests : IDisposable
{
    private readonly string parent = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.CacheProtection", Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(parent, "profile");
    private string State => Path.Combine(Root, "local-cache-verifications.json");
    public LocalCacheProtectionTests() => Directory.CreateDirectory(Root);
    private async Task EnableCatalog(LocalProfileLease lease)
    {
        await new SqliteManifestStore(Path.Combine(Root, "manifests.db")).ListAsync(default); SqliteConnection.ClearAllPools();
        LocalDatabaseProtection.Request(Root, Path.Combine(parent, "catalog-key.json"), "cache protection passphrase", lease);
        await LocalDatabaseStartup.PrepareAsync(Root, lease, _ => throw new InvalidOperationException(), default);
    }
    private byte[] SeedState()
    {
        var record = new LocalCacheVerification("private-id", new string('A', 64), 1, LocalCacheIntegrityState.Missing, 0, 1, DateTimeOffset.UnixEpoch, [new("private/path", false, null, null)]);
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, LocalCacheVerification> { [record.FileId] = record }); File.WriteAllBytes(State, plain); return plain;
    }
    [Fact]
    public async Task ScopedRequestStartupAndFactoryPreserveLegacyCacheWithExistingCatalogKey()
    {
        using var lease = LocalProfileLease.TryAcquire(Root)!; await EnableCatalog(lease); var plain = SeedState();
        using (var legacy = LocalCacheProtection.OpenStore(Root)) Assert.Single(await legacy.LoadAllAsync(default));
        LocalCacheProtection.Request(Root, lease); Assert.Equal(plain, File.ReadAllBytes(State));
        Assert.Throws<FileNotFoundException>(() => LocalCacheProtection.OpenStore(Root));
        var keyBefore = LocalDatabaseProtection.DatabaseKey(Root);
        await LocalDatabaseStartup.PrepareAsync(Root, lease, _ => throw new InvalidOperationException(), default);
        Assert.True(LocalCacheProtection.IsReady(Root)); Assert.Equal(keyBefore, LocalDatabaseProtection.DatabaseKey(Root));
        using var store = LocalCacheProtection.OpenStore(Root); Assert.Single(await store.LoadAllAsync(default));
        using var cipher = LocalDatabaseProtection.RecordCipher(Root, "state"); Assert.Equal(plain, cipher.Unprotect(File.ReadAllBytes(State)));
        await store.RemoveManyAsync(["private-id"], default); Assert.Empty(await store.LoadAllAsync(default));
        await LocalDatabaseStartup.PrepareAsync(Root, lease, _ => throw new InvalidOperationException(), default);
    }
    [Theory]
    [InlineData("policy")]
    [InlineData("journal")]
    [InlineData("state")]
    public async Task LostOrDamagedProtectedRecordsNeverOpenAsPlaintext(string target)
    {
        using var lease = LocalProfileLease.TryAcquire(Root)!; await EnableCatalog(lease); SeedState(); LocalCacheProtection.Request(Root, lease);
        await LocalDatabaseStartup.PrepareAsync(Root, lease, _ => throw new InvalidOperationException(), default);
        var before = File.ReadAllBytes(State);
        if (target == "policy") File.Delete(Path.Combine(Root, "cache-record-policy.tsc"));
        else if (target == "journal") File.Delete(Path.Combine(Root, "cache-record-migration.tsc"));
        else { var bad = before.ToArray(); bad[^1] ^= 1; File.WriteAllBytes(State, bad); }
        if (target != "state") Assert.Throws<FileNotFoundException>(() => LocalCacheProtection.OpenStore(Root));
        else { using var store = LocalCacheProtection.OpenStore(Root); await Assert.ThrowsAnyAsync<CryptographicException>(() => store.RemoveManyAsync(["private-id"], default)); }
        var error = await Record.ExceptionAsync(() => LocalDatabaseStartup.PrepareAsync(Root, lease, _ => throw new InvalidOperationException(), default)); Assert.NotNull(error);
        if (target != "state") Assert.Equal(before, File.ReadAllBytes(State));
    }
    [Fact]
    public void NoExistingCatalogKeyCannotCreateCachePolicy()
    {
        using var lease = LocalProfileLease.TryAcquire(Root)!;
        Assert.Throws<FileNotFoundException>(() => LocalCacheProtection.Request(Root, lease)); Assert.False(LocalCacheProtection.IsConfigured(Root));
    }
    [Fact]
    public async Task AccountMigrationInvalidatesOnlyMovedVerificationRowsInProtectedSharedCache()
    {
        using var lease = LocalProfileLease.TryAcquire(Root)!; await EnableCatalog(lease);
        var db = Path.Combine(Root, "manifests.db"); var key = LocalDatabaseProtection.DatabaseKey(Root); var shared = new VaultProfileStores(Root, key);
        var hash = new string('A', 64);
        await shared.Manifests.SaveAsync(new(1, "private-id", "Private", 1, hash, 1, [new(0, 0, 1, hash, null, false)], false, "42"), default);
        var first = new LocalCacheVerification("private-id", hash, 1, LocalCacheIntegrityState.Missing, 0, 1, DateTimeOffset.UnixEpoch, [new(null, false, null, null)]);
        var other = first with { FileId = "other-id" };
        File.WriteAllBytes(State, JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, LocalCacheVerification> { [first.FileId] = first, [other.FileId] = other }));
        LocalCacheProtection.Request(Root, lease); await LocalDatabaseStartup.PrepareAsync(Root, lease, _ => throw new InvalidOperationException(), default);
        using var cache = LocalCacheProtection.OpenStore(Root); var target = new VaultProfileStores(Path.Combine(Root, "accounts", "42"));
        await new AccountProfileDataMigrator(shared.Manifests, target.Manifests, shared.Queue, target.Queue, shared.Checkpoints, target.Checkpoints, cache, shared.StagingRoot, target.StagingRoot).MigrateAsync("42", default);
        Assert.Equal("other-id", Assert.Single(await cache.LoadAllAsync(default)).Key); Assert.NotNull(await target.Manifests.LoadAsync("private-id", default));
        Assert.Null(await shared.Manifests.LoadAsync("private-id", default));
    }
    [Fact]
    public async Task MissingDpapiIsRecoveredBeforeCacheMigrationWithoutNewMaster()
    {
        using var lease = LocalProfileLease.TryAcquire(Root)!; await EnableCatalog(lease); SeedState(); LocalCacheProtection.Request(Root, lease);
        File.Delete(Path.Combine(Root, "local-db-key.dpapi")); var calls = 0;
        await LocalDatabaseStartup.PrepareAsync(Root, lease, root => { calls++; LocalDatabaseProtection.Recover(root, Path.Combine(parent, "catalog-key.json"), "cache protection passphrase", lease); return Task.FromResult(true); }, default);
        Assert.Equal(1, calls); using var store = LocalCacheProtection.OpenStore(Root); Assert.Single(await store.LoadAllAsync(default));
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(parent, true); }
}
