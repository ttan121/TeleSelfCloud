using System.Security.Cryptography;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class AccountVaultScopedMigrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.ScopedProfileMigration", Guid.NewGuid().ToString("N"));
    private const string Account = "42";
    private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public async Task RegisteredPrimaryImportsOnlyProvenChatOrUnsentDraftAndKeepsSharedCheckpoints()
    {
        var shared = new VaultProfileStores(Path.Combine(root, "shared"));
        var target = new VaultProfileStores(Path.Combine(root, "primary"));
        Directory.CreateDirectory(shared.StagingRoot);
        var bytes = new byte[] { 2, 7, 1, 8 };
        var partPath = Path.Combine(shared.StagingRoot, "current.part");
        await File.WriteAllBytesAsync(partPath, bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var current = new FileManifest(1, "current", "current.bin", bytes.Length, hash, bytes.Length,
            [new(0, 0, bytes.Length, hash, "-101/10", true, partPath)], true, Account);
        var other = Remote("other", "-102/20");
        var mixed = new FileManifest(1, "mixed", "mixed.bin", 2, Hash, 1,
            [new(0, 0, 1, Hash, "-101/30", true), new(1, 1, 1, Hash, "-102/40", true)], true, Account);
        var draft = new FileManifest(1, "draft", "draft.bin", 1, Hash, 1, [new(0, 0, 1, Hash, null, false)], false);
        var copyOther = new FileManifest(1, "copy-other", "copy.bin", 1, Hash, 1,
            [new(0, 0, 1, Hash, null, false, CopySource: new(Account, "old-owner", 0, "-102/50"))], false, Account);
        var malformed = Remote("malformed", "not-a-remote-id");
        var foreign = Remote("foreign", "-101/60") with { AccountId = "43" };
        foreach (var manifest in new[] { current, other, mixed, draft, copyOther, malformed, foreign })
        {
            await shared.Manifests.SaveAsync(manifest, default);
            await shared.Queue.EnsureAsync(manifest.FileId, manifest.FileName, manifest.TransferSize, default);
        }
        var time = DateTimeOffset.Parse("2026-10-06T01:00:00Z");
        await shared.Checkpoints.SaveAsync(Account, -101, new(111, time), default);
        await shared.Checkpoints.SaveAsync(Account, -102, new(222, time), default);
        await target.Checkpoints.SaveAsync(Account, -101, new(333, time), default);

        var result = await Migrator(shared, target).MigrateAsync(Account, default, storageChatId: -101);

        Assert.Equal(2, result.MigratedManifestCount);
        Assert.Equal(2, result.MigratedQueueItemCount);
        Assert.Equal(0, result.MigratedCheckpointCount);
        Assert.Equal(4, result.RetainedManifestCount);
        Assert.Equal(new[] { "current", "draft" }, (await target.Manifests.ListAsync(default)).Select(item => item.FileId).Order());
        Assert.Equal(Account, (await target.Manifests.LoadAsync("draft", default))!.AccountId);
        Assert.Equal(new[] { "copy-other", "foreign", "malformed", "mixed", "other" }, (await shared.Manifests.ListAsync(default)).Select(item => item.FileId).Order());
        Assert.Equal(new[] { "current", "draft" }, (await target.Queue.ListAsync(default)).Select(item => item.FileId).Order());
        Assert.Equal(5, (await shared.Queue.ListAsync(default)).Count);
        Assert.Equal(new RemoteSyncCheckpoint(111, time), await shared.Checkpoints.LoadAsync(Account, -101, default));
        Assert.Equal(new RemoteSyncCheckpoint(222, time), await shared.Checkpoints.LoadAsync(Account, -102, default));
        Assert.Equal(new RemoteSyncCheckpoint(333, time), await target.Checkpoints.LoadAsync(Account, -101, default));
        Assert.Null(await target.Checkpoints.LoadAsync(Account, -102, default));
        var copied = (await target.Manifests.LoadAsync("current", default))!.Parts[0].StagingPath!;
        Assert.Equal(bytes, await File.ReadAllBytesAsync(copied));
        Assert.False(File.Exists(partPath));
    }

    [Fact]
    public async Task ScopedDestinationMismatchStopsBeforeSourceRemovalOrQueueImport()
    {
        var shared = new VaultProfileStores(Path.Combine(root, "shared"));
        var target = new VaultProfileStores(Path.Combine(root, "primary"));
        await shared.Manifests.SaveAsync(Remote("same", "-101/10"), default);
        await shared.Queue.EnsureAsync("same", "same.bin", 1, default);
        await target.Manifests.SaveAsync(Remote("same", "-102/20"), default);

        await Assert.ThrowsAsync<InvalidDataException>(() => Migrator(shared, target).MigrateAsync(Account, default, -101));

        Assert.Equal("-101/10", (await shared.Manifests.LoadAsync("same", default))!.Parts[0].RemoteId);
        Assert.Single(await shared.Queue.ListAsync(default));
        Assert.Empty(await target.Queue.ListAsync(default));
        Assert.Equal("-102/20", (await target.Manifests.LoadAsync("same", default))!.Parts[0].RemoteId);
    }

    private static FileManifest Remote(string id, string remote) =>
        new(1, id, id + ".bin", 1, Hash, 1, [new(0, 0, 1, Hash, remote, true)], true, Account);

    private static AccountProfileDataMigrator Migrator(VaultProfileStores shared, VaultProfileStores target) =>
        new(shared.Manifests, target.Manifests, shared.Queue, target.Queue, shared.Checkpoints, target.Checkpoints,
            shared.Cache, shared.StagingRoot, target.StagingRoot);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
