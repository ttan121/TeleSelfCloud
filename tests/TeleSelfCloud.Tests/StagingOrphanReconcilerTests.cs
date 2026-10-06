using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;
using System.Diagnostics;

namespace TeleSelfCloud.Tests;

public sealed class StagingOrphanReconcilerTests
{
    [Fact]
    public async Task RemovesOnlyExpiredUnreferencedGeneratedPartsAndEncryptedPreparationFiles()
    {
        var root = NewRoot();
        using var lease = LocalProfileLease.TryAcquire(root)!;
        var staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(staging);
        var now = DateTimeOffset.UtcNow;
        var staleDirectory = MakePartDirectory(staging, [1, 2, 3], now.AddDays(-31).UtcDateTime);
        var recentDirectory = MakePartDirectory(staging, [4], now.AddDays(-1).UtcDateTime);
        var referencedDirectory = MakePartDirectory(staging, [5, 6], now.AddDays(-31).UtcDateTime);
        var unknownDirectory = Path.Combine(staging, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unknownDirectory);
        var unknown = Path.Combine(unknownDirectory, "keep-me.txt");
        await File.WriteAllTextAsync(unknown, "keep");
        File.SetLastWriteTimeUtc(unknown, now.AddDays(-31).UtcDateTime);
        Directory.SetLastWriteTimeUtc(unknownDirectory, now.AddDays(-31).UtcDateTime);

        var encryptedRoot = Path.Combine(staging, "encrypted-preparation");
        Directory.CreateDirectory(encryptedRoot);
        var encryptedOrphan = Path.Combine(encryptedRoot, Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(encryptedOrphan, [7, 8, 9]);
        File.SetLastWriteTimeUtc(encryptedOrphan, now.AddDays(-31).UtcDateTime);
        var encryptedReferenced = Path.Combine(encryptedRoot, Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(encryptedReferenced, [10, 11]);
        File.SetLastWriteTimeUtc(encryptedReferenced, now.AddDays(-31).UtcDateTime);

        var referencedManifest = new FileManifest(1, Guid.NewGuid().ToString("N"), "keep.bin", 2,
            HashHex([5, 6]), 2, [new PartRecord(0, 0, 2, HashHex([5, 6]), null, false, referencedDirectory.Part)], false);
        var encryptedReferenceManifest = referencedManifest with
        {
            FileId = Guid.NewGuid().ToString("N"),
            Encryption = new EncryptedPayloadDescriptor(1, 2, HashHex([10, 11]),
                new PassphraseKeyEnvelope(1, "PBKDF2-SHA256", 100_000, "AES-256-GCM", "salt", "nonce", "ciphertext", "tag"), encryptedReferenced)
        };
        var referenced = new MemoryManifestStore([referencedManifest, encryptedReferenceManifest]);
        var result = await StagingOrphanReconciler.ReconcileAsync(root, staging, referenced, lease, now);

        Assert.True(result.CatalogReadable);
        Assert.Equal(2, result.DeletedFiles);
        Assert.Equal(1, result.DeletedDirectories);
        Assert.Equal(6, result.DeletedBytes);
        Assert.False(File.Exists(staleDirectory.Part));
        Assert.False(Directory.Exists(staleDirectory.Directory));
        Assert.False(File.Exists(encryptedOrphan));
        Assert.True(File.Exists(recentDirectory.Part));
        Assert.True(File.Exists(referencedDirectory.Part));
        Assert.True(File.Exists(unknown));
    }

    [Fact]
    public async Task CatalogReadFailurePreservesEveryCandidate()
    {
        var root = NewRoot();
        using var lease = LocalProfileLease.TryAcquire(root)!;
        var staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(staging);
        var old = MakePartDirectory(staging, [1], DateTime.UtcNow.AddDays(-60));

        var result = await StagingOrphanReconciler.ReconcileAsync(root, staging, new MemoryManifestStore(failList: true), lease, DateTimeOffset.UtcNow);

        Assert.False(result.CatalogReadable);
        Assert.True(File.Exists(old.Part));
    }

    [Fact]
    public async Task RefusesAReparsePointProfileOutsideTheHeldLease()
    {
        var root = NewRoot();
        var other = NewRoot();
        using var lease = LocalProfileLease.TryAcquire(root)!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => StagingOrphanReconciler.ReconcileAsync(
            other, Path.Combine(other, "staging"), new MemoryManifestStore(), lease, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task PreservesJunctionTargetAndSkipsLinkedCandidate()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = NewRoot();
        using var lease = LocalProfileLease.TryAcquire(root)!;
        var staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(staging);
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "part-00000000.bin");
        await File.WriteAllBytesAsync(sentinel, [1, 2, 3]);
        File.SetLastWriteTimeUtc(sentinel, DateTime.UtcNow.AddDays(-60));
        var junction = Path.Combine(staging, Guid.NewGuid().ToString("N"));
        using var create = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe", UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "/c", "mklink", "/J", junction, outside }
        }) ?? throw new InvalidOperationException("Could not start junction fixture creation.");
        await create.WaitForExitAsync();
        Assert.Equal(0, create.ExitCode);

        var result = await StagingOrphanReconciler.ReconcileAsync(root, staging, new MemoryManifestStore(), lease, DateTimeOffset.UtcNow);

        Assert.Equal(0, result.DeletedFiles);
        Assert.True(File.Exists(sentinel));
        Directory.Delete(junction, recursive: false);
    }

    private static (string Directory, string Part) MakePartDirectory(string staging, byte[] bytes, DateTime modifiedUtc)
    {
        var directory = Path.Combine(staging, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var part = Path.Combine(directory, "part-00000000.bin");
        File.WriteAllBytes(part, bytes);
        File.SetLastWriteTimeUtc(part, modifiedUtc);
        Directory.SetLastWriteTimeUtc(directory, modifiedUtc);
        return (directory, part);
    }

    private static string HashHex(ReadOnlySpan<byte> data) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud-StagingReconcile", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class MemoryManifestStore(IEnumerable<FileManifest>? manifests = null, bool failList = false) : IManifestStore
    {
        private readonly IReadOnlyList<FileManifest> manifests = manifests?.ToArray() ?? [];
        public Task SaveAsync(FileManifest manifest, CancellationToken token) => throw new NotSupportedException();
        public Task<FileManifest?> LoadAsync(string fileId, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<FileManifest>> ListAsync(CancellationToken token) => failList
            ? Task.FromException<IReadOnlyList<FileManifest>>(new IOException("Injected catalog read failure"))
            : Task.FromResult(manifests);
        public Task DeleteManyAsync(IEnumerable<string> ids, CancellationToken token) => throw new NotSupportedException();
    }
}
