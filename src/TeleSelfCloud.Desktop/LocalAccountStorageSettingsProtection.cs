using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Desktop;

/// <summary>Protects account-level channel routing settings before protected account catalogs open.</summary>
public static class LocalAccountStorageSettingsProtection
{
    private const int MaxPlaintextBytes = 16 * 1024;
    private const int MaxStoredBytes = MaxPlaintextBytes + 76;
    private const string Purpose = "account-storage-settings";

    public static void PrepareOffline(string catalogRoot, LocalProfileLease lease)
    {
        var root = Path.GetFullPath(catalogRoot);
        lease.RequireWithin(root);
        var accountId = AccountIdForRoot(root);
        if (accountId is null || !LocalDatabaseProtection.IsConfigured(root)) return;

        var path = Path.Combine(root, "storage-channel.json");
        RejectPath(path);
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        using var cipher = LocalDatabaseProtection.RecordCipher(root,
            $"account:{accountId}:storage-channel-settings", Purpose);
        var raw = ReadBounded(path);
        try
        {
            var protectedRecord = LocalRecordCipher.IsProtectedRecord(raw);
            if (protectedRecord)
            {
                byte[] plaintext;
                try { plaintext = cipher.Unprotect(raw); }
                catch (CryptographicException) { return; }
                try
                {
                    if (plaintext.Length > MaxPlaintextBytes)
                        throw new InvalidDataException("The protected account storage settings exceed their size limit. The file was kept.");
                }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
                return;
            }

            if (raw.Length > MaxPlaintextBytes)
                throw new InvalidDataException("The account storage settings exceed their size limit. The file was kept.");
            var protectedBytes = cipher.Protect(raw);
            try { AtomicReplace(path, protectedBytes, lease, root); }
            finally { CryptographicOperations.ZeroMemory(protectedBytes); }
        }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }

    private static string? AccountIdForRoot(string root)
    {
        var directory = new DirectoryInfo(root);
        var accounts = directory.Parent;
        if (accounts?.Name.Equals("accounts", StringComparison.OrdinalIgnoreCase) != true) return null;
        var sharedRoot = accounts.Parent?.FullName;
        if (sharedRoot is null || !long.TryParse(directory.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var account) || account <= 0)
            return null;
        var accountId = account.ToString(CultureInfo.InvariantCulture);
        return string.Equals(root, TelegramAccountProfileStore.GetDirectory(sharedRoot, accountId), StringComparison.OrdinalIgnoreCase)
            ? accountId : null;
    }

    private static byte[] ReadBounded(string path)
    {
        RejectPath(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxStoredBytes) throw new InvalidDataException("The account storage settings exceed their size limit. The file was kept.");
        var bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        return bytes;
    }

    private static void AtomicReplace(string path, byte[] bytes, LocalProfileLease lease, string root)
    {
        lease.RequireWithin(root);
        RejectPath(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (LocalFileSystemPathGuard.ContainsReparsePoint(temporary))
                throw new InvalidDataException("The account storage settings temporary path is linked. The source file was kept.");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                output.Write(bytes);
                output.Flush(true);
            }
            lease.RequireWithin(root);
            RejectPath(path);
            File.Replace(temporary, path, null);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void RejectPath(string path)
    {
        if (LocalFileSystemPathGuard.ContainsReparsePoint(path))
            throw new InvalidDataException("The account storage settings path contains a filesystem link. The file was kept.");
        if (Directory.Exists(path))
            throw new InvalidDataException("A directory occupies the account storage settings path. It was kept.");
    }
}
