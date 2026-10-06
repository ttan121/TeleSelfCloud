using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Infrastructure.Telegram;

public enum VaultCreationPhase { Prepared, Dispatched, Confirmed }
public sealed record VaultCreationAttempt(int SchemaVersion, string AccountId, string RequestId, string Title,
    DateTimeOffset CreatedAtUtc, VaultCreationPhase Phase, long? ChatId);

public sealed class VaultCreationJournal(string accountDirectory, string accountId,
    Func<string, LocalRecordCipher>? cipherFactory = null, LocalProfileLease? profileLease = null)
{
    private const int MaxBytes = 65536;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private sealed record Envelope(VaultCreationAttempt Attempt, string Sha256);
    private string PathFor => Path.Combine(Path.GetFullPath(accountDirectory), "vault-creation.json");
    private LocalRecordCipher? CipherFor(string path) => cipherFactory?.Invoke(Path.GetRelativePath(accountDirectory, path).Replace(Path.DirectorySeparatorChar, '/'));
    public bool HasPending => File.Exists(PathFor);
    public IDisposable AcquireLease()
    {
        Directory.CreateDirectory(accountDirectory);
        try { return new FileStream(PathFor + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new InvalidOperationException("A vault creation is already in progress. Retry after it finishes.", ex); }
    }
    public async Task<VaultCreationAttempt?> LoadAsync(CancellationToken token)
    {
        if (!File.Exists(PathFor)) return null;
        return await LoadRecordAsync(PathFor, token);
    }
    public async Task SaveAsync(VaultCreationAttempt attempt, CancellationToken token)
    {
        Validate(attempt);
        await WriteRecordAsync(PathFor, attempt, token, replace: true);
    }
    public async Task ArchiveRegisteredAsync(string expectedRequestId, CancellationToken token)
    {
        var attempt = await LoadAsync(token);
        if (attempt?.RequestId != expectedRequestId || attempt.Phase != VaultCreationPhase.Confirmed) throw Invalid();
        await ArchiveAsync(attempt, "created-vaults", token);
    }
    public async Task AbandonAsync(string expectedRequestId, CancellationToken token)
    {
        using var lease = AcquireLease();
        await PrepareProtectedArchivesAsync(token);
        var attempt = await LoadAsync(token);
        if (attempt?.RequestId != expectedRequestId) throw Invalid();
        await ArchiveAsync(attempt, "abandoned-vault-creations", token);
    }

    public async Task PrepareProtectedArchivesAsync(CancellationToken token)
    {
        if (cipherFactory is null) return;
        var lease = profileLease ?? throw new InvalidOperationException("A local profile lease is required to protect vault creation records.");
        lease.RequireWithin(accountDirectory);
        foreach (var name in new[] { "created-vaults", "abandoned-vault-creations" })
        {
            var directory = Path.Combine(Path.GetFullPath(accountDirectory), name);
            if (LocalFileSystemPathGuard.ContainsReparsePoint(directory)) throw Invalid();
            if (!Directory.Exists(directory)) continue;
            var paths = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Take(2049).ToArray();
            if (paths.Length > 2048) throw new InvalidDataException("The vault creation receipt inventory exceeds its safe limit.");
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                _ = await LoadRecordAsync(path, token);
            }
        }
    }

    private async Task ArchiveAsync(VaultCreationAttempt attempt, string subdirectory, CancellationToken token)
    {
        var directory = Path.Combine(Path.GetFullPath(accountDirectory), subdirectory);
        Directory.CreateDirectory(directory);
        var receipt = Path.Combine(directory, attempt.RequestId + ".json");
        token.ThrowIfCancellationRequested();
        if (File.Exists(receipt))
        {
            var existing = await LoadRecordAsync(receipt, token);
            if (existing != attempt) throw Invalid();
            File.Delete(PathFor);
            return;
        }
        await WriteRecordAsync(receipt, attempt, token, replace: false);
        File.Delete(PathFor);
    }

    private async Task<VaultCreationAttempt> LoadRecordAsync(string path, CancellationToken token)
    {
        var cipher = CipherFor(path);
        try
        {
            var raw = await ReadBoundedAsync(path, token);
            if (cipher is not null && !LocalRecordCipher.IsProtectedRecord(raw))
            {
                CryptographicOperations.ZeroMemory(raw);
                var lease = profileLease ?? throw new InvalidOperationException("A local profile lease is required to protect vault creation records.");
                lease.RequireWithin(accountDirectory);
                var relative = Path.GetRelativePath(accountDirectory, path);
                var safeId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative)));
                using var stateCipher = CipherFor(path)!;
                using var migrationCipher = cipherFactory!("migration:" + safeId);
                var migration = new LocalRecordMigration(relative, ".vault-creation-migration-" + safeId + ".tsc",
                    ".vault-creation-recovery-" + safeId, MaxBytes, bytes => _ = ParseRecord(bytes, path), null);
                await migration.MigrateAsync(accountDirectory, lease, stateCipher, migrationCipher, token,
                    profileLeaseIsExclusive: true);
                raw = await ReadBoundedAsync(path, token);
            }
            byte[] plain;
            if (cipher is null) plain = raw;
            else
            {
                try { plain = LocalRecordCipher.IsProtectedRecord(raw) ? cipher.Unprotect(raw) : throw Invalid(); }
                finally { CryptographicOperations.ZeroMemory(raw); }
            }
            try { return ParseRecord(plain, path); }
            finally { if (cipher is not null) CryptographicOperations.ZeroMemory(plain); }
        }
        finally { cipher?.Dispose(); }
    }

    private VaultCreationAttempt ParseRecord(byte[] bytes, string path)
    {
        Envelope? envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope>(bytes, Options); }
        catch (JsonException ex) { throw Invalid(ex); }
        Validate(envelope?.Attempt);
        if (envelope!.Sha256 != Hash(envelope.Attempt)) throw Invalid();
        var parentName = Path.GetFileName(Path.GetDirectoryName(path));
        var fileName = Path.GetFileName(path);
        if (parentName is "created-vaults" or "abandoned-vault-creations" && fileName != envelope.Attempt.RequestId + ".json")
            throw Invalid();
        return envelope.Attempt;
    }

    private async Task<byte[]> ReadBoundedAsync(string path, CancellationToken token)
    {
        if (LocalFileSystemPathGuard.ContainsReparsePoint(path)) throw Invalid();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, true);
        if (stream.Length > MaxBytes + 76) throw Invalid();
        var bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes, token); return bytes;
    }

    private async Task WriteRecordAsync(string path, VaultCreationAttempt attempt, CancellationToken token, bool replace)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Envelope(attempt, Hash(attempt)), Options);
        if (plain.Length > MaxBytes) { CryptographicOperations.ZeroMemory(plain); throw Invalid(); }
        var cipher = CipherFor(path);
        byte[] bytes;
        try
        {
            if (cipher is null) bytes = plain;
            else
            {
                (profileLease ?? throw new InvalidOperationException("A local profile lease is required to protect vault creation records.")).RequireWithin(accountDirectory);
                bytes = cipher.Protect(plain);
            }
        }
        finally { cipher?.Dispose(); if (cipher is not null) CryptographicOperations.ZeroMemory(plain); }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        if (LocalFileSystemPathGuard.ContainsReparsePoint(path) || LocalFileSystemPathGuard.ContainsReparsePoint(temporary))
        { CryptographicOperations.ZeroMemory(bytes); throw Invalid(); }
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            { await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); stream.Flush(true); }
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, replace);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string Hash(VaultCreationAttempt attempt) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(attempt, Options)));
    private void Validate(VaultCreationAttempt? attempt)
    {
        if (attempt is null || attempt.SchemaVersion != 1 || attempt.AccountId != accountId ||
            !Guid.TryParseExact(attempt.RequestId, "N", out var id) || id.ToString("N") != attempt.RequestId ||
            string.IsNullOrWhiteSpace(attempt.Title) || attempt.Title.Length > 128 || attempt.CreatedAtUtc == default ||
            !Enum.IsDefined(attempt.Phase) || (attempt.Phase == VaultCreationPhase.Confirmed ? attempt.ChatId is null or 0 : attempt.ChatId is not null)) throw Invalid();
    }
    private static InvalidDataException Invalid(Exception? inner = null) => new("The pending vault creation is invalid. It was kept; restore a profile backup before creating another vault.", inner);
}
