using System.Security.Cryptography;
using System.Text.Json;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

public enum LocalCacheIntegrityState
{
    AvailableOffline,
    Partial,
    Missing,
    IntegrityError
}

public sealed record LocalPartFileSnapshot(string? Path, bool Exists, long? Length, DateTimeOffset? LastWriteTimeUtc);

public sealed record LocalCacheVerification(
    string FileId,
    string ManifestFingerprint,
    long LogicalSize,
    LocalCacheIntegrityState State,
    int ValidParts,
    int PartCount,
    DateTimeOffset VerifiedAtUtc,
    IReadOnlyList<LocalPartFileSnapshot> Parts)
{
    public bool IsCurrentFor(FileManifest manifest)
    {
        if (!string.Equals(FileId, manifest.FileId, StringComparison.Ordinal) ||
            !string.Equals(ManifestFingerprint, LocalCacheFingerprint.Compute(manifest), StringComparison.Ordinal) ||
            LogicalSize != manifest.LogicalSize || Parts.Count != manifest.Parts.Count)
            return false;

        var orderedParts = manifest.Parts.OrderBy(part => part.Index).ToArray();
        for (var index = 0; index < orderedParts.Length; index++)
        {
            var rawPath = orderedParts[index].StagingPath;
            string? expectedPath;
            try { expectedPath = string.IsNullOrWhiteSpace(rawPath) ? rawPath : Path.GetFullPath(rawPath); }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
            var snapshot = Parts[index];
            if (!string.Equals(expectedPath, snapshot.Path, StringComparison.OrdinalIgnoreCase)) return false;
            var exists = !string.IsNullOrWhiteSpace(expectedPath) && File.Exists(expectedPath);
            if (exists != snapshot.Exists) return false;
            if (!exists) continue;

            try
            {
                var info = new FileInfo(expectedPath!);
                if (info.Length != snapshot.Length ||
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) != snapshot.LastWriteTimeUtc)
                    return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Persists the result of explicit SHA-256 checks over the local staged-part cache.</summary>
public sealed class LocalCacheVerificationStore(string stateFilePath, LocalRecordCipher? recordCipher = null, bool ownsCipher = false) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int disposed;
    private void CheckOpen() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    private async Task<FileStream> AcquireWriterAsync(CancellationToken token)
    {
        var path = Path.GetFullPath(stateFilePath) + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (var attempt = 0; ; attempt++)
        {
            CheckOpen(); token.ThrowIfCancellationRequested();
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The cache writer lock is a filesystem link. Its files were kept.");
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && attempt < 200)
            { await Task.Delay(25, token); }
        }
    }

    public async Task<IReadOnlyDictionary<string, LocalCacheVerification>> LoadAllAsync(CancellationToken cancellationToken)
    {
        CheckOpen();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadStateAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveManyAsync(IEnumerable<string> fileIds, CancellationToken cancellationToken)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(fileIds);
        var ids = fileIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var writer = await AcquireWriterAsync(cancellationToken);
            var state = await ReadStateAsync(cancellationToken);
            foreach (var id in ids) state.Remove(id);
            await WriteStateAsync(state, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<LocalCacheVerification> VerifyAndSaveAsync(FileManifest manifest, CancellationToken cancellationToken,
        LocalStagingContentStore? stagingContentStore = null)
    {
        CheckOpen();
        ManifestValidator.ValidateStructure(manifest);
        var snapshots = new List<LocalPartFileSnapshot>(manifest.Parts.Count);
        var validParts = 0;
        var integrityError = false;
        using var totalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var expectedPayloadHash = manifest.Encryption?.PayloadSha256 ?? manifest.TotalSha256;

        foreach (var part in manifest.Parts.OrderBy(part => part.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(part.StagingPath))
            {
                snapshots.Add(new LocalPartFileSnapshot(null, false, null, null));
                continue;
            }

            var path = Path.GetFullPath(part.StagingPath);
            if (!File.Exists(path))
            {
                snapshots.Add(new LocalPartFileSnapshot(path, false, null, null));
                continue;
            }

            var infoBefore = new FileInfo(path);
            var beforeLength = infoBefore.Length;
            var beforeLastWrite = new DateTimeOffset(infoBefore.LastWriteTimeUtc, TimeSpan.Zero);
            snapshots.Add(new LocalPartFileSnapshot(path, true, beforeLength, beforeLastWrite));
            await using var materialized = stagingContentStore is null ? null : await stagingContentStore.MaterializeAsync(
                path, StagingFileIdentity.Part(manifest.FileId, part.Index), cancellationToken);
            await using var directInput = materialized is null
                ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true)
                : null;
            var input = materialized?.Stream ?? directInput!;
            using var partHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long readTotal = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                readTotal += read;
                partHash.AppendData(buffer, 0, read);
                totalHash.AppendData(buffer, 0, read);
            }

            var partMatches = readTotal == part.Length && ManifestValidator.HashMatches(part.Sha256, partHash.GetHashAndReset());
            var infoAfter = new FileInfo(path);
            var stableDuringRead = infoAfter.Exists && infoAfter.Length == beforeLength &&
                                   new DateTimeOffset(infoAfter.LastWriteTimeUtc, TimeSpan.Zero) == beforeLastWrite;
            if (!stableDuringRead)
            {
                integrityError = true;
                continue;
            }

            if (!partMatches)
            {
                integrityError = true;
                continue;
            }

            validParts++;
        }

        var state = integrityError
            ? LocalCacheIntegrityState.IntegrityError
            : validParts == manifest.Parts.Count
                ? ManifestValidator.HashMatches(expectedPayloadHash, totalHash.GetHashAndReset())
                    ? LocalCacheIntegrityState.AvailableOffline
                    : LocalCacheIntegrityState.IntegrityError
                : validParts == 0
                    ? LocalCacheIntegrityState.Missing
                    : LocalCacheIntegrityState.Partial;
        var result = new LocalCacheVerification(manifest.FileId, LocalCacheFingerprint.Compute(manifest), manifest.LogicalSize,
            state, validParts, manifest.Parts.Count, DateTimeOffset.UtcNow, snapshots);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var writer = await AcquireWriterAsync(cancellationToken);
            var all = await ReadStateAsync(cancellationToken);
            all[manifest.FileId] = result;
            await WriteStateAsync(all, cancellationToken);
        }
        finally { _gate.Release(); }

        return result;
    }

    private async Task WriteStateAsync(Dictionary<string, LocalCacheVerification> state, CancellationToken cancellationToken)
    {
        CheckOpen();
        var directory = Path.GetDirectoryName(Path.GetFullPath(stateFilePath))!;
        Directory.CreateDirectory(directory);
        var tempPath = stateFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[]? plain = null;
        try
        {
            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, true))
            {
                if (recordCipher is null) await JsonSerializer.SerializeAsync(output, state, JsonOptions, cancellationToken);
                else { plain = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions); await output.WriteAsync(recordCipher.Protect(plain), cancellationToken); }
                await output.FlushAsync(cancellationToken); output.Flush(true);
            }
            CheckOpen(); cancellationToken.ThrowIfCancellationRequested(); File.Move(tempPath, stateFilePath, true);
        }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); if (File.Exists(tempPath)) File.Delete(tempPath); }
    }

    private async Task<Dictionary<string, LocalCacheVerification>> ReadStateAsync(CancellationToken cancellationToken)
    {
        CheckOpen();
        if (!File.Exists(stateFilePath))
            return new Dictionary<string, LocalCacheVerification>(StringComparer.Ordinal);
        await using var input = new FileStream(stateFilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 16 * 1024, true);
        if (recordCipher is not null)
        {
            if (input.Length > LocalRecordCipher.MaxCiphertextBytes) throw new InvalidDataException("The protected cache verification state exceeds its size limit.");
            var encrypted = new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(encrypted, cancellationToken);
            var plain = recordCipher.Unprotect(encrypted);
            try { return JsonSerializer.Deserialize<Dictionary<string, LocalCacheVerification>>(plain, JsonOptions) ?? throw new InvalidDataException("The protected cache verification state is empty."); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        return await JsonSerializer.DeserializeAsync<Dictionary<string, LocalCacheVerification>>(input, JsonOptions, cancellationToken)
            ?? new Dictionary<string, LocalCacheVerification>(StringComparer.Ordinal);
    }

    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0 && ownsCipher) recordCipher?.Dispose(); }

}

public static class LocalCacheFingerprint
{
    public static string Compute(FileManifest manifest)
    {
        var orderedParts = manifest.Parts.OrderBy(part => part.Index)
            .Select(part => new { part.Index, part.Offset, part.Length, part.Sha256, part.StagingPath });
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            manifest.FileId,
            manifest.LogicalSize,
            manifest.TotalSha256,
            manifest.Encryption,
            Parts = orderedParts
        });
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
