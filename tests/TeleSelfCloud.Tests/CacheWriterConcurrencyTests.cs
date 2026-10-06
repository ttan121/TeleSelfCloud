using System.Security.Cryptography;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class CacheWriterConcurrencyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.CacheWriters", Guid.NewGuid().ToString("N"));
    private string State => Path.Combine(root, "cache.json");
    private readonly string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private readonly string scope = Guid.NewGuid().ToString("N");
    private LocalRecordCipher Cipher(string identity = "state") => new(key, scope, "cache-verification", identity);
    public CacheWriterConcurrencyTests() => Directory.CreateDirectory(root);
    private static FileManifest Manifest(int index) => new(1, "id" + index, "name" + index, 1, new string('A', 64), 1,
        [new(0, 0, 1, new string('A', 64), null, false)], false, "42");
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParallelInstancesPreserveEveryDisjointVerificationAndRemoval(bool protectedState)
    {
        using var cipher = Cipher();
        var stores = Enumerable.Range(0, 12).Select(_ => new LocalCacheVerificationStore(State, protectedState ? cipher : null)).ToArray();
        try
        {
            await Task.WhenAll(stores.Select((store, i) => store.VerifyAndSaveAsync(Manifest(i), default)));
            Assert.Equal(12, (await stores[0].LoadAllAsync(default)).Count);
            await Task.WhenAll(stores.Take(6).Select((store, i) => store.RemoveManyAsync(["id" + i], default)));
            var remaining = await stores[0].LoadAllAsync(default); Assert.Equal(6, remaining.Count);
            Assert.All(Enumerable.Range(6, 6), i => Assert.Contains("id" + i, remaining.Keys)); Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { foreach (var store in stores) store.Dispose(); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelWhileWaitingForWriterKeepsExactPriorBytes(bool protectedState)
    {
        using var cipher = Cipher(); using var store = new LocalCacheVerificationStore(State, protectedState ? cipher : null);
        await store.VerifyAndSaveAsync(Manifest(0), default); var before = File.ReadAllBytes(State);
        using var held = new FileStream(State + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RemoveManyAsync(["id0"], stop.Token));
        Assert.Equal(before, File.ReadAllBytes(State)); Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }
    [Fact]
    public async Task MigrationCannotPrepareWhileCacheWriterLeaseIsHeld()
    {
        var fixedState = Path.Combine(root, "local-cache-verifications.json"); File.WriteAllText(fixedState, "{}");
        using var lease = LocalProfileLease.TryAcquire(root)!; using var state = Cipher(); using var journal = Cipher("migration");
        using var held = new FileStream(fixedState + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAnyAsync<IOException>(() => LocalCacheStateMigration.MigrateAsync(root, lease, state, journal, default));
        Assert.Equal("{}", File.ReadAllText(fixedState)); Assert.False(File.Exists(Path.Combine(root, "cache-record-migration.tsc")));
    }
    [Fact]
    public async Task DisposedStoreRejectsReadAndWriteButDoesNotDisposeBorrowedCipher()
    {
        using var cipher = Cipher(); var store = new LocalCacheVerificationStore(State, cipher); store.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.LoadAllAsync(default));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.RemoveManyAsync(["id"], default));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.VerifyAndSaveAsync(Manifest(0), default));
        Assert.Empty(cipher.Unprotect(cipher.Protect([]))); Assert.False(File.Exists(State));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
