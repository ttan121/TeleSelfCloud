using System.Text.Json;
using System.Security.Cryptography;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class MetadataConflictRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.MetadataConflict", Guid.NewGuid().ToString("N"));
    private SqliteManifestStore Store => new(Path.Combine(root, "manifest.db"));
    private SqliteTransferQueueStore Queue => new(Path.Combine(root, "manifest.db"));
    private MetadataConflictArchive Archive => new(Path.Combine(root, "history"), "account", -100500);
    private MetadataConflictArchive ProtectedArchive(LocalProfileLease lease) => new(Path.Combine(root, "history"), "account", -100500,
        identity => new LocalRecordCipher(Convert.ToBase64String(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray()),
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "metadata-history", "account:-100500:" + identity), lease);
    private static FileManifest Original() => new(1, "file", "before.bin", 3, new string('A', 64), 3,
        [new PartRecord(0, 0, 3, new string('A', 64), "-100500/20", true, "D:/local-cache/part")], true, "account",
        Revision: 2, UpdatedAtUtc: DateTimeOffset.Parse("2026-10-05T00:00:00Z"));
    private async Task<FileManifest> PrepareAsync()
    {
        Directory.CreateDirectory(root);
        var current = Original() with { FileName = "current.bin", IsFavorite = true };
        var selected = Original() with { FolderPath = "chosen-folder", IsHidden = true };
        await Store.SaveAsync(current, default);
        await Archive.RecordCompetingAsync(current, selected, default);
        return selected;
    }
    private MetadataConflictResolver Resolver(Func<FileManifest, CancellationToken, Task> publish, IManifestStore? store = null) => new(Archive, store ?? Store, Queue, publish);

    [Fact]
    public async Task ArchiveSurvivesRestartDeduplicatesReplayAndRemovesCachePaths()
    {
        var selected = await PrepareAsync();
        await Archive.RecordCompetingAsync(selected, Original() with { FileName = "current.bin", IsFavorite = true }, default);
        var history = (await Archive.LoadAsync("file", default))!;
        Assert.Equal(2, history.Versions.Count);
        Assert.All(history.Versions.SelectMany(item => item.Parts), part => Assert.Null(part.StagingPath));
        Assert.DoesNotContain("local-cache", await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(root, "history"), "*.json").Single()));
        Assert.Null(await new MetadataConflictArchive(Path.Combine(root, "history"), "other-account", -100500).LoadAsync("file", default));
        Assert.Null(await new MetadataConflictArchive(Path.Combine(root, "history"), "account", -100501).LoadAsync("file", default));
    }

    [Fact]
    public async Task ProtectedMetadataHistoryMigratesLegacyVersionsAndResumesResolution()
    {
        var selected = await PrepareAsync();
        using var lease = LocalProfileLease.TryAcquire(root)!;
        var archive = ProtectedArchive(lease);
        MetadataConflictHistory? history;
        using (archive.AcquireLease("file")) history = await archive.LoadAsync("file", default);
        Assert.Equal(2, history!.Versions.Count);
        var record = Directory.GetFiles(Path.Combine(root, "history"), "*.json").Single();
        var encrypted = await File.ReadAllBytesAsync(record);
        Assert.Equal("TSCREC01", System.Text.Encoding.ASCII.GetString(encrypted, 0, 8));
        Assert.DoesNotContain("before.bin", System.Text.Encoding.UTF8.GetString(encrypted));

        var resolver = new MetadataConflictResolver(archive, Store, Queue, (_, _) => Task.CompletedTask);
        var current = (await Store.LoadAsync("file", default))!;
        current = current with { Revision = 8, Parts = [current.Parts[0] with { RemoteId = "-100500/99" }] };
        await Store.SaveAsync(current, default);
        await resolver.ApplyAsync("file", ManifestRevisionSelector.PortableFingerprint(selected), default);
        Assert.Null((await ProtectedArchive(lease).LoadAsync("file", default))!.Pending);
        Assert.Equal("TSCREC01", System.Text.Encoding.ASCII.GetString(await File.ReadAllBytesAsync(record), 0, 8));

        var wrong = new MetadataConflictArchive(Path.Combine(root, "history"), "account", -100500,
            identity => new LocalRecordCipher(Convert.ToBase64String(Enumerable.Range(0, 32).Select(value => (byte)(value + 1)).ToArray()),
                "cccccccccccccccccccccccccccccccc", "metadata-history", "account:-100500:" + identity), lease);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => wrong.LoadAsync("file", default));
        Assert.Equal("TSCREC01", System.Text.Encoding.ASCII.GetString(await File.ReadAllBytesAsync(record), 0, 8));
    }

    [Fact]
    public async Task OrdinarySequentialEditIsNotMisreportedAsConflict()
    {
        var current = Original();
        await Archive.RecordCompetingAsync(current, FileManifestMetadata.Rename(current, "next.bin"), default);
        Assert.Null(await Archive.LoadAsync("file", default));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("vault")]
    [InlineData("content")]
    public async Task OffScopeOrChangedContentCannotEnterArchive(string fault)
    {
        var current = Original();
        var candidate = current with { FileName = "changed.bin" };
        candidate = fault switch
        {
            "account" => candidate with { AccountId = "foreign" },
            "vault" => candidate with { Parts = [candidate.Parts[0] with { RemoteId = "-100501/20" }] },
            _ => candidate with { TotalSha256 = new string('B', 64) }
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => Archive.RecordCompetingAsync(current, candidate, default));
        Assert.Null(await Archive.LoadAsync("file", default));
    }

    [Fact]
    public async Task ResolutionPublishesHigherRevisionWithCurrentPartsAndCache()
    {
        var selected = await PrepareAsync();
        var current = (await Store.LoadAsync("file", default))!;
        current = current with { Revision = 8, Parts = [current.Parts[0] with { RemoteId = "-100500/99" }] };
        await Store.SaveAsync(current, default);
        FileManifest? published = null;
        var result = await Resolver((manifest, _) => { published = manifest; return Task.CompletedTask; })
            .ApplyAsync("file", ManifestRevisionSelector.PortableFingerprint(selected), default);
        Assert.Equal(9, result.Revision);
        Assert.Equal(selected.FileName, result.FileName);
        Assert.Equal(selected.FolderPath, result.FolderPath);
        Assert.True(result.IsHidden);
        Assert.False(result.IsFavorite);
        Assert.Equal(current.Parts[0], result.Parts[0]);
        Assert.Equal("-100500/99", published!.Parts[0].RemoteId);
        Assert.Null(published.Parts[0].StagingPath);
        Assert.Null((await Archive.LoadAsync("file", default))!.Pending);
        Assert.Equal(2, (await Archive.LoadAsync("file", default))!.Versions.Count);
    }

    [Fact]
    public async Task LocalSaveFailureAfterRemoteAcceptanceReplaysExactPreparedRevision()
    {
        var selected = await PrepareAsync();
        var published = new List<string>();
        Task Publish(FileManifest manifest, CancellationToken _) { published.Add(JsonSerializer.Serialize(manifest)); return Task.CompletedTask; }
        await Assert.ThrowsAsync<IOException>(() => Resolver(Publish, new SaveInterceptor(Store, _ => throw new IOException("local checkpoint unavailable")))
            .ApplyAsync("file", ManifestRevisionSelector.PortableFingerprint(selected), default));
        Assert.NotNull((await Archive.LoadAsync("file", default))!.Pending);
        var resumed = await Resolver(Publish).ApplyAsync("file", ManifestRevisionSelector.PortableFingerprint(selected), default);
        Assert.Equal(2, published.Count);
        Assert.Equal(published[0], published[1]);
        Assert.Equal(3, resumed.Revision);
        Assert.Null((await Archive.LoadAsync("file", default))!.Pending);
    }

    [Fact]
    public async Task AcceptedCancellationStillPersistsLocalStateAndCompletesPlan()
    {
        var selected = await PrepareAsync();
        using var cancellation = new CancellationTokenSource();
        var result = await Resolver((_, _) => { cancellation.Cancel(); return Task.CompletedTask; })
            .ApplyAsync("file", ManifestRevisionSelector.PortableFingerprint(selected), cancellation.Token);
        Assert.Equal(ManifestRevisionSelector.PortableFingerprint(result), ManifestRevisionSelector.PortableFingerprint((await Store.LoadAsync("file", default))!));
        Assert.Null((await Archive.LoadAsync("file", default))!.Pending);
    }

    [Fact]
    public async Task FailedRemotePublishKeepsPreparedPlanAndLocalMetadata()
    {
        var selected = await PrepareAsync();
        var before = (await Store.LoadAsync("file", default))!;
        await Assert.ThrowsAsync<IOException>(() => Resolver((_, _) => throw new IOException("network disconnected"))
            .ApplyAsync("file", ManifestRevisionSelector.PortableFingerprint(selected), default));
        Assert.Equal(ManifestRevisionSelector.PortableFingerprint(before), ManifestRevisionSelector.PortableFingerprint((await Store.LoadAsync("file", default))!));
        Assert.NotNull((await Archive.LoadAsync("file", default))!.Pending);
    }

    [Fact]
    public async Task NewMetadataDuringInterruptedResolutionCannotBeOverwritten()
    {
        var selected = await PrepareAsync();
        await Assert.ThrowsAsync<IOException>(() => Resolver((_, _) => throw new IOException("network"))
            .ApplyAsync("file", ManifestRevisionSelector.PortableFingerprint(selected), default));
        var changed = FileManifestMetadata.Rename((await Store.LoadAsync("file", default))!, "other-device.bin");
        await Store.SaveAsync(changed, default);
        var sends = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Resolver((_, _) => { sends++; return Task.CompletedTask; })
            .ApplyAsync("file", ManifestRevisionSelector.PortableFingerprint(selected), default));
        Assert.Equal(0, sends);
        Assert.Equal(changed.FileName, (await Store.LoadAsync("file", default))!.FileName);
        Assert.NotNull((await Archive.LoadAsync("file", default))!.Pending);
    }

    [Fact]
    public async Task PendingChoiceCannotBeReplacedSilently()
    {
        var selected = await PrepareAsync();
        await Assert.ThrowsAsync<IOException>(() => Resolver((_, _) => throw new IOException("network"))
            .ApplyAsync("file", ManifestRevisionSelector.PortableFingerprint(selected), default));
        var alternative = (await Archive.LoadAsync("file", default))!.Versions.Single(item => item.FileName == "current.bin");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Resolver((_, _) => Task.CompletedTask)
            .ApplyAsync("file", ManifestRevisionSelector.PortableFingerprint(alternative), default));
        Assert.Equal(ManifestRevisionSelector.PortableFingerprint(selected), (await Archive.LoadAsync("file", default))!.Pending!.SelectedFingerprint);
    }

    [Theory]
    [InlineData(TransferQueueState.Pending)]
    [InlineData(TransferQueueState.Running)]
    [InlineData(TransferQueueState.Paused)]
    public async Task UnfinishedTransferBlocksPreparationAndPublish(TransferQueueState state)
    {
        var selected = await PrepareAsync();
        await Queue.EnsureAsync("file", "current.bin", 3, default);
        var task = Assert.Single(await Queue.ListAsync(default));
        if (state != TransferQueueState.Pending)
            await Queue.SetStateAsync(task.TaskId, TransferQueueState.Running, null, default);
        if (state == TransferQueueState.Paused)
            await Queue.SetStateAsync(task.TaskId, TransferQueueState.Paused, null, default);
        var sends = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Resolver((_, _) => { sends++; return Task.CompletedTask; })
            .ApplyAsync("file", ManifestRevisionSelector.PortableFingerprint(selected), default));
        Assert.Equal(0, sends);
        Assert.Null((await Archive.LoadAsync("file", default))!.Pending);
    }

    [Fact]
    public async Task CorruptArchiveIsRetainedAndBlocksPublish()
    {
        var selected = await PrepareAsync();
        var path = Directory.GetFiles(Path.Combine(root, "history"), "*.json").Single();
        await File.WriteAllTextAsync(path, "{broken");
        var sends = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => Resolver((_, _) => { sends++; return Task.CompletedTask; })
            .ApplyAsync("file", ManifestRevisionSelector.PortableFingerprint(selected), default));
        Assert.Equal(0, sends);
        Assert.Equal("{broken", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ArchiveCannotBeReplacedWhileItsOperationLeaseIsOwned()
    {
        var selected = await PrepareAsync();
        using (Archive.AcquireLease("file"))
            await Assert.ThrowsAsync<InvalidOperationException>(() => Archive.RecordCompetingAsync(Original() with { FileName = "third.bin" }, selected, default));
        await Archive.RecordCompetingAsync(Original() with { FileName = "third.bin" }, selected, default);
        Assert.Equal(3, (await Archive.LoadAsync("file", default))!.Versions.Count);
    }

    [Fact]
    public async Task ExplicitNewRevisionKeepsSupersededPlanAndUsesLatestParts()
    {
        var selected = await PrepareAsync();
        await Assert.ThrowsAsync<IOException>(() => Resolver((_, _) => throw new IOException("network"))
            .ApplyAsync("file", ManifestRevisionSelector.PortableFingerprint(selected), default));
        var oldPlan = (await Archive.LoadAsync("file", default))!.Pending!;
        var current = FileManifestMetadata.Rename((await Store.LoadAsync("file", default))!, "newer.bin");
        current = current with { Parts = [current.Parts[0] with { RemoteId = "-100500/99" }] };
        await Store.SaveAsync(current, default);
        var chosen = (await Archive.LoadAsync("file", default))!.Versions.Single(version => version.FileName == "current.bin");
        string? prepared = null;
        await Assert.ThrowsAsync<IOException>(() => Resolver((manifest, _) => { prepared = JsonSerializer.Serialize(manifest); throw new IOException("new revision interrupted"); })
            .ApplyAsync("file", ManifestRevisionSelector.PortableFingerprint(chosen), default, prepareNewRevision: true));
        Assert.Single((await Archive.LoadAsync("file", default))!.SupersededPlans!);
        var result = await Resolver((manifest, _) => { Assert.Equal(prepared, JsonSerializer.Serialize(manifest)); return Task.CompletedTask; })
            .ApplyAsync("file", ManifestRevisionSelector.PortableFingerprint(chosen), default);
        Assert.True(result.Revision > Math.Max(oldPlan.After.Revision, current.Revision));
        Assert.Equal(chosen.FileName, result.FileName);
        Assert.Equal(current.Parts[0], result.Parts[0]);
        var history = (await Archive.LoadAsync("file", default))!;
        Assert.Null(history.Pending);
        var savedPlan = Assert.Single(history.SupersededPlans!);
        Assert.Equal(ManifestRevisionSelector.PortableFingerprint(oldPlan.After), ManifestRevisionSelector.PortableFingerprint(savedPlan.After));
    }

    [Fact]
    public async Task AcceptedLocalCommitWithArchiveCleanupFailureDoesNotRepublishOnRetry()
    {
        var selected = await PrepareAsync();
        FileStream? archiveLock = null;
        var sends = 0;
        try
        {
            var store = new SaveInterceptor(Store, _ => archiveLock = new FileStream(
                Directory.GetFiles(Path.Combine(root, "history"), "*.json").Single(), FileMode.Open, FileAccess.Read, FileShare.Read));
            var failure = await Record.ExceptionAsync(() => Resolver((_, _) => { sends++; return Task.CompletedTask; }, store)
                .ApplyAsync("file", ManifestRevisionSelector.PortableFingerprint(selected), default));
            Assert.True(failure is IOException or UnauthorizedAccessException, "A locked archive must reject checkpoint replacement.");
        }
        finally { archiveLock?.Dispose(); }
        Assert.NotNull((await Archive.LoadAsync("file", default))!.Pending);
        await Resolver((_, _) => { sends++; return Task.CompletedTask; }).ApplyAsync("file", ManifestRevisionSelector.PortableFingerprint(selected), default);
        Assert.Equal(1, sends);
        Assert.Null((await Archive.LoadAsync("file", default))!.Pending);
    }

    [Fact]
    public async Task ChoosingOldPlainSnapshotKeepsCurrentEncryptionAndCiphertextCache()
    {
        var selected = await PrepareAsync();
        var key = new PassphraseKeyEnvelope(1, "PBKDF2-SHA256", 600000, "AES-256-GCM", Convert.ToBase64String(new byte[16]),
            Convert.ToBase64String(new byte[12]), Convert.ToBase64String(new byte[32]), Convert.ToBase64String(new byte[16]));
        var current = (await Store.LoadAsync("file", default))! with
        {
            Revision = 4,
            Encryption = new(1, 3, new string('B', 64), key, "D:/local-cache/ciphertext"),
            Parts = [new(0, 0, 3, new string('B', 64), "-100500/99", true, "D:/local-cache/cipherpart")]
        };
        await Store.SaveAsync(current, default);
        var result = await Resolver((_, _) => Task.CompletedTask).ApplyAsync("file", ManifestRevisionSelector.PortableFingerprint(selected), default);
        Assert.Equal(current.Encryption, result.Encryption);
        Assert.Equal(current.Parts[0], result.Parts[0]);
        Assert.Equal(selected.FileName, result.FileName);
    }

    private sealed class SaveInterceptor(IManifestStore inner, Action<FileManifest> beforeSave) : IManifestStore
    {
        public Task SaveAsync(FileManifest manifest, CancellationToken token) { beforeSave(manifest); return inner.SaveAsync(manifest, token); }
        public Task<FileManifest?> LoadAsync(string id, CancellationToken token) => inner.LoadAsync(id, token);
        public Task<IReadOnlyList<FileManifest>> ListAsync(CancellationToken token) => inner.ListAsync(token);
        public Task DeleteManyAsync(IEnumerable<string> ids, CancellationToken token) => inner.DeleteManyAsync(ids, token);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
