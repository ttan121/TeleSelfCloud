using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class TelegramVaultRegistryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.VaultTests", Guid.NewGuid().ToString("N"));
    private TelegramVaultRegistry Registry => new(root, "42");
    private static TelegramStorageChannelInfo Channel(long id) => new(id, "42", "Same title");
    private sealed record LegacyState(int SchemaVersion, string AccountId, long PrimaryChatId, long ActiveChatId,
        IReadOnlyList<TelegramStorageChannelInfo> Vaults);
    private sealed record LegacyEnvelope(LegacyState State, string Sha256);

    [Fact]
    public async Task PrimaryDirectoryIsImmutableAndAdditionalSelectionSurvivesRestart()
    {
        var first = await Registry.RegisterAsync(Channel(-101), true, default);
        Assert.Equal(Path.GetFullPath(root), Registry.GetDataDirectory(first, -101));
        var second = await Registry.RegisterAsync(Channel(-102), true, default);
        Assert.Equal(-101, second.PrimaryChatId);
        Assert.Equal(-102, (await Registry.LoadAsync(default))!.ActiveChatId);
        Assert.NotEqual(root, Registry.GetDataDirectory(second, -102));
        var back = await Registry.SelectAsync(-101, default);
        Assert.Equal(root, Registry.GetDataDirectory(back, -101));
        Assert.Equal(2, back.Vaults.Count);
    }

    [Fact]
    public async Task IsolatedPrimaryUsesVaultDirectoryAndKeepsLegacyAccountRootUntouched()
    {
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "legacy-recovery-marker");
        await File.WriteAllTextAsync(marker, "keep");

        var initial = await Registry.RegisterIsolatedPrimaryAsync(Channel(-101), default);
        var isolatedPath = Registry.GetDataDirectory(initial, -101);
        Assert.True(initial.PrimaryVaultUsesIsolatedDirectory);
        Assert.NotEqual(Path.GetFullPath(root), isolatedPath);
        var expectedPath = Path.Combine(root, "vaults", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("-101"))));
        Assert.Equal(Path.GetFullPath(expectedPath), isolatedPath);
        Assert.Equal("keep", await File.ReadAllTextAsync(marker));

        var withAdditionalVault = await Registry.RegisterAsync(Channel(-102), select: false, default);
        Assert.True(withAdditionalVault.PrimaryVaultUsesIsolatedDirectory);
        Assert.Equal(-101, withAdditionalVault.PrimaryChatId);
        Assert.Equal(isolatedPath, Registry.GetDataDirectory(withAdditionalVault, -101));
        Assert.Equal("keep", await File.ReadAllTextAsync(marker));
    }

    [Fact]
    public async Task RegistryWithoutIsolatedPrimaryFieldRetainsItsOriginalChecksumAndMapping()
    {
        Directory.CreateDirectory(root);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var state = new LegacyState(1, "42", -101, -101, [Channel(-101)]);
        var stateBytes = JsonSerializer.SerializeToUtf8Bytes(state, options);
        var hash = Convert.ToHexString(SHA256.HashData(stateBytes));
        var envelope = JsonSerializer.SerializeToUtf8Bytes(new LegacyEnvelope(state, hash), options);
        await File.WriteAllBytesAsync(Path.Combine(root, "vaults.json"), envelope);

        var loaded = await Registry.LoadAsync(default);

        Assert.NotNull(loaded);
        Assert.False(loaded.PrimaryVaultUsesIsolatedDirectory);
        Assert.Equal(Path.GetFullPath(root), Registry.GetDataDirectory(loaded, -101));
    }

    [Fact]
    public async Task DuplicateRegistrationUpdatesTitleWithoutRebindingPrimaryOrSelection()
    {
        await Registry.RegisterAsync(Channel(-101), true, default);
        await Registry.RegisterAsync(Channel(-102), true, default);
        var updated = await Registry.RegisterAsync(Channel(-101) with { Title = "Renamed" }, false, default);
        Assert.Equal(-102, updated.ActiveChatId);
        Assert.Equal(-101, updated.PrimaryChatId);
        Assert.Equal(2, updated.Vaults.Count);
        Assert.Equal("Renamed", updated.Vaults.Single(v => v.ChatId == -101).Title);
    }

    [Fact]
    public async Task ForeignAccountUnknownSelectionAndCanceledWriteKeepRegistryBytes()
    {
        await Registry.RegisterAsync(Channel(-101), true, default);
        var path = Path.Combine(root, "vaults.json");
        var before = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => Registry.RegisterAsync(Channel(-102) with { AccountId = "43" }, true, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Registry.SelectAsync(-102, default));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Registry.RegisterAsync(Channel(-102), true, cancel.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        await Assert.ThrowsAsync<InvalidDataException>(() => new TelegramVaultRegistry(root, "43").LoadAsync(default));
        Assert.Single(Directory.GetFiles(root, "*.json"));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public async Task CorruptChecksumNeverCreatesAReplacementOrChangesDirectoryMapping()
    {
        await Registry.RegisterAsync(Channel(-101), true, default);
        var path = Path.Combine(root, "vaults.json");
        var damaged = (await File.ReadAllTextAsync(path)).Replace("Same title", "Other title");
        await File.WriteAllTextAsync(path, damaged);
        await Assert.ThrowsAsync<InvalidDataException>(() => Registry.RegisterAsync(Channel(-102), true, default));
        Assert.Equal(damaged, await File.ReadAllTextAsync(path));
        Assert.False(Directory.Exists(Path.Combine(root, "vaults")));
    }

    [Fact]
    public async Task RegistryLeaseRejectsConcurrentMutation()
    {
        await Registry.RegisterAsync(Channel(-101), true, default);
        using (new FileStream(Path.Combine(root, "vaults.json.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<InvalidOperationException>(() => Registry.SelectAsync(-101, default));
        Assert.Equal(-101, (await Registry.SelectAsync(-101, default)).ActiveChatId);
    }

    [Fact]
    public async Task FailedAtomicSelectionRetainsPreviousActiveVaultAndRegistry()
    {
        await Registry.RegisterAsync(Channel(-101), true, default);
        await Registry.RegisterAsync(Channel(-102), false, default);
        var path = Path.Combine(root, "vaults.json");
        var before = await File.ReadAllBytesAsync(path);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => Registry.SelectAsync(-102, default));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal(-101, (await Registry.LoadAsync(default))!.ActiveChatId);
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public async Task IdenticalFileIdsHaveIndependentCatalogQueueFoldersCheckpointsAndCacheDirectories()
    {
        await Registry.RegisterAsync(Channel(-101), true, default);
        var state = await Registry.RegisterAsync(Channel(-102), false, default);
        var first = new VaultProfileStores(Registry.GetDataDirectory(state, -101));
        var second = new VaultProfileStores(Registry.GetDataDirectory(state, -102));
        await first.PrepareAsync("42", default, -101);
        await second.PrepareAsync("42", default, -102);
        var manifest = new FileManifest(1, "same-id", "first.bin", 1, new string('A', 64), 1,
            [new(0, 0, 1, new string('A', 64), "-101/10", true)], true, "42");
        await first.Manifests.SaveAsync(manifest, default);
        await second.Manifests.SaveAsync(manifest with { FileName = "second.bin", Parts = [manifest.Parts[0] with { RemoteId = "-102/10" }] }, default);
        await first.Queue.EnsureAsync("same-id", "first.bin", 1, default);
        await second.Queue.EnsureAsync("same-id", "second.bin", 1, default);
        await first.Folders.CreateAsync("42", "first-folder", default);
        await second.Folders.CreateAsync("42", "second-folder", default);
        await first.Checkpoints.SaveAsync("42", -101, new(100, DateTimeOffset.UtcNow), default);
        await second.Checkpoints.SaveAsync("42", -102, new(200, DateTimeOffset.UtcNow), default);
        await second.Manifests.DeleteManyAsync(["same-id"], default);
        Assert.Equal("first.bin", (await first.Manifests.LoadAsync("same-id", default))!.FileName);
        Assert.Equal("first.bin", (await first.Queue.ListAsync(default)).Single().FileName);
        Assert.Equal("second.bin", (await second.Queue.ListAsync(default)).Single().FileName);
        Assert.Equal("first-folder", (await first.Folders.ListAsync("42", default)).Single().Path);
        Assert.Equal("second-folder", (await second.Folders.ListAsync("42", default)).Single().Path);
        Assert.Equal(100, (await first.Checkpoints.LoadAsync("42", -101, default))!.HighestMessageId);
        Assert.Null(await second.Checkpoints.LoadAsync("42", -101, default));
        Assert.NotEqual(first.StagingRoot, second.StagingRoot);
        await first.PrepareAsync("42", default, -101);
    }

    [Theory]
    [InlineData("43", "-101/10")]
    [InlineData("42", "-102/10")]
    public async Task LegacyMixedOwnershipStopsBeforeOpeningWithoutEditingRows(string account, string remote)
    {
        var stores = new VaultProfileStores(root);
        Directory.CreateDirectory(root);
        var manifest = new FileManifest(1, "file", "original.bin", 1, new string('A', 64), 1,
            [new(0, 0, 1, new string('A', 64), remote, true)], true, account);
        await stores.Manifests.SaveAsync(manifest, default);
        var report = await stores.InspectOwnershipAsync("42", -101, default);
        Assert.Equal(1, report.ManifestCount);
        Assert.Equal(account == "42" ? 0 : 1, report.ForeignAccountManifestCount);
        Assert.Equal(account == "42" ? 1 : 0, report.ForeignChatManifestCount);
        await Assert.ThrowsAsync<InvalidDataException>(() => stores.PrepareAsync("42", default, -101));
        Assert.False(Directory.Exists(stores.StagingRoot));
        var retained = (await stores.Manifests.LoadAsync("file", default))!;
        Assert.Equal(manifest.FileName, retained.FileName);
        Assert.Equal(manifest.AccountId, retained.AccountId);
        Assert.Equal(manifest.Parts, retained.Parts);
        Assert.Null(await Registry.LoadAsync(default));
    }

    [Fact]
    public async Task OwnershipInspectionCountsMixedAndUnboundLegacyRecordsWithoutChangingThem()
    {
        var stores = new VaultProfileStores(root);
        Directory.CreateDirectory(root);
        var hash = new string('A', 64);
        await stores.Manifests.SaveAsync(new FileManifest(1, "mixed", "mixed.bin", 2, hash, 1,
            [new(0, 0, 1, hash, "-101/1", true), new(1, 1, 1, hash, "-102/2", true)], true, "42"), default);
        await stores.Manifests.SaveAsync(new FileManifest(1, "legacy", "legacy.bin", 1, hash, 1,
            [new(0, 0, 1, hash, "-102/3", true)], true), default);

        var report = await stores.InspectOwnershipAsync("42", -101, default);

        Assert.Equal(2, report.ManifestCount);
        Assert.Equal(0, report.ForeignAccountManifestCount);
        Assert.Equal(1, report.UnboundAccountManifestCount);
        Assert.Equal(2, report.ForeignChatManifestCount);
        Assert.Equal(1, report.MixedChatManifestCount);
        Assert.Equal(2, report.ForeignChatPartCount);
        Assert.Equal(0, report.MalformedRemoteReferenceCount);
        Assert.Equal("mixed.bin", (await stores.Manifests.LoadAsync("mixed", default))!.FileName);
        Assert.Equal("legacy.bin", (await stores.Manifests.LoadAsync("legacy", default))!.FileName);
        Assert.False(Directory.Exists(stores.StagingRoot));
    }

    [Fact]
    public async Task OwnershipInspectionIncludesDedupCopySourceChatScope()
    {
        var stores = new VaultProfileStores(root);
        Directory.CreateDirectory(root);
        var hash = new string('A', 64);
        var manifest = new FileManifest(1, "draft", "draft.bin", 1, hash, 1,
            [new(0, 0, 1, hash, null, false, CopySource: new PartCopySource("42", "source", 0, "-102/2"))], false, "42");
        await stores.Manifests.SaveAsync(manifest, default);

        var report = await stores.InspectOwnershipAsync("42", -101, default);

        Assert.Equal(1, report.ForeignChatManifestCount);
        Assert.Equal(1, report.ForeignChatPartCount);
        Assert.Equal(0, report.ForeignAccountReferenceCount);
        await Assert.ThrowsAsync<InvalidDataException>(() => stores.PrepareAsync("42", default, -101));
        var retained = (await stores.Manifests.LoadAsync("draft", default))!;
        Assert.Equal(manifest.FileName, retained.FileName);
        Assert.Equal(manifest.AccountId, retained.AccountId);
        Assert.Equal(manifest.Parts, retained.Parts);
        Assert.False(Directory.Exists(stores.StagingRoot));
    }

    [Fact]
    public async Task LegacyIsolationCreatesEmptyPrimaryAndPreservesLegacyCatalogQueueCheckpointAndStaging()
    {
        Directory.CreateDirectory(root);
        var legacy = new VaultProfileStores(root);
        var hash = new string('A', 64);
        var current = new FileManifest(1, "current", "current.bin", 1, hash, 1,
            [new(0, 0, 1, hash, "-101/10", true)], true, "42");
        var historical = new FileManifest(1, "historical", "historical.bin", 1, hash, 1,
            [new(0, 0, 1, hash, "-102/20", true)], true, "42");
        await legacy.Manifests.SaveAsync(current, default);
        await legacy.Manifests.SaveAsync(historical, default);
        await legacy.Queue.EnsureAsync("current", "current.bin", 1, default);
        await legacy.Checkpoints.SaveAsync("42", -101, new(88, DateTimeOffset.UtcNow), default);
        Directory.CreateDirectory(legacy.StagingRoot);
        var stage = Path.Combine(legacy.StagingRoot, "keep.part");
        await File.WriteAllTextAsync(stage, "retained stage");
        var manifestsBefore = await legacy.Manifests.ListAsync(default);
        var queueBefore = await legacy.Queue.ListAsync(default);
        var checkpointBefore = await legacy.Checkpoints.ListAccountAsync("42", default);
        var isolatedRegistry = Registry;
        var target = new VaultProfileStores(isolatedRegistry.GetIsolatedPrimaryDirectory(-101));

        var result = await VaultLegacyPrimaryIsolationWorkflow.IsolateAsync(legacy, target, isolatedRegistry, Channel(-101),
            (path, token) => target.PrepareAsync("42", token, -101), default);

        Assert.True(result.Registry.PrimaryVaultUsesIsolatedDirectory);
        Assert.Equal(Path.GetFullPath(target.Root), result.IsolatedDirectory);
        Assert.Equal(["current"], result.Partition.CurrentVaultFileIds);
        Assert.Equal(["historical"], result.Partition.LocalRecoveryFileIds);
        Assert.Empty(await target.Manifests.ListAsync(default));
        Assert.Empty(await target.Queue.ListAsync(default));
        Assert.Empty(await target.Checkpoints.ListAccountAsync("42", default));
        Assert.Equal(JsonSerializer.Serialize(manifestsBefore), JsonSerializer.Serialize(await legacy.Manifests.ListAsync(default)));
        Assert.Equal(JsonSerializer.Serialize(queueBefore), JsonSerializer.Serialize(await legacy.Queue.ListAsync(default)));
        Assert.Equal(JsonSerializer.Serialize(checkpointBefore), JsonSerializer.Serialize(await legacy.Checkpoints.ListAccountAsync("42", default)));
        Assert.Equal("retained stage", await File.ReadAllTextAsync(stage));
        Assert.True(File.Exists(Path.Combine(target.Root, ".tsc-legacy-primary-isolation.json")));
        Assert.Equal(target.Root, (await isolatedRegistry.LoadAsync(default)) is { } registered
            ? isolatedRegistry.GetDataDirectory(registered, -101) : "");
    }

    [Fact]
    public async Task FailedLegacyIsolationCanResumeAndUnknownTargetDataIsNeverAdopted()
    {
        Directory.CreateDirectory(root);
        var legacy = new VaultProfileStores(root);
        var hash = new string('A', 64);
        await legacy.Manifests.SaveAsync(new FileManifest(1, "historical", "historical.bin", 1, hash, 1,
            [new(0, 0, 1, hash, "-102/20", true)], true, "42"), default);
        var isolatedRegistry = Registry;
        var target = new VaultProfileStores(isolatedRegistry.GetIsolatedPrimaryDirectory(-101));
        await Assert.ThrowsAsync<IOException>(() => VaultLegacyPrimaryIsolationWorkflow.IsolateAsync(legacy, target,
            isolatedRegistry, Channel(-101), (_, _) => throw new IOException("simulated prepare interruption"), default));
        Assert.Null(await isolatedRegistry.LoadAsync(default));
        await VaultLegacyPrimaryIsolationWorkflow.IsolateAsync(legacy, target, isolatedRegistry, Channel(-101),
            (path, token) => target.PrepareAsync("42", token, -101), default);
        Assert.NotNull(await isolatedRegistry.LoadAsync(default));

        var secondRoot = root + "-unknown";
        Directory.CreateDirectory(secondRoot);
        try
        {
            var otherRegistry = new TelegramVaultRegistry(secondRoot, "42");
            var unknownTarget = new VaultProfileStores(otherRegistry.GetIsolatedPrimaryDirectory(-101));
            Directory.CreateDirectory(unknownTarget.Root);
            await File.WriteAllTextAsync(Path.Combine(unknownTarget.Root, "unknown.txt"), "keep");
            await Assert.ThrowsAsync<InvalidDataException>(() => VaultLegacyPrimaryIsolationWorkflow.IsolateAsync(legacy,
                unknownTarget, otherRegistry, Channel(-101), (path, token) => unknownTarget.PrepareAsync("42", token, -101), default));
            Assert.Null(await otherRegistry.LoadAsync(default));
            Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(unknownTarget.Root, "unknown.txt")));
        }
        finally { if (Directory.Exists(secondRoot)) Directory.Delete(secondRoot, true); }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
