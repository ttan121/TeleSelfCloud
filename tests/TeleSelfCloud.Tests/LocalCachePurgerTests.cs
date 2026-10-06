using System.Security.Cryptography;
using System.Diagnostics;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class LocalCachePurgerTests
{
    [Fact]
    public async Task PurgeVerifiesRemotePartThenClearsOnlyLocalCacheReferences()
    {
        var root = NewDirectory();
        try
        {
            var bytes = Enumerable.Range(0, 257).Select(value => (byte)value).ToArray();
            var fixture = await CreateFixtureAsync(root, bytes);
            await fixture.CacheStore.VerifyAndSaveAsync(fixture.Manifest, CancellationToken.None);

            await fixture.Purger.PurgeAsync(fixture.Manifest.FileId, "acct-a", new HashSet<string> { fixture.Manifest.FileId }, [fixture.StagingRoot], CancellationToken.None);

            Assert.False(File.Exists(fixture.PartPath));
            Assert.Null((await fixture.Manifests.LoadAsync(fixture.Manifest.FileId, CancellationToken.None))!.Parts.Single().StagingPath);
            Assert.Empty(await fixture.CacheStore.LoadAllAsync(CancellationToken.None));
            Assert.Equal(1, fixture.Transport.DownloadCount);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PurgeKeepsLocalCacheWhenRemotePartDoesNotMatchManifest()
    {
        var root = NewDirectory();
        try
        {
            var fixture = await CreateFixtureAsync(root, [1, 2, 3, 4]);
            fixture.Transport.Parts["-100/44"] = [1, 2, 3, 5];

            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Purger.PurgeAsync(
                fixture.Manifest.FileId, "acct-a", new HashSet<string> { fixture.Manifest.FileId }, [fixture.StagingRoot], CancellationToken.None));

            Assert.True(File.Exists(fixture.PartPath));
            Assert.Equal(fixture.PartPath, (await fixture.Manifests.LoadAsync(fixture.Manifest.FileId, CancellationToken.None))!.Parts.Single().StagingPath);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PurgeRequiresObservedRemoteManifestAndApprovedStagingPath()
    {
        var root = NewDirectory();
        try
        {
            var stagingRoot = Path.Combine(root, "approved");
            var outsideRoot = Path.Combine(root, "outside");
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(outsideRoot);
            var localPath = Path.Combine(outsideRoot, "part.bin");
            var bytes = new byte[] { 9, 8, 7 };
            await File.WriteAllBytesAsync(localPath, bytes);
            var manifest = CreateManifest(localPath, bytes, accountId: "acct-a");
            var manifests = new FakeManifestStore();
            await manifests.SaveAsync(manifest, CancellationToken.None);
            var transport = new FakeTransport();
            var purger = new LocalCachePurger(manifests, new FakeQueueStore(), new LocalCacheVerificationStore(Path.Combine(root, "cache.json")), transport);

            await Assert.ThrowsAsync<InvalidOperationException>(() => purger.PurgeAsync(
                manifest.FileId, "acct-a", new HashSet<string>(), [stagingRoot], CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => purger.PurgeAsync(
                manifest.FileId, "acct-a", new HashSet<string> { manifest.FileId }, [stagingRoot], CancellationToken.None));

            Assert.Equal(0, transport.DownloadCount);
            Assert.True(File.Exists(localPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PurgeRefusesStagingJunctionThatEscapesApprovedRoot()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = NewDirectory();
        string? junction = null;
        try
        {
            var bytes = new byte[] { 4, 5, 6, 7 };
            var fixture = await CreateFixtureAsync(root, bytes);
            var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(outside);
            var outsideFile = Path.Combine(outside, "part.bin");
            await File.WriteAllBytesAsync(outsideFile, bytes);
            File.Delete(fixture.PartPath);
            junction = Path.Combine(fixture.StagingRoot, "linked");
            using var createJunction = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "/c", "mklink", "/J", junction, outside }
            }) ?? throw new InvalidOperationException("Could not start junction fixture creation.");
            await createJunction.WaitForExitAsync();
            Assert.Equal(0, createJunction.ExitCode);

            var linkedFile = Path.Combine(junction, "part.bin");
            var linkedManifest = fixture.Manifest with
            {
                Parts = [fixture.Manifest.Parts.Single() with { StagingPath = linkedFile }]
            };
            await fixture.Manifests.SaveAsync(linkedManifest, CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Purger.PurgeAsync(
                linkedManifest.FileId, "acct-a", new HashSet<string> { linkedManifest.FileId },
                [fixture.StagingRoot], CancellationToken.None));

            Assert.True(File.Exists(outsideFile));
            Assert.Equal(0, fixture.Transport.DownloadCount);
        }
        finally
        {
            if (junction is not null && Directory.Exists(junction)) Directory.Delete(junction, recursive: false);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<Fixture> CreateFixtureAsync(string root, byte[] bytes)
    {
        var stagingRoot = Path.Combine(root, "staging");
        Directory.CreateDirectory(stagingRoot);
        var partPath = Path.Combine(stagingRoot, "part.bin");
        await File.WriteAllBytesAsync(partPath, bytes);
        var manifest = CreateManifest(partPath, bytes, "acct-a");
        var manifests = new FakeManifestStore();
        await manifests.SaveAsync(manifest, CancellationToken.None);
        var transport = new FakeTransport();
        transport.Parts["-100/44"] = bytes;
        var cacheStore = new LocalCacheVerificationStore(Path.Combine(root, "cache.json"));
        return new Fixture(stagingRoot, partPath, manifest, manifests, transport, cacheStore,
            new LocalCachePurger(manifests, new FakeQueueStore(), cacheStore, transport));
    }

    private static FileManifest CreateManifest(string partPath, byte[] bytes, string accountId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        return new FileManifest(1, Guid.NewGuid().ToString("N"), "cache.bin", bytes.LongLength, hash,
            Math.Max(bytes.Length, 1), [new PartRecord(0, 0, bytes.LongLength, hash, "-100/44", true, partPath)],
            true, accountId, UpdatedAtUtc: DateTimeOffset.UtcNow);
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "TeleSelfCloud-LocalCachePurger", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record Fixture(string StagingRoot, string PartPath, FileManifest Manifest,
        FakeManifestStore Manifests, FakeTransport Transport, LocalCacheVerificationStore CacheStore, LocalCachePurger Purger);

    private sealed class FakeManifestStore : IManifestStore
    {
        private readonly Dictionary<string, FileManifest> _manifests = new(StringComparer.Ordinal);
        public Task SaveAsync(FileManifest manifest, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ManifestValidator.ValidateStructure(manifest);
            _manifests[manifest.FileId] = manifest;
            return Task.CompletedTask;
        }
        public Task<FileManifest?> LoadAsync(string fileId, CancellationToken cancellationToken) =>
            Task.FromResult(_manifests.GetValueOrDefault(fileId));
        public Task<IReadOnlyList<FileManifest>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FileManifest>>(_manifests.Values.ToArray());
        public Task DeleteManyAsync(IEnumerable<string> fileIds, CancellationToken cancellationToken)
        {
            foreach (var fileId in fileIds) _manifests.Remove(fileId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeQueueStore : ITransferQueueStore
    {
        public Task EnsureAsync(string fileId, string fileName, long totalBytes, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task EnqueueAsync(string fileId, string fileName, long totalBytes, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<TransferQueueItem> EnqueueDownloadAsync(string fileId, string fileName, string destinationPath, long totalBytes, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetStateAsync(string taskId, TransferQueueState state, string? error, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpdateProgressAsync(string taskId, long transferredBytes, long totalBytes, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RequeueForRetryAsync(string taskId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteTasksAsync(IEnumerable<string> taskIds, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteItemsForFilesAsync(IEnumerable<string> fileIds, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<IReadOnlyList<TransferQueueItem>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TransferQueueItem>>([]);
    }

    private sealed class FakeTransport : IPartTransport
    {
        public Dictionary<string, byte[]> Parts { get; } = new(StringComparer.Ordinal);
        public int DownloadCount { get; private set; }
        public Task<string> UploadPartAsync(string path, string fileId, int index, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> DownloadPartAsync(string remoteId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadCount++;
            return Task.FromResult<Stream>(new MemoryStream(Parts[remoteId], writable: false));
        }
    }
}
