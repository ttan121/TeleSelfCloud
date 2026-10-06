using System.Security.Cryptography;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

/// <summary>Builds independently owned staging from verified local bytes; never imports source metadata or locators as confirmed.</summary>
internal static class DedupLayoutStaging
{
    public static async Task<FileManifest> AdoptAsync(FileManifest staged, FileManifest candidate, long maxPartBytes, CancellationToken token,
        LocalStagingContentStore? contentStore = null)
    {
        ManifestValidator.ValidateStructure(staged); ManifestValidator.ValidateStructure(candidate);
        if (staged.Committed || staged.Encryption is not null || staged.Parts.Any(p => p.Confirmed || p.CopySource is not null) ||
            !candidate.Committed || candidate.IsInTrash || candidate.Encryption is not null || candidate.FileId == staged.FileId ||
            candidate.AccountId != staged.AccountId || candidate.LogicalSize != staged.LogicalSize ||
            !string.Equals(candidate.TotalSha256, staged.TotalSha256, StringComparison.OrdinalIgnoreCase) || candidate.Parts.Any(p => p.Length > maxPartBytes))
            throw new InvalidDataException("The verified copy layout cannot be adopted by this draft.");
        PartRecord Pending(PartRecord part, string? path) => part with { RemoteId = null, Confirmed = false, StagingPath = path,
            CopySource = new(staged.AccountId!, candidate.FileId, part.Index, part.RemoteId!) };
        var same = candidate.Parts.Count == staged.Parts.Count && candidate.Parts.Zip(staged.Parts).All(p =>
            p.First.Index == p.Second.Index && p.First.Offset == p.Second.Offset && p.First.Length == p.Second.Length &&
            string.Equals(p.First.Sha256, p.Second.Sha256, StringComparison.OrdinalIgnoreCase));
        if (same)
        {
            var result = staged with { Parts = candidate.Parts.Select((p, i) => Pending(p, staged.Parts[i].StagingPath)).ToArray() };
            ManifestValidator.ValidateStructure(result); return result;
        }
        var inputs = new List<Stream>(); var materialized = new List<LocalStagingContentStore.MaterializedFile>();
        var created = new List<string>(); string? directory = null;
        var buffer = new byte[65536];
        try
        {
            // Hold all original files through validation and repartitioning, so accepted hashes cannot change midway.
            foreach (var part in staged.Parts)
            {
                token.ThrowIfCancellationRequested();
                var partPath = part.StagingPath ?? throw new InvalidDataException("Copy layout requires local staging.");
                Stream stream;
                if (contentStore is null)
                    stream = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                else
                {
                    var file = await contentStore.MaterializeAsync(partPath, StagingFileIdentity.Part(staged.FileId, part.Index), token);
                    materialized.Add(file);
                    stream = file.Stream;
                }
                inputs.Add(stream);
                if (stream.Length != part.Length || !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(stream, token), Convert.FromHexString(part.Sha256)))
                    throw new InvalidDataException("Original staging failed validation during copy layout adoption.");
                stream.Position = 0;
            }
            directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(staged.Parts[0].StagingPath!))!, "dedup-layout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            using var total = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var parts = new List<PartRecord>(); int inputIndex = 0;
            foreach (var target in candidate.Parts)
            {
                token.ThrowIfCancellationRequested(); var path = Path.Combine(directory, $"part-{target.Index:D8}.bin");
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true); created.Add(path);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long remaining = target.Length;
                while (remaining > 0)
                {
                    while (inputIndex < inputs.Count && inputs[inputIndex].Position == inputs[inputIndex].Length) inputIndex++;
                    if (inputIndex == inputs.Count) throw new EndOfStreamException("Original staging ended during copy layout adoption.");
                    var count = await inputs[inputIndex].ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
                    if (count == 0) throw new EndOfStreamException("Original staging ended during copy layout adoption.");
                    total.AppendData(buffer, 0, count); hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), token); remaining -= count;
                }
                if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(target.Sha256)))
                    throw new InvalidDataException("Candidate part layout does not match local content.");
                await output.FlushAsync(token); output.Flush(flushToDisk: true); parts.Add(Pending(target, path));
            }
            if (inputs.Any(s => s.Position != s.Length) || !CryptographicOperations.FixedTimeEquals(total.GetHashAndReset(), Convert.FromHexString(staged.TotalSha256)))
                throw new InvalidDataException("Repartitioned content failed whole-file verification.");
            if (contentStore is not null)
                foreach (var part in parts)
                    await contentStore.ProtectInPlaceAsync(part.StagingPath!, StagingFileIdentity.Part(staged.FileId, part.Index),
                        part.Length, part.Sha256, token);
            var result = staged with { PartSizeBytes = candidate.PartSizeBytes, Parts = parts }; ManifestValidator.ValidateStructure(result); return result;
        }
        catch
        {
            // Only files created by this attempt are removed. Original staging and the saved draft stay intact.
            foreach (var path in created) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            if (directory is not null) { try { Directory.Delete(directory, false); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            throw;
        }
        finally
        {
            foreach (var file in materialized) await file.DisposeAsync();
            foreach (var input in inputs.Except(materialized.Select(file => file.Stream))) await input.DisposeAsync();
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}
