using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class CatalogRecoveryGuardTests
{
    private const long ChatId = -100500;
    private const string AccountId = "test-account";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("TSC-MANIFEST|2|file|hash")]
    [InlineData("TSC-MANIFEST|1|file")]
    [InlineData("TSC-FOLDERS|2|test-account")]
    public async Task UnsupportedOrMalformedProtocolDoesNotAdvanceCheckpoint(string caption)
    {
        var call = 0;
        var fixture = new Fixture((_, _) => Task.FromResult(++call == 1 ? Page((30, caption)) : Page()));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Catalog.ImportRecentAsync(CancellationToken.None));
        Assert.Equal(fixture.Previous, fixture.Checkpoints.Value);
        Assert.Empty(fixture.Manifests.Values);
        Assert.Empty(fixture.Transport.Downloads);
    }

    [Fact]
    public async Task ForeignHistoryMessageDoesNotSilentlyCommitSync()
    {
        var page = Page((30, "TSC-MANIFEST|1|file|hash"));
        page["messages"]![0]!["chat_id"] = ChatId - 1;
        var call = 0;
        var fixture = new Fixture((_, _) => Task.FromResult(++call == 1 ? page : Page()));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Catalog.ImportRecentAsync(CancellationToken.None));
        Assert.Equal(fixture.Previous, fixture.Checkpoints.Value);
        Assert.Empty(fixture.Transport.Downloads);
    }

    [Theory]
    [InlineData("-100501/20")]
    [InlineData("not-a-reference")]
    public async Task RemotePartMustBelongToScannedVault(string remoteId)
    {
        var manifest = Manifest() with { Parts = [Manifest().Parts[0] with { RemoteId = remoteId }] };
        var fixture = ManifestFixture(manifest);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Catalog.ImportRecentAsync(CancellationToken.None));
        Assert.Empty(fixture.Manifests.Values);
        Assert.Equal(fixture.Previous, fixture.Checkpoints.Value);
        Assert.Null(fixture.Catalog.LastSuccessfulSyncUtc);
    }

    [Fact]
    public async Task NullPartCollectionIsReportedAsInvalidData()
    {
        var fixture = ManifestFixture(Manifest() with { Parts = null! });
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Catalog.ImportRecentAsync(CancellationToken.None));
        Assert.Empty(fixture.Manifests.Values);
        Assert.Equal(fixture.Previous, fixture.Checkpoints.Value);
    }

    [Fact]
    public async Task CancellationAtEndOfHistoryDoesNotCommitSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture((_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(Page());
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Catalog.ImportRecentAsync(cancellation.Token));
        Assert.Equal(fixture.Previous, fixture.Checkpoints.Value);
        Assert.Null(fixture.Catalog.LastSuccessfulSyncUtc);
    }

    [Fact]
    public async Task LaterPageCannotReplayMessagesNewerThanRequestedCursor()
    {
        var call = 0;
        var fixture = new Fixture((_, _) => Task.FromResult(++call switch
        {
            1 => Page((30, null), (20, null)),
            2 => Page((25, null), (10, null)),
            _ => Page()
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Catalog.ImportRecentAsync(CancellationToken.None));
        Assert.Equal(fixture.Previous, fixture.Checkpoints.Value);
    }

    [Fact]
    public async Task BoundaryMessageCanRepeatWhileHistoryStillAdvances()
    {
        var pages = new Queue<JsonObject>([Page((30, null), (20, null)), Page((20, null), (10, null)), Page()]);
        var fixture = new Fixture((_, _) => Task.FromResult(pages.Dequeue()));
        await fixture.Catalog.ImportRecentAsync(CancellationToken.None, forceFullRescan: true);
        Assert.Equal(30, fixture.Checkpoints.Value!.HighestMessageId);
        Assert.NotNull(fixture.Catalog.LastSuccessfulSyncUtc);
    }

    [Fact]
    public async Task InclusiveOldestBoundaryCompletesFullCatalogScan()
    {
        var pages = new Queue<JsonObject>([Page((30, null), (20, null)), Page((20, null))]);
        var fixture = new Fixture((_, _) => Task.FromResult(pages.Dequeue()));
        await fixture.Catalog.ImportRecentAsync(CancellationToken.None, forceFullRescan: true);
        Assert.Equal(30, fixture.Checkpoints.Value!.HighestMessageId);
        Assert.NotNull(fixture.Catalog.LastSuccessfulSyncUtc);
    }

    [Fact]
    public async Task ReusingCatalogClearsPreviousSuccessAndPerRunCounters()
    {
        var call = 0;
        var fixture = new Fixture((_, _) => ++call switch
        {
            1 => Task.FromResult(Page((30, null))),
            2 => Task.FromResult(Page()),
            _ => Task.FromException<JsonObject>(new IOException("history unavailable"))
        });
        await fixture.Catalog.ImportRecentAsync(CancellationToken.None, forceFullRescan: true);
        Assert.Equal(1, fixture.Catalog.PagesRead);
        var completedCheckpoint = fixture.Checkpoints.Value;
        await Assert.ThrowsAsync<IOException>(() => fixture.Catalog.ImportRecentAsync(CancellationToken.None));
        Assert.Null(fixture.Catalog.LastSuccessfulSyncUtc);
        Assert.Equal(0, fixture.Catalog.PagesRead);
        Assert.Equal(0, fixture.Catalog.MessagesRead);
        Assert.Equal(completedCheckpoint, fixture.Checkpoints.Value);
    }

    [Fact]
    public async Task OverlappingScanIsRejectedAndGuardReleasesAfterCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        var fixture = new Fixture(async (_, token) =>
        {
            if (first)
            {
                first = false;
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return Page();
        });
        using var cancellation = new CancellationTokenSource();
        var running = fixture.Catalog.ImportRecentAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Catalog.ImportRecentAsync(CancellationToken.None));
        }
        finally { cancellation.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await fixture.Catalog.ImportRecentAsync(CancellationToken.None);
        Assert.NotNull(fixture.Catalog.LastSuccessfulSyncUtc);
    }

    [Fact]
    public async Task ConflictingContentKeepsLocalManifestAndPreviousCheckpoint()
    {
        var current = Manifest();
        var remote = current with { Revision = 5, TotalSha256 = new string('B', 64) };
        var fixture = ManifestFixture(remote);
        fixture.Manifests.Values[current.FileId] = current;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Catalog.ImportRecentAsync(CancellationToken.None));
        Assert.Same(current, fixture.Manifests.Values[current.FileId]);
        Assert.Equal(fixture.Previous, fixture.Checkpoints.Value);
        Assert.Null(fixture.Catalog.LastSuccessfulSyncUtc);
    }

    [Fact]
    public async Task AcceptedCommitReplacesInterruptedLocalDraftAndAdvancesCheckpoint()
    {
        var remote = Manifest() with { Revision = 1 };
        var fixture = ManifestFixture(remote);
        fixture.Manifests.Values[remote.FileId] = remote with { Committed = false, Revision = 20 };
        await fixture.Catalog.ImportRecentAsync(CancellationToken.None);
        Assert.True(fixture.Manifests.Values[remote.FileId].Committed);
        Assert.Equal(1, fixture.Manifests.Values[remote.FileId].Revision);
        Assert.Equal(30, fixture.Checkpoints.Value!.HighestMessageId);
        Assert.NotNull(fixture.Catalog.LastSuccessfulSyncUtc);
    }

    [Fact]
    public async Task TwoCatalogsWithOppositeLocalSnapshotsSelectTheSamePortableMetadata()
    {
        var first = Manifest() with { Revision = 2, IsFavorite = true };
        var second = first with { IsFavorite = false, FolderPath = "moved" };
        var left = ManifestFixture(first);
        var right = ManifestFixture(second);
        left.Manifests.Values[first.FileId] = second;
        right.Manifests.Values[first.FileId] = first with { AccountId = null };
        await left.Catalog.ImportRecentAsync(CancellationToken.None);
        await right.Catalog.ImportRecentAsync(CancellationToken.None);
        Assert.Equal(ManifestRevisionSelector.PortableFingerprint(left.Manifests.Values[first.FileId]),
            ManifestRevisionSelector.PortableFingerprint(right.Manifests.Values[first.FileId]));
        Assert.Equal(AccountId, right.Manifests.Values[first.FileId].AccountId);
        Assert.Equal(30, left.Checkpoints.Value!.HighestMessageId);
        Assert.Equal(30, right.Checkpoints.Value!.HighestMessageId);
    }

    [Fact]
    public async Task CatalogArchivesBothCompetingSnapshotsBeforeIndexReplacement()
    {
        var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.CatalogHistory", Guid.NewGuid().ToString("N"));
        try
        {
            var archive = new MetadataConflictArchive(root, AccountId, ChatId);
            var remote = Manifest() with { Revision = 2, FileName = "remote.bin" };
            var current = remote with { FileName = "local.bin" };
            var fixture = ManifestFixture(remote, archive);
            fixture.Manifests.Values[current.FileId] = current;
            await fixture.Catalog.ImportRecentAsync(default);
            var history = (await archive.LoadAsync(current.FileId, default))!;
            Assert.Equal(new[] { "local.bin", "remote.bin" }, history.Versions.Select(item => item.FileName).OrderBy(name => name));
            Assert.Equal(30, fixture.Checkpoints.Value!.HighestMessageId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ArchiveWriteFailureLeavesOriginalIndexAndCheckpoint()
    {
        var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.CatalogHistory", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var occupiedPath = Path.Combine(root, "occupied-file");
        await File.WriteAllTextAsync(occupiedPath, "keep this file");
        try
        {
            var remote = Manifest() with { Revision = 2, FileName = "remote.bin" };
            var current = remote with { FileName = "local.bin" };
            var fixture = ManifestFixture(remote, new MetadataConflictArchive(occupiedPath, AccountId, ChatId));
            fixture.Manifests.Values[current.FileId] = current;
            await Assert.ThrowsAsync<IOException>(() => fixture.Catalog.ImportRecentAsync(default));
            Assert.Same(current, fixture.Manifests.Values[current.FileId]);
            Assert.Equal(fixture.Previous, fixture.Checkpoints.Value);
            Assert.Equal("keep this file", await File.ReadAllTextAsync(occupiedPath));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FailedIndexWriteIsNotReportedAsObservedOrIndexed()
    {
        var fixture = ManifestFixture(Manifest());
        fixture.Manifests.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Catalog.ImportRecentAsync(default));
        Assert.Empty(fixture.Catalog.ObservedFileIds);
        Assert.Equal(0, fixture.Catalog.FilesIndexed);
        Assert.Equal(fixture.Previous, fixture.Checkpoints.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulScanReplacesObservationsOnlyForFullScan(bool full)
    {
        var fixture = ManifestFixture(Manifest());
        fixture.Manifests.Values["old"] = Manifest() with { FileId = "old" };
        using var report = new ReportFixture();
        await report.Store.SaveAsync(report.Seed(["old"]), default);
        var workflow = new CatalogSyncWorkflow(fixture.Catalog, report.Store, AccountId, ChatId, _ => Task.CompletedTask);
        var result = await workflow.RunAsync(full, default);
        Assert.Null(result.Error);
        Assert.Equal(CatalogSyncOutcome.Completed, result.Report.Outcome);
        Assert.Equal(full ? new[] { "file" } : new[] { "file", "old" }, result.Report.ObservedFileIds);
        Assert.True(fixture.Manifests.Values.ContainsKey("old"));
        Assert.Equal(result.Report.ObservedFileIds, (await report.Store.LoadAsync(default))!.ObservedFileIds);
    }

    [Fact]
    public async Task PartialScanRetainsImportedRowsAndPriorObservationsWithoutAdvancingCheckpoint()
    {
        var manifest = Manifest();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        var caption = $"TSC-MANIFEST|1|file|{Convert.ToHexString(SHA256.HashData(bytes))}";
        var calls = 0;
        var fixture = new Fixture((_, _) => ++calls == 1 ? Task.FromResult(Page((30, caption))) : Task.FromException<JsonObject>(new IOException("offline")));
        fixture.Transport.Bytes[$"{ChatId}/30"] = bytes;
        using var report = new ReportFixture();
        await report.Store.SaveAsync(report.Seed(["old"]), default);
        var result = await new CatalogSyncWorkflow(fixture.Catalog, report.Store, AccountId, ChatId,
            _ => throw new Exception("Must not publish after failed scan")).RunAsync(true, default);
        Assert.IsType<IOException>(result.Error);
        Assert.Equal(CatalogSyncOutcome.Failed, result.Report.Outcome);
        Assert.Equal(new[] { "file", "old" }, result.Report.ObservedFileIds);
        Assert.Equal(1, result.Report.FilesIndexed);
        Assert.True(fixture.Manifests.Values.ContainsKey("file"));
        Assert.Equal(fixture.Previous, fixture.Checkpoints.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicationFailureRetriesWithoutScanningAndKeepsCommittedCheckpoint(bool cancel)
    {
        var calls = 0;
        var fixture = new Fixture((_, _) => { calls++; return Task.FromResult(Page()); });
        using var report = new ReportFixture();
        var publications = 0;
        var workflow = new CatalogSyncWorkflow(fixture.Catalog, report.Store, AccountId, ChatId, _ =>
        {
            if (++publications == 1) return Task.FromException(cancel ? new OperationCanceledException() : new IOException("offline"));
            return Task.CompletedTask;
        });
        var first = await workflow.RunAsync(true, default);
        Assert.Equal(CatalogSyncOutcome.FolderPublicationPending, first.Report.Outcome);
        Assert.NotNull(first.Report.CatalogCompletedAtUtc);
        var checkpoint = fixture.Checkpoints.Value;
        var second = await workflow.RetryFolderPublicationAsync(default);
        Assert.Null(second.Error);
        Assert.Equal(CatalogSyncOutcome.Completed, second.Report.Outcome);
        Assert.Equal(1, calls);
        Assert.Equal(2, publications);
        Assert.Equal(checkpoint, fixture.Checkpoints.Value);
    }

    [Fact]
    public async Task ObserverFailureAfterCheckpointIsReportedAsPublicationPending()
    {
        var fixture = new Fixture((_, _) => Task.FromResult(Page()));
        fixture.Catalog.ScanStatusChanged += (_, text) => { if (text.Contains("completed")) throw new IOException("observer failed"); };
        using var report = new ReportFixture();
        var result = await new CatalogSyncWorkflow(fixture.Catalog, report.Store, AccountId, ChatId,
            _ => throw new Exception("Must wait for explicit retry")).RunAsync(true, default);
        Assert.Equal(CatalogSyncOutcome.FolderPublicationPending, result.Report.Outcome);
        Assert.NotNull(result.Report.CatalogCompletedAtUtc);
        Assert.NotEqual(fixture.Previous, fixture.Checkpoints.Value);
    }

    [Fact]
    public async Task AcceptedPublicationCompletesDespiteLateCancellation()
    {
        var fixture = new Fixture((_, _) => Task.FromResult(Page()));
        using var report = new ReportFixture();
        using var cancellation = new CancellationTokenSource();
        var result = await new CatalogSyncWorkflow(fixture.Catalog, report.Store, AccountId, ChatId,
            _ => { cancellation.Cancel(); return Task.CompletedTask; }).RunAsync(true, cancellation.Token);
        Assert.Equal(CatalogSyncOutcome.Completed, result.Report.Outcome);
        Assert.Null(result.Error);
        Assert.Equal(CatalogSyncOutcome.Completed, (await report.Store.LoadAsync(default))!.Outcome);
    }

    [Fact]
    public async Task CorruptReportBlocksIncrementalButFullScanPreservesOriginalBytes()
    {
        using var report = new ReportFixture();
        await report.Store.SaveAsync(report.Seed(["old"]), default);
        var path = Directory.GetFiles(report.Root, "*.json").Single();
        var broken = (await File.ReadAllTextAsync(path)).Replace("old", "new");
        await File.WriteAllTextAsync(path, broken);
        var calls = 0;
        var fixture = new Fixture((_, _) => { calls++; return Task.FromResult(Page()); });
        var workflow = new CatalogSyncWorkflow(fixture.Catalog, report.Store, AccountId, ChatId, _ => Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidDataException>(() => workflow.RunAsync(false, default));
        Assert.Equal(0, calls);
        var result = await workflow.RunAsync(true, default);
        Assert.Null(result.Error);
        Assert.Equal(broken, await File.ReadAllTextAsync(Directory.GetFiles(report.Root, "*.invalid").Single()));
        Assert.Empty(result.Report.ObservedFileIds);
    }

    [Fact]
    public async Task ReportsAreScopedAndExclusiveAndRunningModeSurvivesRestart()
    {
        using var report = new ReportFixture();
        var running = report.Seed(["old"]) with { RequestedFull = true, Outcome = CatalogSyncOutcome.Running, CatalogCompletedAtUtc = null };
        await report.Store.SaveAsync(running, default);
        var restarted = new CatalogSyncAttemptStore(report.Root, AccountId, ChatId);
        Assert.True((await restarted.LoadAsync(default))!.RequestedFull);
        Assert.Null(await new CatalogSyncAttemptStore(report.Root, AccountId, ChatId - 1).LoadAsync(default));
        Assert.Null(await new CatalogSyncAttemptStore(report.Root, "other", ChatId).LoadAsync(default));
        using (report.Store.AcquireLease()) Assert.Throws<InvalidOperationException>(() => restarted.AcquireLease());
        using var lease = restarted.AcquireLease();
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.SaveAsync(running with { AccountId = "other" }, default));
        Assert.Equal("old", (await restarted.LoadAsync(default))!.ObservedFileIds.Single());
    }

    [Fact]
    public async Task ProtectedSyncReportMigratesEncryptsAndPreservesInvalidRecoveryRecord()
    {
        using var report = new ReportFixture();
        await report.Store.SaveAsync(report.Seed(["private-file-token"]), default);
        using var lease = LocalProfileLease.TryAcquire(report.Root)!;
        var key = Convert.ToBase64String(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        CatalogSyncAttemptStore Protected(Func<string, string>? keyProvider = null) => new(report.Root, AccountId, ChatId,
            identity => new LocalRecordCipher(keyProvider?.Invoke(identity) ?? key,
                keyProvider is null ? "dddddddddddddddddddddddddddddddd" : "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                "sync-report", identity), lease);

        var protectedStore = Protected();
        CatalogSyncAttempt loaded;
        using (protectedStore.AcquireLease()) loaded = (await protectedStore.LoadAsync(default))!;
        Assert.Equal("private-file-token", loaded.ObservedFileIds.Single());
        var path = Directory.GetFiles(report.Root, "*.json").Single();
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal("TSCREC01", System.Text.Encoding.ASCII.GetString(bytes, 0, 8));
        Assert.DoesNotContain("private-file-token", System.Text.Encoding.UTF8.GetString(bytes));
        var wrongStore = Protected(identity => Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => wrongStore.LoadAsync(default));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));

        await protectedStore.SaveAsync(loaded with { Pages = 9 }, default);
        Assert.Equal(9, (await Protected().LoadAsync(default))!.Pages);
        var beforePreserve = await File.ReadAllBytesAsync(path);
        using (protectedStore.AcquireLease()) Protected().PreserveInvalidReport();
        var preserved = await File.ReadAllBytesAsync(Directory.GetFiles(report.Root, "*.invalid").Single());
        Assert.Equal(beforePreserve, preserved);
    }

    private sealed class ReportFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.SyncReport", Guid.NewGuid().ToString("N"));
        public CatalogSyncAttemptStore Store { get; }
        public ReportFixture() => Store = new(Root, AccountId, ChatId);
        public CatalogSyncAttempt Seed(string[] ids) => new(1, AccountId, ChatId, false, true, CatalogSyncOutcome.Completed,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, 1, 1, 1, DateTimeOffset.UtcNow, ids);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private static FileManifest Manifest()
    {
        var hash = Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 }));
        return new FileManifest(1, "file", "file.bin", 3, hash, 3,
            [new PartRecord(0, 0, 3, hash, $"{ChatId}/20", true)], true, AccountId);
    }

    private static Fixture ManifestFixture(FileManifest manifest, MetadataConflictArchive? archive = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        var caption = $"TSC-MANIFEST|1|{manifest.FileId}|{Convert.ToHexString(SHA256.HashData(bytes))}";
        var call = 0;
        var fixture = new Fixture((_, _) => Task.FromResult(++call == 1 ? Page((30, caption)) : Page()), archive);
        fixture.Transport.Bytes[$"{ChatId}/30"] = bytes;
        return fixture;
    }

    private static JsonObject Page(params (long Id, string? Caption)[] items)
    {
        var messages = new JsonArray();
        foreach (var (id, caption) in items)
            messages.Add(new JsonObject
            {
                ["id"] = id, ["chat_id"] = ChatId,
                ["content"] = caption is null ? new JsonObject { ["@type"] = "messageText" } :
                    new JsonObject { ["@type"] = "messageDocument", ["caption"] = new JsonObject { ["text"] = caption } }
            });
        return new JsonObject { ["@type"] = "messages", ["messages"] = messages };
    }

    private sealed class Fixture
    {
        public RemoteSyncCheckpoint Previous { get; } = new(5, DateTimeOffset.Parse("2026-10-01T00:00:00Z"));
        public MemoryCheckpoints Checkpoints { get; }
        public MemoryManifests Manifests { get; } = new();
        public MemoryTransport Transport { get; } = new();
        public TelegramRemoteManifestCatalog Catalog { get; }
        public Fixture(Func<JsonObject, CancellationToken, Task<JsonObject>> execute, MetadataConflictArchive? archive = null)
        {
            Checkpoints = new MemoryCheckpoints { Value = Previous };
            Catalog = new TelegramRemoteManifestCatalog(new Requests(execute), Transport, Manifests, ChatId, AccountId, Checkpoints, conflictArchive: archive);
        }
    }

    private sealed class Requests(Func<JsonObject, CancellationToken, Task<JsonObject>> execute) : ITelegramRequestClient
    {
        public Task<JsonObject> ExecuteAsync(JsonObject request, CancellationToken cancellationToken) => execute(request, cancellationToken);
    }

    private sealed class MemoryCheckpoints : IRemoteSyncCheckpointStore
    {
        public RemoteSyncCheckpoint? Value { get; set; }
        public Task<RemoteSyncCheckpoint?> LoadAsync(string accountId, long chatId, CancellationToken token) => Task.FromResult(Value);
        public Task SaveAsync(string accountId, long chatId, RemoteSyncCheckpoint value, CancellationToken token)
        { Value = value; return Task.CompletedTask; }
    }

    private sealed class MemoryManifests : IManifestStore
    {
        public bool FailSave { get; set; }
        public Dictionary<string, FileManifest> Values { get; } = new();
        public Task SaveAsync(FileManifest manifest, CancellationToken token) { if (FailSave) throw new IOException("index unavailable"); Values[manifest.FileId] = manifest; return Task.CompletedTask; }
        public Task<FileManifest?> LoadAsync(string id, CancellationToken token) => Task.FromResult(Values.GetValueOrDefault(id));
        public Task<IReadOnlyList<FileManifest>> ListAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<FileManifest>>(Values.Values.ToArray());
        public Task DeleteManyAsync(IEnumerable<string> ids, CancellationToken token) { foreach (var id in ids) Values.Remove(id); return Task.CompletedTask; }
    }

    private sealed class MemoryTransport : IPartTransport
    {
        public Dictionary<string, byte[]> Bytes { get; } = new();
        public List<string> Downloads { get; } = [];
        public Task<string> UploadPartAsync(string path, string fileId, int index, CancellationToken token) => throw new NotSupportedException();
        public Task<Stream> DownloadPartAsync(string remoteId, CancellationToken token)
        { Downloads.Add(remoteId); return Task.FromResult<Stream>(new MemoryStream(Bytes[remoteId], writable: false)); }
    }
}
