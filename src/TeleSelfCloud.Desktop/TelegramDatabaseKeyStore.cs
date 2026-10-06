using System.IO;
using System.Security.Cryptography;
using System.Text;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Desktop;

internal sealed record TelegramDatabaseKey(string Value, bool IsProtected, bool IsLegacyPlaintext);

internal static class TelegramDatabaseKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TeleSelfCloud TDLib database key v1");
    private const string KeyFileName = "tdlib-database-key.dpapi";
    private const int MaximumProtectedKeyBytes = 16 * 1024;

    public static TelegramDatabaseKey LoadOrCreate(string sessionDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        var directory = Path.GetFullPath(sessionDirectory);
        RejectReparsePoints(directory);
        Directory.CreateDirectory(directory);
        RejectReparsePoints(directory);
        var keyPath = Path.Combine(directory, KeyFileName);
        RejectReparsePoints(keyPath);
        if (Directory.Exists(keyPath))
            throw new InvalidDataException("The TDLib database key path cannot be a directory.");
        if (File.Exists(keyPath)) return LoadProtectedKey(keyPath);
        var temporaryPath = keyPath + ".tmp";
        RejectReparsePoints(temporaryPath);
        if (Directory.Exists(temporaryPath))
            throw new InvalidDataException("The temporary TDLib database key path cannot be a directory.");
        if (File.Exists(temporaryPath))
        {
            // A prior process can stop after flushing the protected key but before
            // its atomic rename. Promote that exact key before TDLib opens the DB.
            var recovered = LoadProtectedKey(temporaryPath);
            File.Move(temporaryPath, keyPath, overwrite: false);
            return recovered;
        }

        if (Directory.EnumerateFileSystemEntries(directory)
            .Any(path => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(keyPath), StringComparison.OrdinalIgnoreCase)))
            return new TelegramDatabaseKey(string.Empty, IsProtected: false, IsLegacyPlaintext: true);

        var random = RandomNumberGenerator.GetBytes(24);
        var key = Convert.ToBase64String(random);
        CryptographicOperations.ZeroMemory(random);
        var clearBytes = Encoding.ASCII.GetBytes(key);
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = ProtectedData.Protect(clearBytes, Entropy, DataProtectionScope.CurrentUser);
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                output.Write(protectedBytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, keyPath, overwrite: false);
            return new TelegramDatabaseKey(key, IsProtected: true, IsLegacyPlaintext: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch (IOException) { }
        }
    }

    private static TelegramDatabaseKey LoadProtectedKey(string path)
    {
        var protectedBytes = ReadProtectedKeyFile(path);
        byte[]? clearBytes = null;
        try
        {
            clearBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            var value = Encoding.ASCII.GetString(clearBytes);
            if (value.Length != 32 || value.Any(character =>
                    !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/')))
                throw new CryptographicException("The protected TDLib database key has an invalid format.");
            return new TelegramDatabaseKey(value, IsProtected: true, IsLegacyPlaintext: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (clearBytes is not null) CryptographicOperations.ZeroMemory(clearBytes);
        }
    }

    private static byte[] ReadProtectedKeyFile(string path)
    {
        RejectReparsePoints(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > MaximumProtectedKeyBytes)
            throw new InvalidDataException("The protected TDLib database key file has an invalid size.");
        var bytes = new byte[(int)input.Length];
        try
        {
            input.ReadExactly(bytes);
            if (input.ReadByte() != -1)
                throw new InvalidDataException("The protected TDLib database key file exceeds the allowed size.");
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    private static void RejectReparsePoints(string path)
    {
        if (LocalFileSystemPathGuard.ContainsReparsePoint(path))
            throw new InvalidDataException("The TDLib session key path contains a filesystem link.");
    }
}
