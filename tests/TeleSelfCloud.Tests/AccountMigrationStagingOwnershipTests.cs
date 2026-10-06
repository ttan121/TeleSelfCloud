using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class AccountMigrationStagingOwnershipTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.MigrationStagingOwnership", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Bytes = [2, 7, 1, 8];

    [Fact]
    public async Task EquivalentTargetReferencingSharedGeneratedPartKeepsSourceBeforeOrphanReconciliation()
    {
        var (shared, target, source, now) = await SetupAsync();
        await target.Manifests.SaveAsync(source, default);

        await Assert.ThrowsAsync<InvalidDataException>(() => Migrator(shared, target).MigrateAsync("42", default, -101));

        await AssertSourceKeptAsync(shared, target, source);
        using var lease = LocalProfileLease.TryAcquire(root)!;
        Assert.NotNull(lease);
        var result = await StagingOrphanReconciler.ReconcileAsync(shared.Root, shared.StagingRoot, shared.Manifests, lease, now);

        Assert.True(result.CatalogReadable);
        Assert.Equal(0, result.DeletedFiles);
        Assert.Equal(0, result.DeletedDirectories);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(source.Parts[0].StagingPath!));
    }

    [Fact]
    public async Task EquivalentEncryptedTargetReferencingSharedPayloadKeepsSourceAndPayload()
    {
        var (shared, target, original, now) = await SetupAsync();
        var key = AesGcmFileCipher.CreateFileKey();
        byte[] payload;
        PassphraseKeyEnvelope envelope;
        try
        {
            envelope = AesGcmFileCipher.WrapFileKey(key, "fixture recovery phrase");
            using var encrypted = new MemoryStream();
            await AesGcmFileCipher.EncryptAsync(new MemoryStream(Bytes), encrypted, key);
            payload = encrypted.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        var sourcePart = original.Parts[0].StagingPath!;
        await File.WriteAllBytesAsync(sourcePart, payload);
        File.SetLastWriteTimeUtc(sourcePart, now.AddDays(-60).UtcDateTime);
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(sourcePart)!, now.AddDays(-60).UtcDateTime);
        var payloadDirectory = Path.Combine(shared.StagingRoot, "encrypted-preparation");
        Directory.CreateDirectory(payloadDirectory);
        var payloadPath = Path.Combine(payloadDirectory, Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(payloadPath, payload);
        File.SetLastWriteTimeUtc(payloadPath, now.AddDays(-60).UtcDateTime);
        var source = original with
        {
            PartSizeBytes = payload.Length,
            Parts = [original.Parts[0] with { Length = payload.Length, Sha256 = Hash(payload) }],
            Encryption = new(1, payload.Length, Hash(payload), envelope, payloadPath)
        };
        await shared.Manifests.SaveAsync(source, default);
        Directory.CreateDirectory(target.StagingRoot);
        var targetPart = Path.Combine(target.StagingRoot, "target.part");
        await File.WriteAllBytesAsync(targetPart, payload);
        var destination = source with { Parts = [source.Parts[0] with { StagingPath = targetPart }] };
        await target.Manifests.SaveAsync(destination, default);

        await Assert.ThrowsAsync<InvalidDataException>(() => Migrator(shared, target).MigrateAsync("42", default, -101));

        await AssertSourceKeptAsync(shared, target, source, destination);
        using var lease = LocalProfileLease.TryAcquire(root)!;
        Assert.NotNull(lease);
        var result = await StagingOrphanReconciler.ReconcileAsync(shared.Root, shared.StagingRoot, shared.Manifests, lease, now);

        Assert.True(result.CatalogReadable);
        Assert.Equal(0, result.DeletedFiles);
        Assert.Equal(payload, await File.ReadAllBytesAsync(sourcePart));
        Assert.Equal(payload, await File.ReadAllBytesAsync(payloadPath));
        Assert.Equal(payload, await File.ReadAllBytesAsync(targetPart));
    }

    [Fact]
    public async Task EquivalentTargetPartUnderJunctionKeepsSharedCatalogAndOutsideBytes()
    {
        var (shared, target, source, _) = await SetupAsync();
        Directory.CreateDirectory(target.StagingRoot);
        var outside = Path.Combine(root, "outside-target");
        Directory.CreateDirectory(outside);
        var outsidePart = Path.Combine(outside, "target.part");
        await File.WriteAllBytesAsync(outsidePart, Bytes);
        var junction = Path.Combine(target.StagingRoot, "linked");
        try
        {
            await CreateJunctionAsync(junction, outside);
            var destination = source with { Parts = [source.Parts[0] with { StagingPath = Path.Combine(junction, "target.part") }] };
            await target.Manifests.SaveAsync(destination, default);

            await Assert.ThrowsAsync<InvalidDataException>(() => Migrator(shared, target).MigrateAsync("42", default, -101));

            await AssertSourceKeptAsync(shared, target, source, destination);
            Assert.Equal(Bytes, await File.ReadAllBytesAsync(outsidePart));
        }
        finally { if (Directory.Exists(junction)) Directory.Delete(junction, recursive: false); }
    }

    [Fact]
    public async Task CopyingSourceThroughSharedStagingJunctionDoesNotDeleteOutsideFileDuringCleanup()
    {
        var (shared, target, original, _) = await SetupAsync();
        var outside = Path.Combine(root, "outside-source");
        Directory.CreateDirectory(outside);
        var outsidePart = Path.Combine(outside, "part.bin");
        await File.WriteAllBytesAsync(outsidePart, Bytes);
        var junction = Path.Combine(shared.StagingRoot, "linked");
        try
        {
            await CreateJunctionAsync(junction, outside);
            var source = original with { Parts = [original.Parts[0] with { StagingPath = Path.Combine(junction, "part.bin") }] };
            await shared.Manifests.SaveAsync(source, default);

            var result = await Migrator(shared, target).MigrateAsync("42", default, -101);

            Assert.Equal(1, result.MigratedManifestCount);
            Assert.Null(await shared.Manifests.LoadAsync(source.FileId, default));
            Assert.Empty(await shared.Queue.ListAsync(default));
            Assert.Single(await target.Queue.ListAsync(default));
            var destination = (await target.Manifests.LoadAsync(source.FileId, default))!;
            Assert.Equal(ManifestRevisionSelector.PortableFingerprint(source), ManifestRevisionSelector.PortableFingerprint(destination));
            Assert.StartsWith(Path.GetFullPath(target.StagingRoot) + Path.DirectorySeparatorChar, Path.GetFullPath(destination.Parts[0].StagingPath!), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Bytes, await File.ReadAllBytesAsync(destination.Parts[0].StagingPath!));
            Assert.Equal(Bytes, await File.ReadAllBytesAsync(outsidePart));
        }
        finally { if (Directory.Exists(junction)) Directory.Delete(junction, recursive: false); }
    }

    private async Task<(VaultProfileStores Shared, VaultProfileStores Target, FileManifest Source, DateTimeOffset Now)> SetupAsync()
    {
        var shared = new VaultProfileStores(Path.Combine(root, "shared"));
        var target = new VaultProfileStores(Path.Combine(root, "target"));
        var directory = Path.Combine(shared.StagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "part-00000000.bin");
        await File.WriteAllBytesAsync(path, Bytes);
        var now = DateTimeOffset.UtcNow;
        File.SetLastWriteTimeUtc(path, now.AddDays(-60).UtcDateTime);
        Directory.SetLastWriteTimeUtc(directory, now.AddDays(-60).UtcDateTime);
        var source = new FileManifest(1, "same-id", "source.bin", Bytes.Length, Hash(Bytes), Bytes.Length,
            [new(0, 0, Bytes.Length, Hash(Bytes), "-101/10", true, path)], true, "42");
        await shared.Manifests.SaveAsync(source, default);
        await shared.Queue.EnsureAsync(source.FileId, source.FileName, source.TransferSize, default);
        return (shared, target, source, now);
    }

    private static async Task AssertSourceKeptAsync(VaultProfileStores shared, VaultProfileStores target, FileManifest source, FileManifest? destination = null)
    {
        Assert.Equal(JsonSerializer.Serialize(source), JsonSerializer.Serialize(await shared.Manifests.LoadAsync(source.FileId, default)));
        Assert.Single(await shared.Queue.ListAsync(default));
        Assert.Empty(await target.Queue.ListAsync(default));
        Assert.Equal(JsonSerializer.Serialize(destination ?? source), JsonSerializer.Serialize(await target.Manifests.LoadAsync(source.FileId, default)));
    }

    private static async Task CreateJunctionAsync(string junction, string destination)
    {
        using var create = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe", UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "/c", "mklink", "/J", junction, destination }
        }) ?? throw new InvalidOperationException("Could not start staging ownership junction fixture creation.");
        await create.WaitForExitAsync();
        Assert.Equal(0, create.ExitCode);
        Assert.True(LocalFileSystemPathGuard.ContainsReparsePoint(junction));
    }

    private static AccountProfileDataMigrator Migrator(VaultProfileStores shared, VaultProfileStores target) =>
        new(shared.Manifests, target.Manifests, shared.Queue, target.Queue, shared.Checkpoints, target.Checkpoints,
            shared.Cache, shared.StagingRoot, target.StagingRoot);

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
