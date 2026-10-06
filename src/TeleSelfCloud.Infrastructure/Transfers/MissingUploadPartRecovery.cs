using System.Security.Cryptography;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

public static class MissingUploadPartRecovery
{
    public static async Task<FileManifest> RestageEncryptedFromCachedPayloadAsync(
        FileManifest manifest, string stagingRoot, CancellationToken cancellationToken,
        LocalStagingContentStore? contentStore = null)
    {
        ManifestValidator.ValidateStructure(manifest);
        var descriptor = manifest.Encryption
            ?? throw new InvalidOperationException("The selected upload does not have an encrypted payload.");
        if (string.IsNullOrWhiteSpace(descriptor.StagingPath) || !File.Exists(descriptor.StagingPath))
            throw new FileNotFoundException("The encrypted staging cache is missing; this upload cannot safely rebuild its remaining parts.");
        var payloadManifest = manifest with
        {
            LogicalSize = descriptor.PayloadSize,
            TotalSha256 = descriptor.PayloadSha256,
            Encryption = null
        };
        await using var materialized = contentStore is null ? null : await contentStore.MaterializeAsync(
            descriptor.StagingPath, StagingFileIdentity.EncryptedPayload(manifest.FileId), cancellationToken);
        await using var direct = materialized is null
            ? new FileStream(descriptor.StagingPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true)
            : null;
        var restagedPayload = await RestageFromStreamAsync(payloadManifest, materialized?.Stream ?? direct!, stagingRoot, cancellationToken,
            contentStore);
        return manifest with { Parts = restagedPayload.Parts };
    }

    public static async Task<FileManifest> RestageAsync(
        FileManifest manifest, string sourcePath, string stagingRoot, CancellationToken cancellationToken,
        LocalStagingContentStore? contentStore = null)
    {
        ManifestValidator.ValidateStructure(manifest);
        if (manifest.Committed) throw new InvalidOperationException("A committed upload does not need source recovery.");
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);

        var fullSourcePath = Path.GetFullPath(sourcePath);
        await using var source = new FileStream(fullSourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        return await RestageFromStreamAsync(manifest, source, stagingRoot, cancellationToken, contentStore);
    }

    private static async Task<FileManifest> RestageFromStreamAsync(
        FileManifest manifest, Stream source, string stagingRoot, CancellationToken cancellationToken,
        LocalStagingContentStore? contentStore)
    {
        ManifestValidator.ValidateStructure(manifest);
        if (manifest.Committed) throw new InvalidOperationException("A committed upload does not need source recovery.");
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        source.Position = 0;
        if (source.Length != manifest.LogicalSize)
            throw new InvalidDataException("The selected source file size does not match the queued upload.");
        var totalHash = await SHA256.HashDataAsync(source, cancellationToken);
        if (!ManifestValidator.HashMatches(manifest.TotalSha256, totalHash))
            throw new InvalidDataException("The selected source file content does not match the queued upload.");

        var pendingParts = manifest.Parts.Where(part => !part.Confirmed).OrderBy(part => part.Index).ToArray();
        if (pendingParts.Length == 0)
            throw new InvalidOperationException("The upload has no unconfirmed parts to recover.");
        var recoveryDirectory = Path.Combine(Path.GetFullPath(stagingRoot), "recovered-uploads", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(recoveryDirectory);
        var updatedParts = manifest.Parts.ToDictionary(part => part.Index);
        var temporaryPaths = new List<string>();
        try
        {
            foreach (var part in pendingParts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(recoveryDirectory, $"part-{part.Index:D8}.bin");
                var temporary = destination + ".partial";
                temporaryPaths.Add(temporary);
                source.Position = part.Offset;
                using var partHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024];
                long remaining = part.Length;
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                {
                    while (remaining > 0)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                        if (read == 0) throw new EndOfStreamException("The selected source file ended while rebuilding a staged part.");
                        partHash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        remaining -= read;
                    }
                    await output.FlushAsync(cancellationToken);
                }
                if (!ManifestValidator.HashMatches(part.Sha256, partHash.GetHashAndReset()))
                    throw new InvalidDataException($"The selected source file does not match queued part {part.Index}.");

                var recoveredPath = destination;
                File.Move(temporary, recoveredPath);
                temporaryPaths.Remove(temporary);
                if (contentStore is not null)
                    await contentStore.ProtectInPlaceAsync(recoveredPath, StagingFileIdentity.Part(manifest.FileId, part.Index),
                        part.Length, part.Sha256, cancellationToken);
                updatedParts[part.Index] = part with { StagingPath = recoveredPath };
            }

            return manifest with { Parts = manifest.Parts.Select(part => updatedParts[part.Index]).ToArray() };
        }
        catch
        {
            foreach (var path in temporaryPaths)
                if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(recoveryDirectory)) Directory.Delete(recoveryDirectory, recursive: true);
            throw;
        }
    }
}
