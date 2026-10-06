using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Telegram;

namespace TeleSelfCloud.Infrastructure.Transfers;

public sealed record MetadataResolutionPlan(FileManifest Before, FileManifest After, string SelectedFingerprint);
public sealed record MetadataConflictHistory(int SchemaVersion, string AccountId, long ChatId, string FileId,
    IReadOnlyList<FileManifest> Versions, MetadataResolutionPlan? Pending = null, IReadOnlyList<MetadataResolutionPlan>? SupersededPlans = null);

/// <summary>Retains competing organization snapshots and a durable resolution outbox in one vault.</summary>
public sealed class MetadataConflictArchive(string directory, string accountId, long chatId,
    Func<string, LocalRecordCipher>? cipherFactory = null, LocalProfileLease? profileLease = null)
{
    private const int MaxBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private sealed record Envelope(string Sha256, MetadataConflictHistory History);
    private LocalRecordCipher? CipherFor(string path) => cipherFactory?.Invoke(Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/'));

    public IDisposable AcquireLease(string fileId)
    {
        Directory.CreateDirectory(directory);
        try { return new FileStream(PathFor(fileId) + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new InvalidOperationException("Metadata history is busy. Wait for the current operation and retry.", ex); }
    }

    public async Task RecordCompetingAsync(FileManifest current, FileManifest candidate, CancellationToken token)
    {
        if (!current.Committed || !candidate.Committed || current.Revision != candidate.Revision || SameOrganization(current, candidate)) return;
        // Includes ownership/content checks before any archive or index mutation.
        _ = ManifestRevisionSelector.PreferNewest(current, candidate);
        current = Portable(current);
        candidate = Portable(candidate);
        ValidateManifest(current, current.FileId);
        ValidateManifest(candidate, current.FileId);
        using var lease = AcquireLease(current.FileId);
        var history = await LoadAsync(current.FileId, token) ?? new(1, accountId, chatId, current.FileId, []);
        var versions = history.Versions.ToList();
        foreach (var version in new[] { current, candidate })
            if (!versions.Any(saved => ManifestRevisionSelector.PortableFingerprint(saved) == ManifestRevisionSelector.PortableFingerprint(version)))
                versions.Add(version);
        if (versions.Count != history.Versions.Count)
            await SaveAsync(history with { Versions = versions }, token);
    }

    public async Task<MetadataConflictHistory?> LoadAsync(string fileId, CancellationToken token)
    {
        var path = PathFor(fileId);
        if (!File.Exists(path)) return null;
        return await LoadEnvelopeAsync(path, fileId, token);
    }

    public async Task SaveAsync(MetadataConflictHistory history, CancellationToken token)
    {
        Validate(history, history.FileId);
        var bytes = SerializeForPath(PathFor(history.FileId), history);
        Directory.CreateDirectory(directory);
        var path = PathFor(history.FileId);
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
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal void ValidateManifest(FileManifest manifest, string fileId)
    {
        ManifestValidator.ValidateStructure(manifest);
        if (!manifest.Committed || manifest.FileId != fileId || manifest.AccountId != accountId ||
            manifest.Parts.Any(part => TelegramRemoteMessageId.Parse(part.RemoteId!).ChatId != chatId))
            throw new InvalidDataException("Metadata history belongs to a different file, account, or vault. Local data was kept.");
    }

    private void Validate(MetadataConflictHistory history, string fileId)
    {
        if (history.SchemaVersion != 1 || history.AccountId != accountId || history.ChatId != chatId || history.FileId != fileId ||
            history.Versions is null || history.Versions.Count < 2 || history.Versions.Count > 256)
            throw new InvalidDataException("Metadata history is invalid. Its file was kept.");
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var version in history.Versions)
        {
            if (version is null) throw new InvalidDataException("Metadata history is invalid. Its file was kept.");
            ValidateManifest(version, fileId);
            _ = ManifestRevisionSelector.PreferNewest(history.Versions[0], version);
            if (!fingerprints.Add(ManifestRevisionSelector.PortableFingerprint(version)))
                throw new InvalidDataException("Metadata history is invalid. Its file was kept.");
        }
        if (history.SupersededPlans?.Count > 256) throw new InvalidDataException("Metadata history exceeds the safe size limit. Its file was kept.");
        foreach (var pending in (history.SupersededPlans ?? []).Concat(history.Pending is null ? [] : new[] { history.Pending }))
        {
            if (pending is null || pending.Before is null || pending.After is null)
                throw new InvalidDataException("Metadata resolution plan is invalid. Its file was kept.");
            ValidateManifest(pending.Before, fileId);
            ValidateManifest(pending.After, fileId);
            var chosen = history.Versions.SingleOrDefault(version => ManifestRevisionSelector.PortableFingerprint(version) == pending.SelectedFingerprint);
            if (chosen is null || pending.After.Revision <= pending.Before.Revision || pending.After.UpdatedAtUtc is null ||
                !SameOrganization(chosen, pending.After) || pending.After.Revision <= chosen.Revision ||
                ManifestRevisionSelector.PortableFingerprint(pending.After with
                    { FileName = pending.Before.FileName, FolderPath = pending.Before.FolderPath, IsInTrash = pending.Before.IsInTrash,
                      IsFavorite = pending.Before.IsFavorite, IsArchived = pending.Before.IsArchived, IsHidden = pending.Before.IsHidden,
                      FileModifiedAtUtc = pending.Before.FileModifiedAtUtc, Revision = pending.Before.Revision, UpdatedAtUtc = pending.Before.UpdatedAtUtc }) !=
                ManifestRevisionSelector.PortableFingerprint(pending.Before))
                throw new InvalidDataException("Metadata resolution plan is invalid. Its file was kept.");
        }
    }

    private async Task<MetadataConflictHistory> LoadEnvelopeAsync(string path, string fileId, CancellationToken token)
    {
        var cipher = CipherFor(path);
        try
        {
            var raw = await ReadBoundedAsync(path, token);
            if (cipher is not null && !LocalRecordCipher.IsProtectedRecord(raw))
            {
                CryptographicOperations.ZeroMemory(raw);
                var lease = profileLease ?? throw new InvalidOperationException("A local profile lease is required to protect metadata history.");
                lease.RequireWithin(directory);
                var relative = Path.GetRelativePath(directory, path);
                var safeId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative)));
                using var stateCipher = CipherFor(path)!;
                using var migrationCipher = cipherFactory!("migration:" + safeId);
                var migration = new LocalRecordMigration(relative, ".metadata-history-migration-" + safeId + ".tsc",
                    ".metadata-history-recovery-" + safeId, MaxBytes, bytes => _ = ParseEnvelope(bytes, fileId), null);
                await migration.MigrateAsync(directory, lease, stateCipher, migrationCipher, token, profileLeaseIsExclusive: true);
                raw = await ReadBoundedAsync(path, token);
            }
            byte[] plain;
            if (cipher is null) plain = raw;
            else
            {
                try { plain = LocalRecordCipher.IsProtectedRecord(raw) ? cipher.Unprotect(raw) : throw new InvalidDataException("Metadata history was not protected after migration."); }
                finally { CryptographicOperations.ZeroMemory(raw); }
            }
            try { return ParseEnvelope(plain, fileId); }
            finally { if (cipher is not null) CryptographicOperations.ZeroMemory(plain); }
        }
        finally { cipher?.Dispose(); }
    }

    private MetadataConflictHistory ParseEnvelope(byte[] bytes, string fileId)
    {
        Envelope? envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope>(bytes, Options); }
        catch (JsonException ex) { throw new InvalidDataException("Metadata history is corrupt. Its file was kept; restore a profile backup before retrying.", ex); }
        if (envelope?.History is null || envelope.Sha256 != Hash(envelope.History))
            throw new InvalidDataException("Metadata history is corrupt. Its file was kept; restore a profile backup before retrying.");
        Validate(envelope.History, fileId);
        return envelope.History;
    }

    private byte[] SerializeForPath(string path, MetadataConflictHistory history)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Envelope(Hash(history), history), Options);
        if (plain.Length > MaxBytes) { CryptographicOperations.ZeroMemory(plain); throw new InvalidDataException("Metadata history exceeds the safe size limit. Its file was kept."); }
        var cipher = CipherFor(path);
        try
        {
            if (cipher is null) return plain;
            (profileLease ?? throw new InvalidOperationException("A local profile lease is required to protect metadata history.")).RequireWithin(directory);
            return cipher.Protect(plain);
        }
        finally { cipher?.Dispose(); if (cipher is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    private async Task<byte[]> ReadBoundedAsync(string path, CancellationToken token)
    {
        if (LocalFileSystemPathGuard.ContainsReparsePoint(path)) throw new InvalidDataException("The metadata history path is a filesystem link. Its file was kept.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, true);
        if (stream.Length > MaxBytes + 76) throw new InvalidDataException("Metadata history exceeds the safe size limit. Its file was kept.");
        var bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes, token); return bytes;
    }

    internal static bool SameOrganization(FileManifest left, FileManifest right) =>
        left.FileName == right.FileName && left.FolderPath == right.FolderPath && left.IsInTrash == right.IsInTrash &&
        left.IsFavorite == right.IsFavorite && left.IsArchived == right.IsArchived && left.IsHidden == right.IsHidden &&
        left.FileModifiedAtUtc == right.FileModifiedAtUtc;

    internal FileManifest Portable(FileManifest manifest) => manifest with
    {
        AccountId = string.IsNullOrWhiteSpace(manifest.AccountId) ? accountId : manifest.AccountId,
        Parts = manifest.Parts.Select(part => part with { StagingPath = null }).ToArray(),
        Encryption = manifest.Encryption is null ? null : manifest.Encryption with { StagingPath = null }
    };

    private string PathFor(string fileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (chatId == 0) throw new ArgumentOutOfRangeException(nameof(chatId));
        // Include scope in the filename as well as the application directory and envelope.
        return Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { accountId, chatId, fileId })))) + ".json");
    }

    private static string Hash(MetadataConflictHistory history) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(history, Options)));
}
