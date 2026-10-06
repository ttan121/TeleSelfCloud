using System.Security.Cryptography;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

/// <summary>Assembles the verified encrypted payload bytes from staged parts without exposing an unverified result.</summary>
public static class StagedEncryptedPayloadAssembler
{
    public static async Task AssembleAsync(FileManifest manifest, string destinationPath, CancellationToken token,
        LocalStagingContentStore? contentStore = null)
    {
        ManifestValidator.ValidateStructure(manifest);
        var descriptor = manifest.Encryption ?? throw new InvalidDataException("The manifest has no encrypted payload descriptor.");
        if (manifest.Parts.Any(part => string.IsNullOrWhiteSpace(part.StagingPath)))
            throw new InvalidDataException("An encrypted payload part has no local staging path.");

        var fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var created = false;
        try
        {
            using var totalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long totalBytes = 0;
            await using (var output = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                created = true;
                foreach (var part in manifest.Parts.OrderBy(part => part.Index))
                {
                    token.ThrowIfCancellationRequested();
                    await using var materialized = contentStore is null ? null : await contentStore.MaterializeAsync(
                        part.StagingPath!, StagingFileIdentity.Part(manifest.FileId, part.Index), token);
                    await using var direct = materialized is null
                        ? new FileStream(part.StagingPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true)
                        : null;
                    var input = materialized?.Stream ?? direct!;
                    using var partHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long partBytes = 0;
                    var buffer = new byte[128 * 1024];
                    int read;
                    while ((read = await input.ReadAsync(buffer, token)) > 0)
                    {
                        partBytes = checked(partBytes + read);
                        totalBytes = checked(totalBytes + read);
                        if (partBytes > part.Length || totalBytes > descriptor.PayloadSize)
                            throw new InvalidDataException($"Encrypted staged part {part.Index} exceeds its manifest length.");
                        partHash.AppendData(buffer, 0, read);
                        totalHash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                    }
                    if (partBytes != part.Length || !ManifestValidator.HashMatches(part.Sha256, partHash.GetHashAndReset()))
                        throw new InvalidDataException($"Encrypted staged part {part.Index} failed integrity verification.");
                }

                if (totalBytes != descriptor.PayloadSize || !ManifestValidator.HashMatches(descriptor.PayloadSha256, totalHash.GetHashAndReset()))
                    throw new InvalidDataException("Encrypted staged payload failed its manifest integrity check.");
                await output.FlushAsync(token);
                output.Flush(flushToDisk: true);
            }
        }
        catch
        {
            if (created)
            {
                try { File.Delete(fullPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }
}
