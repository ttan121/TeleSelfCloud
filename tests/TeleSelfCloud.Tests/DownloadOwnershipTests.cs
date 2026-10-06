using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class DownloadOwnershipTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.DownloadOwnership", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Content = [1, 2, 3, 4];
    private string Destination => Path.Combine(root, "result.bin");
    private static FileManifest Manifest => new(1, "download-owner", "result.bin", 4, Hash(Content), 2,
        [new PartRecord(0, 0, 2, Hash([1, 2]), "part-0", true), new PartRecord(1, 2, 2, Hash([3, 4]), "part-1", true)], true, AccountId: "account-a");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    [Fact]
    public async Task SuccessfulRestorePreservesUnownedLegacyPartial()
    {
        Directory.CreateDirectory(root);
        byte[] unrelated = [9, 8, 7, 6, 5];
        await File.WriteAllBytesAsync(Destination + ".partial", unrelated);
        await new FileTransferCoordinator(new Transport()).ReassembleAsync(Manifest, Destination, default);
        Assert.Equal(Content, await File.ReadAllBytesAsync(Destination));
        Assert.True(File.Exists(Destination + ".partial"));
        Assert.Equal(unrelated, await File.ReadAllBytesAsync(Destination + ".partial"));
    }

    [Fact]
    public async Task WrongPassphraseDoesNotDeleteUnownedDecryptSidecarOrOverwriteLegacyCipherFile()
    {
        Directory.CreateDirectory(root);
        var key = AesGcmFileCipher.CreateFileKey();
        try
        {
            await using var plain = new MemoryStream(Content);
            await using var cipher = new MemoryStream();
            await AesGcmFileCipher.EncryptAsync(plain, cipher, key, cancellationToken: default);
            var encrypted = cipher.ToArray();
            var manifest = Manifest with
            {
                PartSizeBytes = encrypted.Length,
                Parts = [new PartRecord(0, 0, encrypted.Length, Hash(encrypted), "cipher", true)],
                Encryption = new EncryptedPayloadDescriptor(1, encrypted.Length, Hash(encrypted), AesGcmFileCipher.WrapFileKey(key, "correct passphrase"))
            };
            byte[] sentinel = [77, 88];
            await File.WriteAllBytesAsync(Destination + ".decrypt.partial", sentinel);
            await File.WriteAllBytesAsync(Destination + ".encrypted-payload", sentinel);
            await File.WriteAllBytesAsync(Destination, sentinel);
            var transport = new Transport { Cipher = encrypted };
            await Assert.ThrowsAsync<InvalidOperationException>(() => new FileTransferCoordinator(transport, (_, _) => Task.FromResult("incorrect passphrase"))
                .ReassembleAsync(manifest, Destination, default));
            Assert.Equal(sentinel, await File.ReadAllBytesAsync(Destination));
            Assert.True(File.Exists(Destination + ".decrypt.partial"));
            Assert.Equal(sentinel, await File.ReadAllBytesAsync(Destination + ".decrypt.partial"));
            Assert.Equal(sentinel, await File.ReadAllBytesAsync(Destination + ".encrypted-payload"));
            var requests = transport.Requests.Count;
            await new FileTransferCoordinator(transport, (_, _) => Task.FromResult("correct passphrase"))
                .ReassembleAsync(manifest, Destination, default);
            Assert.Equal(Content, await File.ReadAllBytesAsync(Destination));
            Assert.Equal(requests, transport.Requests.Count);
            Assert.Equal(sentinel, await File.ReadAllBytesAsync(Destination + ".encrypted-payload"));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    [Fact]
    public async Task RestartDownloadsOnlyMissingPartAndPreservesExistingDestinationUntilVerified()
    {
        Directory.CreateDirectory(root);
        byte[] sentinel = [88];
        await File.WriteAllBytesAsync(Destination, sentinel);
        var interrupted = new Transport { FailSecond = true };
        await Assert.ThrowsAsync<IOException>(() => new FileTransferCoordinator(interrupted).ReassembleAsync(Manifest, Destination, default));
        Assert.Equal(sentinel, await File.ReadAllBytesAsync(Destination));
        var resumed = new Transport();
        await new FileTransferCoordinator(resumed).ReassembleAsync(Manifest, Destination, default);
        Assert.Equal(new[] { "part-1" }, resumed.Requests);
        Assert.Equal(Content, await File.ReadAllBytesAsync(Destination));
    }

    [Fact]
    public async Task SameDestinationIsLockedAcrossFilesWhileIndependentDestinationRemainsAvailable()
    {
        Directory.CreateDirectory(root);
        using (await DownloadWorkspace.OpenAsync(Manifest, Destination, default))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new FileTransferCoordinator(new Transport())
                .ReassembleAsync(Manifest with { FileId = "other-file" }, Destination.ToUpperInvariant(), default));
            await new FileTransferCoordinator(new Transport()).ReassembleAsync(Manifest, Path.Combine(root, "other.bin"), default);
            Assert.False(File.Exists(Destination));
        }
        await new FileTransferCoordinator(new Transport()).ReassembleAsync(Manifest, Destination, default);
        Assert.Equal(Content, await File.ReadAllBytesAsync(Destination));
    }

    [Fact]
    public async Task CorruptRecoveryRecordCannotTruncateCheckpointOrTouchDestination()
    {
        Directory.CreateDirectory(root);
        await Assert.ThrowsAsync<IOException>(() => new FileTransferCoordinator(new Transport { FailSecond = true })
            .ReassembleAsync(Manifest, Destination, default));
        var checkpoint = Directory.GetFiles(root, ".tsc-download-*.partial").Single();
        var saved = await File.ReadAllBytesAsync(checkpoint);
        var record = Directory.GetFiles(root, ".tsc-download-*.json").Single();
        await File.WriteAllTextAsync(record, "{broken");
        var transport = new Transport();
        await Assert.ThrowsAsync<InvalidDataException>(() => new FileTransferCoordinator(transport).ReassembleAsync(Manifest, Destination, default));
        Assert.Empty(transport.Requests);
        Assert.Equal(saved, await File.ReadAllBytesAsync(checkpoint));
        Assert.Equal("{broken", await File.ReadAllTextAsync(record));
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task DifferentAccountOrManifestCannotConsumeAnotherRecoveryCheckpoint()
    {
        Directory.CreateDirectory(root);
        await Assert.ThrowsAsync<IOException>(() => new FileTransferCoordinator(new Transport { FailSecond = true })
            .ReassembleAsync(Manifest, Destination, default));
        var checkpoint = Directory.GetFiles(root, ".tsc-download-*.partial").Single();
        var before = await File.ReadAllBytesAsync(checkpoint);
        var transport = new Transport();
        await new FileTransferCoordinator(transport).ReassembleAsync(Manifest with { AccountId = "account-b" }, Destination, default);
        Assert.Equal(new[] { "part-0", "part-1" }, transport.Requests);
        Assert.Equal(before, await File.ReadAllBytesAsync(checkpoint));
        Assert.Single(Directory.GetFiles(root, ".tsc-download-*.json"));
    }

    [Fact]
    public async Task PausedSqliteDownloadCanRestartWithVerifiedPrefixAndCompleteItsQueueTask()
    {
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "queue.db");
        var queue = new SqliteTransferQueueStore(database);
        var item = await queue.EnqueueDownloadAsync(Manifest.FileId, Manifest.FileName, Destination, 4, default);
        using var cancellation = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new Transport { BeforeSecond = async token => { ready.SetResult(); await Task.Delay(Timeout.Infinite, token); } };
        var run = new TransferQueueStartGuard(queue).RunAsync(item.TaskId, () => Task.CompletedTask, () =>
            new FileTransferCoordinator(transport).ReassembleAsync(Manifest, Destination, cancellation.Token,
                progress => queue.UpdateProgressAsync(item.TaskId, progress.TransferredBytes, progress.TotalBytes, default)));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        var paused = Assert.Single(await new SqliteTransferQueueStore(database).ListAsync(default));
        Assert.Equal(TransferQueueState.Paused, paused.State);
        Assert.Equal(2, paused.TransferredBytes);
        Assert.False(File.Exists(Destination));
        var reopened = new SqliteTransferQueueStore(database);
        var resumed = new Transport();
        await new TransferQueueStartGuard(reopened).RunAsync(item.TaskId, () => Task.CompletedTask, () =>
            new FileTransferCoordinator(resumed).ReassembleAsync(Manifest, Destination, default,
                progress => reopened.UpdateProgressAsync(item.TaskId, progress.TransferredBytes, progress.TotalBytes, default)));
        var completed = Assert.Single(await reopened.ListAsync(default));
        Assert.Equal(TransferQueueState.Completed, completed.State);
        Assert.Equal(4, completed.TransferredBytes);
        Assert.Equal(2, completed.AttemptCount);
        Assert.Equal(new[] { "part-1" }, resumed.Requests);
        Assert.Empty(Directory.GetFiles(root, ".tsc-download-*.json"));
        Assert.Empty(Directory.GetFiles(root, ".tsc-download-*.partial"));
    }

    [Fact]
    public async Task LocalStagedRestoreAlsoPreservesLegacySidecar()
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.bin");
        await File.WriteAllBytesAsync(source, Content);
        var staged = await new FileTransferCoordinator().PrepareAsync(source, Path.Combine(root, "stage"), 2, default);
        byte[] sentinel = [99];
        await File.WriteAllBytesAsync(Destination + ".partial", sentinel);
        await new StagedPartAssembler().AssembleAsync(staged, Destination, default);
        Assert.Equal(sentinel, await File.ReadAllBytesAsync(Destination + ".partial"));
        Assert.Equal(Content, await File.ReadAllBytesAsync(Destination));
    }

    [Fact]
    public async Task AuthenticationFailureDeletesOnlyOwnedPlaintextTemporary()
    {
        Directory.CreateDirectory(root);
        var key = AesGcmFileCipher.CreateFileKey();
        try
        {
            await using var input = new MemoryStream(Content);
            await using var encrypted = new MemoryStream();
            await AesGcmFileCipher.EncryptAsync(input, encrypted, key, frameSize: 2);
            var cipher = encrypted.ToArray();
            cipher[^1] ^= 1; // First frame decrypts, second frame authentication fails.
            var payload = Path.Combine(root, "payload.cipher");
            await File.WriteAllBytesAsync(payload, cipher);
            var manifest = Manifest with { PartSizeBytes = cipher.Length,
                Parts = [new PartRecord(0, 0, cipher.Length, Hash(cipher), "cipher", true)],
                Encryption = new EncryptedPayloadDescriptor(1, cipher.Length, Hash(cipher), AesGcmFileCipher.WrapFileKey(key, "correct passphrase")) };
            byte[] sentinel = [99];
            await File.WriteAllBytesAsync(Destination, sentinel);
            await File.WriteAllBytesAsync(Destination + ".decrypt.partial", sentinel);
            await Assert.ThrowsAnyAsync<CryptographicException>(() => EncryptedPayloadRestorer.RestoreAsync(manifest, payload, Destination, "correct passphrase", default));
            Assert.Equal(sentinel, await File.ReadAllBytesAsync(Destination));
            Assert.Equal(sentinel, await File.ReadAllBytesAsync(Destination + ".decrypt.partial"));
            Assert.Empty(Directory.GetFiles(root, ".tsc-decrypt-*.partial"));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private sealed class Transport : IPartTransport
    {
        public bool FailSecond { get; init; }
        public byte[] Cipher { get; init; } = [];
        public Func<CancellationToken, Task>? BeforeSecond { get; init; }
        public List<string> Requests { get; } = [];
        public Task<string> UploadPartAsync(string path, string fileId, int index, CancellationToken token) => throw new NotSupportedException();
        public async Task<Stream> DownloadPartAsync(string remoteId, CancellationToken token)
        {
            Requests.Add(remoteId);
            if (remoteId == "part-1" && FailSecond) throw new IOException("Injected interruption");
            if (remoteId == "part-1" && BeforeSecond is not null) await BeforeSecond(token);
            return new MemoryStream(remoteId == "cipher" ? Cipher : remoteId == "part-0" ? [1, 2] : [3, 4]);
        }
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
