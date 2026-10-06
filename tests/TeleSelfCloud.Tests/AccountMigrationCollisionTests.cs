using System.Security.Cryptography;
using System.Text.Json;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class AccountMigrationCollisionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.MigrationCollision", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Bytes = [2, 7, 1, 8];

    [Theory]
    [InlineData("draft")]
    [InlineData("locator")]
    [InlineData("organization")]
    [InlineData("revision")]
    [InlineData("layout")]
    public async Task SamePlaintextContentWithDifferentPortableStateKeepsBothCatalogs(string difference)
    {
        var (shared, target, source) = await SetupAsync();
        var destination = source with { Parts = [source.Parts[0] with { StagingPath = null }] };
        destination = difference switch
        {
            "draft" => destination with { Committed = false, Parts = [destination.Parts[0] with { Confirmed = false, RemoteId = null }] },
            "locator" => destination with { Parts = [destination.Parts[0] with { RemoteId = "-101/20" }] },
            "organization" => destination with { FileName = "renamed.bin", FolderPath = "Kept", IsFavorite = true },
            "revision" => destination with { Revision = 2, UpdatedAtUtc = DateTimeOffset.UnixEpoch },
            "layout" => destination with { PartSizeBytes = 2, Parts =
                [new(0, 0, 2, Hash(Bytes[..2]), "-101/20", true), new(1, 2, 2, Hash(Bytes[2..]), "-101/30", true)] },
            _ => throw new ArgumentOutOfRangeException(nameof(difference))
        };
        await target.Manifests.SaveAsync(destination, default);
        var before = JsonSerializer.Serialize(destination);

        await Assert.ThrowsAsync<InvalidDataException>(() => Migrator(shared, target).MigrateAsync("42", default, -101));

        await AssertSourceKeptAsync(shared, source);
        Assert.Equal(before, JsonSerializer.Serialize(await target.Manifests.LoadAsync(source.FileId, default)));
        Assert.Empty(await target.Queue.ListAsync(default));
    }

    [Theory]
    [InlineData("missing-path")]
    [InlineData("missing-file")]
    [InlineData("corrupt")]
    public async Task EquivalentTargetWithoutVerifiedSourceStagingCoverageKeepsSource(string destinationState)
    {
        var (shared, target, source) = await SetupAsync();
        Directory.CreateDirectory(target.StagingRoot);
        var path = Path.Combine(target.StagingRoot, "target.part");
        if (destinationState == "corrupt") await File.WriteAllBytesAsync(path, [8, 1, 7, 2]);
        var destination = source with { Parts = [source.Parts[0] with { StagingPath = destinationState == "missing-path" ? null : path }] };
        await target.Manifests.SaveAsync(destination, default);

        await Assert.ThrowsAsync<InvalidDataException>(() => Migrator(shared, target).MigrateAsync("42", default, -101));

        await AssertSourceKeptAsync(shared, source);
        Assert.Empty(await target.Queue.ListAsync(default));
    }

    [Fact]
    public async Task VerifiedEquivalentRetryImportsQueueOnlyAfterDestinationProof()
    {
        var (shared, target, source) = await SetupAsync();
        Directory.CreateDirectory(target.StagingRoot);
        var path = Path.Combine(target.StagingRoot, "target.part");
        await File.WriteAllBytesAsync(path, Bytes);
        await target.Manifests.SaveAsync(source with { Parts = [source.Parts[0] with { StagingPath = path }] }, default);

        var result = await Migrator(shared, target).MigrateAsync("42", default, -101);

        Assert.Equal(1, result.MigratedManifestCount);
        Assert.Equal(1, result.MigratedQueueItemCount);
        Assert.Empty(await shared.Manifests.ListAsync(default));
        Assert.Empty(await shared.Queue.ListAsync(default));
        Assert.Single(await target.Queue.ListAsync(default));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(path, (await target.Manifests.LoadAsync(source.FileId, default))!.Parts[0].StagingPath);
    }

    [Fact]
    public async Task EquivalentEncryptedTargetWithoutWholePayloadCoverageKeepsSource()
    {
        var (shared, target, original) = await SetupAsync();
        var key = AesGcmFileCipher.CreateFileKey();
        PassphraseKeyEnvelope envelope;
        try { envelope = AesGcmFileCipher.WrapFileKey(key, "fixture recovery phrase"); }
        finally { CryptographicOperations.ZeroMemory(key); }
        var payloadPath = Path.Combine(shared.StagingRoot, "payload.bin");
        await File.WriteAllBytesAsync(payloadPath, Bytes);
        var source = original with { Encryption = new(1, Bytes.Length, Hash(Bytes), envelope, payloadPath) };
        await shared.Manifests.SaveAsync(source, default);
        Directory.CreateDirectory(target.StagingRoot);
        var targetPart = Path.Combine(target.StagingRoot, "target.part");
        await File.WriteAllBytesAsync(targetPart, Bytes);
        await target.Manifests.SaveAsync(source with
        { Parts = [source.Parts[0] with { StagingPath = targetPart }], Encryption = source.Encryption with { StagingPath = null } }, default);

        await Assert.ThrowsAsync<InvalidDataException>(() => Migrator(shared, target).MigrateAsync("42", default, -101));

        await AssertSourceKeptAsync(shared, source);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(payloadPath));
        Assert.Empty(await target.Queue.ListAsync(default));
    }

    private async Task<(VaultProfileStores Shared, VaultProfileStores Target, FileManifest Source)> SetupAsync()
    {
        var shared = new VaultProfileStores(Path.Combine(root, "shared"));
        var target = new VaultProfileStores(Path.Combine(root, "target"));
        Directory.CreateDirectory(shared.StagingRoot);
        var path = Path.Combine(shared.StagingRoot, "source.part");
        await File.WriteAllBytesAsync(path, Bytes);
        var manifest = new FileManifest(1, "same-id", "source.bin", Bytes.Length, Hash(Bytes), Bytes.Length,
            [new(0, 0, Bytes.Length, Hash(Bytes), "-101/10", true, path)], true, "42");
        await shared.Manifests.SaveAsync(manifest, default);
        await shared.Queue.EnsureAsync(manifest.FileId, manifest.FileName, manifest.TransferSize, default);
        return (shared, target, manifest);
    }

    private static async Task AssertSourceKeptAsync(VaultProfileStores shared, FileManifest source)
    {
        Assert.Equal(JsonSerializer.Serialize(source), JsonSerializer.Serialize(await shared.Manifests.LoadAsync(source.FileId, default)));
        Assert.Single(await shared.Queue.ListAsync(default));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(source.Parts[0].StagingPath!));
    }

    private static AccountProfileDataMigrator Migrator(VaultProfileStores shared, VaultProfileStores target) =>
        new(shared.Manifests, target.Manifests, shared.Queue, target.Queue, shared.Checkpoints, target.Checkpoints,
            shared.Cache, shared.StagingRoot, target.StagingRoot);

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
