using System.Security.Cryptography;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

public sealed class StagedPartAssembler(LocalStagingContentStore? contentStore = null) : IStagedPartAssembler
{
    public async Task AssembleAsync(FileManifest manifest, string destinationPath, CancellationToken cancellationToken)
    {
        ManifestValidator.ValidateStructure(manifest);
        if (manifest.Parts.Any(p => string.IsNullOrWhiteSpace(p.StagingPath)))
            throw new InvalidDataException("Manifest contains a part without a staging path.");

        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        using var destinationLease = DownloadWorkspace.AcquireDestinationLease(fullPath);
        var tempPath = Path.Combine(directory, ".tsc-restore-" + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            using var totalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long expectedOffset = 0;

            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                foreach (var part in manifest.Parts.OrderBy(p => p.Index))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (part.Offset != expectedOffset)
                        throw new InvalidDataException("Manifest part offsets are not contiguous.");

                    await using var materialized = contentStore is null ? null : await contentStore.MaterializeAsync(
                        part.StagingPath!, StagingFileIdentity.Part(manifest.FileId, part.Index), cancellationToken);
                    await using var input = materialized?.Stream ?? new FileStream(part.StagingPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
                    using var partHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[128 * 1024];
                    long copied = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        copied += read;
                        if (copied > part.Length) throw new InvalidDataException($"Part {part.Index} exceeds its manifest length.");
                        partHash.AppendData(buffer, 0, read);
                        totalHash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }

                    if (copied != part.Length || !ManifestValidator.HashMatches(part.Sha256, partHash.GetHashAndReset()))
                        throw new InvalidDataException($"Part {part.Index} failed integrity verification.");
                    expectedOffset += copied;
                }

                await output.FlushAsync(cancellationToken);
                if (expectedOffset != manifest.LogicalSize || !ManifestValidator.HashMatches(manifest.TotalSha256, totalHash.GetHashAndReset()))
                    throw new InvalidDataException("Restored file failed total size or SHA-256 verification.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, fullPath, true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
