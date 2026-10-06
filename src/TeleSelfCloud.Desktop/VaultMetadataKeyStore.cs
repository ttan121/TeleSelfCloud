using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Desktop;

public static class VaultMetadataKeyStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private sealed record LocalKey(int Version, string AccountId, long ChatId, string KeyId, string ProtectedKey, string Proof);
    private sealed record Policy(int Version, string AccountId, long ChatId, string KeyId, string Proof);
    public static bool IsConfigured(string root) => File.Exists(Path.Combine(root, "metadata-key-policy.json")) || File.Exists(Path.Combine(root, "metadata-key.dpapi.json"));
    private static byte[] Entropy(string account, long chat) => Encoding.UTF8.GetBytes($"TeleSelfCloud vault metadata key v1:{account}:{chat.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    private static byte[] ReadBounded(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 65536) throw new InvalidDataException("The local metadata key record is invalid. It was kept.");
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return bytes;
    }
    public static VaultMetadataKey? Load(string root, string account, long chat)
        => LoadCore(root, account, chat);
    /// <summary>Authenticates retained records before deciding whether their key belongs in a newly isolated vault.</summary>
    public static VaultMetadataKey? LoadForIsolatedVault(string root, string account, long chat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        if (chat == 0) throw new InvalidDataException("The isolated vault has invalid metadata key scope.");
        var key = LoadCore(root, account, expectedChat: null);
        if (key is null || key.ChatId == chat) return key;
        key.Dispose();
        return null;
    }
    private static VaultMetadataKey? LoadCore(string root, string account, long? expectedChat)
    {
        if (!IsConfigured(root)) return null;
        var recordPath = Path.Combine(root, "metadata-key.dpapi.json");
        var policyPath = Path.Combine(root, "metadata-key-policy.json");
        if (LocalFileSystemPathGuard.ContainsReparsePoint(recordPath) || LocalFileSystemPathGuard.ContainsReparsePoint(policyPath))
            throw new InvalidDataException("The local metadata key records are linked. They were kept.");
        var record = JsonSerializer.Deserialize<LocalKey>(ReadBounded(recordPath), Options)
            ?? throw new InvalidDataException("The local metadata key record is invalid. It was kept.");
        if (record.Version != 1 || record.AccountId != account || record.ChatId == 0 || (expectedChat is not null && record.ChatId != expectedChat))
            throw new InvalidDataException("The metadata key belongs to another account or vault.");
        if (string.IsNullOrWhiteSpace(record.ProtectedKey) || string.IsNullOrWhiteSpace(record.KeyId) || string.IsNullOrWhiteSpace(record.Proof))
            throw new InvalidDataException("The local metadata key record is invalid. It was kept.");
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(record.ProtectedKey), Entropy(account, record.ChatId), DataProtectionScope.CurrentUser);
        VaultMetadataKey? key = null;
        try
        {
            key = new(account, record.ChatId, record.KeyId, bytes);
            if (record.Proof != key.ScopeProof()) throw new CryptographicException("The local metadata key failed authentication.");
            ValidatePolicy(root, key); return key;
        }
        catch { key?.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static void ValidatePolicy(string root, VaultMetadataKey key)
    {
        var policy = JsonSerializer.Deserialize<Policy>(ReadBounded(Path.Combine(root, "metadata-key-policy.json")), Options);
        if (policy is null || policy.Version != 1 || policy.AccountId != key.AccountId || policy.ChatId != key.ChatId || policy.KeyId != key.KeyId || policy.Proof != key.ScopeProof())
            throw new InvalidDataException("The recovery key does not match this vault's saved metadata key policy.");
    }
    public static void Save(string root, VaultMetadataKey key, bool recoverExisting = false)
    {
        Directory.CreateDirectory(root);
        using var lease = new FileStream(Path.Combine(root, "metadata-key.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (IsConfigured(root) && !recoverExisting) throw new InvalidOperationException("This vault already has a metadata key. Recover it instead of creating a replacement.");
        var policyPath = Path.Combine(root, "metadata-key-policy.json");
        if (File.Exists(policyPath)) ValidatePolicy(root, key);
        else
        {
            var existingPath = Path.Combine(root, "metadata-key.dpapi.json");
            if (File.Exists(existingPath))
            {
                // A lost policy must not turn explicit recovery into key replacement.
                // Binding fields remain readable even on a different Windows user.
                var existing = JsonSerializer.Deserialize<LocalKey>(ReadBounded(existingPath), Options);
                if (existing is null || existing.Version != 1 || existing.AccountId != key.AccountId || existing.ChatId != key.ChatId || existing.KeyId != key.KeyId || existing.Proof != key.ScopeProof())
                    throw new InvalidDataException("The recovery key does not match this vault's existing metadata key record. Both were kept.");
            }
            AtomicWrite(policyPath, JsonSerializer.SerializeToUtf8Bytes(new Policy(1, key.AccountId, key.ChatId, key.KeyId, key.ScopeProof()), Options), overwrite: false);
        }
        var bytes = key.ExportKeyBytes();
        try
        {
            var protectedBytes = ProtectedData.Protect(bytes, Entropy(key.AccountId, key.ChatId), DataProtectionScope.CurrentUser);
            var path = Path.Combine(root, "metadata-key.dpapi.json");
            if (File.Exists(path)) File.Copy(path, path + "." + Guid.NewGuid().ToString("N") + ".backup", false);
            AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(new LocalKey(1, key.AccountId, key.ChatId, key.KeyId, Convert.ToBase64String(protectedBytes), key.ScopeProof()), Options), recoverExisting);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public static void WriteBackup(string path, VaultMetadataBackup backup) => AtomicWrite(Path.GetFullPath(path), JsonSerializer.SerializeToUtf8Bytes(backup, Options), overwrite: true);
    public static VaultMetadataBackup ReadBackup(string path) => JsonSerializer.Deserialize<VaultMetadataBackup>(ReadBounded(path), Options)
        ?? throw new InvalidDataException("The metadata key backup is empty.");
    private static void AtomicWrite(string path, byte[] bytes, bool overwrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
