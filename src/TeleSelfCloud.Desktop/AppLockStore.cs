using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Desktop;

internal sealed class AppLockStore(string path)
{
    private const int SchemaVersion = 1;
    private const int Iterations = 600_000;
    private const int MaximumBytes = 8 * 1024;
    private const int SaltBytes = 16;
    private const int VerifierBytes = 32;
    private static readonly HashSet<int> SupportedIdleTimeouts = [0, 1, 5, 10, 15, 30];
    private readonly string _path = Path.GetFullPath(path);

    private sealed record LockRecord(int Version, int Iterations, string Salt, string Verifier, int IdleTimeoutMinutes);

    public bool IsConfigured => Load() is not null;
    public int IdleTimeoutMinutes => Load()?.IdleTimeoutMinutes ?? 0;

    public void Configure(string passphrase, int idleTimeoutMinutes)
    {
        ValidatePassphrase(passphrase);
        ValidateIdleTimeout(idleTimeoutMinutes);
        if (File.Exists(_path) || LocalFileSystemPathGuard.ContainsReparsePoint(_path))
            throw new InvalidOperationException("An app-lock record already exists or its path is not safe; existing policy was left unchanged.");
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var verifier = Derive(passphrase, salt, Iterations);
        try { Save(new LockRecord(SchemaVersion, Iterations, Convert.ToBase64String(salt), Convert.ToBase64String(verifier), idleTimeoutMinutes)); }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(verifier);
        }
    }

    public bool Verify(string passphrase)
    {
        var record = Load();
        if (record is null) return false;
        if (string.IsNullOrEmpty(passphrase) || passphrase.Length > 256) return false;
        var salt = Convert.FromBase64String(record.Salt);
        var expected = Convert.FromBase64String(record.Verifier);
        var actual = Derive(passphrase, salt, record.Iterations);
        try { return CryptographicOperations.FixedTimeEquals(expected, actual); }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    public void SetIdleTimeout(int minutes)
    {
        ValidateIdleTimeout(minutes);
        var record = Load() ?? throw new InvalidOperationException("The app lock is not configured.");
        Save(record with { IdleTimeoutMinutes = minutes });
    }

    public bool ChangePassphrase(string currentPassphrase, string newPassphrase)
    {
        ValidatePassphrase(newPassphrase);
        var record = Load();
        if (record is null || !Verify(currentPassphrase)) return false;
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var verifier = Derive(newPassphrase, salt, Iterations);
        try { Save(new LockRecord(SchemaVersion, Iterations, Convert.ToBase64String(salt), Convert.ToBase64String(verifier), record.IdleTimeoutMinutes)); }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(verifier);
        }
        return true;
    }

    public bool Remove(string passphrase)
    {
        if (!Verify(passphrase)) return false;
        if (LocalFileSystemPathGuard.ContainsReparsePoint(_path))
            throw new InvalidDataException("The app-lock settings path is a filesystem link.");
        if (File.Exists(_path)) File.Delete(_path);
        return true;
    }

    private LockRecord? Load()
    {
        if (LocalFileSystemPathGuard.ContainsReparsePoint(_path))
            throw new InvalidDataException("The app-lock settings path is a filesystem link.");
        if (!File.Exists(_path)) return null;
        byte[] bytes;
        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
        {
            if (stream.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("The app-lock settings size is invalid.");
            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException("The app-lock settings grew beyond the size limit.");
        }
        LockRecord? record;
        try { record = JsonSerializer.Deserialize<LockRecord>(bytes); }
        catch (JsonException ex) { throw new InvalidDataException("The app-lock settings are malformed.", ex); }
        if (record is null || record.Version != SchemaVersion || record.Iterations != Iterations)
            throw new InvalidDataException("The app-lock settings version or KDF parameters are unsupported.");
        ValidateIdleTimeout(record.IdleTimeoutMinutes);
        if (!TryBase64(record.Salt, SaltBytes) || !TryBase64(record.Verifier, VerifierBytes))
            throw new InvalidDataException("The app-lock verifier is invalid.");
        return record;
    }

    private void Save(LockRecord record)
    {
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("The app-lock settings directory is missing.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (LocalFileSystemPathGuard.ContainsReparsePoint(_path) || LocalFileSystemPathGuard.ContainsReparsePoint(temporaryPath))
                throw new InvalidDataException("The app-lock settings path contains a filesystem link.");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("The app-lock settings exceed the size limit.");
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (LocalFileSystemPathGuard.ContainsReparsePoint(_path) || LocalFileSystemPathGuard.ContainsReparsePoint(temporaryPath))
                throw new InvalidDataException("The app-lock settings path contains a filesystem link.");
            if (File.Exists(_path)) File.Replace(temporaryPath, _path, null, ignoreMetadataErrors: true);
            else File.Move(temporaryPath, _path);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static byte[] Derive(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, VerifierBytes);

    private static bool TryBase64(string value, int requiredLength)
    {
        try { return Convert.FromBase64String(value).Length == requiredLength; }
        catch (FormatException) { return false; }
    }

    private static void ValidatePassphrase(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 8 or > 256)
            throw new ArgumentException("The app-lock passphrase must contain 8 to 256 characters.", nameof(value));
    }

    private static void ValidateIdleTimeout(int value)
    {
        if (!SupportedIdleTimeouts.Contains(value)) throw new ArgumentOutOfRangeException(nameof(value));
    }

}

internal sealed class AppLockAttemptThrottle
{
    private int _failedAttempts;
    private DateTimeOffset _retryAfterUtc;

    public TimeSpan Remaining(DateTimeOffset now) => _retryAfterUtc > now ? _retryAfterUtc - now : TimeSpan.Zero;

    public TimeSpan RecordFailure(DateTimeOffset now)
    {
        _failedAttempts = Math.Min(_failedAttempts + 1, 8);
        var seconds = Math.Min(30, 1 << Math.Min(_failedAttempts - 1, 5));
        _retryAfterUtc = now.AddSeconds(seconds);
        return TimeSpan.FromSeconds(seconds);
    }

    public void Reset()
    {
        _failedAttempts = 0;
        _retryAfterUtc = default;
    }
}
