using System.Security.Cryptography;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class LocalStagingContentStoreTests
{
    private const string Key = "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=";
    private const string ProtectionId = "5f4f2ae5d8b54a9eb145f230fa4b3df8";
    private const string Identity = "file:fixture:part:0";
    private static ReadOnlySpan<byte> OwnerMarker => "TeleSelfCloud/upload-materialization/v1"u8;

    [Fact]
    public async Task ProtectedFileIsFullyMaterializedUnderProfileAndRemovedWhenHandleCloses()
    {
        using var profile = new TemporaryProfile();
        var root = profile.Root;
        var lease = profile.Lease;
        var protectedPath = await WriteProtectedPartAsync(root, [1, 2, 3, 4, 5]);
        var store = Store(root, lease);

        var materialized = await store.MaterializeAsync(protectedPath, Identity);
        var workspace = Path.GetDirectoryName(materialized.Path)!;
        Assert.StartsWith(Path.Combine(root, "transient", "staging"), materialized.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, await ReadAllAsync(materialized.Stream));
        Assert.True(File.Exists(materialized.Path));

        await materialized.DisposeAsync();
        Assert.False(Directory.Exists(workspace));
        Assert.True(LocalStagingCipher.HasProtectedHeader(await ReadPrefixAsync(protectedPath)));
    }

    [Fact]
    public async Task AuthenticationFailureReturnsNoHandleAndRemovesIncompletePlaintextScratch()
    {
        using var profile = new TemporaryProfile();
        var root = profile.Root;
        var lease = profile.Lease;
        var protectedPath = await WriteProtectedPartAsync(root, Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray());
        using (var file = new FileStream(protectedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            file.Position = file.Length - 1;
            var value = file.ReadByte();
            file.Position = file.Length - 1;
            file.WriteByte((byte)(value ^ 0x80));
        }

        await Assert.ThrowsAnyAsync<CryptographicException>(() => Store(root, lease).MaterializeAsync(protectedPath, Identity));

        var scratchRoot = Path.Combine(root, "transient", "staging");
        Assert.True(Directory.Exists(scratchRoot));
        Assert.Empty(Directory.GetDirectories(scratchRoot));
        Assert.Empty(Directory.GetFiles(scratchRoot));
    }

    [Fact]
    public async Task LegacyPlaintextIsReadInPlaceOnlyWhenCompatibilityModeAllowsIt()
    {
        using var profile = new TemporaryProfile();
        var root = profile.Root;
        var lease = profile.Lease;
        var path = Path.Combine(root, "legacy.part");
        await File.WriteAllBytesAsync(path, [7, 6, 5]);

        await using (var legacy = await Store(root, lease, allowLegacy: true).MaterializeAsync(path, Identity))
        {
            Assert.Equal(Path.GetFullPath(path), legacy.Path);
            Assert.Equal(new byte[] { 7, 6, 5 }, await ReadAllAsync(legacy.Stream));
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => Store(root, lease, allowLegacy: false).MaterializeAsync(path, Identity));
        Assert.Equal(new byte[] { 7, 6, 5 }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task StartupCleanupDeletesOnlyKnownOwnedWorkspacesAndPreservesUnknownContents()
    {
        using var profile = new TemporaryProfile();
        var root = profile.Root;
        var lease = profile.Lease;
        var transient = Path.Combine(root, "transient", "staging");
        Directory.CreateDirectory(transient);
        var orphan = Path.Combine(transient, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);
        await File.WriteAllBytesAsync(Path.Combine(orphan, ".tsc-upload-owner"), OwnerMarker.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(orphan, "payload.tmp"), [1, 2, 3]);
        var unowned = Path.Combine(transient, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unowned);
        await File.WriteAllTextAsync(Path.Combine(unowned, "payload.tmp"), "keep");

        LocalStagingContentStore.CleanupOrphans(root, lease);

        Assert.False(Directory.Exists(orphan));
        Assert.True(File.Exists(Path.Combine(unowned, "payload.tmp")));
    }

    [Fact]
    public async Task StagedAssemblerMaterializesProtectedPartsAndRemovesTransientPlaintext()
    {
        using var profile = new TemporaryProfile();
        var plain = new byte[] { 9, 8, 7, 6 };
        var protectedPath = await WriteProtectedPartAsync(profile.Root, plain);
        var manifest = new FileManifest(1, "fixture", "fixture.bin", plain.Length,
            Convert.ToHexString(SHA256.HashData(plain)), plain.Length,
            [new PartRecord(0, 0, plain.Length, Convert.ToHexString(SHA256.HashData(plain)), null, false, protectedPath)],
            false);
        var destination = Path.Combine(profile.Root, "restored.bin");

        await new StagedPartAssembler(Store(profile.Root, profile.Lease)).AssembleAsync(manifest, destination, default);

        Assert.Equal(plain, await File.ReadAllBytesAsync(destination));
        var scratch = Path.Combine(profile.Root, "transient", "staging");
        Assert.Empty(Directory.GetDirectories(scratch));
        Assert.True(LocalStagingCipher.HasProtectedHeader(await ReadPrefixAsync(protectedPath)));
    }

    [Fact]
    public async Task OfflineCacheVerificationHashesAuthenticatedPlaintextAndSnapshotsProtectedFile()
    {
        using var profile = new TemporaryProfile();
        var bytes = new byte[] { 2, 4, 6, 8 };
        var protectedPath = await WriteProtectedPartAsync(profile.Root, bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var manifest = new TeleSelfCloud.Core.Transfers.FileManifest(1, "fixture", "fixture.bin", bytes.Length, hash,
            bytes.Length, [new TeleSelfCloud.Core.Transfers.PartRecord(0, 0, bytes.Length, hash, "remote", true, protectedPath)], true);
        using var verification = new LocalCacheVerificationStore(Path.Combine(profile.Root, "cache.json"));

        var result = await verification.VerifyAndSaveAsync(manifest, default, Store(profile.Root, profile.Lease));

        Assert.Equal(LocalCacheIntegrityState.AvailableOffline, result.State);
        Assert.Equal(new FileInfo(protectedPath).Length, result.Parts[0].Length);
        Assert.True(result.Parts[0].Length > bytes.Length);
        Assert.Empty(Directory.GetDirectories(Path.Combine(profile.Root, "transient", "staging")));
    }

    [Fact]
    public async Task EncryptedPayloadAssemblerVerifiesProtectedPartsBeforePublishingTemporaryPayload()
    {
        using var profile = new TemporaryProfile();
        var payload = new byte[] { 10, 20, 30, 40, 50 };
        var protectedPath = await WriteProtectedPartAsync(profile.Root, payload);
        var hash = Convert.ToHexString(SHA256.HashData(payload));
        var key = AesGcmFileCipher.CreateFileKey();
        try
        {
            var descriptor = new TeleSelfCloud.Core.Transfers.EncryptedPayloadDescriptor(1, payload.Length, hash,
                AesGcmFileCipher.WrapFileKey(key, "fixture passphrase"));
            var manifest = new TeleSelfCloud.Core.Transfers.FileManifest(1, "fixture", "fixture.bin", 1, hash,
                payload.Length, [new TeleSelfCloud.Core.Transfers.PartRecord(0, 0, payload.Length, hash, "remote", true, protectedPath)], true,
                Encryption: descriptor);
            var destination = Path.Combine(profile.Root, "assembled.cipher");

            await StagedEncryptedPayloadAssembler.AssembleAsync(manifest, destination, default, Store(profile.Root, profile.Lease));

            Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
            Assert.Empty(Directory.GetDirectories(Path.Combine(profile.Root, "transient", "staging")));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    [Fact]
    public async Task MissingEncryptedUploadPartsCanBeRestagedFromProtectedPayloadCache()
    {
        using var profile = new TemporaryProfile();
        var payload = new byte[] { 3, 1, 4, 1, 5, 9 };
        var payloadHash = Convert.ToHexString(SHA256.HashData(payload));
        var encryptedPath = Path.Combine(profile.Root, "encrypted-payload.bin");
        using (var cipher = new LocalStagingCipher(Key, ProtectionId,
                   StagingFileIdentity.EncryptedPayload("fixture")))
        await using (var input = new MemoryStream(payload, writable: false))
        await using (var output = new FileStream(encryptedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await cipher.EncryptAsync(input, output, payload.Length);

        var fileKey = AesGcmFileCipher.CreateFileKey();
        try
        {
            var descriptor = new TeleSelfCloud.Core.Transfers.EncryptedPayloadDescriptor(1, payload.Length, payloadHash,
                AesGcmFileCipher.WrapFileKey(fileKey, "fixture passphrase"), encryptedPath);
            var missingPart = new TeleSelfCloud.Core.Transfers.PartRecord(0, 0, payload.Length, payloadHash, null, false);
            var manifest = new TeleSelfCloud.Core.Transfers.FileManifest(1, "fixture", "fixture.bin", 1,
                Convert.ToHexString(SHA256.HashData([42])), payload.Length, [missingPart], false, Encryption: descriptor);

            var recovered = await MissingUploadPartRecovery.RestageEncryptedFromCachedPayloadAsync(manifest,
                Path.Combine(profile.Root, "staging"), default, Store(profile.Root, profile.Lease));

            await using (var recoveredPlaintext = await Store(profile.Root, profile.Lease).MaterializeAsync(
                             recovered.Parts[0].StagingPath!, StagingFileIdentity.Part(manifest.FileId, 0)))
                Assert.Equal(payload, await ReadAllAsync(recoveredPlaintext.Stream));
            Assert.Empty(Directory.GetDirectories(Path.Combine(profile.Root, "transient", "staging")));
            Assert.True(LocalStagingCipher.HasProtectedHeader(await ReadPrefixAsync(encryptedPath)));
        }
        finally { CryptographicOperations.ZeroMemory(fileKey); }
    }

    [Fact]
    public async Task ProtectInPlaceReplacesOnlyVerifiedPlaintextAndIsIdempotent()
    {
        using var profile = new TemporaryProfile();
        var bytes = new byte[] { 5, 4, 3, 2, 1 };
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var path = Path.Combine(profile.Root, "part.bin");
        await File.WriteAllBytesAsync(path, bytes);
        var store = Store(profile.Root, profile.Lease);

        await store.ProtectInPlaceAsync(path, Identity, bytes.Length, hash);
        Assert.True(LocalStagingCipher.HasProtectedHeader(await ReadPrefixAsync(path)));
        await store.ProtectInPlaceAsync(path, Identity, bytes.Length, hash);
        await using (var materialized = await store.MaterializeAsync(path, Identity))
            Assert.Equal(bytes, await ReadAllAsync(materialized.Stream));
        Assert.Empty(Directory.GetDirectories(Path.Combine(profile.Root, "transient", "staging")));
    }

    [Fact]
    public async Task ProtectInPlaceKeepsSourceUnchangedWhenManifestHashDoesNotMatch()
    {
        using var profile = new TemporaryProfile();
        var bytes = new byte[] { 1, 3, 3, 7 };
        var path = Path.Combine(profile.Root, "part.bin");
        await File.WriteAllBytesAsync(path, bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => Store(profile.Root, profile.Lease)
            .ProtectInPlaceAsync(path, Identity, bytes.Length, new string('0', 64)));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        var scratch = Path.Combine(profile.Root, "transient", "staging");
        Assert.Empty(Directory.GetDirectories(scratch));
    }

    [Fact]
    public async Task LocalStageWorkflowProtectsGeneratedPartsBeforeSavingManifest()
    {
        using var profile = new TemporaryProfile();
        var bytes = new byte[] { 1, 2, 4, 8, 16 };
        var source = Path.Combine(profile.Root, "source.bin");
        await File.WriteAllBytesAsync(source, bytes);
        var contentStore = Store(profile.Root, profile.Lease);
        var manifests = new SqliteManifestStore(Path.Combine(profile.Root, "manifests.db"));
        var workflow = new LocalFileWorkflow(new FileTransferCoordinator(), manifests,
            new StagedPartAssembler(contentStore), contentStore);

        var manifest = await workflow.PrepareAsync(source, Path.Combine(profile.Root, "staging"), 2, default);

        var saved = await manifests.LoadAsync(manifest.FileId, default);
        Assert.NotNull(saved);
        foreach (var part in saved.Parts)
        {
            Assert.True(LocalStagingCipher.HasProtectedHeader(await ReadPrefixAsync(part.StagingPath!)));
            await using var plaintext = await contentStore.MaterializeAsync(part.StagingPath!,
                StagingFileIdentity.Part(saved.FileId, part.Index));
            Assert.Equal(part.Length, plaintext.Stream.Length);
        }
    }

    private static LocalStagingContentStore Store(string root, LocalProfileLease lease, bool allowLegacy = true) =>
        new(root, lease, identity => new LocalStagingCipher(Key, ProtectionId, identity), allowLegacy);

    private static async Task<string> WriteProtectedPartAsync(string root, byte[] bytes)
    {
        var staging = Path.Combine(root, "staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var path = Path.Combine(staging, "part-00000000.bin");
        using var cipher = new LocalStagingCipher(Key, ProtectionId, Identity);
        await using var input = new MemoryStream(bytes, writable: false);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await cipher.EncryptAsync(input, output, bytes.Length);
        return path;
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

    private static async Task<byte[]> ReadPrefixAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var prefix = new byte[8];
        await stream.ReadExactlyAsync(prefix);
        return prefix;
    }

    private sealed class TemporaryProfile : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "TeleSelfCloud-StagingContent", Guid.NewGuid().ToString("N"));
        public LocalProfileLease Lease { get; }
        public TemporaryProfile()
        {
            Directory.CreateDirectory(Root);
            Lease = LocalProfileLease.TryAcquire(Root) ?? throw new InvalidOperationException("Could not acquire test profile lease.");
        }
        public void Dispose()
        {
            Lease.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
