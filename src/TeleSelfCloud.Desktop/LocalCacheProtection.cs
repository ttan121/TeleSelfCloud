using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Desktop;

public static class LocalCacheProtection
{
    private const string PolicyFile = "cache-record-policy.tsc";
    public static bool IsConfigured(string root) => File.Exists(Path.Combine(root, PolicyFile)) || File.Exists(Path.Combine(root, "cache-record-migration.tsc"));
    private static void ValidatePolicy(string root)
    {
        var path = Path.Combine(root, PolicyFile);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The cache policy is a link. It was kept.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 4096) throw new InvalidDataException("The cache policy exceeds its limit.");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        using var cipher = LocalDatabaseProtection.RecordCipher(root, "policy"); var plain = cipher.Unprotect(bytes);
        try { if (!plain.AsSpan().SequenceEqual("{\"version\":1,\"cacheVerification\":true}"u8)) throw new InvalidDataException("The cache policy is invalid. It was kept."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static void Request(string root, LocalProfileLease lease)
    {
        lease.RequireWithin(root);
        if (IsConfigured(root)) throw new InvalidOperationException("This workspace already has a cache protection request.");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The workspace is a link. Its files were kept.");
        using var cipher = LocalDatabaseProtection.RecordCipher(root, "policy"); var bytes = cipher.Protect("{\"version\":1,\"cacheVerification\":true}"u8);
        var path = Path.Combine(root, PolicyFile); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); } File.Move(temporary, path, false); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        ValidatePolicy(root);
    }
    public static async Task PrepareAsync(string root, LocalProfileLease lease, CancellationToken token, Action<string>? progress = null)
    {
        lease.RequireWithin(root); if (!IsConfigured(root)) return; ValidatePolicy(root);
        using var state = LocalDatabaseProtection.RecordCipher(root, "state"); using var journal = LocalDatabaseProtection.RecordCipher(root, "migration");
        void Report(string stage) { try { progress?.Invoke(stage); } catch (Exception) { } }
        Report("CacheMigrating"); await LocalCacheStateMigration.MigrateAsync(root, lease, state, journal, token, Report);
        token.ThrowIfCancellationRequested();
    }
    public static LocalCacheVerificationStore OpenStore(string root)
    {
        var path = Path.Combine(root, "local-cache-verifications.json");
        if (!IsConfigured(root)) return new(path);
        ValidatePolicy(root); using var journal = LocalDatabaseProtection.RecordCipher(root, "migration"); LocalCacheStateMigration.RequireReady(root, journal);
        return new(path, LocalDatabaseProtection.RecordCipher(root, "state"), ownsCipher: true);
    }
    public static bool IsReady(string root)
    {
        if (!IsConfigured(root)) return false;
        ValidatePolicy(root); using var journal = LocalDatabaseProtection.RecordCipher(root, "migration"); LocalCacheStateMigration.RequireReady(root, journal); return true;
    }
}
