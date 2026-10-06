using System.Text.Json;
using Microsoft.Data.Sqlite;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class UploadCommitRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.UploadCommitTests", Guid.NewGuid().ToString("N"));
    private SqliteManifestStore Store => new(Path.Combine(root, "manifest.db"));

    private async Task<FileManifest> StageAsync()
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.bin");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
        return await Pipeline(Store, new Transport(), new Publisher()).StageForUploadAsync(source, Path.Combine(root, "stage"), 2, default, forceChunking: true);
    }

    private static UploadPipeline Pipeline(IManifestStore store, Transport transport, Publisher publisher) =>
        new(new FileTransferCoordinator(), new Capability(), transport, store, publisher);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartAfterRemoteCommitAndLocalSaveFailureReplaysIdenticalMetadataWithoutReupload(bool legacyConfirmedDraft)
    {
        var staged = await StageAsync();
        if (legacyConfirmedDraft)
        {
            staged = staged with { UpdatedAtUtc = null, Parts = staged.Parts.Select(part => part with { Confirmed = true, RemoteId = "-100/" + (part.Index + 1) }).ToArray() };
            await Store.SaveAsync(staged, default);
        }
        var publisher = new Publisher();
        var transport = new Transport();
        await Assert.ThrowsAsync<IOException>(() => Pipeline(new FailCommittedSave(Store), transport, publisher).ResumeAsync(staged.FileId, default));
        Assert.Equal(legacyConfirmedDraft ? 0 : 2, transport.Uploads);
        Assert.False((await Store.LoadAsync(staged.FileId, default))!.Committed);
        Assert.All((await Store.LoadAsync(staged.FileId, default))!.Parts, part => Assert.True(part.Confirmed));
        var first = JsonSerializer.Serialize(Assert.Single(publisher.Published));
        Assert.NotNull((await Store.LoadAsync(staged.FileId, default))!.UpdatedAtUtc);
        var completed = await Pipeline(Store, transport, publisher).ResumeAsync(staged.FileId, default);
        Assert.True(completed.Committed);
        Assert.Equal(first, JsonSerializer.Serialize(publisher.Published[1]));
        Assert.Equal(legacyConfirmedDraft ? 0 : 2, transport.Uploads);
    }

    [Fact]
    public async Task CorruptDraftIsRejectedBeforeProgressTransportOrPublication()
    {
        var staged = await StageAsync();
        // Simulate corruption on disk, bypassing the store's existing write validation.
        await using (var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "manifest.db")))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Manifests SET Json = $json WHERE FileId = $id";
            command.Parameters.AddWithValue("$id", staged.FileId);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(staged with { TotalSha256 = "bad-hash" }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            await command.ExecuteNonQueryAsync();
        }
        var transport = new Transport();
        var publisher = new Publisher();
        var callbacks = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => Pipeline(Store, transport, publisher).ResumeAsync(staged.FileId, default,
            _ => { callbacks++; return Task.CompletedTask; }));
        Assert.Equal(0, callbacks);
        Assert.Equal(0, transport.Uploads);
        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task CancellationDuringRemoteCommitPersistsAcceptedCommitBeforeReturning()
    {
        var staged = await StageAsync();
        using var cancellation = new CancellationTokenSource();
        var publisher = new Publisher { Accepted = () => cancellation.Cancel() };
        var completed = await Pipeline(Store, new Transport(), publisher).ResumeAsync(staged.FileId, cancellation.Token);
        Assert.True(completed.Committed);
        Assert.True((await Store.LoadAsync(staged.FileId, default))!.Committed);
    }

    [Fact]
    public async Task DeclaredPartLengthMustMatchActualStagingFileBeforeSending()
    {
        var staged = await StageAsync();
        var changed = staged with { LogicalSize = 3, Parts =
            [staged.Parts[0] with { Length = 1 }, staged.Parts[1] with { Offset = 1 }] };
        await Store.SaveAsync(changed, default);
        var transport = new Transport();
        var publisher = new Publisher();
        await Assert.ThrowsAsync<InvalidDataException>(() => Pipeline(Store, transport, publisher).ResumeAsync(staged.FileId, default));
        Assert.Equal(0, transport.Uploads);
        Assert.Empty(publisher.Published);
        Assert.False((await Store.LoadAsync(staged.FileId, default))!.Committed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialCatalogSaveFailureCleansPreparationOnlyWhenReadBackProvesItWasNotCommitted(bool encrypted)
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.bin");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
        var staging = Path.Combine(root, "stage-save-failure");
        var store = new FailInitialSave(Store, persistBeforeThrow: false);
        var pipeline = Pipeline(store, new Transport(), new Publisher());

        await Assert.ThrowsAsync<IOException>(() => pipeline.StageForUploadAsync(source, staging, 2, default,
            forceChunking: true, recoveryPassphrase: encrypted ? "recovery phrase" : null));

        Assert.Empty(Directory.Exists(staging) ? Directory.GetFiles(staging, "*", SearchOption.AllDirectories) : []);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbiguousInitialCatalogSaveRetainsPreparationWhenReadBackFindsManifest(bool encrypted)
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.bin");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
        var staging = Path.Combine(root, "stage-ambiguous-save");
        var store = new FailInitialSave(Store, persistBeforeThrow: true);
        var pipeline = Pipeline(store, new Transport(), new Publisher());

        await Assert.ThrowsAsync<IOException>(() => pipeline.StageForUploadAsync(source, staging, 2, default,
            forceChunking: true, recoveryPassphrase: encrypted ? "recovery phrase" : null));

        var saved = await Store.LoadAsync(Assert.Single(await Store.ListAsync(default)).FileId, default);
        Assert.NotNull(saved);
        Assert.All(saved.Parts, part => Assert.True(File.Exists(part.StagingPath)));
        if (encrypted) Assert.True(File.Exists(saved.Encryption!.StagingPath));
    }

    [Fact]
    public async Task ResumeMaterializesProtectedPartOnlyForTelegramAndCleansItAfterSend()
    {
        Directory.CreateDirectory(root);
        using var lease = LocalProfileLease.TryAcquire(root)!;
        const string key = "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=";
        const string protectionId = "5f4f2ae5d8b54a9eb145f230fa4b3df8";
        var bytes = new byte[] { 2, 4 };
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var fileId = Guid.NewGuid().ToString("N");
        var stagingRoot = Path.Combine(root, "stage");
        var job = Path.Combine(stagingRoot, fileId);
        Directory.CreateDirectory(job);
        var stagingPath = Path.Combine(job, "part-00000000.bin");
        using (var cipher = new LocalStagingCipher(key, protectionId, StagingFileIdentity.Part(fileId, 0)))
        await using (var source = new MemoryStream(bytes, writable: false))
        await using (var encrypted = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await cipher.EncryptAsync(source, encrypted, bytes.Length);

        var manifest = new FileManifest(1, fileId, "protected.bin", bytes.Length, hash, bytes.Length,
            [new PartRecord(0, 0, bytes.Length, hash, null, false, stagingPath)], false, "account-a");
        var store = Store;
        await store.SaveAsync(manifest, default);
        var transport = new Transport();
        var content = new LocalStagingContentStore(root, lease,
            identity => new LocalStagingCipher(key, protectionId, identity), allowLegacyPlaintext: false);
        var pipeline = new UploadPipeline(new FileTransferCoordinator(), new Capability(), transport, store, new Publisher(),
            stagingContentStore: content);

        var completed = await pipeline.ResumeAsync(fileId, default);

        Assert.True(completed.Committed);
        Assert.Equal([bytes], transport.UploadedContents);
        var uploadedPath = Assert.Single(transport.UploadedPaths);
        Assert.Contains(Path.Combine("transient", "staging"), uploadedPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(uploadedPath));
        Assert.True(File.Exists(stagingPath));
        Assert.True(LocalStagingCipher.HasProtectedHeader(await ReadPrefixAsync(stagingPath)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationProtectsPartsAndEncryptedPayloadBeforeManifestCommit(bool encrypted)
    {
        Directory.CreateDirectory(root);
        using var lease = LocalProfileLease.TryAcquire(root)!;
        const string key = "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=";
        const string protectionId = "5f4f2ae5d8b54a9eb145f230fa4b3df8";
        var contentStore = new LocalStagingContentStore(root, lease,
            identity => new LocalStagingCipher(key, protectionId, identity), allowLegacyPlaintext: false);
        var sourceBytes = new byte[] { 1, 4, 9, 16, 25, 36 };
        var source = Path.Combine(root, "prepare-source.bin");
        await File.WriteAllBytesAsync(source, sourceBytes);
        var store = Store;
        var pipeline = new UploadPipeline(new FileTransferCoordinator(), new Capability(), new Transport(), store,
            new Publisher(), stagingContentStore: contentStore);

        var manifest = await pipeline.StageForUploadAsync(source, Path.Combine(root, "protected-stage"), 3, default,
            forceChunking: true, recoveryPassphrase: encrypted ? "recovery phrase" : null);

        var reopened = await store.LoadAsync(manifest.FileId, default);
        Assert.NotNull(reopened);
        Assert.Equal(manifest.FileId, reopened.FileId);
        Assert.Equal(manifest.Parts, reopened.Parts);
        foreach (var part in manifest.Parts)
        {
            Assert.True(LocalStagingCipher.HasProtectedHeader(await ReadPrefixAsync(part.StagingPath!)));
            await using var materialized = await contentStore.MaterializeAsync(part.StagingPath!, StagingFileIdentity.Part(manifest.FileId, part.Index));
            Assert.Equal(part.Length, materialized.Stream.Length);
        }
        if (manifest.Encryption?.StagingPath is { } payloadPath)
        {
            Assert.True(LocalStagingCipher.HasProtectedHeader(await ReadPrefixAsync(payloadPath)));
            await using var payload = await contentStore.MaterializeAsync(payloadPath, StagingFileIdentity.EncryptedPayload(manifest.FileId));
            Assert.Equal(manifest.Encryption.PayloadSize, payload.Stream.Length);
        }
    }

    [Fact]
    public async Task VerifiedStagingBytesRemainReadLockedUntilPartIsAcceptedAndCheckpointed()
    {
        var staged = await StageAsync();
        var protectedSends = 0;
        var transport = new Transport { OnSend = path =>
        {
            Assert.Throws<IOException>(() => { using var write = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
            protectedSends++;
        }};
        Assert.True((await Pipeline(Store, transport, new Publisher()).ResumeAsync(staged.FileId, default)).Committed);
        Assert.Equal(2, protectedSends);
        using var released = new FileStream(staged.Parts[0].StagingPath!, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
    }

    private sealed class Capability : IUploadCapabilityProvider
    {
        public Task RefreshAsync(CancellationToken token) => Task.CompletedTask;
        public Task<UploadCapability?> GetCurrentAsync(CancellationToken token) => Task.FromResult<UploadCapability?>(new("account-a", 2, DateTimeOffset.UtcNow, "fixture"));
    }

    private sealed class Transport : IPartTransport
    {
        public int Uploads { get; private set; }
        public Action<string>? OnSend { get; init; }
        public List<string> UploadedPaths { get; } = [];
        public List<byte[]> UploadedContents { get; } = [];
        public Task<string> UploadPartAsync(string path, string fileId, int index, CancellationToken token)
        { OnSend?.Invoke(path); UploadedPaths.Add(path); UploadedContents.Add(File.ReadAllBytes(path)); Uploads++; return Task.FromResult("-100/" + (index + 1)); }
        public Task<Stream> DownloadPartAsync(string remoteId, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class Publisher : IRemoteManifestPublisher
    {
        public Action? Accepted { get; init; }
        public List<FileManifest> Published { get; } = [];
        public Task PublishCommittedAsync(FileManifest manifest, CancellationToken token) { Published.Add(manifest); Accepted?.Invoke(); return Task.CompletedTask; }
    }

    private sealed class FailCommittedSave(IManifestStore store) : IManifestStore
    {
        public Task SaveAsync(FileManifest manifest, CancellationToken token) => manifest.Committed ? throw new IOException("Injected commit save failure") : store.SaveAsync(manifest, token);
        public Task<FileManifest?> LoadAsync(string fileId, CancellationToken token) => store.LoadAsync(fileId, token);
        public Task<IReadOnlyList<FileManifest>> ListAsync(CancellationToken token) => store.ListAsync(token);
        public Task DeleteManyAsync(IEnumerable<string> ids, CancellationToken token) => store.DeleteManyAsync(ids, token);
    }

    private sealed class FailInitialSave(IManifestStore store, bool persistBeforeThrow) : IManifestStore
    {
        private bool failed;
        public async Task SaveAsync(FileManifest manifest, CancellationToken token)
        {
            if (!failed)
            {
                failed = true;
                if (persistBeforeThrow) await store.SaveAsync(manifest, token);
                throw new IOException("Injected initial manifest save failure");
            }
            await store.SaveAsync(manifest, token);
        }
        public Task<FileManifest?> LoadAsync(string fileId, CancellationToken token) => store.LoadAsync(fileId, token);
        public Task<IReadOnlyList<FileManifest>> ListAsync(CancellationToken token) => store.ListAsync(token);
        public Task DeleteManyAsync(IEnumerable<string> ids, CancellationToken token) => store.DeleteManyAsync(ids, token);
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }

    private static async Task<byte[]> ReadPrefixAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var prefix = new byte[8];
        await stream.ReadExactlyAsync(prefix);
        return prefix;
    }
}
