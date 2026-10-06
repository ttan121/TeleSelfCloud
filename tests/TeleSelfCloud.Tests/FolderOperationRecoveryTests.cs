using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class FolderOperationRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.FolderRecovery", Guid.NewGuid().ToString("N"));
    private const string Account = "account-a";
    private string Database => Path.Combine(root, "catalog.db");
    private SqliteManifestStore Manifests => new(Database);
    private SqliteLocalFolderStore Folders => new(Database);
    private FolderOperationJournal Journal => new(Path.Combine(root, "journal"));
    private readonly List<FileManifest> published = [];

    private async Task SeedAsync()
    {
        Directory.CreateDirectory(root);
        await Folders.CreateAsync(Account, "Old/Nested", default);
        var hash = Convert.ToHexString(SHA256.HashData(new byte[] { 1 }));
        foreach (var id in new[] { "a", "b" })
            await Manifests.SaveAsync(new FileManifest(1, id, id + ".bin", 1, hash, 1,
                [new PartRecord(0, 0, 1, hash, "-100/123", true)], true,
                AccountId: Account, FolderPath: "Old/Nested"), default);
    }

    private FolderManagementService Service(Func<FileManifest, CancellationToken, Task>? send = null,
        Func<CancellationToken, Task>? snapshot = null, IManifestStore? store = null) => new(
        Folders, store ?? Manifests, new SqliteTransferQueueStore(Database),
        send ?? ((file, _) => { published.Add(file); return Task.CompletedTask; }),
        snapshot ?? (_ => Task.CompletedTask), Journal);

    private async Task FailSecondAsync(bool delete = false)
    {
        var service = Service((file, _) =>
        {
            if (file.FileId == "b") throw new IOException("Injected send failure");
            published.Add(file);
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<FolderOperationException>(() => delete
            ? service.DeleteAsync(Account, "Old", default)
            : service.RenameAsync(Account, "Old", "New", default));
        Assert.Equal(1, (await Journal.LoadAsync(Account, default))!.AppliedFiles);
    }

    [Fact]
    public async Task RestartAfterPartialRenameFinishesWithoutCollidingWithItsOwnFiles()
    {
        await SeedAsync();
        var original = await Manifests.LoadAsync("a", default);
        await FailSecondAsync();
        var result = await Service().RenameAsync(Account, "Old", "New", default);
        Assert.True(result.FolderStatePublished);
        Assert.Equal(new[] { "a", "b" }, published.Select(file => file.FileId));
        foreach (var file in await Manifests.ListAsync(default))
        {
            Assert.Equal("New/Nested", file.FolderPath);
            Assert.Equal(original!.Revision + 1, file.Revision);
            Assert.Equal(original.Parts, file.Parts);
        }
        Assert.Null(await Journal.LoadAsync(Account, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotRetryAndLostFolderAcknowledgmentDoNotRepublishFileRevisions(bool delete)
    {
        await SeedAsync();
        var service = Service(snapshot: _ => throw new IOException("Injected snapshot failure"));
        var result = delete ? await service.DeleteAsync(Account, "Old", default) : await service.RenameAsync(Account, "Old", "New", default);
        Assert.False(result.FolderStatePublished);
        var pending = (await Journal.LoadAsync(Account, default))!;
        Assert.True(pending.FolderApplied);
        // Simulate termination between committing the folder transaction and acknowledging it.
        await Journal.SaveAsync(pending with { FolderApplied = false }, default);
        Assert.True((await Service().ResumePendingAsync(Account, default)).FolderStatePublished);
        Assert.Equal(2, published.Count);
        Assert.All(await Manifests.ListAsync(default), file => Assert.Equal(delete ? "" : "New/Nested", file.FolderPath));
        Assert.Contains(await Folders.ListTombstonesAsync(Account, default), item => item.Path == "Old");
        Assert.Null(await Journal.LoadAsync(Account, default));
    }

    [Fact]
    public async Task CancellationAfterRemoteAcceptanceKeepsLocalFileAndCheckpointForRestart()
    {
        await SeedAsync();
        using var cancellation = new CancellationTokenSource();
        var service = Service((file, _) => { published.Add(file); cancellation.Cancel(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RenameAsync(Account, "Old", "New", cancellation.Token));
        Assert.Equal("New/Nested", (await Manifests.LoadAsync("a", default))!.FolderPath);
        Assert.Equal(1, (await Journal.LoadAsync(Account, default))!.AppliedFiles);
        await Service().ResumePendingAsync(Account, default);
        Assert.Equal(new[] { "a", "b" }, published.Select(file => file.FileId));
    }

    [Fact]
    public async Task AcceptedRemoteRevisionIsReplayedIdenticallyAfterLocalWriteFailure()
    {
        await SeedAsync();
        await Assert.ThrowsAsync<FolderOperationException>(() => Service(store: new FailingSaveStore(Manifests))
            .RenameAsync(Account, "Old", "New", default));
        Assert.Equal(0, (await Journal.LoadAsync(Account, default))!.AppliedFiles);
        await Service().ResumePendingAsync(Account, default);
        Assert.Equal(JsonSerializer.Serialize(published[0]), JsonSerializer.Serialize(published[1]));
        Assert.Equal(new[] { "a", "a", "b" }, published.Select(file => file.FileId));
    }

    [Fact]
    public async Task LocalCommitWithoutAcknowledgmentSkipsAlreadySavedRevision()
    {
        await SeedAsync();
        await FailSecondAsync();
        var pending = (await Journal.LoadAsync(Account, default))!;
        await Journal.SaveAsync(pending with { AppliedFiles = 0 }, default);
        await Service().ResumePendingAsync(Account, default);
        Assert.Equal(new[] { "a", "b" }, published.Select(file => file.FileId));
    }

    [Theory]
    [InlineData("new-source-file")]
    [InlineData("new-destination-file")]
    [InlineData("newer-revision")]
    [InlineData("reverted-checkpoint")]
    public async Task ConcurrentFileChangesKeepPlanAndNeverOverwriteConflictingData(string fault)
    {
        await SeedAsync();
        await FailSecondAsync();
        var pending = (await Journal.LoadAsync(Account, default))!;
        var file = pending.Files[0].Before;
        var conflicting = fault switch
        {
            "new-source-file" => file with { FileId = "new-file" },
            "new-destination-file" => file with { FileId = "new-file", FolderPath = "New" },
            "newer-revision" => FileManifestMetadata.Move(pending.Files[0].After, "Elsewhere"),
            _ => file
        };
        await Manifests.SaveAsync(conflicting, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ResumePendingAsync(Account, default));
        Assert.Single(published);
        Assert.Equal(JsonSerializer.Serialize(conflicting), JsonSerializer.Serialize(await Manifests.LoadAsync(conflicting.FileId, default)));
        Assert.NotNull(await Journal.LoadAsync(Account, default));
    }

    [Fact]
    public async Task ActiveQueueBlocksBeforeCreatingRecoveryPlanOrInferredFolder()
    {
        await SeedAsync();
        await Folders.DeleteAsync(Account, "Old", default);
        await new SqliteTransferQueueStore(Database).EnqueueDownloadAsync("a", "a.bin", Path.Combine(root, "out"), 1, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().RenameAsync(Account, "Old", "New", default));
        Assert.Empty(await Folders.ListAsync(Account, default));
        Assert.Null(await Journal.LoadAsync(Account, default));
        Assert.Empty(published);
    }

    [Fact]
    public async Task OtherOperationAndAccountCannotConsumePendingPlan()
    {
        await SeedAsync();
        await FailSecondAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().DeleteAsync(Account, "Old", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ResumePendingAsync("account-b", default));
        Assert.Null(await new FolderOperationJournal(Path.Combine(root, "other-vault")).LoadAsync(Account, default));
        Assert.NotNull(await Journal.LoadAsync(Account, default));
        Assert.Single(published);
    }

    [Fact]
    public async Task CorruptJournalIsPreservedAndOverlappingWriterIsRejected()
    {
        await SeedAsync();
        await FailSecondAsync();
        using (Journal.AcquireLease(Account))
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ResumePendingAsync(Account, default));
        var path = Directory.GetFiles(Path.Combine(root, "journal"), "*.json").Single();
        await File.WriteAllTextAsync(path, "{broken");
        await Assert.ThrowsAsync<InvalidDataException>(() => Service().ResumePendingAsync(Account, default));
        Assert.Equal("{broken", await File.ReadAllTextAsync(path));
        Assert.Single(published);
    }

    [Theory]
    [InlineData("added-source")]
    [InlineData("removed-source")]
    [InlineData("added-destination")]
    public async Task ChangedEmptyFolderTreeStopsBeforePublishingMoreFiles(string fault)
    {
        await SeedAsync();
        await FailSecondAsync();
        if (fault == "removed-source") await Folders.DeleteAsync(Account, "Old/Nested", default);
        else await Folders.CreateAsync(Account, fault == "added-source" ? "Old/Other" : "New", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ResumePendingAsync(Account, default));
        Assert.Single(published);
        Assert.Equal("Old/Nested", (await Manifests.LoadAsync("b", default))!.FolderPath);
        Assert.NotNull(await Journal.LoadAsync(Account, default));
    }

    [Fact]
    public async Task RecreatedSourceAfterSnapshotFailureIsNotDeletedByRetry()
    {
        await SeedAsync();
        Assert.False((await Service(snapshot: _ => throw new IOException("snapshot failure"))
            .DeleteAsync(Account, "Old", default)).FolderStatePublished);
        await Folders.CreateAsync(Account, "Old/Recreated", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ResumePendingAsync(Account, default));
        Assert.Contains(await Folders.ListAsync(Account, default), folder => folder.Path == "Old/Recreated");
        Assert.Equal(2, published.Count);
        Assert.NotNull(await Journal.LoadAsync(Account, default));
    }

    [Fact]
    public async Task ReviewDistinguishesAppliedOriginalAndNewFilesWithoutPublishing()
    {
        await SeedAsync();
        await FailSecondAsync();
        var extra = (await Manifests.LoadAsync("b", default))! with { FileId = "new", FileName = "new.bin" };
        await Manifests.SaveAsync(extra, default);
        await Manifests.SaveAsync(extra with { FileId = "foreign", AccountId = "other-account" }, default);
        var review = await Service().ReviewPendingAsync(Account, default);
        Assert.Equal(1, review.NewFiles);
        Assert.Equal(FolderRecoveryFileState.After, review.Files.Single(file => file.FileId == "a").State);
        Assert.Equal(FolderRecoveryFileState.Before, review.Files.Single(file => file.FileId == "b").State);
        Assert.Single(published);
    }

    [Theory]
    [InlineData("changed", FolderRecoveryFileState.Changed)]
    [InlineData("missing", FolderRecoveryFileState.Missing)]
    [InlineData("foreign", FolderRecoveryFileState.OtherAccount)]
    public async Task ReviewShowsConflictAndHidesForeignDetails(string mutation, FolderRecoveryFileState expected)
    {
        await SeedAsync();
        await FailSecondAsync();
        var file = (await Manifests.LoadAsync("b", default))!;
        if (mutation == "missing") await Manifests.DeleteManyAsync(["b"], default);
        else await Manifests.SaveAsync(file with { FileName = "newer-name.bin", FolderPath = "Private", Revision = 8,
            AccountId = mutation == "foreign" ? "other-account" : Account }, default);
        var row = (await Service().ReviewPendingAsync(Account, default)).Files.Single(item => item.FileId == "b");
        Assert.Equal(expected, row.State);
        if (mutation != "changed") { Assert.Null(row.CurrentFolder); Assert.Null(row.CurrentRevision); Assert.Equal("b.bin", row.FileName); }
        else { Assert.Equal("newer-name.bin", row.FileName); Assert.Equal("Private", row.CurrentFolder); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopPartialOperationKeepsCurrentFilesFoldersAndArchivesPlan(bool delete)
    {
        await SeedAsync();
        await FailSecondAsync(delete);
        var pending = (await Journal.LoadAsync(Account, default))!;
        var before = (await Manifests.ListAsync(default)).Select(ManifestRevisionSelector.PortableFingerprint).ToArray();
        var folders = JsonSerializer.Serialize(await Folders.ListAsync(Account, default));
        var result = await Service().KeepCurrentStateAsync(Account, pending.OperationId, default);
        Assert.True(result.KeptCurrentState);
        Assert.True(result.FolderStatePublished);
        Assert.Null(await Journal.LoadAsync(Account, default));
        Assert.Equal(before, (await Manifests.ListAsync(default)).Select(ManifestRevisionSelector.PortableFingerprint));
        Assert.Equal(folders, JsonSerializer.Serialize(await Folders.ListAsync(Account, default)));
        Assert.Single(published);
        var stopped = (await Journal.LoadStoppedAsync(Account, pending.OperationId, default))!;
        Assert.NotNull(stopped.StopRequestedAtUtc);
        Assert.Equal(pending.Files.Count, stopped.Files.Count);
        Assert.Equal(pending.AppliedFiles, stopped.AppliedFiles);
    }

    [Fact]
    public async Task StopFailedSnapshotRestartNeverContinuesFileMoves()
    {
        await SeedAsync();
        await FailSecondAsync();
        var pending = (await Journal.LoadAsync(Account, default))!;
        Assert.False((await Service(snapshot: _ => throw new IOException("snapshot offline"))
            .KeepCurrentStateAsync(Account, pending.OperationId, default)).FolderStatePublished);
        var requested = (await Journal.LoadAsync(Account, default))!.StopRequestedAtUtc;
        Assert.NotNull(requested);
        var current = FileManifestMetadata.Rename((await Manifests.LoadAsync("b", default))!, "latest.bin");
        await Manifests.SaveAsync(current, default);
        var extra = current with { FileId = "new" };
        await Manifests.SaveAsync(extra, default);
        Assert.True((await Service().ResumePendingAsync(Account, default)).KeptCurrentState);
        Assert.Single(published);
        Assert.Equal(ManifestRevisionSelector.PortableFingerprint(current), ManifestRevisionSelector.PortableFingerprint((await Manifests.LoadAsync("b", default))!));
        Assert.Equal(requested, (await Journal.LoadStoppedAsync(Account, pending.OperationId, default))!.StopRequestedAtUtc);
    }

    [Fact]
    public async Task StopArchiveFailureRetainsDecisionUntilArchiveCanBeWritten()
    {
        await SeedAsync();
        await FailSecondAsync();
        var pending = (await Journal.LoadAsync(Account, default))!;
        var occupied = Path.Combine(root, "journal", "stopped");
        await File.WriteAllTextAsync(occupied, "keep this fixture");
        var result = await Service().KeepCurrentStateAsync(Account, pending.OperationId, default);
        Assert.False(result.FolderStatePublished);
        Assert.NotNull((await Journal.LoadAsync(Account, default))!.StopRequestedAtUtc);
        Assert.Equal("keep this fixture", await File.ReadAllTextAsync(occupied));
        File.Delete(occupied);
        Assert.True((await Service().ResumePendingAsync(Account, default)).FolderStatePublished);
        Assert.NotNull(await Journal.LoadStoppedAsync(Account, pending.OperationId, default));
        Assert.Single(published);
    }

    [Fact]
    public async Task StopPendingDeletionFailureReusesExistingReceiptAfterRestart()
    {
        await SeedAsync();
        await FailSecondAsync();
        var pending = (await Journal.LoadAsync(Account, default))!;
        FileStream? pendingLock = null;
        try
        {
            var result = await Service(snapshot: _ =>
            {
                pendingLock = new FileStream(Directory.GetFiles(Path.Combine(root, "journal"), "*.json").Single(), FileMode.Open, FileAccess.Read, FileShare.Read);
                return Task.CompletedTask;
            }).KeepCurrentStateAsync(Account, pending.OperationId, default);
            Assert.False(result.FolderStatePublished);
            Assert.NotNull(await Journal.LoadStoppedAsync(Account, pending.OperationId, default));
        }
        finally { pendingLock?.Dispose(); }
        Assert.True((await Service().ResumePendingAsync(Account, default)).FolderStatePublished);
        Assert.Null(await Journal.LoadAsync(Account, default));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "journal", "stopped"), "*.json"));
        Assert.Single(published);
    }

    [Fact]
    public async Task StopKeepsRecreatedFolderAfterOriginalDeleteWasApplied()
    {
        await SeedAsync();
        Assert.False((await Service(snapshot: _ => throw new IOException("offline"))
            .DeleteAsync(Account, "Old", default)).FolderStatePublished);
        await Folders.CreateAsync(Account, "Old/Recreated", default);
        var pending = (await Journal.LoadAsync(Account, default))!;
        Assert.True((await Service().KeepCurrentStateAsync(Account, pending.OperationId, default)).FolderStatePublished);
        Assert.Contains(await Folders.ListAsync(Account, default), folder => folder.Path == "Old/Recreated");
        Assert.Equal(2, published.Count);
    }

    [Fact]
    public async Task CancellationAfterStopSnapshotAcceptanceStillPersistsReceipt()
    {
        await SeedAsync();
        await FailSecondAsync();
        var pending = (await Journal.LoadAsync(Account, default))!;
        using var cancellation = new CancellationTokenSource();
        var result = await Service(snapshot: _ => { cancellation.Cancel(); return Task.CompletedTask; })
            .KeepCurrentStateAsync(Account, pending.OperationId, cancellation.Token);
        Assert.True(result.FolderStatePublished);
        Assert.Null(await Journal.LoadAsync(Account, default));
        Assert.NotNull(await Journal.LoadStoppedAsync(Account, pending.OperationId, default));
        Assert.Single(published);
    }

    [Fact]
    public async Task CorruptStoppedReceiptCannotReplacePendingPlanOrBeOverwritten()
    {
        await SeedAsync();
        await FailSecondAsync();
        var pending = (await Journal.LoadAsync(Account, default))!;
        await Service(snapshot: _ => throw new IOException("offline")).KeepCurrentStateAsync(Account, pending.OperationId, default);
        await Journal.ArchiveStoppedAsync((await Journal.LoadAsync(Account, default))!, default);
        var path = Directory.GetFiles(Path.Combine(root, "journal", "stopped"), "*.json").Single();
        await File.WriteAllTextAsync(path, "{corrupt");
        Assert.False((await Service().ResumePendingAsync(Account, default)).FolderStatePublished);
        Assert.Equal("{corrupt", await File.ReadAllTextAsync(path));
        Assert.NotNull(await Journal.LoadAsync(Account, default));
        Assert.Single(published);
    }

    [Fact]
    public async Task StaleReviewCannotStopOrResumeAReplacementPlan()
    {
        await SeedAsync();
        await FailSecondAsync();
        var wrongId = Guid.NewGuid().ToString("N");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().KeepCurrentStateAsync(Account, wrongId, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ResumePendingAsync(Account, default, wrongId));
        Assert.Null((await Journal.LoadAsync(Account, default))!.StopRequestedAtUtc);
        Assert.Single(published);
    }

    [Fact]
    public async Task LegacyJournalWithoutStopFieldStillValidatesAndResumes()
    {
        await SeedAsync();
        await FailSecondAsync();
        var pending = (await Journal.LoadAsync(Account, default))!;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        // Original schema-1 layout, independently declared without the new optional field.
        var legacy = new { pending.SchemaVersion, pending.OperationId, pending.AccountId, pending.Source, pending.Destination,
            pending.Delete, pending.FolderPaths, pending.Files, pending.AppliedFiles, pending.FolderApplied, pending.CreatedAtUtc, pending.SourceInferred };
        var legacyBytes = JsonSerializer.SerializeToUtf8Bytes(legacy, options);
        var hash = Convert.ToHexString(SHA256.HashData(legacyBytes));
        var envelope = JsonSerializer.Serialize(new { sha256 = hash, operation = legacy }, options);
        Assert.DoesNotContain("stopRequestedAtUtc", envelope);
        await File.WriteAllTextAsync(Directory.GetFiles(Path.Combine(root, "journal"), "*.json").Single(), envelope);
        Assert.Null((await Journal.LoadAsync(Account, default))!.StopRequestedAtUtc);
        Assert.True((await Service().ResumePendingAsync(Account, default)).FolderStatePublished);
        Assert.Equal(2, published.Count);
    }

    [Fact]
    public async Task StopAfterLostLocalAcknowledgmentDoesNotRollbackAcceptedRemoteMetadata()
    {
        await SeedAsync();
        await Assert.ThrowsAsync<FolderOperationException>(() => Service(store: new FailingSaveStore(Manifests))
            .RenameAsync(Account, "Old", "New", default));
        var accepted = Assert.Single(published);
        var pending = (await Journal.LoadAsync(Account, default))!;
        Assert.Equal(0, pending.AppliedFiles);
        var before = (await Manifests.LoadAsync(accepted.FileId, default))!;
        Assert.Equal("Old/Nested", before.FolderPath);
        Assert.True((await Service().KeepCurrentStateAsync(Account, pending.OperationId, default)).FolderStatePublished);
        Assert.Single(published);
        Assert.Equal("Old/Nested", (await Manifests.LoadAsync(accepted.FileId, default))!.FolderPath);
        // Later catalog recovery may legitimately find the already-accepted remote revision.
        Assert.Equal("New/Nested", ManifestRevisionSelector.PreferNewest(before, accepted).FolderPath);
        Assert.NotNull(await Journal.LoadStoppedAsync(Account, pending.OperationId, default));
    }

    [Fact]
    public async Task ProtectedJournalEncryptsNewPlansAndKeepsThemReadableAcrossInstances()
    {
        await SeedAsync();
        using var lease = LocalProfileLease.TryAcquire(root)!;
        var protectedJournal = ProtectedJournal(lease);
        var service = new FolderManagementService(Folders, Manifests, new SqliteTransferQueueStore(Database),
            (file, _) => { if (file.FileId == "b") throw new IOException("Injected send failure"); return Task.CompletedTask; },
            _ => Task.CompletedTask, protectedJournal);
        await Assert.ThrowsAsync<FolderOperationException>(() => service.RenameAsync(Account, "Old", "New", default));
        var record = Directory.GetFiles(Path.Combine(root, "journal"), "*.json").Single();
        Assert.Equal("TSCREC01", System.Text.Encoding.ASCII.GetString(await File.ReadAllBytesAsync(record), 0, 8));
        Assert.NotNull(await ProtectedJournal(lease).LoadAsync(Account, default));
        await new FolderManagementService(Folders, Manifests, new SqliteTransferQueueStore(Database),
            (_, _) => Task.CompletedTask, _ => Task.CompletedTask, ProtectedJournal(lease)).ResumePendingAsync(Account, default);
        Assert.Null(await ProtectedJournal(lease).LoadAsync(Account, default));
    }

    [Fact]
    public async Task ProtectedJournalMigratesLegacyPlanWithoutLosingRecoveryState()
    {
        await SeedAsync();
        await FailSecondAsync();
        var legacyPath = Directory.GetFiles(Path.Combine(root, "journal"), "*.json").Single();
        using var lease = LocalProfileLease.TryAcquire(root)!;
        var protectedJournal = ProtectedJournal(lease);
        PendingFolderOperation? loaded;
        using (protectedJournal.AcquireLease(Account)) loaded = await protectedJournal.LoadAsync(Account, default);
        Assert.Equal(1, loaded!.AppliedFiles);
        Assert.Equal("TSCREC01", System.Text.Encoding.ASCII.GetString(await File.ReadAllBytesAsync(legacyPath), 0, 8));
        Assert.True(File.Exists(legacyPath));
        await new FolderManagementService(Folders, Manifests, new SqliteTransferQueueStore(Database),
            (file, _) => { published.Add(file); return Task.CompletedTask; }, _ => Task.CompletedTask, protectedJournal)
            .ResumePendingAsync(Account, default);
        Assert.Null(await protectedJournal.LoadAsync(Account, default));
        Assert.Equal(new[] { "a", "b" }, published.Select(file => file.FileId));
    }

    [Fact]
    public async Task ProtectedStoppedArchiveIsEncryptedAndCanBeReviewedAfterRestart()
    {
        await SeedAsync();
        await FailSecondAsync();
        using var lease = LocalProfileLease.TryAcquire(root)!;
        var journal = ProtectedJournal(lease);
        var pending = (await journal.LoadAsync(Account, default))!;
        pending = pending with { StopRequestedAtUtc = DateTimeOffset.UtcNow };
        await journal.ArchiveStoppedAsync(pending, default);
        var archive = Directory.GetFiles(Path.Combine(root, "journal", "stopped"), "*.json").Single();
        Assert.Equal("TSCREC01", System.Text.Encoding.ASCII.GetString(await File.ReadAllBytesAsync(archive), 0, 8));
        Assert.Equal(pending.OperationId, (await ProtectedJournal(lease).LoadStoppedAsync(Account, pending.OperationId, default))!.OperationId);
    }

    private FolderOperationJournal ProtectedJournal(LocalProfileLease lease) => new(
        Path.Combine(root, "journal"), identity => new LocalRecordCipher(
            Convert.ToBase64String(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray()),
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "folder-journal", identity), lease);

    private sealed class FailingSaveStore(IManifestStore inner) : IManifestStore
    {
        public Task SaveAsync(FileManifest manifest, CancellationToken token) => throw new IOException("Injected local write failure");
        public Task<FileManifest?> LoadAsync(string id, CancellationToken token) => inner.LoadAsync(id, token);
        public Task<IReadOnlyList<FileManifest>> ListAsync(CancellationToken token) => inner.ListAsync(token);
        public Task DeleteManyAsync(IEnumerable<string> ids, CancellationToken token) => inner.DeleteManyAsync(ids, token);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
