using System.Text.Json;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class DedupPipelineTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.DedupPipeline", Guid.NewGuid().ToString("N"));
    public DedupPipelineTests() => Directory.CreateDirectory(root);
    private sealed class Capability : IUploadCapabilityProvider
    {
        public Task RefreshAsync(CancellationToken token) => Task.CompletedTask;
        public Task<UploadCapability?> GetCurrentAsync(CancellationToken token) => Task.FromResult<UploadCapability?>(new("42", 1000, DateTimeOffset.UtcNow, "fixture"));
    }
    private sealed class Transport : IPartTransport, IAcceptedPartRecovery
    {
        public Dictionary<(string, int), string> Accepted { get; } = []; public int Uploads, Recoveries; public bool FailRecovery;
        public Task<string> UploadPartAsync(string path, string fileId, int index, CancellationToken token) { Uploads++; return Task.FromResult("-100/" + (40 + index)); }
        public Task<Stream> DownloadPartAsync(string id, CancellationToken token) => throw new InvalidOperationException();
        public Task<string?> FindAcceptedPartAsync(string path, string fileId, int index, CancellationToken token)
        { Recoveries++; if (FailRecovery) throw new IOException("History unavailable."); return Task.FromResult(Accepted.GetValueOrDefault((fileId, index))); }
    }
    private sealed class Dedup(IManifestStore store, Transport transport, bool lostAck = false) : IUploadDeduplication
    {
        public int Copies, Plans;
        public Task<IReadOnlyList<PartCopySource>?> PlanAsync(FileManifest staged, CancellationToken token)
        { Plans++; return Task.FromResult<IReadOnlyList<PartCopySource>?>(staged.Parts.Select(p => new PartCopySource("42", "original", p.Index, "-100/" + (10 + p.Index))).ToArray()); }
        public async Task<string> CopyAsync(PartRecord part, PartCopySource source, string target, CancellationToken token)
        {
            var saved = await store.LoadAsync(target, token); Assert.Equal(source, saved!.Parts[part.Index].CopySource); Assert.False(saved.Parts[part.Index].Confirmed);
            Copies++; var id = "-100/" + (20 + part.Index); transport.Accepted[(target, part.Index)] = id;
            if (lostAck && Copies == 1) throw new IOException("Simulated acknowledgment loss."); return id;
        }
    }
    private sealed class Publisher(bool failOnce = false) : IRemoteManifestPublisher
    {
        public int Calls; public FileManifest? Manifest;
        public Task PublishCommittedAsync(FileManifest manifest, CancellationToken token)
        {
            Calls++; Assert.All(manifest.Parts, p => Assert.Null(p.CopySource)); Manifest = manifest;
            if (failOnce && Calls == 1) throw new IOException("Simulated publication failure."); return Task.CompletedTask;
        }
    }
    private string Source()
    { var path = Path.Combine(root, "file.bin"); File.WriteAllBytes(path, "ABCDEF"u8.ToArray()); return path; }
    private UploadPipeline Pipeline(IManifestStore store, Transport transport, Publisher publisher, IUploadDeduplication? dedup = null) => new(new FileTransferCoordinator(), new Capability(), transport, store, publisher, dedup);
    [Fact]
    public async Task StagePersistsIntentAndRestartCopiesOwnedMessagesWithoutUploadingBytes()
    {
        var db = Path.Combine(root, "db"); var store = new SqliteManifestStore(db); var transport = new Transport(); var publisher = new Publisher(); var dedup = new Dedup(store, transport);
        var staged = await Pipeline(store, transport, publisher, dedup).StageForUploadAsync(Source(), Path.Combine(root, "stage"), 3, default, true);
        Assert.All(staged.Parts, p => Assert.NotNull(p.CopySource)); Assert.Equal(0, dedup.Copies);
        var reopened = new SqliteManifestStore(db); var restored = (await reopened.LoadAsync(staged.FileId, default))!; Assert.Equal(staged.Parts[0].CopySource, restored.Parts[0].CopySource);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Pipeline(reopened, transport, publisher).ResumeAsync(staged.FileId, default)); Assert.Equal(0, transport.Uploads);
        var resumedDedup = new Dedup(reopened, transport); var completed = await Pipeline(reopened, transport, publisher, resumedDedup).ResumeAsync(staged.FileId, default);
        Assert.True(completed.Committed); Assert.Equal(2, resumedDedup.Copies); Assert.Equal(0, resumedDedup.Plans); Assert.Equal(0, transport.Uploads);
        Assert.Equal(new[] { "-100/20", "-100/21" }, completed.Parts.Select(p => p.RemoteId)); Assert.All(completed.Parts, p => Assert.Null(p.CopySource));
    }
    [Fact]
    public async Task AcceptedCopyWithLostAckIsRecoveredAndNotSentTwice()
    {
        var store = new SqliteManifestStore(Path.Combine(root, "db")); var transport = new Transport(); var publisher = new Publisher(); var dedup = new Dedup(store, transport, true);
        await Assert.ThrowsAsync<IOException>(() => Pipeline(store, transport, publisher, dedup).UploadAsync(Source(), Path.Combine(root, "stage"), 3, default, true));
        var saved = Assert.Single(await store.ListAsync(default)); Assert.NotNull(saved.Parts[0].CopySource); Assert.False(saved.Parts[0].Confirmed);
        var resumed = await Pipeline(store, transport, publisher, dedup).ResumeAsync(saved.FileId, default);
        Assert.True(resumed.Committed); Assert.Equal(2, dedup.Copies); Assert.Equal(1, transport.Recoveries); Assert.Equal(0, transport.Uploads);
    }
    [Fact]
    public async Task PublicationRetryKeepsConfirmedCopyIdsAndMetadataTimestamp()
    {
        var store = new SqliteManifestStore(Path.Combine(root, "db")); var transport = new Transport(); var publisher = new Publisher(true); var dedup = new Dedup(store, transport);
        await Assert.ThrowsAsync<IOException>(() => Pipeline(store, transport, publisher, dedup).UploadAsync(Source(), Path.Combine(root, "stage"), 3, default, true));
        var saved = Assert.Single(await store.ListAsync(default)); Assert.False(saved.Committed); Assert.All(saved.Parts, p => Assert.True(p.Confirmed)); var timestamp = saved.UpdatedAtUtc;
        var resumed = await Pipeline(store, transport, publisher).ResumeAsync(saved.FileId, default);
        Assert.Equal(timestamp, resumed.UpdatedAtUtc); Assert.Equal(2, dedup.Copies); Assert.Equal(2, publisher.Calls); Assert.Equal(0, transport.Uploads);
    }
    [Fact]
    public async Task OrdinaryUploadRemainsDefaultAndNullIntentDoesNotChangeLegacyJson()
    {
        var store = new SqliteManifestStore(Path.Combine(root, "db")); var transport = new Transport(); var publisher = new Publisher();
        var completed = await Pipeline(store, transport, publisher).UploadAsync(Source(), Path.Combine(root, "stage"), 3, default, true);
        Assert.Equal(2, transport.Uploads); Assert.DoesNotContain("copySource", JsonSerializer.Serialize(completed, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var invalid = completed with { Committed = false, Parts = completed.Parts.Select(p => p with { Confirmed = false, RemoteId = null, CopySource = new("99", "original", p.Index, "-100/10") }).ToArray() };
        Assert.Throws<InvalidDataException>(() => ManifestValidator.ValidateStructure(invalid));
    }
    [Fact]
    public async Task ExplicitFallbackRecoversAcceptedCopyAndUploadsOnlyUnacceptedRemainder()
    {
        var store = new SqliteManifestStore(Path.Combine(root, "db")); var transport = new Transport(); var publisher = new Publisher(); var dedup = new Dedup(store, transport, true);
        await Assert.ThrowsAsync<IOException>(() => Pipeline(store, transport, publisher, dedup).UploadAsync(Source(), Path.Combine(root, "stage"), 3, default, true));
        var saved = Assert.Single(await store.ListAsync(default)); var ordinary = Pipeline(store, transport, publisher);
        var changed = await ordinary.SwitchPendingCopiesToUploadAsync(saved.FileId, default); Assert.True(changed.Parts[0].Confirmed); Assert.Equal("-100/20", changed.Parts[0].RemoteId);
        Assert.False(changed.Parts[1].Confirmed); Assert.All(changed.Parts, p => Assert.Null(p.CopySource)); Assert.Equal(0, transport.Uploads);
        var done = await ordinary.ResumeAsync(saved.FileId, default); Assert.True(done.Committed); Assert.Equal(1, transport.Uploads); Assert.Equal(1, dedup.Copies);
    }
    [Theory]
    [InlineData("history")]
    [InlineData("staged")]
    [InlineData("cancel")]
    public async Task FailedFallbackKeepsExactIntentAndDoesNotSend(string fault)
    {
        var store = new SqliteManifestStore(Path.Combine(root, "db")); var transport = new Transport(); var publisher = new Publisher(); var dedup = new Dedup(store, transport);
        var saved = await Pipeline(store, transport, publisher, dedup).StageForUploadAsync(Source(), Path.Combine(root, "stage"), 3, default, true);
        var before = JsonSerializer.Serialize(saved); if (fault == "history") transport.FailRecovery = true;
        if (fault == "staged") File.WriteAllBytes(saved.Parts[0].StagingPath!, "BAD"u8.ToArray());
        using var stop = new CancellationTokenSource(); if (fault == "cancel") stop.Cancel();
        Assert.NotNull(await Record.ExceptionAsync(() => Pipeline(store, transport, publisher).SwitchPendingCopiesToUploadAsync(saved.FileId, stop.Token)));
        Assert.Equal(before, JsonSerializer.Serialize(await store.LoadAsync(saved.FileId, default))); Assert.Equal(0, transport.Uploads); Assert.Equal(0, dedup.Copies);
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
