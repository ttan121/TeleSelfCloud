using System.Security.Cryptography;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class LocalStagingProtectionMigratorTests : IDisposable
{
    private const string Key = "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=";
    private const string ProtectionId = "5f4f2ae5d8b54a9eb145f230fa4b3df8";
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.StagingMigration", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InterruptedPerFileMigrationResumesIdempotentlyWithoutChangingManifestPaths()
    {
        Directory.CreateDirectory(root);
        using var lease = LocalProfileLease.TryAcquire(root)!;
        var stagingRoot = Path.Combine(root, "staging"); Directory.CreateDirectory(stagingRoot);
        var firstBytes = new byte[] { 1, 2, 3 };
        var secondBytes = new byte[] { 8, 5, 3, 0 };
        var firstPath = Path.Combine(stagingRoot, "part-0.bin");
        var secondPath = Path.Combine(stagingRoot, "part-1.bin");
        await File.WriteAllBytesAsync(firstPath, firstBytes);
        await File.WriteAllBytesAsync(secondPath, secondBytes);
        var firstHash = Hash(firstBytes); var secondHash = Hash(secondBytes);
        var totalHash = Hash(firstBytes.Concat(secondBytes).ToArray());
        var manifest = new FileManifest(1, "draft", "draft.bin", firstBytes.Length + secondBytes.Length, totalHash,
            secondBytes.Length, [new PartRecord(0, 0, firstBytes.Length, firstHash, null, false, firstPath),
                new PartRecord(1, firstBytes.Length, secondBytes.Length, secondHash, null, false, secondPath)], false);
        var manifestStore = new SqliteManifestStore(Path.Combine(root, "manifests.db"));
        await manifestStore.SaveAsync(manifest, default);
        var contentStore = new LocalStagingContentStore(root, lease,
            identity => new LocalStagingCipher(Key, ProtectionId, identity), allowLegacyPlaintext: true);
        using var cancel = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalStagingProtectionMigrator.MigrateAsync(
            stagingRoot, manifestStore, contentStore, cancel.Token, (current, _) => { if (current == 1) cancel.Cancel(); }));

        Assert.True(LocalStagingCipher.HasProtectedHeader(await ReadPrefixAsync(firstPath)));
        Assert.False(LocalStagingCipher.HasProtectedHeader(await ReadPrefixAsync(secondPath)));
        var afterInterrupted = await manifestStore.LoadAsync(manifest.FileId, default);
        Assert.Equal(manifest.Parts.Select(part => part.StagingPath), afterInterrupted!.Parts.Select(part => part.StagingPath));

        var result = await LocalStagingProtectionMigrator.MigrateAsync(stagingRoot, manifestStore, contentStore, default);

        Assert.Equal(1, result.ProtectedFileCount);
        Assert.Equal(1, result.AlreadyProtectedFileCount);
        await using var first = await contentStore.MaterializeAsync(firstPath, StagingFileIdentity.Part(manifest.FileId, 0));
        await using var second = await contentStore.MaterializeAsync(secondPath, StagingFileIdentity.Part(manifest.FileId, 1));
        Assert.Equal(firstBytes, await ReadAllAsync(first.Stream));
        Assert.Equal(secondBytes, await ReadAllAsync(second.Stream));
        Assert.Equal(manifest.Parts.Select(part => part.StagingPath),
            (await manifestStore.LoadAsync(manifest.FileId, default))!.Parts.Select(part => part.StagingPath));
    }

    [Fact]
    public async Task MigrationCountsDistinctOutsideCatalogPathsWithoutChangingThem()
    {
        Directory.CreateDirectory(root);
        using var lease = LocalProfileLease.TryAcquire(root)!;
        var stagingRoot = Path.Combine(root, "staging"); Directory.CreateDirectory(stagingRoot);
        var outsidePath = Path.Combine(root, "legacy", "part.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(outsidePath)!);
        var bytes = new byte[] { 1, 2, 3 };
        await File.WriteAllBytesAsync(outsidePath, bytes);
        var hash = Hash(bytes);
        var manifests = new SqliteManifestStore(Path.Combine(root, "manifests.db"));
        foreach (var fileId in new[] { "one", "two" })
            await manifests.SaveAsync(new FileManifest(1, fileId, fileId + ".bin", bytes.Length, hash, bytes.Length,
                [new PartRecord(0, 0, bytes.Length, hash, null, false, outsidePath)], false), default);
        var contentStore = new LocalStagingContentStore(root, lease,
            identity => new LocalStagingCipher(Key, ProtectionId, identity), allowLegacyPlaintext: true);

        var result = await LocalStagingProtectionMigrator.MigrateAsync(stagingRoot, manifests, contentStore, default);

        Assert.Equal(1, result.OutsideCatalogCount);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(outsidePath));
        Assert.False(LocalStagingCipher.HasProtectedHeader(await ReadPrefixAsync(outsidePath)));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static async Task<byte[]> ReadPrefixAsync(string path)
    {
        await using var stream = File.OpenRead(path); var prefix = new byte[8]; var count = 0;
        while (count < prefix.Length) { var read = await stream.ReadAsync(prefix.AsMemory(count)); if (read == 0) break; count += read; }
        return prefix[..count];
    }
    private static async Task<byte[]> ReadAllAsync(Stream stream) { using var output = new MemoryStream(); await stream.CopyToAsync(output); return output.ToArray(); }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
