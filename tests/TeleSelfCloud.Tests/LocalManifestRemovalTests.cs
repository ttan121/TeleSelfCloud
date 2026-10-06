using System.Security.Cryptography;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class LocalManifestRemovalTests
{
    [Fact]
    public async Task RemoveAsync_RemovesOnlyLocalRecordsAndKeepsStagedPartFiles()
    {
        var root = CreateRoot();
        var database = Path.Combine(root, "manifests.db");
        var manifests = new SqliteManifestStore(database);
        var queue = new SqliteTransferQueueStore(database);
        var cache = new LocalCacheVerificationStore(Path.Combine(root, "cache.json"));
        var stagedPath = Path.Combine(root, "staged.part");
        var bytes = new byte[] { 1, 2, 3 };
        await File.WriteAllBytesAsync(stagedPath, bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var manifest = new FileManifest(1, "stale-file", "stale.bin", bytes.Length, hash, bytes.Length,
            [new PartRecord(0, 0, bytes.Length, hash, "-100/123", true, stagedPath)], true,
            AccountId: "101");
        await manifests.SaveAsync(manifest, CancellationToken.None);
        await queue.EnqueueAsync(manifest.FileId, manifest.FileName, manifest.LogicalSize, CancellationToken.None);
        await queue.SetStateAsync(manifest.FileId, TransferQueueState.Completed, null, CancellationToken.None);
        await cache.VerifyAndSaveAsync(manifest, CancellationToken.None);

        try
        {
            await new LocalManifestRemoval(manifests, queue, cache)
                .RemoveAsync([manifest.FileId], "101", CancellationToken.None);

            Assert.Null(await manifests.LoadAsync(manifest.FileId, CancellationToken.None));
            Assert.Empty(await queue.ListAsync(CancellationToken.None));
            Assert.False((await cache.LoadAllAsync(CancellationToken.None)).ContainsKey(manifest.FileId));
            Assert.True(File.Exists(stagedPath));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RemoveAsync_RefusesActiveTransfersAndOtherAccountsWithoutDeletingManifest()
    {
        var root = CreateRoot();
        var database = Path.Combine(root, "manifests.db");
        var manifests = new SqliteManifestStore(database);
        var queue = new SqliteTransferQueueStore(database);
        var cache = new LocalCacheVerificationStore(Path.Combine(root, "cache.json"));
        var hash = Convert.ToHexString(SHA256.HashData([7]));
        var manifest = new FileManifest(1, "owned-file", "owned.bin", 1, hash, 1,
            [new PartRecord(0, 0, 1, hash, "-100/456", true)], true, AccountId: "101");
        await manifests.SaveAsync(manifest, CancellationToken.None);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new LocalManifestRemoval(manifests, queue, cache).RemoveAsync([manifest.FileId], "202", CancellationToken.None));
            Assert.NotNull(await manifests.LoadAsync(manifest.FileId, CancellationToken.None));

            await queue.EnqueueAsync(manifest.FileId, manifest.FileName, manifest.LogicalSize, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new LocalManifestRemoval(manifests, queue, cache).RemoveAsync([manifest.FileId], "101", CancellationToken.None));
            Assert.NotNull(await manifests.LoadAsync(manifest.FileId, CancellationToken.None));
            Assert.Single(await queue.ListAsync(CancellationToken.None));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud-LocalManifestRemovalTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
