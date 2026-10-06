using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

public sealed record FolderFileChange(FileManifest Before, FileManifest After);
public sealed record PendingFolderOperation(
    int SchemaVersion, string OperationId, string AccountId, string Source, string Destination,
    bool Delete, IReadOnlyList<string> FolderPaths, IReadOnlyList<FolderFileChange> Files,
    int AppliedFiles, bool FolderApplied, DateTimeOffset CreatedAtUtc, bool SourceInferred = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? StopRequestedAtUtc = null);

/// <summary>Atomic local outbox for folder changes; one pending operation per account in this vault.</summary>
public sealed class FolderOperationJournal(string directory, Func<string, LocalRecordCipher>? cipherFactory = null, LocalProfileLease? profileLease = null)
{
    private const int MaxBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private sealed record Envelope(string Sha256, PendingFolderOperation Operation);

    private LocalRecordCipher? CipherFor(string path) => cipherFactory?.Invoke(Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/'));

    public IDisposable AcquireLease(string accountId)
    {
        Directory.CreateDirectory(directory);
        try { return new FileStream(PathFor(accountId) + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new InvalidOperationException("A folder change is already running for this account and vault.", ex); }
    }

    public async Task<PendingFolderOperation?> LoadAsync(string accountId, CancellationToken token)
    {
        var path = PathFor(accountId);
        if (!File.Exists(path)) return null;
        return await LoadEnvelopeAsync(path, accountId, token);
    }

    public async Task SaveAsync(PendingFolderOperation operation, CancellationToken token)
    {
        Validate(operation, operation.AccountId);
        var bytes = SerializeForPath(PathFor(operation.AccountId), operation);
        Directory.CreateDirectory(directory);
        var path = PathFor(operation.AccountId);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                await stream.WriteAsync(bytes, token);
                await stream.FlushAsync(token);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Complete(string accountId) => File.Delete(PathFor(accountId));
    public bool HasPending(string accountId) => File.Exists(PathFor(accountId));

    public async Task ArchiveStoppedAsync(PendingFolderOperation operation, CancellationToken token)
    {
        Validate(operation, operation.AccountId);
        if (operation.StopRequestedAtUtc is null)
            throw new InvalidOperationException("Only a stopped folder plan can be archived.");
        var path = StoppedPath(operation.AccountId, operation.OperationId);
        if (File.Exists(path))
        {
            var existing = await LoadEnvelopeAsync(path, operation.AccountId, token);
            if (Hash(existing) != Hash(operation))
                throw new InvalidDataException("The stopped folder archive differs from its pending plan. Both files were kept.");
            return;
        }
        var bytes = SerializeForPath(path, operation);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                await stream.WriteAsync(bytes, token);
                await stream.FlushAsync(token);
                stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<PendingFolderOperation?> LoadStoppedAsync(string accountId, string operationId, CancellationToken token)
    {
        var path = StoppedPath(accountId, operationId);
        if (!File.Exists(path)) return null;
        var operation = await LoadEnvelopeAsync(path, accountId, token);
        if (operation.OperationId != operationId || operation.StopRequestedAtUtc is null)
            throw new InvalidDataException("The stopped folder archive differs from its pending plan. Both files were kept.");
        return operation;
    }

    private async Task<PendingFolderOperation> LoadEnvelopeAsync(string path, string accountId, CancellationToken token)
    {
        var cipher = CipherFor(path);
        try
        {
            var raw = await ReadBoundedAsync(path, token);
            if (cipher is not null && !LocalRecordCipher.IsProtectedRecord(raw))
            {
                CryptographicOperations.ZeroMemory(raw);
                var lease = profileLease ?? throw new InvalidOperationException("A local profile lease is required to protect folder recovery records.");
                lease.RequireWithin(directory);
                var relative = Path.GetRelativePath(directory, path);
                var safeId = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(relative)));
                using var stateCipher = CipherFor(path)!;
                using var migrationCipher = cipherFactory!("migration:" + safeId);
                var migration = new LocalRecordMigration(relative, ".folder-record-migration-" + safeId + ".tsc",
                    ".folder-record-recovery-" + safeId, MaxBytes, bytes => _ = ParseEnvelope(bytes, accountId), null);
                await migration.MigrateAsync(directory, lease, stateCipher, migrationCipher, token, profileLeaseIsExclusive: true);
                raw = await ReadBoundedAsync(path, token);
            }
            byte[] plain;
            if (cipher is null) plain = raw;
            else
            {
                try { plain = LocalRecordCipher.IsProtectedRecord(raw) ? cipher.Unprotect(raw) : throw new InvalidDataException("The folder recovery record was not protected after migration."); }
                finally { CryptographicOperations.ZeroMemory(raw); }
            }
            try { return ParseEnvelope(plain, accountId); }
            finally { if (cipher is not null) CryptographicOperations.ZeroMemory(plain); }
        }
        finally { cipher?.Dispose(); }
    }

    private byte[] SerializeForPath(string path, PendingFolderOperation operation)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Envelope(Hash(operation), operation), JsonOptions);
        if (plain.Length > MaxBytes) { CryptographicOperations.ZeroMemory(plain); throw new InvalidDataException("The pending folder operation exceeds the safe size limit."); }
        var cipher = CipherFor(path);
        try
        {
            if (cipher is null) return plain;
            (profileLease ?? throw new InvalidOperationException("A local profile lease is required to protect folder recovery records.")).RequireWithin(directory);
            return cipher.Protect(plain);
        }
        finally { cipher?.Dispose(); if (cipher is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    private async Task<byte[]> ReadBoundedAsync(string path, CancellationToken token)
    {
        if (LocalFileSystemPathGuard.ContainsReparsePoint(path)) throw new InvalidDataException("The folder recovery path is a filesystem link. Its file was kept.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, true);
        if (stream.Length > MaxBytes + 76) throw new InvalidDataException("The pending folder operation exceeds the safe size limit.");
        var bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes, token); return bytes;
    }

    private static PendingFolderOperation ParseEnvelope(byte[] bytes, string accountId)
    {
        Envelope? envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope>(bytes, JsonOptions); }
        catch (JsonException ex) { throw new InvalidDataException("The pending folder operation is corrupt; its recovery file was kept.", ex); }
        if (envelope?.Operation is null || envelope.Sha256 != Hash(envelope.Operation))
            throw new InvalidDataException("The pending folder operation is corrupt; its recovery file was kept.");
        Validate(envelope.Operation, accountId);
        return envelope.Operation;
    }

    private string StoppedPath(string accountId, string operationId)
    {
        if (!Guid.TryParseExact(operationId, "N", out _)) throw new ArgumentException("Invalid folder operation ID.", nameof(operationId));
        return Path.Combine(directory, "stopped", Path.GetFileNameWithoutExtension(PathFor(accountId)) + "-" + operationId + ".json");
    }

    private string PathFor(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        return Path.Combine(directory, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(accountId))) + ".json");
    }

    private static string Hash(PendingFolderOperation operation) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(operation, JsonOptions)));

    private static void Validate(PendingFolderOperation operation, string accountId)
    {
        if (operation.SchemaVersion != 1 || !Guid.TryParseExact(operation.OperationId, "N", out _) ||
            operation.AccountId != accountId || string.IsNullOrWhiteSpace(operation.AccountId) ||
            string.IsNullOrEmpty(operation.Source) || operation.Source != FileManifestMetadata.NormalizeFolderPath(operation.Source) ||
            operation.Destination is null ||
            operation.Destination != FileManifestMetadata.NormalizeFolderPath(operation.Destination) ||
            operation.FolderPaths is null || operation.Files is null || operation.AppliedFiles < 0 ||
            operation.AppliedFiles > operation.Files.Count || operation.CreatedAtUtc == default || operation.StopRequestedAtUtc == default(DateTimeOffset))
            throw new InvalidDataException("The pending folder operation is invalid; its recovery file was kept.");
        if (operation.FolderApplied && operation.AppliedFiles != operation.Files.Count)
            throw new InvalidDataException("The pending folder operation has an invalid checkpoint.");
        var fileIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in operation.Files)
        {
            if (change?.Before is null || change.After is null)
                throw new InvalidDataException("The pending folder operation has a missing file plan.");
            ManifestValidator.ValidateStructure(change.Before);
            ManifestValidator.ValidateStructure(change.After);
            if (!fileIds.Add(change.Before.FileId) || change.Before.FileId != change.After.FileId ||
                change.Before.AccountId != accountId || change.After.AccountId != accountId ||
                !IsSubpath(change.Before.FolderPath, operation.Source) ||
                change.After.Revision != change.Before.Revision + (change.Before.Committed ? 1 : 0))
                throw new InvalidDataException("The pending folder operation has an inconsistent file plan.");
            var expectedPath = operation.Delete ? operation.Destination :
                operation.Destination + change.Before.FolderPath[operation.Source.Length..];
            if (change.After.FolderPath != expectedPath || !SamePortableManifest(change.Before with
                { FolderPath = change.After.FolderPath, Revision = change.After.Revision, UpdatedAtUtc = change.After.UpdatedAtUtc }, change.After))
                throw new InvalidDataException("The pending folder operation changes file data outside its folder.");
        }
        if (!operation.FolderPaths.Contains(operation.Source, StringComparer.OrdinalIgnoreCase) ||
            operation.FolderPaths.Any(path => path is null || !IsSubpath(path, operation.Source) || path != FileManifestMetadata.NormalizeFolderPath(path)))
            throw new InvalidDataException("The pending folder operation has an inconsistent folder plan.");
    }

    internal static bool IsSubpath(string candidate, string path) =>
        string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase);

    internal static bool SamePortableManifest(FileManifest left, FileManifest right) =>
        JsonSerializer.Serialize(Portable(left), JsonOptions) == JsonSerializer.Serialize(Portable(right), JsonOptions);

    private static FileManifest Portable(FileManifest manifest) => manifest with
    {
        Parts = manifest.Parts.Select(part => part with { StagingPath = null }).ToArray(),
        Encryption = manifest.Encryption is null ? null : manifest.Encryption with { StagingPath = null }
    };
}
