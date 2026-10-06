using System.Security.Cryptography;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class DedupLayoutTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.Layout", Guid.NewGuid().ToString("N"));
    private const string StagingKey = "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=";
    private const string StagingProtectionId = "5f4f2ae5d8b54a9eb145f230fa4b3df8";
    private sealed class Capability(long max = 1000) : IUploadCapabilityProvider
    {
        public Task RefreshAsync(CancellationToken token) => Task.CompletedTask;
        public Task<UploadCapability?> GetCurrentAsync(CancellationToken token) => Task.FromResult<UploadCapability?>(new("42", max, DateTimeOffset.UtcNow, "fixture"));
    }
    [Theory]
    [InlineData(2, 3)]
    [InlineData(3, 2)]
    [InlineData(6, 2)]
    [InlineData(2, 6)]
    public async Task StageReopenCopyOrExplicitFallbackRetainsVerifiedAdoptedLayout(int remoteSize, int requestedSize)
    {
        Directory.CreateDirectory(root); var bytes = "ABCDEF"u8.ToArray(); var path = Path.Combine(root, "source.bin"); File.WriteAllBytes(path, bytes);
        var world = new DedupOwnershipAcceptanceTests.World(root); var transport = new TelegramFileTransport(world, -100, Path.Combine(root, "download"));
        var db = Path.Combine(root, "db"); var store = new SqliteManifestStore(db); var publisher = new TelegramManifestPublisher(transport, Path.Combine(root, "publication"));
        var original = await new UploadPipeline(new FileTransferCoordinator(), new Capability(), transport, store, publisher).UploadAsync(path, Path.Combine(root, "stage"), remoteSize, default, true);
        var sends = world.LocalSends;
        var staged = await new UploadPipeline(new FileTransferCoordinator(), new Capability(), transport, store, publisher, new TelegramUploadDeduplication(store, world, transport, "42"))
            .StageForUploadAsync(path, Path.Combine(root, "stage"), requestedSize, default, true);
        Assert.Equal(sends, world.LocalSends); Assert.Equal(0, world.IdSends);
        Assert.NotEqual(original.FileId, staged.FileId); Assert.Equal(remoteSize, staged.PartSizeBytes); Assert.Equal(original.Parts.Count, staged.Parts.Count);
        Assert.All(staged.Parts, p => { Assert.False(p.Confirmed); Assert.Null(p.RemoteId); Assert.Equal(original.FileId, p.CopySource!.OwnerFileId); Assert.Contains("dedup-layout-", p.StagingPath); });
        Assert.Equal(bytes, staged.Parts.SelectMany(p => File.ReadAllBytes(p.StagingPath!)).ToArray());
        var reopened = new SqliteManifestStore(db);
        var copied = await new UploadPipeline(new FileTransferCoordinator(), new Capability(), transport, reopened, publisher, new TelegramUploadDeduplication(reopened, world, transport, "42")).ResumeAsync(staged.FileId, default);
        Assert.True(copied.Committed); Assert.Equal(copied.Parts.Count, world.IdSends); Assert.Equal(sends + 1, world.LocalSends);
        var second = await new UploadPipeline(new FileTransferCoordinator(), new Capability(), transport, store, publisher, new TelegramUploadDeduplication(store, world, transport, "42"))
            .StageForUploadAsync(path, Path.Combine(root, "stage"), requestedSize, default, true);
        var pipeline = new UploadPipeline(new FileTransferCoordinator(), new Capability(), transport, store, publisher);
        var switched = await pipeline.SwitchPendingCopiesToUploadAsync(second.FileId, default); Assert.All(switched.Parts, p => Assert.Null(p.CopySource));
        var beforeUpload = world.LocalSends; await pipeline.ResumeAsync(second.FileId, default);
        Assert.Equal(beforeUpload + second.Parts.Count + 1, world.LocalSends); // Remaining parts + master; no copy sends.
        var output = Path.Combine(root, "restore.bin"); await new FileTransferCoordinator(transport).ReassembleAsync(copied, output, default); Assert.Equal(bytes, File.ReadAllBytes(output));
    }
    private sealed class NoSend : IPartTransport, IRemoteManifestPublisher
    {
        public Task<string> UploadPartAsync(string path, string fileId, int index, CancellationToken token) => throw new Xunit.Sdk.XunitException("No send allowed.");
        public Task<Stream> DownloadPartAsync(string id, CancellationToken token) => throw new Xunit.Sdk.XunitException("No download allowed.");
        public Task PublishCommittedAsync(FileManifest manifest, CancellationToken token) => throw new Xunit.Sdk.XunitException("No publish allowed.");
    }
    [Fact]
    public async Task CandidateOverCurrentCapabilityUsesOrdinaryLayoutWithoutRemoteVerification()
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "source.bin"); File.WriteAllBytes(path, "ABCDEF"u8.ToArray());
        var world = new DedupOwnershipAcceptanceTests.World(root); var transport = new TelegramFileTransport(world, -100, Path.Combine(root, "download"));
        var store = new SqliteManifestStore(Path.Combine(root, "db")); var publisher = new TelegramManifestPublisher(transport, Path.Combine(root, "publication"));
        await new UploadPipeline(new FileTransferCoordinator(), new Capability(), transport, store, publisher).UploadAsync(path, Path.Combine(root, "stage"), 6, default, true);
        var staged = await new UploadPipeline(new FileTransferCoordinator(), new Capability(3), transport, store, publisher, new TelegramUploadDeduplication(store, world, transport, "42"))
            .StageForUploadAsync(path, Path.Combine(root, "stage"), 2, default, true);
        Assert.Equal(2, staged.PartSizeBytes); Assert.Equal(3, staged.Parts.Count); Assert.All(staged.Parts, p => Assert.Null(p.CopySource));
        Assert.Equal(0, world.Downloads); Assert.Equal(0, world.IdSends);
    }

    [Fact]
    public async Task DedupLayoutRepartitionMaterializesProtectedSourcePartsAndCleansPlaintextScratch()
    {
        Directory.CreateDirectory(root);
        var bytes = "ABCDEF"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var stageDirectory = Path.Combine(root, "stage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stageDirectory);
        var protectedPath = Path.Combine(stageDirectory, "part-00000000.bin");
        using (var cipher = new LocalStagingCipher(StagingKey, StagingProtectionId, StagingFileIdentity.Part("draft", 0)))
        await using (var input = new MemoryStream(bytes, writable: false))
        await using (var output = new FileStream(protectedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await cipher.EncryptAsync(input, output, bytes.Length);

        var staged = new FileManifest(1, "draft", "draft.bin", bytes.Length, hash, bytes.Length,
            [new PartRecord(0, 0, bytes.Length, hash, null, false, protectedPath)], false, AccountId: "42");
        var targetParts = Enumerable.Range(0, 3).Select(index =>
        {
            var chunk = bytes.AsSpan(index * 2, 2).ToArray();
            return new PartRecord(index, index * 2, 2, Convert.ToHexString(SHA256.HashData(chunk)), $"remote-{index}", true);
        }).ToArray();
        var candidate = new FileManifest(1, "candidate", "candidate.bin", bytes.Length, hash, 2, targetParts, true, AccountId: "42");
        using var lease = LocalProfileLease.TryAcquire(root) ?? throw new InvalidOperationException("Could not acquire test profile lease.");
        var contentStore = new LocalStagingContentStore(root, lease,
            identity => new LocalStagingCipher(StagingKey, StagingProtectionId, identity), allowLegacyPlaintext: true);

        var adopted = await DedupLayoutStaging.AdoptAsync(staged, candidate, 100, default, contentStore);

        var store = new LocalStagingContentStore(root, lease,
            identity => new LocalStagingCipher(StagingKey, StagingProtectionId, identity), allowLegacyPlaintext: true);
        var restored = new List<byte>();
        foreach (var part in adopted.Parts)
        {
            await using var materialized = await store.MaterializeAsync(part.StagingPath!, StagingFileIdentity.Part(staged.FileId, part.Index));
            using var output = new MemoryStream();
            await materialized.Stream.CopyToAsync(output);
            restored.AddRange(output.ToArray());
        }
        Assert.Equal(bytes, restored);
        Assert.Empty(Directory.GetDirectories(Path.Combine(root, "transient", "staging")));
        await using var encrypted = File.OpenRead(protectedPath);
        var header = new byte[8]; await encrypted.ReadExactlyAsync(header);
        Assert.True(LocalStagingCipher.HasProtectedHeader(header));
    }
    private sealed class FaultPlan(string fault, CancellationTokenSource stop) : IUploadLayoutDeduplication
    {
        public Task<IReadOnlyList<PartCopySource>?> PlanAsync(FileManifest staged, CancellationToken token) => throw new Xunit.Sdk.XunitException("Layout extension must be used.");
        public Task<string> CopyAsync(PartRecord part, PartCopySource source, string target, CancellationToken token) => throw new Xunit.Sdk.XunitException("No copy allowed.");
        public Task<FileManifest?> FindVerifiedLayoutAsync(FileManifest staged, long max, CancellationToken token)
        {
            var bytes = "ABCDEF"u8.ToArray(); var size = fault == "limit" ? 6 : 2;
            var parts = Enumerable.Range(0, bytes.Length / size).Select(i => new PartRecord(i, i * size, size,
                Convert.ToHexString(SHA256.HashData(bytes.AsSpan(i * size, size))), "-100/" + (10 + i), true, "remote-supplied-path-must-not-be-used")).ToArray();
            if (fault == "part") parts[0] = parts[0] with { Sha256 = new string('A', 64) };
            if (fault == "staging") File.WriteAllBytes(staged.Parts[0].StagingPath!, "BAD"u8.ToArray());
            if (fault == "cancel") stop.Cancel();
            return Task.FromResult<FileManifest?>(staged with { FileId = "original", PartSizeBytes = size, Parts = parts, Committed = true,
                TotalSha256 = fault == "whole" ? new string('A', 64) : staged.TotalSha256 });
        }
    }
    [Theory]
    [InlineData("part")]
    [InlineData("whole")]
    [InlineData("staging")]
    [InlineData("cancel")]
    [InlineData("limit")]
    public async Task InvalidOrCancelledLayoutKeepsOriginalSavedDraftAndStaging(string fault)
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "source"); File.WriteAllBytes(path, "ABCDEF"u8.ToArray());
        var store = new SqliteManifestStore(Path.Combine(root, "db")); var noSend = new NoSend(); using var stop = new CancellationTokenSource();
        var pipeline = new UploadPipeline(new FileTransferCoordinator(), new Capability(3), noSend, store, noSend, new FaultPlan(fault, stop));
        Assert.NotNull(await Record.ExceptionAsync(() => pipeline.StageForUploadAsync(path, Path.Combine(root, "stage"), 3, stop.Token, true)));
        var saved = Assert.Single(await store.ListAsync(default)); Assert.Equal(3, saved.PartSizeBytes); Assert.Equal(2, saved.Parts.Count);
        Assert.All(saved.Parts, p => { Assert.Null(p.CopySource); Assert.True(File.Exists(p.StagingPath)); });
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(root, "stage"), "*", SearchOption.AllDirectories), p => p.Contains("dedup-layout-"));
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
