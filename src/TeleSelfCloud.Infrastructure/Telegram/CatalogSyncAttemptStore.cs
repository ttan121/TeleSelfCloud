using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Infrastructure.Telegram;

public enum CatalogSyncOutcome { Running, Completed, Canceled, Failed, FolderPublicationPending }
public sealed record CatalogSyncAttempt(int SchemaVersion, string AccountId, long ChatId, bool RequestedFull,
    bool WasIncremental, CatalogSyncOutcome Outcome, DateTimeOffset StartedAtUtc, DateTimeOffset UpdatedAtUtc,
    int Pages, int Messages, int ManifestsFound, int FilesIndexed, DateTimeOffset? CatalogCompletedAtUtc,
    IReadOnlyList<string> ObservedFileIds);

public sealed class CatalogSyncAttemptStore(string directory, string accountId, long chatId,
    Func<string, LocalRecordCipher>? cipherFactory = null, LocalProfileLease? profileLease = null)
{
    private const int MaxBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private sealed record Envelope(CatalogSyncAttempt Report, string Sha256);
    private LocalRecordCipher? Cipher() => cipherFactory?.Invoke("account:" + accountId + ":chat:" + chatId + ":report");
    public IDisposable AcquireLease()
    {
        Directory.CreateDirectory(directory);
        try { return new FileStream(PathFor() + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new InvalidOperationException("A sync is already running for this account and vault.", ex); }
    }
    public async Task<CatalogSyncAttempt?> LoadAsync(CancellationToken token)
    {
        var path = PathFor();
        if (!File.Exists(path)) return null;
        return await LoadRecordAsync(path, token);
    }
    public async Task SaveAsync(CatalogSyncAttempt report, CancellationToken token)
    {
        Validate(report);
        var bytes = SerializeRecord(report);
        Directory.CreateDirectory(directory);
        var path = PathFor();
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        if (LocalFileSystemPathGuard.ContainsReparsePoint(path) || LocalFileSystemPathGuard.ContainsReparsePoint(temporary))
        { CryptographicOperations.ZeroMemory(bytes); throw new InvalidDataException("The sync report path is a filesystem link. Its previous record was kept."); }
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            { await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); stream.Flush(true); }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void PreserveInvalidReport()
    {
        var path = PathFor();
        if (!File.Exists(path)) return;
        var preserved = path + "." + Guid.NewGuid().ToString("N") + ".invalid";
        if (LocalFileSystemPathGuard.ContainsReparsePoint(path) || LocalFileSystemPathGuard.ContainsReparsePoint(preserved))
            throw new InvalidDataException("The saved sync report path is a filesystem link. Its recovery record was kept.");
        File.Move(path, preserved, false);
    }
    private string PathFor()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (chatId == 0) throw new ArgumentOutOfRangeException(nameof(chatId));
        return Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { accountId, chatId })))) + ".json");
    }

    private async Task<CatalogSyncAttempt> LoadRecordAsync(string path, CancellationToken token)
    {
        var cipher = Cipher();
        try
        {
            var raw = await ReadBoundedAsync(path, token);
            if (cipher is not null && !LocalRecordCipher.IsProtectedRecord(raw))
            {
                CryptographicOperations.ZeroMemory(raw);
                var lease = profileLease ?? throw new InvalidOperationException("A local profile lease is required to protect sync reports.");
                lease.RequireWithin(directory);
                var stateName = Path.GetFileName(path);
                using var stateCipher = Cipher()!;
                using var migrationCipher = cipherFactory!("account:" + accountId + ":chat:" + chatId + ":migration");
                var migration = new LocalRecordMigration(stateName, "sync-report-migration.tsc", "sync-report-recovery", MaxBytes,
                    bytes => _ = ParseRecord(bytes), null);
                await migration.MigrateAsync(directory, lease, stateCipher, migrationCipher, token, profileLeaseIsExclusive: true);
                raw = await ReadBoundedAsync(path, token);
            }
            byte[] plain;
            if (cipher is null) plain = raw;
            else
            {
                try { plain = LocalRecordCipher.IsProtectedRecord(raw) ? cipher.Unprotect(raw) : throw Invalid(); }
                finally { CryptographicOperations.ZeroMemory(raw); }
            }
            try { return ParseRecord(plain); }
            finally { if (cipher is not null) CryptographicOperations.ZeroMemory(plain); }
        }
        finally { cipher?.Dispose(); }
    }

    private CatalogSyncAttempt ParseRecord(byte[] bytes)
    {
        Envelope? envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope>(bytes, Options); }
        catch (JsonException ex) { throw Invalid(ex); }
        var report = envelope?.Report;
        Validate(report);
        if (envelope!.Sha256 != Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(report, Options)))) throw Invalid();
        return report!;
    }

    private byte[] SerializeRecord(CatalogSyncAttempt report)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Envelope(report,
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(report, Options)))), Options);
        if (plain.Length > MaxBytes) { CryptographicOperations.ZeroMemory(plain); throw new InvalidDataException("The sync report exceeds the safe size limit. The previous report was kept."); }
        var cipher = Cipher();
        try
        {
            if (cipher is null) return plain;
            (profileLease ?? throw new InvalidOperationException("A local profile lease is required to protect sync reports.")).RequireWithin(directory);
            return cipher.Protect(plain);
        }
        finally { cipher?.Dispose(); if (cipher is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    private async Task<byte[]> ReadBoundedAsync(string path, CancellationToken token)
    {
        if (LocalFileSystemPathGuard.ContainsReparsePoint(path)) throw Invalid();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, true);
        if (stream.Length > MaxBytes + 76) throw Invalid();
        var bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes, token); return bytes;
    }

    private static InvalidDataException Invalid(Exception? inner = null) => new("The saved sync report is invalid. Full rescan can preserve it and create a new report.", inner);
    private void Validate(CatalogSyncAttempt? report)
    {
        if (report is null || report.SchemaVersion != 1 || report.AccountId != accountId || report.ChatId != chatId ||
            !Enum.IsDefined(report.Outcome) || report.StartedAtUtc == default || report.UpdatedAtUtc == default ||
            report.Pages < 0 || report.Messages < 0 || report.ManifestsFound < 0 || report.FilesIndexed < 0 ||
            report.ObservedFileIds is null || report.ObservedFileIds.Count > 100000 ||
            report.ObservedFileIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 512) ||
            report.ObservedFileIds.Distinct(StringComparer.Ordinal).Count() != report.ObservedFileIds.Count ||
            (report.Outcome is CatalogSyncOutcome.Completed or CatalogSyncOutcome.FolderPublicationPending && report.CatalogCompletedAtUtc is null))
            throw new InvalidDataException("The saved sync report is invalid. Full rescan can preserve it and create a new report.");
    }
}
