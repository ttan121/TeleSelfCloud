using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class TransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void RemoteManifestStatus_OnlyMarksObservedCommittedFilesAsKnown()
    {
        var committed = new FileManifest(1, "remote-file", "file.bin", 0,
            Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())), 1,
            new[] { new PartRecord(0, 0, 0, Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())), "1/2", true) }, true);
        var localDraft = committed with { FileId = "draft-file", Committed = false };
        IReadOnlySet<string> observed = new HashSet<string>(StringComparer.Ordinal) { "remote-file" };

        Assert.False(RemoteManifestStatus.IsUnknown(committed, observed));
        Assert.True(RemoteManifestStatus.IsUnknown(committed with { FileId = "absent-file" }, observed));
        Assert.False(RemoteManifestStatus.IsUnknown(localDraft, observed));
    }

    [Fact]
    public void ChunkPlanner_CoversInputWithoutGaps()
    {
        Assert.Equal(new[] { (0L, 4L), (4L, 4L), (8L, 1L) }, ChunkPlanner.Plan(9, 4));
        Assert.Equal(new[] { (0L, 0L) }, ChunkPlanner.Plan(0, 4));
    }

    [Fact]
    public async Task StagingAndRestore_RecreateExactBytesAndManifest()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.bin");
        var expected = Enumerable.Range(0, 37).Select(i => (byte)(i * 7)).ToArray();
        await File.WriteAllBytesAsync(source, expected);

        var coordinator = new FileTransferCoordinator();
        var store = new JsonManifestStore(Path.Combine(_root, "manifests"));
        var workflow = new LocalFileWorkflow(coordinator, store, new StagedPartAssembler());
        var manifest = await workflow.PrepareAsync(source, Path.Combine(_root, "staging"), 8, CancellationToken.None);
        var listed = await workflow.ListAsync(CancellationToken.None);
        var destination = Path.Combine(_root, "restored.bin");

        Assert.Equal(5, manifest.Parts.Count);
        Assert.Equal(manifest.FileId, Assert.Single(listed).FileId);
        await workflow.RestoreAsync(manifest.FileId, destination, CancellationToken.None);
        Assert.Equal(expected, await File.ReadAllBytesAsync(destination));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), manifest.TotalSha256);
    }

    [Fact]
    public async Task MissingUploadPartRecovery_RestagesOnlyAfterWholeSourceHashMatches()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source-recovery.bin");
        var staging = Path.Combine(_root, "staging-recovery");
        var contents = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        await File.WriteAllBytesAsync(source, contents);
        var manifest = await new FileTransferCoordinator().PrepareAsync(source, staging, 16, CancellationToken.None);
        File.Delete(manifest.Parts[0].StagingPath!);

        var recovered = await MissingUploadPartRecovery.RestageAsync(manifest, source, staging, CancellationToken.None);

        Assert.Equal(contents.Take(16), await File.ReadAllBytesAsync(recovered.Parts[0].StagingPath!));
        for (var index = 1; index < recovered.Parts.Count; index++)
            Assert.Equal(contents.Skip(index * 16).Take(16), await File.ReadAllBytesAsync(recovered.Parts[index].StagingPath!));
        Assert.Equal(manifest.TotalSha256, recovered.TotalSha256);
        var recoveryDirectories = Directory.GetDirectories(Path.Combine(staging, "recovered-uploads")).Length;

        var wrongSource = Path.Combine(_root, "wrong-source.bin");
        await File.WriteAllBytesAsync(wrongSource, contents.Select(value => (byte)(value ^ 0xff)).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => MissingUploadPartRecovery.RestageAsync(manifest, wrongSource, staging, CancellationToken.None));
        Assert.Equal(recoveryDirectories, Directory.GetDirectories(Path.Combine(staging, "recovered-uploads")).Length);
    }

    [Fact]
    public async Task AesGcmFileCipher_RoundTripsFramedDataAndPassphraseWrappedKey()
    {
        using (var knownAnswer = new AesGcm(new byte[32], 16))
        {
            var knownTag = new byte[16];
            knownAnswer.Encrypt(new byte[12], ReadOnlySpan<byte>.Empty, Span<byte>.Empty, knownTag);
            Assert.Equal("530F8AFBC74536B9A963B4F1C4CB738B", Convert.ToHexString(knownTag));
        }
        var key = AesGcmFileCipher.CreateFileKey();
        var envelope = AesGcmFileCipher.WrapFileKey(key, "correct horse battery staple");
        var recoveredKey = AesGcmFileCipher.UnwrapFileKey(envelope, "correct horse battery staple");
        Assert.Equal(key, recoveredKey);

        foreach (var expected in new[] { Array.Empty<byte>(), Enumerable.Range(0, 37).Select(i => (byte)(i * 17)).ToArray() })
        {
            using var encrypted = new MemoryStream();
            await AesGcmFileCipher.EncryptAsync(new MemoryStream(expected), encrypted, key, frameSize: 7);
            encrypted.Position = 0;
            using var actual = new MemoryStream();
            await AesGcmFileCipher.DecryptAsync(encrypted, actual, recoveredKey);
            Assert.Equal(expected, actual.ToArray());
        }

        Assert.ThrowsAny<CryptographicException>(() => AesGcmFileCipher.UnwrapFileKey(envelope, "wrong recovery passphrase"));
        Assert.Throws<InvalidDataException>(() => AesGcmFileCipher.UnwrapFileKey(envelope with { Iterations = int.MaxValue }, "correct horse battery staple"));
        using var interrupted = new MemoryStream();
        interrupted.WriteByte(7);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AesGcmFileCipher.EncryptAsync(
            new MemoryStream(new byte[] { 1 }), interrupted, key, cancellationToken: canceled.Token));
        Assert.Equal(new byte[] { 7 }, interrupted.ToArray());
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(recoveredKey);
    }

    [Fact]
    public async Task AesGcmFileCipher_RejectsTamperedOrTruncatedFrames()
    {
        var key = AesGcmFileCipher.CreateFileKey();
        using var encrypted = new MemoryStream();
        await AesGcmFileCipher.EncryptAsync(new MemoryStream(Enumerable.Range(0, 30).Select(i => (byte)i).ToArray()), encrypted, key, frameSize: 10);
        var bytes = encrypted.ToArray();
        var tampered = bytes.ToArray();
        tampered[33] ^= 0x40;
        using var tamperedOutput = new MemoryStream();
        tamperedOutput.WriteByte(99);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => AesGcmFileCipher.DecryptAsync(new MemoryStream(tampered), tamperedOutput, key));
        Assert.Equal(new byte[] { 99 }, tamperedOutput.ToArray());
        var reordered = bytes.ToArray();
        var firstFrame = reordered.AsSpan(33, 26).ToArray();
        reordered.AsSpan(59, 26).CopyTo(reordered.AsSpan(33, 26));
        firstFrame.CopyTo(reordered, 59);
        using var reorderedOutput = new MemoryStream();
        await Assert.ThrowsAnyAsync<CryptographicException>(() => AesGcmFileCipher.DecryptAsync(new MemoryStream(reordered), reorderedOutput, key));
        Assert.Empty(reorderedOutput.ToArray());
        var changedHeader = bytes.ToArray();
        changedHeader[21] ^= 1;
        using var headerOutput = new MemoryStream();
        await Assert.ThrowsAnyAsync<CryptographicException>(() => AesGcmFileCipher.DecryptAsync(new MemoryStream(changedHeader), headerOutput, key));
        Assert.Empty(headerOutput.ToArray());
        using var truncatedOutput = new MemoryStream();
        await Assert.ThrowsAsync<EndOfStreamException>(() => AesGcmFileCipher.DecryptAsync(new MemoryStream(bytes[..^1]), truncatedOutput, key));
        Assert.Empty(truncatedOutput.ToArray());
        CryptographicOperations.ZeroMemory(key);
    }

    [Fact]
    public async Task ZipPreviewReader_ListsEntriesWithoutExtractingArchivePaths()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "preview.zip");
        await using (var file = new FileStream(path, FileMode.CreateNew))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            archive.CreateEntry("folder/");
            var fileEntry = archive.CreateEntry("../outside.txt");
            await using var content = fileEntry.Open();
            await content.WriteAsync(new byte[] { 1, 2, 3 });
        }

        var entries = ZipPreviewReader.Read(path);

        Assert.Equal(2, entries.Count);
        Assert.True(entries[0].IsDirectory);
        Assert.Equal("../outside.txt", entries[1].Name);
        Assert.Equal(3, entries[1].UncompressedSize);
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
    }

    [Fact]
    public async Task EmptyFile_StagesAndRestoresAsOneEmptyPart()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "empty.bin");
        await File.WriteAllBytesAsync(source, Array.Empty<byte>());
        var coordinator = new FileTransferCoordinator();
        var manifest = await coordinator.PrepareAsync(source, Path.Combine(_root, "staging"), 128, CancellationToken.None);
        var destination = Path.Combine(_root, "restored-empty.bin");

        Assert.Equal(0, manifest.LogicalSize);
        Assert.Equal(0, Assert.Single(manifest.Parts).Length);
        await new StagedPartAssembler().AssembleAsync(manifest, destination, CancellationToken.None);
        Assert.Empty(await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task CanceledLocalRestorePreservesDestinationAndRemovesOwnedPartialFile()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "restore-source.bin");
        await File.WriteAllBytesAsync(source, Enumerable.Range(0, 8192).Select(value => (byte)value).ToArray());
        var manifest = await new FileTransferCoordinator().PrepareAsync(source, Path.Combine(_root, "staging"), 128, default);
        var destination = Path.Combine(_root, "existing-destination.bin");
        var original = new byte[] { 9, 8, 7, 6 };
        await File.WriteAllBytesAsync(destination, original);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StagedPartAssembler().AssembleAsync(manifest, destination, canceled.Token));

        Assert.Equal(original, await File.ReadAllBytesAsync(destination));
        Assert.Empty(Directory.EnumerateFiles(_root, ".tsc-restore-*.partial"));
    }

    [Fact]
    public async Task LocalCacheVerificationStore_PersistsFullOfflineVerificationAndDetectsChangedFiles()
    {
        Directory.CreateDirectory(_root);
        var first = new byte[] { 1, 2, 3, 4 };
        var second = new byte[] { 5, 6, 7 };
        var firstPath = Path.Combine(_root, "part-0.bin");
        var secondPath = Path.Combine(_root, "part-1.bin");
        await File.WriteAllBytesAsync(firstPath, first);
        await File.WriteAllBytesAsync(secondPath, second);
        var all = first.Concat(second).ToArray();
        var manifest = new FileManifest(1, "cache-file", "cache.bin", all.Length,
            Convert.ToHexString(SHA256.HashData(all)), first.Length,
            new[]
            {
                new PartRecord(0, 0, first.Length, Convert.ToHexString(SHA256.HashData(first)), null, false, firstPath),
                new PartRecord(1, first.Length, second.Length, Convert.ToHexString(SHA256.HashData(second)), null, false, secondPath)
            }, false);
        var statePath = Path.Combine(_root, "cache-verifications.json");
        var store = new LocalCacheVerificationStore(statePath);

        var result = await store.VerifyAndSaveAsync(manifest, CancellationToken.None);

        Assert.Equal(LocalCacheIntegrityState.AvailableOffline, result.State);
        Assert.Equal(2, result.ValidParts);
        Assert.True(result.IsCurrentFor(manifest));
        var reopened = new LocalCacheVerificationStore(statePath);
        var persistedStates = await reopened.LoadAllAsync(CancellationToken.None);
        Assert.True(persistedStates.TryGetValue("cache-file", out var persisted));
        Assert.NotNull(persisted);
        Assert.Equal(LocalCacheIntegrityState.AvailableOffline, persisted.State);
        Assert.True(persisted.IsCurrentFor(manifest with { FileName = "renamed.bin" }));

        await File.WriteAllBytesAsync(secondPath, new byte[] { 9, 8, 7 });
        File.SetLastWriteTimeUtc(secondPath, DateTime.UtcNow.AddMinutes(5));
        Assert.False(persisted.IsCurrentFor(manifest));
    }

    [Fact]
    public async Task LocalCacheVerificationStore_RemovesOnlyMigratedAccountEntries()
    {
        Directory.CreateDirectory(_root);
        var bytes = new byte[] { 1, 2, 3 };
        var path = Path.Combine(_root, "cache-removal.bin");
        await File.WriteAllBytesAsync(path, bytes);
        var manifestA = new FileManifest(1, "account-a", "a.bin", bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length,
            new[] { new PartRecord(0, 0, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), null, false, path) }, false);
        var manifestB = manifestA with { FileId = "account-b", FileName = "b.bin" };
        var store = new LocalCacheVerificationStore(Path.Combine(_root, "cache-removal.json"));
        await store.VerifyAndSaveAsync(manifestA, CancellationToken.None);
        await store.VerifyAndSaveAsync(manifestB, CancellationToken.None);

        await store.RemoveManyAsync(new[] { "account-a" }, CancellationToken.None);

        var remaining = await store.LoadAllAsync(CancellationToken.None);
        Assert.DoesNotContain("account-a", remaining.Keys);
        Assert.Contains("account-b", remaining.Keys);
    }

    [Fact]
    public async Task LocalCacheVerificationStore_ReportsTamperedPartAsIntegrityError()
    {
        Directory.CreateDirectory(_root);
        var bytes = new byte[] { 11, 12, 13, 14 };
        var path = Path.Combine(_root, "tampered.bin");
        await File.WriteAllBytesAsync(path, new byte[] { 91, 92, 93, 94 });
        var manifest = new FileManifest(1, "tampered-cache", "cache.bin", bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length,
            new[] { new PartRecord(0, 0, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), null, false, path) }, false);
        var store = new LocalCacheVerificationStore(Path.Combine(_root, "tampered-cache.json"));

        var result = await store.VerifyAndSaveAsync(manifest, CancellationToken.None);

        Assert.Equal(LocalCacheIntegrityState.IntegrityError, result.State);
        Assert.Equal(0, result.ValidParts);
        Assert.True(result.IsCurrentFor(manifest));
    }

    [Fact]
    public async Task LocalCacheVerificationStore_ReportsPartialWhenOnlySomePartsExist()
    {
        Directory.CreateDirectory(_root);
        var first = new byte[] { 21, 22 };
        var missingPath = Path.Combine(_root, "missing-part.bin");
        var firstPath = Path.Combine(_root, "present-part.bin");
        await File.WriteAllBytesAsync(firstPath, first);
        var expected = first.Concat(new byte[] { 23, 24 }).ToArray();
        var manifest = new FileManifest(1, "partial-cache", "cache.bin", expected.Length,
            Convert.ToHexString(SHA256.HashData(expected)), first.Length,
            new[]
            {
                new PartRecord(0, 0, 2, Convert.ToHexString(SHA256.HashData(first)), null, false, firstPath),
                new PartRecord(1, 2, 2, Convert.ToHexString(SHA256.HashData(new byte[] { 23, 24 })), null, false, missingPath)
            }, false);
        var store = new LocalCacheVerificationStore(Path.Combine(_root, "partial-cache.json"));

        var result = await store.VerifyAndSaveAsync(manifest, CancellationToken.None);

        Assert.Equal(LocalCacheIntegrityState.Partial, result.State);
        Assert.Equal(1, result.ValidParts);
        Assert.True(result.IsCurrentFor(manifest));
    }

    [Fact]
    public void ManifestAccountScope_ShowsOwnedFilesAndUnboundDraftsOnly()
    {
        var owned = CreateScopeManifest("owned", "account-a", committed: true);
        var foreign = CreateScopeManifest("foreign", "account-b", committed: true);
        var localDraft = CreateScopeManifest("draft", null, committed: false);
        var unboundCommitted = CreateScopeManifest("legacy", null, committed: true);

        Assert.True(ManifestAccountScope.IsVisible(owned, "account-a"));
        Assert.False(ManifestAccountScope.IsVisible(foreign, "account-a"));
        Assert.True(ManifestAccountScope.IsVisible(localDraft, "account-a"));
        Assert.True(ManifestAccountScope.IsVisible(localDraft, null));
        Assert.False(ManifestAccountScope.IsVisible(owned, null));
        Assert.False(ManifestAccountScope.IsVisible(unboundCommitted, "account-a"));
    }

    [Fact]
    public void TelegramAccountProfileStore_UsesDistinctDirectoriesAndRejectsInvalidIds()
    {
        var accountA = TelegramAccountProfileStore.GetDirectory(_root, "1234");
        var accountB = TelegramAccountProfileStore.GetDirectory(_root, "5678");

        Assert.NotEqual(accountA, accountB);
        Assert.EndsWith(Path.Combine("accounts", "1234"), accountA, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ArgumentException>(() => TelegramAccountProfileStore.GetDirectory(_root, "..\\other"));
    }

    [Fact]
    public void TelegramAccountProfileStore_CopiesLegacyChannelOnlyForItsOwnerAndKeepsOriginal()
    {
        Directory.CreateDirectory(_root);
        var legacyPath = Path.Combine(_root, "telegram-account", "storage-channel.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        var legacy = new TelegramStorageChannelInfo(-100123, "1234", "TeleSelfCloud Storage");
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(legacy));

        var ownerPath = TelegramAccountProfileStore.GetStorageChannelSettingsPath(_root, "1234", legacyPath);
        var otherAccountPath = TelegramAccountProfileStore.GetStorageChannelSettingsPath(_root, "5678", legacyPath);

        Assert.True(File.Exists(ownerPath));
        Assert.Equal(legacy, JsonSerializer.Deserialize<TelegramStorageChannelInfo>(File.ReadAllText(ownerPath)));
        Assert.False(File.Exists(otherAccountPath));
        Assert.True(File.Exists(legacyPath));
    }

    private static FileManifest CreateScopeManifest(string fileId, string? accountId, bool committed) =>
        new(1, fileId, fileId + ".bin", 0, "", 1, Array.Empty<PartRecord>(), committed, accountId);

    [Fact]
    public async Task RemoteRestore_ResumesOnlyFromVerifiedPartBoundary()
    {
        Directory.CreateDirectory(_root);
        var first = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var second = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();
        var expected = first.Concat(second).ToArray();
        var firstHash = Convert.ToHexString(SHA256.HashData(first));
        var secondHash = Convert.ToHexString(SHA256.HashData(second));
        var manifest = new FileManifest(1, "resume-download", "payload.bin", expected.Length,
            Convert.ToHexString(SHA256.HashData(expected)), first.Length,
            new[]
            {
                new PartRecord(0, 0, first.Length, firstHash, "remote-0", true),
                new PartRecord(1, first.Length, second.Length, secondHash, "remote-1", true)
            }, true);
        var destination = Path.Combine(_root, "restored.bin");
        await File.WriteAllBytesAsync(destination + ".partial", first.Concat(new byte[] { 99, 98, 97 }).ToArray());
        var transport = new FakeTransport();
        transport.Downloads["remote-0"] = first;
        transport.Downloads["remote-1"] = second;
        var progress = new List<TransferProgress>();

        await new FileTransferCoordinator(transport).ReassembleAsync(manifest, destination, CancellationToken.None,
            update => { progress.Add(update); return Task.CompletedTask; });

        Assert.Equal(expected, await File.ReadAllBytesAsync(destination));
        Assert.Equal(new[] { "remote-1" }, transport.DownloadedRemoteIds);
        Assert.Equal(first.Length, progress[0].TransferredBytes);
        Assert.Equal(1, progress[0].CompletedParts);
        // Legacy sidecars lack ownership metadata and are copied read-only, never deleted.
        Assert.Equal(first.Concat(new byte[] { 99, 98, 97 }), await File.ReadAllBytesAsync(destination + ".partial"));
    }

    [Fact]
    public async Task RemoteRestore_StillFetchesZeroLengthRemotePart()
    {
        Directory.CreateDirectory(_root);
        var emptyHash = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>()));
        var manifest = new FileManifest(1, "empty-remote", "empty.bin", 0, emptyHash, 1,
            new[] { new PartRecord(0, 0, 0, emptyHash, "remote-empty", true) }, true);
        var destination = Path.Combine(_root, "empty.bin");
        await File.WriteAllBytesAsync(destination + ".partial", Array.Empty<byte>());
        var transport = new FakeTransport();
        transport.Downloads["remote-empty"] = Array.Empty<byte>();

        await new FileTransferCoordinator(transport).ReassembleAsync(manifest, destination, CancellationToken.None);

        Assert.Empty(await File.ReadAllBytesAsync(destination));
        Assert.Equal(new[] { "remote-empty" }, transport.DownloadedRemoteIds);
    }

    [Fact]
    public async Task RemoteRestoreIntegrityFailureKeepsExistingDestinationAndRetryReplacesItOnlyAfterVerification()
    {
        Directory.CreateDirectory(_root);
        var expected = Enumerable.Range(0, 97).Select(value => (byte)(value * 3)).ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(expected));
        var manifest = new FileManifest(1, "atomic-restore", "important.bin", expected.Length, hash, expected.Length,
            [new PartRecord(0, 0, expected.Length, hash, "remote-atomic", true)], true);
        var destination = Path.Combine(_root, "important.bin");
        var original = "user data that must survive a failed restore"u8.ToArray();
        await File.WriteAllBytesAsync(destination, original);
        var transport = new FakeTransport();
        transport.Downloads["remote-atomic"] = expected.Select(value => (byte)(value ^ 0xff)).ToArray();
        var coordinator = new FileTransferCoordinator(transport);

        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.ReassembleAsync(manifest, destination, CancellationToken.None));
        Assert.Equal(original, await File.ReadAllBytesAsync(destination));

        transport.Downloads["remote-atomic"] = expected;
        await coordinator.ReassembleAsync(manifest, destination, CancellationToken.None);

        Assert.Equal(expected, await File.ReadAllBytesAsync(destination));
        Assert.Equal(new[] { "remote-atomic", "remote-atomic" }, transport.DownloadedRemoteIds);
    }

    [Fact]
    public async Task RemoteRestoreCancellationKeepsDestinationAndRetryReusesOnlyVerifiedParts()
    {
        Directory.CreateDirectory(_root);
        var first = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        var second = Enumerable.Range(64, 64).Select(value => (byte)value).ToArray();
        var expected = first.Concat(second).ToArray();
        var firstHash = Convert.ToHexString(SHA256.HashData(first));
        var secondHash = Convert.ToHexString(SHA256.HashData(second));
        var manifest = new FileManifest(1, "cancelled-restore", "important.bin", expected.Length,
            Convert.ToHexString(SHA256.HashData(expected)), first.Length,
            [new PartRecord(0, 0, first.Length, firstHash, "remote-first", true),
             new PartRecord(1, first.Length, second.Length, secondHash, "remote-second", true)], true);
        var destination = Path.Combine(_root, "important-cancelled.bin");
        var original = "keep destination until complete"u8.ToArray();
        await File.WriteAllBytesAsync(destination, original);
        using var cancellation = new CancellationTokenSource();
        var interrupted = new InterruptingPartTransport(first, second, cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FileTransferCoordinator(interrupted).ReassembleAsync(manifest, destination, cancellation.Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(destination));
        Assert.Equal(new[] { "remote-first", "remote-second" }, interrupted.DownloadedRemoteIds);

        var retry = new FakeTransport();
        retry.Downloads["remote-first"] = first;
        retry.Downloads["remote-second"] = second;
        await new FileTransferCoordinator(retry).ReassembleAsync(manifest, destination, CancellationToken.None);

        Assert.Equal(expected, await File.ReadAllBytesAsync(destination));
        Assert.Equal(new[] { "remote-second" }, retry.DownloadedRemoteIds);
    }

    [Fact]
    public async Task SqliteManifestStore_PersistsAndImportsLegacyManifests()
    {
        Directory.CreateDirectory(_root);
        var hash = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>()));
        var manifest = new FileManifest(1, "legacy-id", "empty.bin", 0, hash, 1,
            new[] { new PartRecord(0, 0, 0, hash, null, false) }, false);
        var legacy = new FakeManifestStore();
        legacy.Manifests[manifest.FileId] = manifest;
        var databasePath = Path.Combine(_root, "manifests.db");
        var store = new SqliteManifestStore(databasePath);

        await store.ImportLegacyAsync(legacy, CancellationToken.None);
        await store.ImportLegacyAsync(legacy, CancellationToken.None);

                var loaded = await store.LoadAsync(manifest.FileId, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(manifest.FileId, loaded.FileId);
        Assert.Equal(manifest.FileName, loaded.FileName);
        Assert.Equal(manifest.Parts, loaded.Parts);
        Assert.Equal(manifest.FileId, Assert.Single(await store.ListAsync(CancellationToken.None)).FileId);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var reopened = new SqliteManifestStore(databasePath);
        Assert.Equal(manifest.FileId, (await reopened.LoadAsync(manifest.FileId, CancellationToken.None))?.FileId);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task SqliteTransferQueueStore_RecoversRunningTransfersAfterProcessRestart()
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "transfers.db");
        var queue = new SqliteTransferQueueStore(databasePath);
        await queue.EnqueueAsync("file-1", "archive.zip", 10_000, CancellationToken.None);
        await queue.SetStateAsync("file-1", TransferQueueState.Running, null, CancellationToken.None);
        await queue.UpdateProgressAsync("file-1", 4_000, 10_000, CancellationToken.None);

        var reopenedQueue = new SqliteTransferQueueStore(databasePath);
        Assert.Equal(1, await reopenedQueue.RecoverInterruptedAsync(CancellationToken.None));
        var recovered = Assert.Single(await reopenedQueue.ListAsync(CancellationToken.None));
        Assert.Equal(TransferQueueState.Paused, recovered.State);
        Assert.Equal(1, recovered.AttemptCount);
        Assert.Equal(4_000, recovered.TransferredBytes);
        Assert.Equal(10_000, recovered.TotalBytes);
        Assert.Contains("app closed", recovered.LastError, StringComparison.OrdinalIgnoreCase);

        await reopenedQueue.SetStateAsync("file-1", TransferQueueState.Running, null, CancellationToken.None);
        await reopenedQueue.SetStateAsync("file-1", TransferQueueState.Completed, null, CancellationToken.None);
        await reopenedQueue.EnqueueAsync("file-1", "archive.zip", 10_000, CancellationToken.None);
        var completed = Assert.Single(await reopenedQueue.ListAsync(CancellationToken.None));
        Assert.Equal(TransferQueueState.Completed, completed.State);
        Assert.Equal(2, completed.AttemptCount);
        Assert.Equal(10_000, completed.TransferredBytes);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task SqliteTransferQueueStore_ReEnqueuePreservesActiveStateAndCheckpoint()
    {
        Directory.CreateDirectory(_root);
        var queue = new SqliteTransferQueueStore(Path.Combine(_root, "reenqueue-preserves-state.db"));
        await queue.EnqueueAsync("running-file", "old-name.bin", 100, CancellationToken.None);
        await queue.SetStateAsync("running-file", TransferQueueState.Running, null, CancellationToken.None);
        await queue.UpdateProgressAsync("running-file", 40, 100, CancellationToken.None);

        await queue.EnqueueAsync("running-file", "new-name.bin", 200, CancellationToken.None);

        var running = Assert.Single(await queue.ListAsync(CancellationToken.None));
        Assert.Equal(TransferQueueState.Running, running.State);
        Assert.Equal(1, running.AttemptCount);
        Assert.Equal(40, running.TransferredBytes);
        Assert.Equal(200, running.TotalBytes);
        Assert.Equal("new-name.bin", running.FileName);
        await queue.SetStateAsync("running-file", TransferQueueState.Completed, null, CancellationToken.None);

        var completed = Assert.Single(await queue.ListAsync(CancellationToken.None));
        Assert.Equal(TransferQueueState.Completed, completed.State);
        Assert.Equal(200, completed.TransferredBytes);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task SqliteTransferQueueStore_RejectsInvalidStateTransitions()
    {
        Directory.CreateDirectory(_root);
        var queue = new SqliteTransferQueueStore(Path.Combine(_root, "queue.db"));
        await queue.EnqueueAsync("file-1", "archive.zip", 100, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.SetStateAsync(
            "file-1", TransferQueueState.Paused, null, CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => queue.SetStateAsync(
            "missing", TransferQueueState.Running, null, CancellationToken.None));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task TransferQueueStartGuard_MarksTaskFailedWhenQueueRefreshFails()
    {
        Directory.CreateDirectory(_root);
        var queue = new SqliteTransferQueueStore(Path.Combine(_root, "queue-start-guard.db"));
        await queue.EnqueueAsync("file-1", "archive.zip", 100, CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => new TransferQueueStartGuard(queue).StartAsync(
            "file-1", () => Task.FromException(new IOException("queue refresh failed"))));

        var item = Assert.Single(await queue.ListAsync(CancellationToken.None));
        Assert.Equal(TransferQueueState.Failed, item.State);
        Assert.Equal(SafeFailure.Storage, item.LastError);
        Assert.Equal(1, item.AttemptCount);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task TransferQueueStartGuard_PausesTaskAndKeepsCheckpointWhenRefreshIsCanceled()
    {
        Directory.CreateDirectory(_root);
        var queue = new SqliteTransferQueueStore(Path.Combine(_root, "queue-start-cancel.db"));
        await queue.EnqueueAsync("file-1", "archive.zip", 100, CancellationToken.None);
        await queue.UpdateProgressAsync("file-1", 40, 100, CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TransferQueueStartGuard(queue).StartAsync(
            "file-1", () => Task.FromCanceled(new CancellationToken(canceled: true))));

        var item = Assert.Single(await queue.ListAsync(CancellationToken.None));
        Assert.Equal(TransferQueueState.Paused, item.State);
        Assert.Equal(40, item.TransferredBytes);
        Assert.Equal(1, item.AttemptCount);
        Assert.Null(item.LastError);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task AtomicQueueStartSkipsCanceledWaitingItemAndDoesNotInvokeTransfer()
    {
        Directory.CreateDirectory(_root);
        var queue = new SqliteTransferQueueStore(Path.Combine(_root, "queue-atomic-start.db"));
        await queue.EnqueueAsync("file-1", "archive.zip", 100, CancellationToken.None);
        await queue.SetStateAsync("file-1", TransferQueueState.Cancelled, null, CancellationToken.None);
        var refreshed = false;
        var transferred = false;

        await new TransferQueueStartGuard(queue).RunAsync("file-1",
            () => { refreshed = true; return Task.CompletedTask; },
            () => { transferred = true; return Task.CompletedTask; });

        var item = Assert.Single(await queue.ListAsync(CancellationToken.None));
        Assert.Equal(TransferQueueState.Cancelled, item.State);
        Assert.Equal(0, item.AttemptCount);
        Assert.False(refreshed);
        Assert.False(transferred);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task AtomicQueueStartCanWinOnlyOnceAndRefusesTerminalState()
    {
        Directory.CreateDirectory(_root);
        var queue = new SqliteTransferQueueStore(Path.Combine(_root, "queue-single-start.db"));
        await queue.EnqueueAsync("file-1", "archive.zip", 100, CancellationToken.None);

        Assert.True(await queue.TryStartAsync("file-1", CancellationToken.None));
        Assert.False(await queue.TryStartAsync("file-1", CancellationToken.None));
        await queue.SetStateAsync("file-1", TransferQueueState.Paused, null, CancellationToken.None);
        await queue.SetStateAsync("file-1", TransferQueueState.Cancelled, null, CancellationToken.None);
        Assert.False(await queue.TryStartAsync("file-1", CancellationToken.None));

        var item = Assert.Single(await queue.ListAsync(CancellationToken.None));
        Assert.Equal(TransferQueueState.Cancelled, item.State);
        Assert.Equal(1, item.AttemptCount);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task SqliteTransferQueueStore_DeletesOnlyRequestedTasks()
    {
        Directory.CreateDirectory(_root);
        var queue = new SqliteTransferQueueStore(Path.Combine(_root, "delete-queue.db"));
        await queue.EnqueueAsync("file-1", "one.bin", 10, CancellationToken.None);
        await queue.EnqueueAsync("file-2", "two.bin", 20, CancellationToken.None);

        var items = await queue.ListAsync(CancellationToken.None);
        await queue.DeleteTasksAsync(new[] { items[0].TaskId }, CancellationToken.None);

        var remaining = Assert.Single(await queue.ListAsync(CancellationToken.None));
        Assert.Equal(items[1].TaskId, remaining.TaskId);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task SqliteTransferQueueStore_PauseAndResumeRetainCheckpointedProgress()
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "pause-resume.db");
        var queue = new SqliteTransferQueueStore(databasePath);
        await queue.EnqueueAsync("file-1", "archive.zip", 1_000, CancellationToken.None);
        await queue.SetStateAsync("file-1", TransferQueueState.Running, null, CancellationToken.None);
        await queue.UpdateProgressAsync("file-1", 450, 1_000, CancellationToken.None);

        await queue.SetStateAsync("file-1", TransferQueueState.Paused, null, CancellationToken.None);
        var paused = Assert.Single(await queue.ListAsync(CancellationToken.None));
        Assert.Equal(TransferQueueState.Paused, paused.State);
        Assert.Equal(450, paused.TransferredBytes);

        await queue.SetStateAsync("file-1", TransferQueueState.Running, null, CancellationToken.None);
        var resumed = Assert.Single(await queue.ListAsync(CancellationToken.None));
        Assert.Equal(TransferQueueState.Running, resumed.State);
        Assert.Equal(450, resumed.TransferredBytes);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task SqliteTransferQueueStore_PersistsDownloadDestinationAndAllowsSameFileUpload()
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "directions.db");
        var queue = new SqliteTransferQueueStore(databasePath);
        await queue.EnqueueAsync("file-1", "report.pdf", 900, CancellationToken.None);
        var destination = Path.Combine(_root, "downloads", "report.pdf");
        var download = await queue.EnqueueDownloadAsync("file-1", "report.pdf", destination, 900, CancellationToken.None);

        var reopened = new SqliteTransferQueueStore(databasePath);
        var items = await reopened.ListAsync(CancellationToken.None);
        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.Direction == TransferDirection.Upload && item.FileId == "file-1");
        var savedDownload = Assert.Single(items, item => item.Direction == TransferDirection.Download);
        Assert.Equal(download.TaskId, savedDownload.TaskId);
        Assert.Equal(Path.GetFullPath(destination), savedDownload.DestinationPath);
        Assert.Equal("file-1", savedDownload.FileId);
        Assert.Equal(900, savedDownload.TotalBytes);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task SqliteTransferQueueStore_MigratesLegacyUploadRowsWithoutDroppingThem()
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "legacy-queue.db");
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE TransferQueue (
                    FileId TEXT PRIMARY KEY NOT NULL,
                    FileName TEXT NOT NULL,
                    State TEXT NOT NULL,
                    AttemptCount INTEGER NOT NULL,
                    CreatedUtc TEXT NOT NULL,
                    UpdatedUtc TEXT NOT NULL,
                    LastError TEXT NULL
                );
                INSERT INTO TransferQueue VALUES ('legacy-file', 'old.bin', 'Paused', 2, '2026-01-01T00:00:00.0000000+00:00', '2026-01-02T00:00:00.0000000+00:00', 'kept');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var migrated = Assert.Single(await new SqliteTransferQueueStore(databasePath).ListAsync(CancellationToken.None));
        Assert.Equal("legacy-file", migrated.TaskId);
        Assert.Equal("legacy-file", migrated.FileId);
        Assert.Equal(TransferDirection.Upload, migrated.Direction);
        Assert.Equal("old.bin", migrated.FileName);
        Assert.Equal(TransferQueueState.Paused, migrated.State);
        Assert.Equal(2, migrated.AttemptCount);
        Assert.Equal(0, migrated.TransferredBytes);
        Assert.Equal(0, migrated.TotalBytes);
        Assert.Equal(SafeFailure.Unknown, migrated.LastError);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void TelegramSessionProfileStore_MovesAndRestoresSessionsByAccount()
    {
        Directory.CreateDirectory(_root);
        var legacy = Path.Combine(_root, "telegram-account");
        Directory.CreateDirectory(legacy);
        var sessionFile = Path.Combine(legacy, "session.dat");
        File.WriteAllText(sessionFile, "account 101 session fixture");

        var accountASession = TelegramSessionProfileStore.MoveToAccountProfile(_root, "101", legacy);

        Assert.False(Directory.Exists(legacy));
        Assert.True(TelegramSessionProfileStore.IsAccountProfileDirectory(_root, accountASession));
        Assert.Equal("account 101 session fixture", File.ReadAllText(Path.Combine(accountASession, "session.dat")));
        Assert.Equal(Path.GetFullPath(accountASession), Path.GetFullPath(TelegramSessionProfileStore.ResolveForRestore(_root)));

        var bootstrap = TelegramSessionProfileStore.CreateSignInDirectory(_root);
        Assert.False(TelegramSessionProfileStore.IsAccountProfileDirectory(_root, bootstrap));
        Assert.Equal(Path.GetFullPath(bootstrap), Path.GetFullPath(TelegramSessionProfileStore.ResolveForRestore(_root)));
        File.WriteAllText(Path.Combine(bootstrap, "session.dat"), "account 202 session fixture");
        var accountBSession = TelegramSessionProfileStore.MoveToAccountProfile(_root, "202", bootstrap);

        Assert.NotEqual(Path.GetFullPath(accountASession), Path.GetFullPath(accountBSession));
        Assert.Equal("account 101 session fixture", File.ReadAllText(Path.Combine(accountASession, "session.dat")));
        Assert.Equal("account 202 session fixture", File.ReadAllText(Path.Combine(accountBSession, "session.dat")));
        Assert.Equal(Path.GetFullPath(accountBSession), Path.GetFullPath(TelegramSessionProfileStore.ResolveForRestore(_root)));
    }

    [Fact]
    public void TelegramSessionProfileStore_ClearsOptOutPointersAndDeletesOnlySessionData()
    {
        Directory.CreateDirectory(_root);
        var loginSession = TelegramSessionProfileStore.CreateSignInDirectory(_root);
        TelegramSessionProfileStore.ClearSignInSessionPointer(_root, loginSession);
        Assert.NotEqual(Path.GetFullPath(loginSession), Path.GetFullPath(TelegramSessionProfileStore.ResolveForRestore(_root)));
        TelegramSessionProfileStore.DiscardSessionDirectory(_root, null, loginSession);
        Assert.False(Directory.Exists(loginSession));

        var legacySession = Path.Combine(_root, "telegram-account");
        Directory.CreateDirectory(legacySession);
        var accountSession = TelegramSessionProfileStore.MoveToAccountProfile(_root, "303", legacySession, persistForRestore: false);
        var accountData = Path.Combine(TelegramAccountProfileStore.GetDirectory(_root, "303"), "manifests.db");
        File.WriteAllText(accountData, "preserve account data");
        Assert.NotEqual(Path.GetFullPath(accountSession), Path.GetFullPath(TelegramSessionProfileStore.ResolveForRestore(_root)));

        TelegramSessionProfileStore.DiscardSessionDirectory(_root, "303", accountSession);

        Assert.False(Directory.Exists(accountSession));
        Assert.Equal("preserve account data", File.ReadAllText(accountData));
    }

    [Fact]
    public void TelegramSessionProfileStore_UsesActivePointerWhenAccountHasOldSessions()
    {
        Directory.CreateDirectory(_root);
        var legacy = Path.Combine(_root, "telegram-account");
        Directory.CreateDirectory(legacy);
        var activeSession = TelegramSessionProfileStore.MoveToAccountProfile(_root, "6269849833", legacy);
        var sessionsRoot = Path.Combine(TelegramAccountProfileStore.GetDirectory(_root, "6269849833"), "tdlib");
        Directory.CreateDirectory(Path.Combine(sessionsRoot, "11111111111111111111111111111111"));

        Assert.Equal(Path.GetFullPath(activeSession), Path.GetFullPath(
            TelegramSessionProfileStore.ResolveForRestore(_root, "6269849833")));
    }

    [Fact]
    public void TelegramSessionProfileStore_IgnoresEmptyOldDirectoriesWhenResolvingOtherAccount()
    {
        Directory.CreateDirectory(_root);
        var legacyA = Path.Combine(_root, "telegram-account");
        Directory.CreateDirectory(legacyA);
        File.WriteAllText(Path.Combine(legacyA, "session.dat"), "account 101 fixture");
        var accountASession = TelegramSessionProfileStore.MoveToAccountProfile(_root, "101", legacyA);
        var sessionsRoot = Path.Combine(TelegramAccountProfileStore.GetDirectory(_root, "101"), "tdlib");
        Directory.CreateDirectory(Path.Combine(sessionsRoot, "11111111111111111111111111111111"));
        Directory.CreateDirectory(Path.Combine(sessionsRoot, "22222222222222222222222222222222"));

        var legacyB = Path.Combine(_root, "telegram-login", "login-session");
        Directory.CreateDirectory(legacyB);
        File.WriteAllText(Path.Combine(legacyB, "session.dat"), "account 202 fixture");
        TelegramSessionProfileStore.MoveToAccountProfile(_root, "202", legacyB);

        Assert.Equal(Path.GetFullPath(accountASession), Path.GetFullPath(
            TelegramSessionProfileStore.ResolveForRestore(_root, "101")));
    }

    [Fact]
    public async Task AccountProfileDataMigrator_RetainsSharedSourceWhenPartIntegrityFails()
    {
        Directory.CreateDirectory(_root);
        var sharedDb = Path.Combine(_root, "shared-integrity.db");
        var accountDb = Path.Combine(_root, "accounts", "101", "profile.db");
        var sharedStaging = Path.Combine(_root, "shared-staging");
        var accountStaging = Path.Combine(_root, "accounts", "101", "staging");
        Directory.CreateDirectory(sharedStaging);
        var wrongBytes = new byte[] { 1, 1, 1, 1 };
        var expectedBytes = new byte[] { 2, 2, 2, 2 };
        var sourcePath = Path.Combine(sharedStaging, "corrupt.part");
        await File.WriteAllBytesAsync(sourcePath, wrongBytes);
        var manifest = new FileManifest(1, "integrity-file", "integrity.bin", expectedBytes.Length,
            Convert.ToHexString(SHA256.HashData(expectedBytes)), expectedBytes.Length,
            new[] { new PartRecord(0, 0, expectedBytes.Length, Convert.ToHexString(SHA256.HashData(expectedBytes)), null, false, sourcePath) }, false, "101");
        var sharedManifests = new SqliteManifestStore(sharedDb);
        await sharedManifests.SaveAsync(manifest, CancellationToken.None);
        var sharedQueue = new SqliteTransferQueueStore(sharedDb);
        await sharedQueue.EnqueueAsync(manifest.FileId, manifest.FileName, manifest.LogicalSize, CancellationToken.None);
        var sharedCheckpoints = new SqliteRemoteSyncCheckpointStore(sharedDb);
        var cache = new LocalCacheVerificationStore(Path.Combine(_root, "cache.json"));
        var migrator = new AccountProfileDataMigrator(
            sharedManifests, new SqliteManifestStore(accountDb), sharedQueue, new SqliteTransferQueueStore(accountDb),
            sharedCheckpoints, new SqliteRemoteSyncCheckpointStore(accountDb), cache, sharedStaging, accountStaging);

        Exception? migrationError = null;
        try { await migrator.MigrateAsync("101", CancellationToken.None); }
        catch (Exception ex) { migrationError = ex; }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert.IsType<InvalidDataException>(migrationError);

        Assert.NotNull(await sharedManifests.LoadAsync(manifest.FileId, CancellationToken.None));
        Assert.Equal(manifest.FileId, Assert.Single(await sharedQueue.ListAsync(CancellationToken.None)).FileId);
        Assert.Null(await new SqliteManifestStore(accountDb).LoadAsync(manifest.FileId, CancellationToken.None));
        Assert.Equal(wrongBytes, await File.ReadAllBytesAsync(sourcePath));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task AccountProfileStores_ImportQueueAndCheckpointsIntoSeparateDatabases()
    {
        Directory.CreateDirectory(_root);
        var sharedPath = Path.Combine(_root, "shared.db");
        var accountPath = Path.Combine(_root, "accounts", "101", "profile.db");
        var sharedQueue = new SqliteTransferQueueStore(sharedPath);
        await sharedQueue.EnqueueAsync("file-A", "a.bin", 500, CancellationToken.None);
        await sharedQueue.SetStateAsync("file-A", TransferQueueState.Running, null, CancellationToken.None);
        await sharedQueue.UpdateProgressAsync("file-A", 125, 500, CancellationToken.None);
        await sharedQueue.EnqueueAsync("file-B", "b.bin", 700, CancellationToken.None);
        var accountQueue = new SqliteTransferQueueStore(accountPath);
        var selectedQueueItems = (await sharedQueue.ListAsync(CancellationToken.None))
            .Where(item => item.FileId == "file-A").ToArray();
        await accountQueue.ImportItemsAsync(selectedQueueItems, CancellationToken.None);
        await accountQueue.RecoverInterruptedAsync(CancellationToken.None);
        await sharedQueue.DeleteItemsForFilesAsync(new[] { "file-A" }, CancellationToken.None);

        var accountQueueItem = Assert.Single(await accountQueue.ListAsync(CancellationToken.None));
        Assert.Equal("file-A", accountQueueItem.FileId);
        Assert.Equal(TransferQueueState.Paused, accountQueueItem.State);
        Assert.Equal(125, accountQueueItem.TransferredBytes);
        Assert.Equal(500, accountQueueItem.TotalBytes);
        Assert.Single(await sharedQueue.ListAsync(CancellationToken.None));
        Assert.Equal("file-B", (await sharedQueue.ListAsync(CancellationToken.None))[0].FileId);

        var sharedCheckpoints = new SqliteRemoteSyncCheckpointStore(sharedPath);
        var accountCheckpoints = new SqliteRemoteSyncCheckpointStore(accountPath);
        var time = DateTimeOffset.Parse("2026-10-02T12:30:00Z");
        await sharedCheckpoints.SaveAsync("101", -1001, new RemoteSyncCheckpoint(123, time), CancellationToken.None);
        await sharedCheckpoints.SaveAsync("202", -2002, new RemoteSyncCheckpoint(456, time), CancellationToken.None);
        foreach (var (chatId, checkpoint) in await sharedCheckpoints.ListAccountAsync("101", CancellationToken.None))
            await accountCheckpoints.SaveAsync("101", chatId, checkpoint, CancellationToken.None);
        await sharedCheckpoints.DeleteAccountAsync("101", CancellationToken.None);

        Assert.Equal(new RemoteSyncCheckpoint(123, time), await accountCheckpoints.LoadAsync("101", -1001, CancellationToken.None));
        Assert.Null(await sharedCheckpoints.LoadAsync("101", -1001, CancellationToken.None));
        Assert.Equal(new RemoteSyncCheckpoint(456, time), await sharedCheckpoints.LoadAsync("202", -2002, CancellationToken.None));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task AccountProfileDataMigrator_MovesOnlyCurrentAccountDataAndVerifiesStagedParts()
    {
        Directory.CreateDirectory(_root);
        var sharedDb = Path.Combine(_root, "shared-profile.db");
        var accountDb = Path.Combine(_root, "profiles", "101", "profile.db");
        var sharedStaging = Path.Combine(_root, "shared-staging");
        var accountStaging = Path.Combine(_root, "profiles", "101", "staging");
        Directory.CreateDirectory(sharedStaging);
        var partBytes = new byte[] { 8, 6, 7, 5, 3, 0, 9 };
        var sourcePartPath = Path.Combine(sharedStaging, "draft.part");
        await File.WriteAllBytesAsync(sourcePartPath, partBytes);
        var partHash = Convert.ToHexString(SHA256.HashData(partBytes));
        var hashEmpty = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>()));
        var owned = new FileManifest(1, "owned-101", "owned.bin", partBytes.Length, partHash, partBytes.Length,
            new[] { new PartRecord(0, 0, partBytes.Length, partHash, null, false, sourcePartPath) }, false, "101");
        var encryptedPlaintext = new byte[] { 4, 2, 4, 2 };
        var encryptedSourcePath = Path.Combine(sharedStaging, "encrypted-payload.bin");
        var encryptedPartPath = Path.Combine(sharedStaging, "encrypted-part.bin");
        var fileKey = AesGcmFileCipher.CreateFileKey();
        var recoveryKey = AesGcmFileCipher.WrapFileKey(fileKey, "correct horse battery staple");
        using (var encrypted = new FileStream(encryptedSourcePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            await AesGcmFileCipher.EncryptAsync(new MemoryStream(encryptedPlaintext), encrypted, fileKey);
        CryptographicOperations.ZeroMemory(fileKey);
        var encryptedPayload = await File.ReadAllBytesAsync(encryptedSourcePath);
        await File.WriteAllBytesAsync(encryptedPartPath, encryptedPayload);
        var encryptedPayloadHash = Convert.ToHexString(SHA256.HashData(encryptedPayload));
        var encryptedManifest = new FileManifest(1, "encrypted-101", "encrypted.txt", encryptedPlaintext.Length,
            Convert.ToHexString(SHA256.HashData(encryptedPlaintext)), encryptedPayload.Length, new[]
            {
                new PartRecord(0, 0, encryptedPayload.Length, encryptedPayloadHash, null, false, encryptedPartPath)
            }, false, "101", Encryption: new EncryptedPayloadDescriptor(1, encryptedPayload.Length,
                encryptedPayloadHash, recoveryKey, encryptedSourcePath));
        var other = new FileManifest(1, "owned-202", "other.bin", 0, hashEmpty, 1,
            new[] { new PartRecord(0, 0, 0, hashEmpty, null, false) }, false, "202");
        var draft = new FileManifest(1, "unbound-draft", "draft.bin", 0, hashEmpty, 1,
            new[] { new PartRecord(0, 0, 0, hashEmpty, null, false) }, false);
        var sharedManifestStore = new SqliteManifestStore(sharedDb);
        await sharedManifestStore.SaveAsync(owned, CancellationToken.None);
        await sharedManifestStore.SaveAsync(other, CancellationToken.None);
        await sharedManifestStore.SaveAsync(draft, CancellationToken.None);
        var sharedQueue = new SqliteTransferQueueStore(sharedDb);
        await sharedQueue.EnqueueAsync(owned.FileId, owned.FileName, owned.LogicalSize, CancellationToken.None);
        await sharedQueue.EnqueueAsync(other.FileId, other.FileName, other.LogicalSize, CancellationToken.None);
        var sharedCheckpoints = new SqliteRemoteSyncCheckpointStore(sharedDb);
        var syncedAt = DateTimeOffset.Parse("2026-10-02T12:30:00Z");
        await sharedCheckpoints.SaveAsync("101", -101, new RemoteSyncCheckpoint(111, syncedAt), CancellationToken.None);
        await sharedCheckpoints.SaveAsync("202", -202, new RemoteSyncCheckpoint(222, syncedAt), CancellationToken.None);
        var sharedCache = new LocalCacheVerificationStore(Path.Combine(_root, "shared-cache.json"));
        await sharedCache.VerifyAndSaveAsync(owned, CancellationToken.None);
        await sharedCache.VerifyAndSaveAsync(other, CancellationToken.None);
        await sharedCache.VerifyAndSaveAsync(draft, CancellationToken.None);

        var profileManifestStore = new SqliteManifestStore(accountDb);
        var profileQueueStore = new SqliteTransferQueueStore(accountDb);
        var profileCheckpointStore = new SqliteRemoteSyncCheckpointStore(accountDb);
        var migrator = new AccountProfileDataMigrator(
            sharedManifestStore, profileManifestStore, sharedQueue, profileQueueStore,
            sharedCheckpoints, profileCheckpointStore, sharedCache, sharedStaging, accountStaging);
        await sharedManifestStore.SaveAsync(encryptedManifest, CancellationToken.None);
        await sharedCache.VerifyAndSaveAsync(encryptedManifest, CancellationToken.None);
        await sharedQueue.EnqueueAsync(encryptedManifest.FileId, encryptedManifest.FileName, encryptedManifest.TransferSize, CancellationToken.None);
        var result = await migrator.MigrateAsync("101", CancellationToken.None);

        Assert.Equal(3, result.MigratedManifestCount);
        Assert.Equal(2, result.MigratedQueueItemCount);
        Assert.Equal(1, result.MigratedCheckpointCount);
        var accountManifests = await profileManifestStore.ListAsync(CancellationToken.None);
        Assert.Equal(3, accountManifests.Count);
        Assert.All(accountManifests, item => Assert.Equal("101", item.AccountId));
        var migratedOwned = await profileManifestStore.LoadAsync(owned.FileId, CancellationToken.None);
        var migratedPart = Assert.Single(migratedOwned!.Parts);
        Assert.NotEqual(sourcePartPath, migratedPart.StagingPath);
        Assert.Equal(partBytes, await File.ReadAllBytesAsync(migratedPart.StagingPath!));
        var migratedEncrypted = (await profileManifestStore.LoadAsync(encryptedManifest.FileId, CancellationToken.None))!;
        Assert.NotEqual(encryptedSourcePath, migratedEncrypted.Encryption!.StagingPath);
        Assert.Equal(encryptedPayload, await File.ReadAllBytesAsync(migratedEncrypted.Encryption.StagingPath!));
        Assert.False(File.Exists(encryptedSourcePath));
        Assert.False(File.Exists(encryptedPartPath));
        Assert.False(File.Exists(sourcePartPath));
        Assert.Null(await sharedManifestStore.LoadAsync(owned.FileId, CancellationToken.None));
        Assert.NotNull(await sharedManifestStore.LoadAsync(other.FileId, CancellationToken.None));
        Assert.Null(await sharedManifestStore.LoadAsync(draft.FileId, CancellationToken.None));
        Assert.Equal(new[] { encryptedManifest.FileId, owned.FileId }, (await profileQueueStore.ListAsync(CancellationToken.None)).Select(item => item.FileId).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(other.FileId, Assert.Single(await sharedQueue.ListAsync(CancellationToken.None)).FileId);
        Assert.Equal(new RemoteSyncCheckpoint(111, syncedAt), await profileCheckpointStore.LoadAsync("101", -101, CancellationToken.None));
        Assert.Null(await sharedCheckpoints.LoadAsync("101", -101, CancellationToken.None));
        Assert.Equal(new RemoteSyncCheckpoint(222, syncedAt), await sharedCheckpoints.LoadAsync("202", -202, CancellationToken.None));
        var cacheEntries = await sharedCache.LoadAllAsync(CancellationToken.None);
        Assert.DoesNotContain(owned.FileId, cacheEntries.Keys);
        Assert.DoesNotContain(draft.FileId, cacheEntries.Keys);
        Assert.Contains(other.FileId, cacheEntries.Keys);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task SqliteManifestStore_DeleteManyRemovesOnlyMigratedRecords()
    {
        Directory.CreateDirectory(_root);
        var store = new SqliteManifestStore(Path.Combine(_root, "manifest-delete.db"));
        var hash = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>()));
        foreach (var id in new[] { "owned-101", "owned-202" })
        {
            var manifest = new FileManifest(1, id, $"{id}.bin", 0, hash, 1,
                new[] { new PartRecord(0, 0, 0, hash, null, false) }, false);
            await store.SaveAsync(manifest, CancellationToken.None);
        }

        await store.DeleteManyAsync(new[] { "owned-101", "missing" }, CancellationToken.None);

        Assert.Null(await store.LoadAsync("owned-101", CancellationToken.None));
        Assert.NotNull(await store.LoadAsync("owned-202", CancellationToken.None));
        Assert.Single(await store.ListAsync(CancellationToken.None));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task SqliteLocalFolderStore_PersistsEmptyFoldersAndKeepsAccountsSeparate()
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "folders.db");
        var store = new SqliteLocalFolderStore(databasePath);

        await store.CreateAsync("account-a", "Empty/Nested", CancellationToken.None);
        await store.CreateAsync("account-a", "Empty/Nested", CancellationToken.None);
        await store.CreateAsync("account-b", "Empty/Nested", CancellationToken.None);

        Assert.Equal(new[] { "Empty", "Empty/Nested" }, (await new SqliteLocalFolderStore(databasePath).ListAsync("account-a", CancellationToken.None)).Select(folder => folder.Path));
        Assert.Equal(2, (await store.ListAsync("account-b", CancellationToken.None)).Count);
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync("account-a", "../outside", CancellationToken.None));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task SqliteLocalFolderStore_RenamesAndDeletesSubtreesWithReusableTombstones()
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "folder-tombstones.db");
        var store = new SqliteLocalFolderStore(databasePath);
        await store.CreateAsync("account-a", "Old/Nested", CancellationToken.None);

        await store.RenameAsync("account-a", "Old", "New", CancellationToken.None);

        Assert.Equal(new[] { "New", "New/Nested" },
            (await store.ListAsync("account-a", CancellationToken.None)).Select(folder => folder.Path).OrderBy(path => path, StringComparer.Ordinal));
        Assert.Equal(new[] { "Old", "Old/Nested" },
            (await store.ListTombstonesAsync("account-a", CancellationToken.None)).Select(item => item.Path).OrderBy(path => path, StringComparer.Ordinal));

        await store.DeleteAsync("account-a", "New", CancellationToken.None);
        Assert.Empty(await store.ListAsync("account-a", CancellationToken.None));
        Assert.Equal(new[] { "New", "New/Nested", "Old", "Old/Nested" },
            (await store.ListTombstonesAsync("account-a", CancellationToken.None)).Select(item => item.Path).OrderBy(path => path, StringComparer.Ordinal));

        await store.CreateAsync("account-a", "New/Nested", CancellationToken.None);
        Assert.Equal(new[] { "New", "New/Nested" },
            (await store.ListAsync("account-a", CancellationToken.None)).Select(folder => folder.Path).OrderBy(path => path, StringComparer.Ordinal));
        Assert.DoesNotContain(await store.ListTombstonesAsync("account-a", CancellationToken.None), item => item.Path is "New" or "New/Nested");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task SqliteLocalFolderStore_RemoteTombstoneRemovesOldSubtreeAndLaterFolderStateRecreatesIt()
    {
        Directory.CreateDirectory(_root);
        var store = new SqliteLocalFolderStore(Path.Combine(_root, "remote-folder-tombstones.db"));
        await store.CreateAsync("account-a", "Reports/Old", CancellationToken.None);
        var deletion = DateTimeOffset.Parse("2026-10-04T00:00:00Z");

        await store.MergeRemoteAsync("account-a", Array.Empty<LocalFolder>(),
            new[] { new FolderTombstone("account-a", "Reports", deletion) }, CancellationToken.None);

        Assert.Empty(await store.ListAsync("account-a", CancellationToken.None));
        Assert.Equal(deletion, Assert.Single(await store.ListTombstonesAsync("account-a", CancellationToken.None)).DeletedAtUtc);

        var recreated = new LocalFolder("account-a", "Reports", deletion.AddMinutes(1));
        await store.MergeRemoteAsync("account-a", new[] { recreated }, Array.Empty<FolderTombstone>(), CancellationToken.None);
        Assert.Equal("Reports", Assert.Single(await store.ListAsync("account-a", CancellationToken.None)).Path);
        Assert.Empty(await store.ListTombstonesAsync("account-a", CancellationToken.None));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task FolderManagement_RenameUpdatesNestedCommittedManifestsAndPreservesParts()
    {
        Directory.CreateDirectory(_root);
        var db = Path.Combine(_root, "folder-management-rename.db");
        var folders = new SqliteLocalFolderStore(db);
        var manifests = new SqliteManifestStore(db);
        var queue = new SqliteTransferQueueStore(db);
        var hash = Convert.ToHexString(SHA256.HashData(new byte[] { 1 }));
        var manifest = new FileManifest(1, "file-a", "file.bin", 1, hash, 1,
            new[] { new PartRecord(0, 0, 1, hash, "-100/123", true) }, true,
            AccountId: "account-a", FolderPath: "Old/Nested");
        await manifests.SaveAsync(manifest, CancellationToken.None);
        var published = new List<FileManifest>();
        var service = new FolderManagementService(folders, manifests, queue,
            (item, _) => { published.Add(item); return Task.CompletedTask; }, _ => Task.CompletedTask,
            new FolderOperationJournal(Path.Combine(_root, "folder-journal")));

        var result = await service.RenameAsync("account-a", "Old", "New", CancellationToken.None);

        var saved = await manifests.LoadAsync("file-a", CancellationToken.None);
        Assert.Equal("New/Nested", saved!.FolderPath);
        Assert.Equal(manifest.Parts, saved.Parts);
        Assert.Equal("New/Nested", Assert.Single(published).FolderPath);
        Assert.True(result.FolderStatePublished);
        Assert.Contains(await folders.ListTombstonesAsync("account-a", CancellationToken.None), item => item.Path == "Old");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task FolderManagement_DeleteMovesFilesToParentAndActiveTransferBlocksMutation()
    {
        Directory.CreateDirectory(_root);
        var db = Path.Combine(_root, "folder-management-delete.db");
        var folders = new SqliteLocalFolderStore(db);
        var manifests = new SqliteManifestStore(db);
        var queue = new SqliteTransferQueueStore(db);
        var hash = Convert.ToHexString(SHA256.HashData(new byte[] { 1 }));
        var manifest = new FileManifest(1, "file-a", "file.bin", 1, hash, 1,
            new[] { new PartRecord(0, 0, 1, hash, "-100/123", true) }, true,
            AccountId: "account-a", FolderPath: "Parent/Old/Nested");
        await manifests.SaveAsync(manifest, CancellationToken.None);
        await folders.CreateAsync("account-a", "Parent/Old/Nested", CancellationToken.None);
        var published = new List<FileManifest>();
        var service = new FolderManagementService(folders, manifests, queue,
            (item, _) => { published.Add(item); return Task.CompletedTask; }, _ => Task.CompletedTask,
            new FolderOperationJournal(Path.Combine(_root, "folder-journal")));

        var result = await service.DeleteAsync("account-a", "Parent/Old", CancellationToken.None);

        Assert.Equal("Parent", (await manifests.LoadAsync("file-a", CancellationToken.None))!.FolderPath);
        Assert.Equal("Parent", Assert.Single(published).FolderPath);
        Assert.True(result.FolderStatePublished);
        Assert.Contains(await folders.ListTombstonesAsync("account-a", CancellationToken.None), item => item.Path == "Parent/Old");

        await folders.CreateAsync("account-a", "Parent/Busy", CancellationToken.None);
        var pending = await queue.EnqueueDownloadAsync("file-a", "file.bin", Path.Combine(_root, "file.bin"), 1, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync("account-a", "Parent", CancellationToken.None));
        Assert.Equal("Parent", (await manifests.LoadAsync("file-a", CancellationToken.None))!.FolderPath);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ManifestBulkActions_RequireMatchingAccountAndPublishEachMetadataRevision()
    {
        Directory.CreateDirectory(_root);
        var store = new SqliteManifestStore(Path.Combine(_root, "bulk.db"));
        var hash = Convert.ToHexString(SHA256.HashData(new byte[] { 1 }));
        foreach (var (id, account) in new[] { ("mine", "account-a"), ("theirs", "account-b") })
            await store.SaveAsync(new FileManifest(1, id, id + ".bin", 1, hash, 1,
                new[] { new PartRecord(0, 0, 1, hash, "-100/123", true) }, true, AccountId: account), CancellationToken.None);
        var published = new List<string>();
        var actions = new ManifestBulkActions(store, (manifest, _) => { published.Add(manifest.FileId); return Task.CompletedTask; });

        var results = await actions.ExecuteAsync(new[] { "mine", "theirs", "missing" }, BulkFileAction.Move, "Empty", "account-a", CancellationToken.None);

        Assert.Equal(new[] { "mine" }, published);
        Assert.Equal(new[] { true, false, false }, results.Select(result => result.Succeeded));
        Assert.Equal("Empty", (await store.LoadAsync("mine", CancellationToken.None))!.FolderPath);
        Assert.Equal(string.Empty, (await store.LoadAsync("theirs", CancellationToken.None))!.FolderPath);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void ManifestMetadata_FavoriteArchiveAndHiddenAreVersionedWithoutChangingFileParts()
    {
        var manifest = CreatePortableManifest("flags", "flags.bin", "-10055/20", new byte[] { 7, 8, 9 }, "account-flags");
        var original = JsonSerializer.Deserialize<FileManifest>(manifest.Bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var favorite = FileManifestMetadata.SetFavorite(original, true);
        var archived = FileManifestMetadata.SetArchived(favorite, true);
        var hidden = FileManifestMetadata.SetHidden(archived, true);

        Assert.True(hidden.IsFavorite);
        Assert.True(hidden.IsArchived);
        Assert.True(hidden.IsHidden);
        Assert.Equal(original.Revision + 3, hidden.Revision);
        Assert.Equal(original.Parts, hidden.Parts);
        Assert.False(original.IsFavorite || original.IsArchived || original.IsHidden);
        var roundTrip = JsonSerializer.Deserialize<FileManifest>(
            JsonSerializer.SerializeToUtf8Bytes(hidden, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(roundTrip.IsFavorite && roundTrip.IsArchived && roundTrip.IsHidden);
        Assert.Throws<InvalidOperationException>(() => FileManifestMetadata.SetHidden(original with { Committed = false }, true));
    }

    [Fact]
    public async Task TelegramRemoteFileDeleter_DeletesOnlyVerifiedTrashRevisionsAndPartsInActiveChat()
    {
        Directory.CreateDirectory(_root);
        var partBytes = new byte[] { 42 };
        var hash = Convert.ToHexString(SHA256.HashData(partBytes));
        var manifest = new FileManifest(1, "delete-me", "delete-me.bin", 1, hash, 1,
            new[] { new PartRecord(0, 0, 1, hash, "-100/12", true) }, true,
            AccountId: "account-a", Revision: 1, IsInTrash: true);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes));
        var requests = new FakeTelegramRequestClient(
            new JsonObject { ["@type"] = "messages", ["messages"] = new JsonArray(new JsonObject
            {
                ["id"] = 100, ["chat_id"] = -100,
                ["content"] = new JsonObject { ["@type"] = "messageDocument", ["caption"] = new JsonObject { ["text"] = $"TSC-MANIFEST|1|delete-me|{manifestHash}" } }
            }) },
            new JsonObject { ["@type"] = "messages", ["messages"] = new JsonArray() },
            new JsonObject { ["@type"] = "message", ["id"] = 12, ["chat_id"] = -100,
                ["content"] = new JsonObject { ["caption"] = new JsonObject { ["text"] = "TSC-PART|1|delete-me|0" },
                    ["document"] = new JsonObject { ["document"] = new JsonObject { ["id"] = 5 } } } },
            new JsonObject { ["@type"] = "ok" });
        var transport = new FakePartTransport(new Dictionary<string, byte[]> { ["-100/100"] = manifestBytes });

        var count = await new TelegramRemoteFileDeleter(requests, transport, -100, "account-a")
            .DeleteTrashedFileAsync(manifest, CancellationToken.None);

        Assert.Equal(2, count);
        var deletion = Assert.Single(requests.Requests, request => request["@type"]?.GetValue<string>() == "deleteMessages");
        Assert.Equal(-100, deletion["chat_id"]?.GetValue<long>());
        Assert.True(deletion["revoke"]?.GetValue<bool>());
        Assert.Equal(new long[] { 12, 100 }, deletion["message_ids"]!.AsArray().Select(id => id!.GetValue<long>()));
    }

    [Fact]
    public async Task TelegramRemoteFileDeleter_RejectsForeignChatReferenceBeforeDeletingAnything()
    {
        var bytes = new byte[] { 42 };
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var manifest = new FileManifest(1, "foreign", "foreign.bin", 1, hash, 1,
            new[] { new PartRecord(0, 0, 1, hash, "-200/12", true) }, true,
            AccountId: "account-a", Revision: 1, IsInTrash: true);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes));
        var requests = new FakeTelegramRequestClient(
            new JsonObject { ["@type"] = "messages", ["messages"] = new JsonArray(new JsonObject
            {
                ["id"] = 100, ["chat_id"] = -100,
                ["content"] = new JsonObject { ["caption"] = new JsonObject { ["text"] = $"TSC-MANIFEST|1|foreign|{manifestHash}" } }
            }) },
            new JsonObject { ["@type"] = "messages", ["messages"] = new JsonArray() });

        await Assert.ThrowsAsync<InvalidDataException>(() => new TelegramRemoteFileDeleter(
            requests, new FakePartTransport(new Dictionary<string, byte[]> { ["-100/100"] = manifestBytes }), -100, "account-a")
            .DeleteTrashedFileAsync(manifest, CancellationToken.None));
        Assert.DoesNotContain(requests.Requests, request => request["@type"]?.GetValue<string>() == "deleteMessages");
    }

    [Fact]
    public async Task PermanentFileDeletion_RefusesFilesWithQueuedTransfersBeforeCallingTelegram()
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "delete-active-queue.db");
        var manifests = new SqliteManifestStore(databasePath);
        var queue = new SqliteTransferQueueStore(databasePath);
        var hash = Convert.ToHexString(SHA256.HashData(new byte[] { 42 }));
        var manifest = new FileManifest(1, "queued-trash", "queued-trash.bin", 1, hash, 1,
            new[] { new PartRecord(0, 0, 1, hash, "-100/12", true) }, true,
            AccountId: "account-a", Revision: 1, IsInTrash: true);
        await manifests.SaveAsync(manifest, CancellationToken.None);
        await queue.EnqueueAsync(manifest.FileId, manifest.FileName, manifest.LogicalSize, CancellationToken.None);
        var requests = new FakeTelegramRequestClient();
        var remote = new TelegramRemoteFileDeleter(requests,
            new FakePartTransport(new Dictionary<string, byte[]>()), -100, "account-a");

        var result = Assert.Single(await new PermanentFileDeletion(manifests, queue, remote)
            .ExecuteAsync(new[] { manifest.FileId }, "account-a", CancellationToken.None));

        Assert.False(result.Succeeded);
        Assert.False(result.RemoteDeletionCompleted);
        Assert.NotNull(await manifests.LoadAsync(manifest.FileId, CancellationToken.None));
        Assert.Equal(0, requests.RequestCount);
        Assert.Single(await queue.ListAsync(CancellationToken.None));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task PermanentFileDeletion_KeepsLocalManifestWhenTelegramDoesNotConfirmDelete()
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "delete-unconfirmed.db");
        var manifests = new SqliteManifestStore(databasePath);
        var queue = new SqliteTransferQueueStore(databasePath);
        var partBytes = new byte[] { 42 };
        var hash = Convert.ToHexString(SHA256.HashData(partBytes));
        var manifest = new FileManifest(1, "unconfirmed-trash", "unconfirmed-trash.bin", 1, hash, 1,
            new[] { new PartRecord(0, 0, 1, hash, "-100/12", true) }, true,
            AccountId: "account-a", Revision: 1, IsInTrash: true);
        await manifests.SaveAsync(manifest, CancellationToken.None);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes));
        var requests = new FakeTelegramRequestClient(
            new JsonObject { ["@type"] = "messages", ["messages"] = new JsonArray(new JsonObject
            {
                ["id"] = 100, ["chat_id"] = -100,
                ["content"] = new JsonObject { ["caption"] = new JsonObject { ["text"] = $"TSC-MANIFEST|1|unconfirmed-trash|{manifestHash}" } }
            }) },
            new JsonObject { ["@type"] = "messages", ["messages"] = new JsonArray() },
            new JsonObject { ["@type"] = "message", ["id"] = 12, ["chat_id"] = -100,
                ["content"] = new JsonObject { ["caption"] = new JsonObject { ["text"] = "TSC-PART|1|unconfirmed-trash|0" },
                    ["document"] = new JsonObject { ["document"] = new JsonObject { ["id"] = 5 } } } },
            new JsonObject { ["@type"] = "error", ["code"] = 500, ["message"] = "simulated delete failure" });
        var remote = new TelegramRemoteFileDeleter(requests,
            new FakePartTransport(new Dictionary<string, byte[]> { ["-100/100"] = manifestBytes }), -100, "account-a");

        var result = Assert.Single(await new PermanentFileDeletion(manifests, queue, remote)
            .ExecuteAsync(new[] { manifest.FileId }, "account-a", CancellationToken.None));

        Assert.False(result.Succeeded);
        Assert.False(result.RemoteDeletionCompleted);
        Assert.NotNull(await manifests.LoadAsync(manifest.FileId, CancellationToken.None));
        Assert.Contains(requests.Requests, request => request["@type"]?.GetValue<string>() == "deleteMessages");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    public void TelegramRemoteMessageId_RejectsInvalidReferences(long chatId, long messageId)
    {
        Assert.Throws<InvalidDataException>(() => TelegramRemoteMessageId.Parse($"{chatId}/{messageId}"));
    }

    [Fact]
    public async Task SqliteRemoteSyncCheckpointStore_PersistsSeparatelyPerAccountAndChannel()
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "sync-state.db");
        var store = new SqliteRemoteSyncCheckpointStore(databasePath);
        var time = DateTimeOffset.Parse("2026-10-02T12:30:00Z");
        await store.SaveAsync("account-A", -1001, new RemoteSyncCheckpoint(12345, time), CancellationToken.None);
        await store.SaveAsync("account-A", -1002, new RemoteSyncCheckpoint(67890, time.AddMinutes(1)), CancellationToken.None);
        await store.SaveAsync("account-B", -1001, new RemoteSyncCheckpoint(333, time.AddMinutes(2)), CancellationToken.None);

        var reopened = new SqliteRemoteSyncCheckpointStore(databasePath);
        Assert.Equal(new RemoteSyncCheckpoint(12345, time), await reopened.LoadAsync("account-A", -1001, CancellationToken.None));
        Assert.Equal(new RemoteSyncCheckpoint(67890, time.AddMinutes(1)), await reopened.LoadAsync("account-A", -1002, CancellationToken.None));
        Assert.Equal(new RemoteSyncCheckpoint(333, time.AddMinutes(2)), await reopened.LoadAsync("account-B", -1001, CancellationToken.None));
        Assert.Null(await reopened.LoadAsync("account-C", -1001, CancellationToken.None));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task RemoteManifestCatalog_IncrementalSyncStopsAtCheckpoint()
    {
        Directory.CreateDirectory(_root);
        const long chatId = -10055;
        const string accountId = "account-A";
        var checkpointStore = new SqliteRemoteSyncCheckpointStore(Path.Combine(_root, "incremental-sync.db"));
        await checkpointStore.SaveAsync(accountId, chatId,
            new RemoteSyncCheckpoint(10, DateTimeOffset.Parse("2026-10-01T12:00:00Z")), CancellationToken.None);
        var first = CreatePortableManifest("file-A", "alpha.txt", "-10055/20", new byte[] { 1, 2, 3 }, accountId);
        var second = CreatePortableManifest("file-B", "beta.txt", "-10055/15", new byte[] { 4, 5, 6 }, accountId);
        var requestClient = new FakeTelegramRequestClient(
            HistoryPage(chatId, (20, first.Caption), (15, second.Caption)),
            HistoryPage(chatId, (10, null), (9, null)));
        var transport = new FakePartTransport(new Dictionary<string, byte[]>
        {
            ["-10055/20"] = first.Bytes,
            ["-10055/15"] = second.Bytes
        });
        var manifestStore = new FakeManifestStore();
        var catalog = new TelegramRemoteManifestCatalog(requestClient, transport, manifestStore, chatId, accountId, checkpointStore);

        var imported = await catalog.ImportRecentAsync(CancellationToken.None);

        Assert.True(catalog.WasIncrementalSync);
        Assert.Equal(2, requestClient.RequestCount);
        Assert.Equal(new long[] { 0, 15 }, requestClient.RequestedFromMessageIds);
        Assert.Equal(2, catalog.PagesRead);
        Assert.Equal(4, catalog.MessagesRead);
        Assert.Equal(2, catalog.ManifestCaptionsFound);
        Assert.Equal(2, catalog.FilesIndexed);
        Assert.Equal(new[] { "file-A", "file-B" }, imported.Select(manifest => manifest.FileId).OrderBy(id => id));
        Assert.Equal(20, (await checkpointStore.LoadAsync(accountId, chatId, CancellationToken.None))?.HighestMessageId);
        Assert.NotNull(catalog.LastSuccessfulSyncUtc);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task RemoteManifestCatalog_BindsLegacyUnownedManifestToScannedChannelOwner()
    {
        Directory.CreateDirectory(_root);
        const long chatId = -10056;
        const string accountId = "account-A";
        var remote = CreatePortableManifest("legacy-remote", "legacy.txt", "-10056/20", new byte[] { 1, 4, 9 }, accountId);
        var candidate = JsonSerializer.Deserialize<FileManifest>(remote.Bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var localStore = new FakeManifestStore();
        localStore.Manifests[candidate.FileId] = candidate with { AccountId = null };
        var requestClient = new FakeTelegramRequestClient(HistoryPage(chatId, (20, remote.Caption)), HistoryPage(chatId));
        var transport = new FakePartTransport(new Dictionary<string, byte[]> { ["-10056/20"] = remote.Bytes });
        var catalog = new TelegramRemoteManifestCatalog(requestClient, transport, localStore, chatId, accountId);

        var imported = await catalog.ImportRecentAsync(CancellationToken.None, forceFullRescan: true);

        var manifest = Assert.Single(imported);
        Assert.Equal(accountId, manifest.AccountId);
        Assert.Equal(accountId, localStore.Manifests[candidate.FileId].AccountId);
    }

    [Fact]
    public async Task RemoteManifestCatalog_RejectsManifestBoundToAnotherAccount()
    {
        Directory.CreateDirectory(_root);
        const long chatId = -10057;
        var remote = CreatePortableManifest("foreign-remote", "foreign.txt", "-10057/20", new byte[] { 2, 5, 8 }, "account-B");
        var localStore = new FakeManifestStore();
        var requestClient = new FakeTelegramRequestClient(HistoryPage(chatId, (20, remote.Caption)));
        var transport = new FakePartTransport(new Dictionary<string, byte[]> { ["-10057/20"] = remote.Bytes });
        var catalog = new TelegramRemoteManifestCatalog(requestClient, transport, localStore, chatId, "account-A");

        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.ImportRecentAsync(CancellationToken.None, forceFullRescan: true));

        Assert.Empty(localStore.Manifests);
        Assert.Null(catalog.LastSuccessfulSyncUtc);
    }

    [Fact]
    public async Task RemoteManifestCatalog_FailedPageLeavesPreviousCheckpointIntact()
    {
        Directory.CreateDirectory(_root);
        const long chatId = -10077;
        const string accountId = "account-B";
        var checkpointStore = new SqliteRemoteSyncCheckpointStore(Path.Combine(_root, "failed-sync.db"));
        var previous = new RemoteSyncCheckpoint(40, DateTimeOffset.Parse("2026-10-01T08:00:00Z"));
        await checkpointStore.SaveAsync(accountId, chatId, previous, CancellationToken.None);
        var remote = CreatePortableManifest("file-C", "gamma.txt", "-10077/50", new byte[] { 9, 8, 7 }, accountId);
        var requestClient = new FakeTelegramRequestClient(
            HistoryPage(chatId, (50, remote.Caption)),
            new IOException("simulated history interruption"));
        var transport = new FakePartTransport(new Dictionary<string, byte[]> { ["-10077/50"] = remote.Bytes });
        var catalog = new TelegramRemoteManifestCatalog(requestClient, transport, new FakeManifestStore(), chatId, accountId, checkpointStore);

        await Assert.ThrowsAsync<IOException>(() => catalog.ImportRecentAsync(CancellationToken.None));

        Assert.Null(catalog.LastSuccessfulSyncUtc);
        Assert.Equal(1, catalog.FilesIndexed);
        Assert.Equal(previous, await checkpointStore.LoadAsync(accountId, chatId, CancellationToken.None));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task RemoteManifestCatalog_FullSyncSetsHighWatermarkAfterHistoryEnds()
    {
        Directory.CreateDirectory(_root);
        const long chatId = -10088;
        const string accountId = "account-C";
        var checkpointStore = new SqliteRemoteSyncCheckpointStore(Path.Combine(_root, "full-sync.db"));
        var cachedBytes = CreatePortableManifest("cached-local", "cached.bin", "-10088/5", new byte[] { 1, 3, 5 }, accountId).Bytes;
        var cachedManifest = JsonSerializer.Deserialize<FileManifest>(cachedBytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var localStore = new FakeManifestStore();
        localStore.Manifests[cachedManifest.FileId] = cachedManifest;
        var requestClient = new FakeTelegramRequestClient(HistoryPage(chatId, (2, null), (1, null)), HistoryPage(chatId));
        var catalog = new TelegramRemoteManifestCatalog(
            requestClient,
            new FakePartTransport(new Dictionary<string, byte[]>()),
            localStore,
            chatId,
            accountId,
            checkpointStore);

        var imported = await catalog.ImportRecentAsync(CancellationToken.None);

        Assert.False(catalog.WasIncrementalSync);
        Assert.Empty(imported);
        Assert.True(RemoteManifestStatus.IsUnknown(cachedManifest, imported.Select(manifest => manifest.FileId).ToHashSet(StringComparer.Ordinal)));
        Assert.Same(cachedManifest, localStore.Manifests[cachedManifest.FileId]);
        Assert.Equal(2, requestClient.RequestCount);
        Assert.Equal(new long[] { 0, 1 }, requestClient.RequestedFromMessageIds);
        Assert.Equal(2, catalog.MessagesRead);
        Assert.Equal(2, (await checkpointStore.LoadAsync(accountId, chatId, CancellationToken.None))?.HighestMessageId);
        Assert.NotNull(catalog.LastSuccessfulSyncUtc);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task RemoteManifestCatalog_UnorderedPageDoesNotAdvanceCheckpoint()
    {
        Directory.CreateDirectory(_root);
        const long chatId = -10099;
        const string accountId = "account-D";
        var checkpointStore = new SqliteRemoteSyncCheckpointStore(Path.Combine(_root, "unordered-sync.db"));
        var previous = new RemoteSyncCheckpoint(40, DateTimeOffset.Parse("2026-10-01T08:00:00Z"));
        await checkpointStore.SaveAsync(accountId, chatId, previous, CancellationToken.None);
        var requestClient = new FakeTelegramRequestClient(HistoryPage(chatId, (50, null), (51, null)));
        var catalog = new TelegramRemoteManifestCatalog(
            requestClient,
            new FakePartTransport(new Dictionary<string, byte[]>()),
            new FakeManifestStore(),
            chatId,
            accountId,
            checkpointStore);

        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.ImportRecentAsync(CancellationToken.None));

        Assert.Null(catalog.LastSuccessfulSyncUtc);
        Assert.Equal(previous, await checkpointStore.LoadAsync(accountId, chatId, CancellationToken.None));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task RemoteManifestCatalog_MergesFolderStateWithoutRemovingLocalFolders()
    {
        Directory.CreateDirectory(_root);
        const long chatId = -10101;
        const string accountId = "account-folders";
        var databasePath = Path.Combine(_root, "folder-state.db");
        var folders = new[]
        {
            new LocalFolder(accountId, "Remote", DateTimeOffset.Parse("2026-10-01T00:00:00Z")),
            new LocalFolder(accountId, "Remote/Nested", DateTimeOffset.Parse("2026-10-01T00:00:00Z"))
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, accountId, folders }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var store = new SqliteLocalFolderStore(databasePath);
        await store.CreateAsync(accountId, "LocalOnly", CancellationToken.None);
        var requestClient = new FakeTelegramRequestClient(HistoryPage(chatId, (20, $"TSC-FOLDERS|1|{accountId}|{hash}")), HistoryPage(chatId));
        var transport = new FakePartTransport(new Dictionary<string, byte[]> { [$"{chatId}/20"] = bytes });
        var catalog = new TelegramRemoteManifestCatalog(requestClient, transport, new FakeManifestStore(), chatId, accountId, folderStore: store);

        await catalog.ImportRecentAsync(CancellationToken.None, forceFullRescan: true);

        Assert.Equal(new[] { "LocalOnly", "Remote", "Remote/Nested" },
            (await new SqliteLocalFolderStore(databasePath).ListAsync(accountId, CancellationToken.None)).Select(folder => folder.Path).OrderBy(path => path, StringComparer.Ordinal));
        Assert.Empty(await store.ListAsync("other-account", CancellationToken.None));
        Assert.Equal(1, catalog.FolderSnapshotsFound);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task RemoteManifestCatalog_LatestFolderTombstoneWinsOverOlderActiveSnapshots()
    {
        Directory.CreateDirectory(_root);
        const long chatId = -10103;
        const string accountId = "account-folder-delete";
        var deletedAt = DateTimeOffset.Parse("2026-10-04T01:00:00Z");
        var oldBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            accountId,
            folders = new[] { new LocalFolder(accountId, "Old", deletedAt.AddMinutes(-1)) }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var currentBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 2,
            accountId,
            folders = Array.Empty<LocalFolder>(),
            tombstones = new[] { new FolderTombstone(accountId, "Old", deletedAt) }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var oldHash = Convert.ToHexString(SHA256.HashData(oldBytes));
        var currentHash = Convert.ToHexString(SHA256.HashData(currentBytes));
        var store = new SqliteLocalFolderStore(Path.Combine(_root, "folder-delete-sync.db"));
        await store.CreateAsync(accountId, "Old", CancellationToken.None);
        var requestClient = new FakeTelegramRequestClient(HistoryPage(chatId,
            (20, $"TSC-FOLDERS|2|{accountId}|{currentHash}"),
            (10, $"TSC-FOLDERS|1|{accountId}|{oldHash}")));
        var transport = new FakePartTransport(new Dictionary<string, byte[]>
        {
            [$"{chatId}/20"] = currentBytes,
            [$"{chatId}/10"] = oldBytes
        });
        var catalog = new TelegramRemoteManifestCatalog(requestClient, transport,
            new FakeManifestStore(), chatId, accountId, folderStore: store);

        await catalog.ImportRecentAsync(CancellationToken.None, forceFullRescan: true);

        Assert.Empty(await store.ListAsync(accountId, CancellationToken.None));
        Assert.Equal(deletedAt, Assert.Single(await store.ListTombstonesAsync(accountId, CancellationToken.None)).DeletedAtUtc);
        Assert.Equal(2, catalog.FolderSnapshotsFound);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task RemoteManifestCatalog_RejectsFolderSnapshotForAnotherAccountWithoutAdvancingCheckpoint()
    {
        Directory.CreateDirectory(_root);
        const long chatId = -10102;
        const string accountId = "account-current";
        const string foreignAccount = "account-foreign";
        var checkpointStore = new SqliteRemoteSyncCheckpointStore(Path.Combine(_root, "folder-state-checkpoint.db"));
        var previous = new RemoteSyncCheckpoint(5, DateTimeOffset.Parse("2026-10-01T00:00:00Z"));
        await checkpointStore.SaveAsync(accountId, chatId, previous, CancellationToken.None);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            accountId = foreignAccount,
            folders = new[] { new LocalFolder(foreignAccount, "Foreign", DateTimeOffset.UtcNow) }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var requestClient = new FakeTelegramRequestClient(HistoryPage(chatId, (9, $"TSC-FOLDERS|1|{foreignAccount}|{hash}")));
        var folderStore = new SqliteLocalFolderStore(Path.Combine(_root, "foreign-folders.db"));
        var catalog = new TelegramRemoteManifestCatalog(requestClient,
            new FakePartTransport(new Dictionary<string, byte[]> { [$"{chatId}/9"] = bytes }),
            new FakeManifestStore(), chatId, accountId, checkpointStore, folderStore);

        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.ImportRecentAsync(CancellationToken.None));

        Assert.Equal(previous, await checkpointStore.LoadAsync(accountId, chatId, CancellationToken.None));
        Assert.Empty(await folderStore.ListAsync(accountId, CancellationToken.None));
        Assert.Null(catalog.LastSuccessfulSyncUtc);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task UploadPipeline_SplitsByAccountCapabilityAndResumesConfirmedParts()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "payload.bin");
        await File.WriteAllBytesAsync(source, Enumerable.Range(0, 10).Select(i => (byte)i).ToArray());
        var capability = new FakeCapabilityProvider(new UploadCapability("acct-A", 4, DateTimeOffset.UtcNow, "test"));
        var transport = new FakeTransport { FailOnCall = 2 };
        var store = new FakeManifestStore();
        var publisher = new FakeManifestPublisher();
        var pipeline = new UploadPipeline(new FileTransferCoordinator(), capability, transport, store, publisher);

        await Assert.ThrowsAsync<IOException>(() => pipeline.UploadAsync(source, Path.Combine(_root, "staging"), 3, CancellationToken.None));
        var partial = Assert.Single(store.Manifests.Values);
        Assert.Equal(4, partial.Parts.Count);
        Assert.Equal(new[] { true, false, false, false }, partial.Parts.Select(p => p.Confirmed));

        transport.FailOnCall = null;
        var completed = await pipeline.ResumeAsync(partial.FileId, CancellationToken.None);
        Assert.True(completed.Committed);
        Assert.Equal(new[] { true, true, true, true }, completed.Parts.Select(p => p.Confirmed));
        Assert.Equal(5, transport.CallCount);
        Assert.Equal(completed.FileId, Assert.Single(publisher.Published).FileId);
        Assert.Equal(2, capability.RefreshCount);
    }

    [Fact]
    public async Task UploadPipeline_StagesForQueueWithoutStartingTelegramUploads()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "queued-payload.bin");
        await File.WriteAllBytesAsync(source, Enumerable.Range(0, 10).Select(i => (byte)i).ToArray());
        var transport = new FakeTransport();
        var store = new FakeManifestStore();
        var pipeline = new UploadPipeline(
            new FileTransferCoordinator(),
            new FakeCapabilityProvider(new UploadCapability("acct-A", 4, DateTimeOffset.UtcNow, "test")),
            transport,
            store,
            new FakeManifestPublisher());

        var manifest = await pipeline.StageForUploadAsync(source, Path.Combine(_root, "staging"), 3, CancellationToken.None);

        Assert.Equal("acct-A", manifest.AccountId);
        Assert.False(manifest.Committed);
        Assert.All(manifest.Parts, part => Assert.False(part.Confirmed));
        Assert.Equal(manifest.FileId, (await store.LoadAsync(manifest.FileId, CancellationToken.None))?.FileId);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task UploadPipeline_StagesEncryptedPartsAndManifestRecoversOriginalBytes()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "encrypted-upload.bin");
        var expected = Enumerable.Range(0, 4_000).Select(i => (byte)(i * 19)).ToArray();
        await File.WriteAllBytesAsync(source, expected);
        var transport = new FakeTransport();
        var manifests = new SqliteManifestStore(Path.Combine(_root, "encrypted-manifests.db"));
        var pipeline = new UploadPipeline(
            new FileTransferCoordinator(),
            new FakeCapabilityProvider(new UploadCapability("acct-encrypted", 512, DateTimeOffset.UtcNow, "test")),
            transport, manifests, new FakeManifestPublisher());

        var manifest = await pipeline.StageForUploadAsync(source, Path.Combine(_root, "encrypted-stage"), 384,
            CancellationToken.None, forceChunking: true, recoveryPassphrase: "correct horse battery staple");

        Assert.NotNull(manifest.Encryption);
        var persisted = await manifests.LoadAsync(manifest.FileId, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(manifest.Encryption, persisted.Encryption);
        Assert.Equal(manifest.Parts, persisted.Parts);
        Assert.Equal(expected.LongLength, manifest.LogicalSize);
        Assert.Equal(manifest.Encryption.PayloadSize, manifest.Parts.Sum(part => part.Length));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), manifest.TotalSha256);
        Assert.All(manifest.Parts, part => Assert.True(part.Length <= 512));
        File.Delete(manifest.Parts[0].StagingPath!);
        manifest = await MissingUploadPartRecovery.RestageEncryptedFromCachedPayloadAsync(
            manifest, Path.Combine(_root, "encrypted-stage"), CancellationToken.None);
        Assert.True(File.Exists(manifest.Parts[0].StagingPath));
        var committedParts = new List<PartRecord>();
        foreach (var part in manifest.Parts.OrderBy(part => part.Index))
        {
            var bytes = await File.ReadAllBytesAsync(part.StagingPath!);
            transport.Downloads[$"remote-{part.Index}"] = bytes;
            committedParts.Add(part with { RemoteId = $"remote-{part.Index}", Confirmed = true });
        }
        var committed = manifest with { Committed = true, Parts = committedParts };
        var destination = Path.Combine(_root, "encrypted-restored.bin");
        var wrongPassphraseRestorer = new FileTransferCoordinator(transport,
            (_, _) => Task.FromResult("wrong recovery passphrase"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            wrongPassphraseRestorer.ReassembleAsync(committed, destination, CancellationToken.None));
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".decrypt.partial"));
        var restorer = new FileTransferCoordinator(transport,
            (_, _) => Task.FromResult("correct horse battery staple"));
        await restorer.ReassembleAsync(committed, destination, CancellationToken.None);
        Assert.Equal(expected, await File.ReadAllBytesAsync(destination));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task UploadPipeline_DeletesEncryptedPreparationCacheOnlyAfterManifestCommit()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "encrypted-commit.bin");
        await File.WriteAllBytesAsync(source, Enumerable.Range(0, 1_024).Select(i => (byte)(i * 13)).ToArray());
        var transport = new FakeTransport();
        var store = new FakeManifestStore();
        var publisher = new FakeManifestPublisher();
        var pipeline = new UploadPipeline(
            new FileTransferCoordinator(),
            new FakeCapabilityProvider(new UploadCapability("acct-encrypted", 512, DateTimeOffset.UtcNow, "test")),
            transport, store, publisher);

        var staged = await pipeline.StageForUploadAsync(source, Path.Combine(_root, "encrypted-commit-stage"), 384,
            CancellationToken.None, forceChunking: true, recoveryPassphrase: "correct horse battery staple");
        var encryptedCachePath = staged.Encryption!.StagingPath!;
        Assert.True(File.Exists(encryptedCachePath));

        var committed = await pipeline.ResumeAsync(staged.FileId, CancellationToken.None);

        Assert.True(committed.Committed);
        Assert.False(File.Exists(encryptedCachePath));
        Assert.Null(committed.Encryption!.StagingPath);
        Assert.Null(store.Manifests[staged.FileId].Encryption!.StagingPath);
        Assert.True(Assert.Single(publisher.Published).Committed);
        Assert.All(committed.Parts, part => Assert.True(part.Confirmed));
    }

    [Fact]
    public async Task UploadPipeline_BlocksWhenAccountLimitIsUnknown()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "payload.bin");
        await File.WriteAllBytesAsync(source, new byte[] { 1 });
        var capability = new FakeCapabilityProvider(null);
        var store = new FakeManifestStore();
        var pipeline = new UploadPipeline(new FileTransferCoordinator(), capability, new FakeTransport(), store, new FakeManifestPublisher());

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.UploadAsync(source, Path.Combine(_root, "staging"), 1, CancellationToken.None));
        Assert.Empty(store.Manifests);
        Assert.Equal(1, capability.RefreshCount);
    }

    [Fact]
    public async Task UploadPipeline_DoesNotStartWhenAlreadyCanceled()
    {
        var capability = new FakeCapabilityProvider(new UploadCapability("acct-A", 8, DateTimeOffset.UtcNow, "test"));
        var pipeline = new UploadPipeline(
            new FileTransferCoordinator(), capability, new FakeTransport(), new FakeManifestStore(), new FakeManifestPublisher());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.UploadAsync(
            "not-needed.bin", Path.Combine(_root, "staging"), 1, cancellation.Token));

        Assert.Equal(0, capability.RefreshCount);
    }

    [Fact]
    public async Task UploadPipeline_PersistsConfirmedPartBeforeHonoringCancellation()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "payload.bin");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3, 4 });
        using var cancellation = new CancellationTokenSource();
        var transport = new FakeTransport
        {
            CancelAfterUploadCall = 1,
            CancelAfterUploadSource = cancellation
        };
        var store = new FakeManifestStore();
        var pipeline = new UploadPipeline(
            new FileTransferCoordinator(),
            new FakeCapabilityProvider(new UploadCapability("acct-A", 2, DateTimeOffset.UtcNow, "test")),
            transport,
            store,
            new FakeManifestPublisher());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.UploadAsync(
            source, Path.Combine(_root, "staging"), 2, cancellation.Token));

        var saved = Assert.Single(store.Manifests.Values);
        Assert.Equal(new[] { true, false }, saved.Parts.Select(part => part.Confirmed));
        Assert.False(saved.Committed);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task UploadPipeline_UsesWholeFileWhenWithinLimitAndBindsAccount()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "payload.bin");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3, 4 });
        var capability = new FakeCapabilityProvider(new UploadCapability("acct-A", 8, DateTimeOffset.UtcNow, "test"));
        var store = new FakeManifestStore();
        var pipeline = new UploadPipeline(new FileTransferCoordinator(), capability, new FakeTransport(), store, new FakeManifestPublisher());

        var result = await pipeline.UploadAsync(source, Path.Combine(_root, "staging"), 2, CancellationToken.None);
                Assert.Single(result.Parts);
        Assert.Equal(4, result.Parts[0].Length);
        Assert.Equal("acct-A", result.AccountId);
    }

    [Fact]
    public async Task UploadPipeline_ForceChunkingSplitsSmallFileAndHonorsAccountLimit()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "small-payload.bin");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3, 4 });
        var capability = new FakeCapabilityProvider(new UploadCapability("acct-A", 3, DateTimeOffset.UtcNow, "test"));
        var store = new FakeManifestStore();
        var pipeline = new UploadPipeline(
            new FileTransferCoordinator(),
            capability,
            new FakeTransport(),
            store,
            new FakeManifestPublisher());

        var result = await pipeline.UploadAsync(
            source,
            Path.Combine(_root, "staging"),
            partSizeBytes: 2,
            cancellationToken: CancellationToken.None,
            forceChunking: true);

        Assert.Equal(new long[] { 2, 2 }, result.Parts.Select(part => part.Length));
        Assert.All(result.Parts, part => Assert.True(part.Confirmed));

        var cappedResult = await pipeline.UploadAsync(
            source,
            Path.Combine(_root, "staging"),
            partSizeBytes: 4,
            cancellationToken: CancellationToken.None,
            forceChunking: true);

        Assert.Equal(new long[] { 3, 1 }, cappedResult.Parts.Select(part => part.Length));
        Assert.All(cappedResult.Parts, part => Assert.InRange(part.Length, 0, 3));
    }

    [Fact]
    public async Task UploadPipeline_RecoversAcceptedPartWhoseCheckpointWasMissed()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "payload.bin");
        await File.WriteAllBytesAsync(source, Enumerable.Range(0, 10).Select(i => (byte)i).ToArray());
        var staged = await new FileTransferCoordinator().PrepareAsync(source, Path.Combine(_root, "staging"), 3, CancellationToken.None);
        var partial = staged with
        {
            AccountId = "acct-A",
            Parts = staged.Parts.Select(part => part.Index == 0
                ? part with { RemoteId = "remote-confirmed-0", Confirmed = true }
                : part).ToArray()
        };
        var transport = new FakeTransport();
        transport.AcceptedParts[(partial.FileId, 1)] = "remote-accepted-before-crash";
        var store = new FakeManifestStore();
        await store.SaveAsync(partial, CancellationToken.None);
        var pipeline = new UploadPipeline(
            new FileTransferCoordinator(),
            new FakeCapabilityProvider(new UploadCapability("acct-A", 4, DateTimeOffset.UtcNow, "test")),
            transport,
            store,
            new FakeManifestPublisher());

        var completed = await pipeline.ResumeAsync(partial.FileId, CancellationToken.None);

        Assert.True(completed.Committed);
        Assert.Equal("remote-accepted-before-crash", completed.Parts[1].RemoteId);
        Assert.True(completed.Parts[1].Confirmed);
        Assert.Equal(1, transport.RecoveryCallCount);
        Assert.Equal(2, transport.CallCount);
        Assert.Equal(new[] { 2, 3 }, transport.UploadedIndexes);
    }

    [Fact]
    public async Task UploadPipeline_RefusesResumeFromDifferentAccount()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "payload.bin");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3, 4 });
        var staged = await new FileTransferCoordinator().PrepareAsync(source, Path.Combine(_root, "staging"), 2, CancellationToken.None);
        var manifest = staged with { AccountId = "acct-owner" };
        var store = new FakeManifestStore();
        await store.SaveAsync(manifest, CancellationToken.None);
        var transport = new FakeTransport();
        var pipeline = new UploadPipeline(
            new FileTransferCoordinator(),
            new FakeCapabilityProvider(new UploadCapability("acct-other", 8, DateTimeOffset.UtcNow, "test")),
            transport,
            store,
            new FakeManifestPublisher());

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ResumeAsync(manifest.FileId, CancellationToken.None));

        Assert.Equal(0, transport.CallCount);
        Assert.Equal(0, transport.RecoveryCallCount);
        Assert.False((await store.LoadAsync(manifest.FileId, CancellationToken.None))!.Committed);
    }

        [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TelegramCapability_UsesVerifiedConservativeCeilingWithoutInferringPremiumLimit(bool isPremium)
    {
        var user = new JsonObject
        {
            ["@type"] = "user",
            ["id"] = 123456789L,
            ["is_premium"] = isPremium
        };
        var observed = DateTimeOffset.UtcNow;

        var capability = TelegramUploadCapabilityProvider.FromCurrentUser(user, observed);

        Assert.Equal("123456789", capability.AccountId);
        Assert.Equal(TelegramUploadCapabilityProvider.ConservativePerFileLimitBytes, capability.MaxFileBytes);
        Assert.Equal(observed, capability.ObservedAt);
        Assert.Contains("time-sensitive", capability.Source);
    }

    [Fact]
    public void TelegramCapability_RejectsMissingAuthenticatedUser()
    {
        Assert.Throws<InvalidDataException>(() => TelegramUploadCapabilityProvider.FromCurrentUser(
            new JsonObject { ["@type"] = "error" }, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task TransferRetryPolicy_RetriesTransientErrorsAndHonorsFloodWaitBounds()
    {
        var delays = new List<TimeSpan>();
        var policy = new TransferRetryPolicy((delay, token) =>
        {
            token.ThrowIfCancellationRequested();
            delays.Add(delay);
            return Task.CompletedTask;
        });
        var calls = 0;
        var result = await policy.ExecuteAsync<int>(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new IOException("temporary network failure");
            return Task.FromResult(42);
        }, CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(2, calls);
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(delays));

        var floodWait = TelegramRequestException.From(420, "FLOOD_WAIT_45");
        Assert.True(floodWait.IsTransient);
        Assert.Equal(TimeSpan.FromSeconds(45), TransferRetryPolicy.GetDelay(floodWait, 1));
        Assert.Null(TransferRetryPolicy.GetDelay(TelegramRequestException.From(420, "FLOOD_WAIT_900"), 1));
        Assert.Null(TransferRetryPolicy.GetDelay(TelegramRequestException.From(401, "Unauthorized"), 1));
        Assert.Null(TransferRetryPolicy.GetDelay(new InvalidDataException("bad remote bytes"), 1));

        var telegramCalls = 0;
        delays.Clear();
        var floodPolicy = new TransferRetryPolicy((delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });
        var telegramResult = await floodPolicy.ExecuteAsync<int>(_ =>
        {
            if (Interlocked.Increment(ref telegramCalls) == 1)
                throw TelegramRequestException.From(420, "FLOOD_WAIT_3");
            return Task.FromResult(7);
        }, CancellationToken.None);
        Assert.Equal(7, telegramResult);
        Assert.Equal(2, telegramCalls);
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(delays));

        var rejectedCalls = 0;
        await Assert.ThrowsAsync<TelegramRequestException>(() => floodPolicy.ExecuteAsync<int>(_ =>
        {
            rejectedCalls++;
            throw TelegramRequestException.From(400, "FILE_PART_INVALID");
        }, CancellationToken.None));
        Assert.Equal(1, rejectedCalls);
        Assert.Single(delays);

        var retryEvents = new List<(int Attempt, TimeSpan Delay)>();
        calls = 0;
        var boundedPolicy = new TransferRetryPolicy((_, _) => Task.CompletedTask, maxAttempts: 2);
        await Assert.ThrowsAsync<IOException>(() => boundedPolicy.ExecuteAsync<int>(_ =>
        {
            Interlocked.Increment(ref calls);
            throw new IOException("still unavailable");
        }, CancellationToken.None, (attempt, delay) =>
        {
            retryEvents.Add((attempt, delay));
            return Task.CompletedTask;
        }));
        Assert.Equal(2, calls);
        Assert.Equal([(1, TimeSpan.FromSeconds(2))], retryEvents);

        using var cancellation = new CancellationTokenSource();
        var cancelledCalls = 0;
        var cancelDuringWait = new TransferRetryPolicy((_, token) =>
        {
            cancellation.Cancel();
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelDuringWait.ExecuteAsync<int>(_ =>
        {
            cancelledCalls++;
            throw new IOException("temporary network failure");
        }, cancellation.Token));
        Assert.Equal(1, cancelledCalls);
    }

    [Fact]
    public async Task BoundedTransferQueueRunner_ExecutesJobsWithoutExceedingConfiguredConcurrency()
    {
        var active = 0;
        var maximumActive = 0;
        var completed = 0;
        await new BoundedTransferQueueRunner().RunAsync(
            Enumerable.Range(0, 12).ToArray(),
            2,
            async (_, cancellationToken) =>
            {
                var nowActive = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, nowActive);
                await Task.Delay(20, cancellationToken);
                Interlocked.Decrement(ref active);
                Interlocked.Increment(ref completed);
            },
            CancellationToken.None);

        Assert.Equal(12, completed);
        Assert.Equal(2, maximumActive);
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        var observed = Volatile.Read(ref target);
        while (candidate > observed)
        {
            var previous = Interlocked.CompareExchange(ref target, candidate, observed);
            if (previous == observed) return;
            observed = previous;
        }
    }

    [Fact]
    public void ManifestValidator_RejectsPathTraversalAndBrokenOffsets()
    {
        var hash = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>()));
        var pathTraversal = new FileManifest(1, "id", "..\\outside.txt", 0, hash, 1,
            new[] { new PartRecord(0, 0, 0, hash, null, false) }, false);
        var badOffsets = pathTraversal with
        {
            FileName = "safe.txt",
            LogicalSize = 2,
            Parts = new[] { new PartRecord(0, 1, 2, hash, null, false) }
        };

        Assert.Throws<InvalidDataException>(() => ManifestValidator.ValidateStructure(pathTraversal));
        Assert.Throws<InvalidDataException>(() => ManifestValidator.ValidateStructure(badOffsets));

        var missingRemoteReference = pathTraversal with
        {
            FileName = "empty.bin",
            Parts = new[] { new PartRecord(0, 0, 0, hash, null, true) }
        };
        var unconfirmedRemoteReference = pathTraversal with
        {
            FileName = "empty.bin",
            Parts = new[] { new PartRecord(0, 0, 0, hash, "-100/42", false) }
        };

        Assert.Throws<InvalidDataException>(() => ManifestValidator.ValidateStructure(missingRemoteReference));
        Assert.Throws<InvalidDataException>(() => ManifestValidator.ValidateStructure(unconfirmedRemoteReference));
    }

    [Fact]
    public void FileManifestMetadata_UpdatesRevisionWithoutChangingRemoteParts()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var original = new FileManifest(1, "file-id", "old.txt", bytes.Length, hash, bytes.Length,
            new[] { new PartRecord(0, 0, bytes.Length, hash, "-100/123", true) }, true,
            AccountId: "account-1", UpdatedAtUtc: DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            FileModifiedAtUtc: DateTimeOffset.Parse("2025-12-31T23:59:00Z"));

        var renamed = FileManifestMetadata.Rename(original, "new.txt", DateTimeOffset.Parse("2026-01-02T00:00:00Z"));
        var moved = FileManifestMetadata.Move(renamed, "Documents/Reports");
        var trashed = FileManifestMetadata.SetTrashed(moved, true);

        Assert.Equal("new.txt", trashed.FileName);
        Assert.Equal("Documents/Reports", trashed.FolderPath);
        Assert.True(trashed.IsInTrash);
        Assert.Equal(3, trashed.Revision);
        Assert.Equal(original.Parts, trashed.Parts);
        Assert.Equal(original.TotalSha256, trashed.TotalSha256);
        Assert.Equal(original.LogicalSize, trashed.LogicalSize);
        Assert.Equal(original.FileModifiedAtUtc, trashed.FileModifiedAtUtc);
        Assert.Throws<ArgumentException>(() => FileManifestMetadata.Move(original, "../outside"));
        Assert.Throws<ArgumentException>(() => FileManifestMetadata.Rename(original, "../outside.txt"));
        Assert.Equal(string.Empty, FileManifestMetadata.NormalizeFolderPath(string.Empty));
        Assert.Same(trashed, ManifestRevisionSelector.PreferNewest(trashed, original));
        var newerTimestamp = trashed with { UpdatedAtUtc = DateTimeOffset.Parse("2027-01-04T00:00:00Z") };
        Assert.Same(newerTimestamp, ManifestRevisionSelector.PreferNewest(trashed, newerTimestamp));
    }

    [Fact]
    public void ManifestRevisionSelector_PreservesMatchingLocalCacheAcrossRemoteMetadataRevision()
    {
        var bytes = new byte[] { 7, 8, 9 };
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var local = new FileManifest(1, "cached-file", "old.bin", bytes.Length, hash, bytes.Length,
            new[] { new PartRecord(0, 0, bytes.Length, hash, "-100/21", true, "C:\\cache\\part.bin") }, true,
            Revision: 1, UpdatedAtUtc: DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var remoteRevision = local with
        {
            FileName = "renamed.bin",
            Revision = 2,
            UpdatedAtUtc = DateTimeOffset.Parse("2026-01-02T00:00:00Z"),
            Parts = new[] { local.Parts[0] with { StagingPath = null } }
        };

        var selected = ManifestRevisionSelector.PreferNewest(local, remoteRevision);

        Assert.Equal(2, selected.Revision);
        Assert.Equal("renamed.bin", selected.FileName);
        Assert.Equal("C:\\cache\\part.bin", Assert.Single(selected.Parts).StagingPath);
    }

    private static (byte[] Bytes, string Caption) CreatePortableManifest(string fileId, string fileName, string remoteId, byte[] contents, string accountId)
    {
        var partHash = Convert.ToHexString(SHA256.HashData(contents));
        var manifest = new FileManifest(1, fileId, fileName, contents.Length, partHash, contents.Length,
            new[] { new PartRecord(0, 0, contents.Length, partHash, remoteId, true) }, true,
            AccountId: accountId, Revision: 1, UpdatedAtUtc: DateTimeOffset.Parse("2026-10-02T00:00:00Z"));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var manifestHash = Convert.ToHexString(SHA256.HashData(bytes));
        return (bytes, $"TSC-MANIFEST|1|{fileId}|{manifestHash}");
    }

    private static JsonObject HistoryPage(long chatId, params (long Id, string? Caption)[] messages)
    {
        var array = new JsonArray();
        foreach (var item in messages)
        {
            var content = item.Caption is null
                ? new JsonObject { ["@type"] = "messageText" }
                : new JsonObject
                {
                    ["@type"] = "messageDocument",
                    ["caption"] = new JsonObject { ["text"] = item.Caption }
                };
            array.Add(new JsonObject
            {
                ["@type"] = "message",
                ["id"] = item.Id,
                ["chat_id"] = chatId,
                ["content"] = content
            });
        }
        return new JsonObject { ["@type"] = "messages", ["messages"] = array };
    }

        public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class FakeCapabilityProvider(UploadCapability? capability) : IUploadCapabilityProvider
    {
        public int RefreshCount { get; private set; }
        public Task RefreshAsync(CancellationToken cancellationToken)
        {
            RefreshCount++;
            return Task.CompletedTask;
        }
        public Task<UploadCapability?> GetCurrentAsync(CancellationToken cancellationToken) => Task.FromResult(capability);
    }

    private sealed class InterruptingPartTransport(byte[] first, byte[] second, CancellationTokenSource cancellation) : IPartTransport
    {
        public List<string> DownloadedRemoteIds { get; } = new();
        public Task<string> UploadPartAsync(string path, string fileId, int index, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<Stream> DownloadPartAsync(string remoteId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadedRemoteIds.Add(remoteId);
            if (remoteId == "remote-first") return Task.FromResult<Stream>(new MemoryStream(first, writable: false));
            if (remoteId == "remote-second") return Task.FromResult<Stream>(new InterruptingReadStream(second, cancellation));
            throw new KeyNotFoundException(remoteId);
        }
    }

    private sealed class InterruptingReadStream(byte[] bytes, CancellationTokenSource cancellation) : Stream
    {
        private int _offset;
        private int _reads;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_offset == bytes.Length) return ValueTask.FromResult(0);
            var take = Math.Min(Math.Min(buffer.Length, 16), bytes.Length - _offset);
            bytes.AsMemory(_offset, take).CopyTo(buffer);
            _offset += take;
            if (++_reads == 3) cancellation.Cancel();
            return ValueTask.FromResult(take);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakeTransport : IPartTransport, IAcceptedPartRecovery
    {
        public int? FailOnCall { get; set; }
        public int CallCount { get; private set; }
        public int RecoveryCallCount { get; private set; }
        public int? CancelAfterUploadCall { get; set; }
        public CancellationTokenSource? CancelAfterUploadSource { get; set; }
        public List<int> UploadedIndexes { get; } = new();
        public Dictionary<(string FileId, int Index), string> AcceptedParts { get; } = new();
        public Dictionary<string, byte[]> Downloads { get; } = new();
        public List<string> DownloadedRemoteIds { get; } = new();

        public Task<string> UploadPartAsync(string path, string fileId, int index, CancellationToken cancellationToken)
        {
            CallCount++;
            UploadedIndexes.Add(index);
            if (CallCount == FailOnCall) throw new IOException("Simulated connection interruption.");
            if (CallCount == CancelAfterUploadCall) CancelAfterUploadSource?.Cancel();
            return Task.FromResult($"remote-{fileId}-{index}");
        }

        public Task<string?> FindAcceptedPartAsync(string path, string fileId, int index, CancellationToken cancellationToken)
        {
            RecoveryCallCount++;
            return Task.FromResult(AcceptedParts.GetValueOrDefault((fileId, index)));
        }

        public Task<Stream> DownloadPartAsync(string remoteId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadedRemoteIds.Add(remoteId);
            return Task.FromResult<Stream>(new MemoryStream(Downloads[remoteId], writable: false));
        }
    }

    private sealed class FakeTelegramRequestClient(params object[] responses) : ITelegramRequestClient
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);
        public List<long> RequestedFromMessageIds { get; } = new();
        public List<JsonObject> Requests { get; } = new();

        public Task<JsonObject> ExecuteAsync(JsonObject request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (RequestedFromMessageIds)
            {
                RequestedFromMessageIds.Add(request["from_message_id"]?.GetValue<long>() ?? 0);
                Requests.Add((JsonObject)request.DeepClone());
            }
            var index = Interlocked.Increment(ref _requestCount) - 1;
            if (index >= responses.Length)
                return Task.FromResult(new JsonObject { ["@type"] = "messages", ["messages"] = new JsonArray() });
            if (responses[index] is Exception error) throw error;
            return Task.FromResult((JsonObject)((JsonObject)responses[index]).DeepClone());
        }
    }

    private sealed class FakePartTransport(IReadOnlyDictionary<string, byte[]> parts) : IPartTransport
    {
        public Task<string> UploadPartAsync(string path, string fileId, int index, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream> DownloadPartAsync(string remoteId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(new MemoryStream(parts[remoteId], writable: false));
        }
    }

    private sealed class FakeManifestStore : IManifestStore
    {
        public Dictionary<string, FileManifest> Manifests { get; } = new();
        public Task SaveAsync(FileManifest manifest, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ManifestValidator.ValidateStructure(manifest);
            Manifests[manifest.FileId] = manifest;
            return Task.CompletedTask;
        }
        public Task<FileManifest?> LoadAsync(string fileId, CancellationToken cancellationToken) =>
            Task.FromResult(Manifests.GetValueOrDefault(fileId));
        public Task<IReadOnlyList<FileManifest>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FileManifest>>(Manifests.Values.ToArray());
        public Task DeleteManyAsync(IEnumerable<string> fileIds, CancellationToken cancellationToken)
        {
            foreach (var id in fileIds) Manifests.Remove(id);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeManifestPublisher : IRemoteManifestPublisher
    {
        public List<FileManifest> Published { get; } = new();
        public Task PublishCommittedAsync(FileManifest manifest, CancellationToken cancellationToken)
        {
            ManifestValidator.ValidateStructure(manifest);
            Published.Add(manifest);
            return Task.CompletedTask;
        }
    }
}

