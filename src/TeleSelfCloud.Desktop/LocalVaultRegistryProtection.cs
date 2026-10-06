using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Desktop;

/// <summary>Separate account-registry opt-in using the existing portable catalog key.</summary>
public static class LocalVaultRegistryProtection
{
    private const string PolicyFile = "vault-registry-policy.tsc";
    private sealed record Policy(int Version, string AccountId);
    public static bool IsConfigured(string root) => File.Exists(Path.Combine(root, PolicyFile)) || File.Exists(Path.Combine(root, "vault-registry-migration.tsc"));
    private static LocalRecordCipher Cipher(string root, string identity) => LocalDatabaseProtection.RecordCipher(root, identity, "vault-registry");
    private static void RequireAccountRoot(string root, string account)
    {
        var path = Path.GetFullPath(root); var directory = new DirectoryInfo(path);
        var shared = directory.Parent?.Parent?.FullName ?? throw new InvalidDataException("The registry account root is invalid.");
        if (!string.Equals(path, TelegramAccountProfileStore.GetDirectory(shared, account), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The registry account root does not match its account.");
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The registry account root is a link.");
    }
    private static string ReadPolicy(string root)
    {
        var path = Path.Combine(root, PolicyFile);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The registry policy is a link.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 4096) throw new InvalidDataException("The registry policy exceeds its limit.");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        using var cipher = Cipher(root, "policy"); var plain = cipher.Unprotect(bytes);
        try
        {
            var policy = JsonSerializer.Deserialize<Policy>(plain);
            if (policy is null || policy.Version != 1) throw new InvalidDataException("The registry policy is invalid.");
            RequireAccountRoot(root, policy.AccountId); return policy.AccountId;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static async Task RequestAsync(string root, string account, LocalProfileLease lease, CancellationToken token)
    {
        lease.RequireWithin(root); RequireAccountRoot(root, account);
        if (IsConfigured(root)) throw new InvalidOperationException("This account already has a registry protection request.");
        using var cipher = Cipher(root, "policy"); // Requires Ready catalog and its existing key; no new master key.
        using var registry = new TelegramVaultRegistry(root, account, requireExisting: true);
        _ = await registry.LoadAsync(token);
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Policy(1, account));
        var path = Path.Combine(root, PolicyFile); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = cipher.Protect(plain); token.ThrowIfCancellationRequested();
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, false);
        }
        finally { CryptographicOperations.ZeroMemory(plain); if (File.Exists(temporary)) File.Delete(temporary); }
        _ = ReadPolicy(root);
    }
    public static async Task PrepareAsync(string root, LocalProfileLease lease, CancellationToken token, Action<string>? progress = null)
    {
        lease.RequireWithin(root); if (!IsConfigured(root)) return;
        var account = ReadPolicy(root); using var state = Cipher(root, "account:" + account + ":state"); using var journal = Cipher(root, "account:" + account + ":migration");
        await TelegramVaultRegistryMigration.MigrateAsync(root, account, lease, state, journal, token, progress);
        token.ThrowIfCancellationRequested();
    }
    public static void RequireReady(string root, string account)
    {
        if (ReadPolicy(root) != account) throw new InvalidDataException("The registry policy belongs to another account.");
        using var journal = Cipher(root, "account:" + account + ":migration"); TelegramVaultRegistryMigration.RequireReady(root, account, journal);
    }
    public static TelegramVaultRegistry OpenRegistry(string root, string account)
    {
        if (!IsConfigured(root)) return new(root, account);
        RequireReady(root, account);
        return new(root, account, Cipher(root, "account:" + account + ":state"), ownsCipher: true, requireExisting: true);
    }
}
