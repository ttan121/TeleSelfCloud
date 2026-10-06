using System.Security.Cryptography;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

public sealed class FileTransferCoordinator(
    IPartTransport? transport = null,
    Func<FileManifest, CancellationToken, Task<string>>? recoveryPassphraseProvider = null) : ITransferCoordinator
{
    public async Task<FileManifest> PrepareAsync(string sourcePath, string stagingRoot, long partSizeBytes, CancellationToken cancellationToken)
    {
        var info = new FileInfo(sourcePath);
        if (!info.Exists) throw new FileNotFoundException("Source file was not found.", sourcePath);
        if (partSizeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(partSizeBytes));

        var id = Guid.NewGuid().ToString("N");
        var jobDirectory = Path.Combine(stagingRoot, id);
        Directory.CreateDirectory(jobDirectory);
        var createdFiles = new List<string>();
        var prepared = false;
        try
        {
        var parts = new List<PartRecord>();
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        using var totalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long offset = 0;
        int index = 0;
        while (offset < input.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var take = Math.Min(partSizeBytes, input.Length - offset);
            using var partHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var partPath = Path.Combine(jobDirectory, $"part-{index:D8}.bin");
            createdFiles.Add(partPath);
            await using var stagedPart = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
            var remaining = take;
            while (remaining > 0)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0) throw new EndOfStreamException("Source changed or ended while preparing parts.");
                totalHash.AppendData(buffer, 0, read);
                partHash.AppendData(buffer, 0, read);
                await stagedPart.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
            await stagedPart.FlushAsync(cancellationToken);
            parts.Add(new PartRecord(index++, offset, take, Convert.ToHexString(partHash.GetHashAndReset()), null, false, partPath));
            offset += take;
        }

        if (parts.Count == 0)
        {
            var emptyPartPath = Path.Combine(jobDirectory, "part-00000000.bin");
            createdFiles.Add(emptyPartPath);
            await using (var emptyPart = new FileStream(emptyPartPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await emptyPart.FlushAsync(cancellationToken);
            var emptyHash = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>()));
            parts.Add(new PartRecord(0, 0, 0, emptyHash, null, false, emptyPartPath));
        }

        if (input.Length != info.Length) throw new IOException("Source file size changed while staging.");
        var manifest = new FileManifest(1, id, info.Name, info.Length,
            Convert.ToHexString(totalHash.GetHashAndReset()), partSizeBytes, parts, false,
            UpdatedAtUtc: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            FileModifiedAtUtc: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        prepared = true;
        return manifest;
        }
        finally
        {
            // A preparation that never returned a manifest has no durable owner and cannot be resumed.
            // Remove only the fresh directory allocated for this attempt; successful staging remains intact.
            if (!prepared)
            {
                CleanupFailedPreparation(jobDirectory, createdFiles);
            }
        }
    }

    private static void CleanupFailedPreparation(string jobDirectory, IEnumerable<string> createdFiles)
    {
        if (LocalFileSystemPathGuard.ContainsReparsePoint(jobDirectory)) return;
        var fullDirectory = Path.GetFullPath(jobDirectory);
        foreach (var file in createdFiles)
        {
            var fullFile = Path.GetFullPath(file);
            if (!string.Equals(Path.GetDirectoryName(fullFile), fullDirectory, StringComparison.OrdinalIgnoreCase) ||
                LocalFileSystemPathGuard.ContainsReparsePoint(fullFile)) continue;
            try { File.Delete(fullFile); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        try { Directory.Delete(fullDirectory, recursive: false); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public async Task<FileManifest> ReassembleAsync(
        FileManifest manifest,
        string destinationPath,
        CancellationToken cancellationToken,
        Func<TransferProgress, Task>? progressCallback = null)
    {
        ManifestValidator.ValidateStructure(manifest);
        if (!manifest.Committed)
            throw new InvalidDataException("Manifest is not committed or has missing parts.");
        if (manifest.Parts.Any(p => !p.Confirmed || string.IsNullOrWhiteSpace(p.RemoteId)))
            throw new InvalidDataException("Manifest contains unconfirmed parts.");
        using var workspace = await DownloadWorkspace.OpenAsync(manifest, destinationPath, cancellationToken);
        if (manifest.Encryption is { } encryption)
        {
            if (transport is null) throw new InvalidOperationException("No Telegram transport has been configured.");
            if (recoveryPassphraseProvider is null)
                throw new InvalidOperationException("A recovery passphrase is required to restore this encrypted file.");
            var passphrase = await recoveryPassphraseProvider(manifest, cancellationToken);
            var fullDestination = Path.GetFullPath(destinationPath);
            var encryptedPath = workspace.EncryptedPayloadPath;
            var payloadManifest = manifest with
            {
                LogicalSize = encryption.PayloadSize,
                TotalSha256 = encryption.PayloadSha256,
                Encryption = null
            };
            if (!await HasVerifiedPayloadAsync(payloadManifest, encryptedPath, cancellationToken))
                await ReassemblePayloadAsync(payloadManifest, encryptedPath, workspace.CheckpointPath, cancellationToken, progressCallback);
            else if (progressCallback is not null)
                await progressCallback(new TransferProgress(manifest.FileId, manifest.TransferSize, manifest.TransferSize, manifest.Parts.Count, manifest.Parts.Count));
            await EncryptedPayloadRestorer.RestoreAsync(manifest, encryptedPath, fullDestination, passphrase, cancellationToken);
            workspace.Complete();
            return manifest;
        }
        var fullPath = Path.GetFullPath(destinationPath);
        await ReassemblePayloadAsync(manifest, fullPath, workspace.CheckpointPath, cancellationToken, progressCallback);
        workspace.Complete();
        return manifest;
    }

    private async Task ReassemblePayloadAsync(FileManifest manifest, string fullPath, string tempPath,
        CancellationToken cancellationToken, Func<TransferProgress, Task>? progressCallback)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        // Keep only fully verified parts in the partial output. If the process stops mid-part,
        // the next attempt validates the prefix and truncates back to the last good boundary.
        using var output = new FileStream(tempPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 128 * 1024, true);
        long expectedOffset = 0;
        long lastReportedBytes = 0;
        var completedParts = 0;
        var orderedParts = manifest.Parts.OrderBy(p => p.Index).ToArray();

        // Recover a checkpoint from the destination-side partial file. A part is reusable only
        // if its full length and SHA-256 match the committed remote manifest.
        foreach (var part in orderedParts)
        {
            // A zero-byte file cannot prove whether its empty Telegram part was ever fetched.
            // Fetch that part again so a missing remote document is still reported.
            if (part.Length == 0 || output.Length - expectedOffset < part.Length) break;
            output.Position = expectedOffset;
            using var checkpointHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var checkpointBuffer = new byte[128 * 1024];
            long checkpointBytes = 0;
            while (checkpointBytes < part.Length)
            {
                var read = await output.ReadAsync(checkpointBuffer.AsMemory(0,
                    (int)Math.Min(checkpointBuffer.Length, part.Length - checkpointBytes)), cancellationToken);
                if (read == 0) break;
                checkpointHash.AppendData(checkpointBuffer, 0, read);
                checkpointBytes += read;
            }
            if (checkpointBytes != part.Length || !ManifestValidator.HashMatches(part.Sha256, checkpointHash.GetHashAndReset()))
            {
                // A partial or corrupt trailing part is not a checkpoint. Keep the valid prefix.
                break;
            }
            expectedOffset += checkpointBytes;
            completedParts++;
        }

        // Build the total hash for the trusted prefix.
        using var totalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        output.Position = 0;
        var prefixBuffer = new byte[128 * 1024];
        long prefixRemaining = expectedOffset;
        while (prefixRemaining > 0)
        {
            var read = await output.ReadAsync(prefixBuffer.AsMemory(0, (int)Math.Min(prefixBuffer.Length, prefixRemaining)), cancellationToken);
            if (read == 0) throw new EndOfStreamException("The verified download checkpoint ended unexpectedly.");
            totalHash.AppendData(prefixBuffer, 0, read);
            prefixRemaining -= read;
        }
        output.SetLength(expectedOffset);
        output.Position = expectedOffset;
        if (expectedOffset > 0 && progressCallback is not null)
            await progressCallback(new TransferProgress(manifest.FileId, expectedOffset, manifest.LogicalSize, completedParts, manifest.Parts.Count));

        foreach (var part in orderedParts.Skip(completedParts))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (part.Offset != expectedOffset) throw new InvalidDataException("Manifest part offsets are not contiguous.");
            if (transport is null) throw new InvalidOperationException("No Telegram transport has been configured.");
            await using var stream = await transport.DownloadPartAsync(part.RemoteId!, cancellationToken);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long copied = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                copied += read;
                if (copied > part.Length) throw new InvalidDataException("Part exceeds its declared length.");
                hash.AppendData(buffer, 0, read);
                totalHash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                var transferred = expectedOffset + copied;
                if (progressCallback is not null && transferred - lastReportedBytes >= 8L * 1024 * 1024)
                {
                    await progressCallback(new TransferProgress(manifest.FileId, transferred, manifest.LogicalSize, completedParts, manifest.Parts.Count));
                    lastReportedBytes = transferred;
                }
            }
            if (copied != part.Length || !ManifestValidator.HashMatches(part.Sha256, hash.GetHashAndReset()))
                throw new InvalidDataException($"Part {part.Index} failed integrity validation.");
            expectedOffset += copied;
            completedParts++;
            if (progressCallback is not null)
            {
                await progressCallback(new TransferProgress(manifest.FileId, expectedOffset, manifest.LogicalSize, completedParts, manifest.Parts.Count));
                lastReportedBytes = expectedOffset;
            }
        }
        await output.FlushAsync(cancellationToken);
        if (expectedOffset != manifest.LogicalSize || !ManifestValidator.HashMatches(manifest.TotalSha256, totalHash.GetHashAndReset()))
            throw new InvalidDataException("Reassembled file failed integrity validation.");

        output.Close();
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(tempPath, fullPath, true);
    }

    private static async Task<bool> HasVerifiedPayloadAsync(FileManifest manifest, string path, CancellationToken token)
    {
        if (!File.Exists(path)) return false;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        return input.Length == manifest.LogicalSize && ManifestValidator.HashMatches(manifest.TotalSha256, await SHA256.HashDataAsync(input, token));
    }

}
