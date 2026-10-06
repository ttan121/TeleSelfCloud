using System.Security.Cryptography;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class AccountProfileStagingCipherMigrationTests : IDisposable
{
    private const string SourceKey = "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=";
    private const string TargetKey = "AgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgI=";
    private const string SourceProtectionId = "5f4f2ae5d8b54a9eb145f230fa4b3df8";
    private const string TargetProtectionId = "6f4f2ae5d8b54a9eb145f230fa4b3df9";
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.StagingProfileMigration", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MigrationRewrapsProtectedPartsAndPayloadForDestinationCatalogBeforeRemovingSource()
    {
        Directory.CreateDirectory(root);
        using var lease = LocalProfileLease.TryAcquire(root)!;
        var sourceStaging = Path.Combine(root, "shared", "staging");
        var destinationStaging = Path.Combine(root, "accounts", "acct", "staging");
        Directory.CreateDirectory(sourceStaging);
        var fileId = "migrated-fixture";
        var original = new byte[] { 2, 7, 1, 8 };
        var payload = new byte[] { 6, 2, 6, 4, 3, 3, 8 };
        var partPath = Path.Combine(sourceStaging, "part.bin");
        var payloadPath = Path.Combine(sourceStaging, "payload.bin");
        await WriteProtectedAsync(partPath, payload, SourceKey, SourceProtectionId, StagingFileIdentity.Part(fileId, 0));
        await WriteProtectedAsync(payloadPath, payload, SourceKey, SourceProtectionId, StagingFileIdentity.EncryptedPayload(fileId));

        var fileKey = AesGcmFileCipher.CreateFileKey();
        var envelope = AesGcmFileCipher.WrapFileKey(fileKey, "fixture recovery phrase");
        CryptographicOperations.ZeroMemory(fileKey);
        var payloadHash = Hash(payload);
        var manifest = new FileManifest(1, fileId, "fixture.bin", original.Length, Hash(original), payload.Length,
            [new PartRecord(0, 0, payload.Length, payloadHash, null, false, partPath)], false, "acct",
            Encryption: new EncryptedPayloadDescriptor(1, payload.Length, payloadHash, envelope, payloadPath));

        var sharedDatabase = Path.Combine(root, "shared", "manifests.db");
        var accountDatabase = Path.Combine(root, "accounts", "acct", "manifests.db");
        var sharedManifests = new SqliteManifestStore(sharedDatabase);
        var accountManifests = new SqliteManifestStore(accountDatabase);
        await sharedManifests.SaveAsync(manifest, default);
        using var cache = new LocalCacheVerificationStore(Path.Combine(root, "shared", "cache.json"));
        var sourceContent = Store(SourceKey, SourceProtectionId, root, lease);
        var targetContent = Store(TargetKey, TargetProtectionId, root, lease);
        var migrator = new AccountProfileDataMigrator(sharedManifests, accountManifests,
            new SqliteTransferQueueStore(sharedDatabase), new SqliteTransferQueueStore(accountDatabase),
            new SqliteRemoteSyncCheckpointStore(sharedDatabase), new SqliteRemoteSyncCheckpointStore(accountDatabase),
            cache, sourceStaging, destinationStaging, sourceContent, targetContent);

        var result = await migrator.MigrateAsync("acct", default);

        Assert.Equal(1, result.MigratedManifestCount);
        var migrated = Assert.IsType<FileManifest>(await accountManifests.LoadAsync(fileId, default));
        var migratedPart = Assert.Single(migrated.Parts);
        Assert.True(LocalStagingCipher.HasProtectedHeader(await ReadPrefixAsync(migratedPart.StagingPath!)));
        Assert.True(LocalStagingCipher.HasProtectedHeader(await ReadPrefixAsync(migrated.Encryption!.StagingPath!)));
        await using (var part = await targetContent.MaterializeAsync(migratedPart.StagingPath!, StagingFileIdentity.Part(fileId, 0)))
            Assert.Equal(payload, await ReadAllAsync(part.Stream));
        await using (var encryptedPayload = await targetContent.MaterializeAsync(migrated.Encryption.StagingPath!,
                         StagingFileIdentity.EncryptedPayload(fileId)))
            Assert.Equal(payload, await ReadAllAsync(encryptedPayload.Stream));
        Assert.False(File.Exists(partPath));
        Assert.False(File.Exists(payloadPath));
        Assert.Null(await sharedManifests.LoadAsync(fileId, default));
    }

    private LocalStagingContentStore Store(string key, string protectionId, string profileRoot, LocalProfileLease lease) =>
        new(profileRoot, lease, identity => new LocalStagingCipher(key, protectionId, identity), allowLegacyPlaintext: true);

    private static async Task WriteProtectedAsync(string path, byte[] bytes, string key, string protectionId, string identity)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var cipher = new LocalStagingCipher(key, protectionId, identity);
        await using var input = new MemoryStream(bytes, writable: false);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await cipher.EncryptAsync(input, output, bytes.Length);
    }

    private static async Task<byte[]> ReadPrefixAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var prefix = new byte[8]; await stream.ReadExactlyAsync(prefix); return prefix;
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var output = new MemoryStream(); await stream.CopyToAsync(output); return output.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
