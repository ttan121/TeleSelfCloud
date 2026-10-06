using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using TeleSelfCloud.Infrastructure.Telegram;

namespace TeleSelfCloud.Desktop;

internal sealed class TelegramCredentialStore
{
    private const int MaxBytes = 16 * 1024;
    private readonly string _path;

    public TelegramCredentialStore(string path) => _path = path;

    public TelegramAppCredentials? Load()
    {
        if (!File.Exists(_path)) return null;
        RejectLink(_path);
        byte[] protectedBytes;
        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length > MaxBytes)
                throw new InvalidDataException("Telegram credential file exceeds the supported size.");
            protectedBytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(protectedBytes);
        }

        byte[]? clearBytes = null;
        try
        {
            clearBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            TelegramAppCredentials? credentials;
            try
            {
                credentials = JsonSerializer.Deserialize<TelegramAppCredentials>(clearBytes);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Telegram credential file is malformed.", exception);
            }

            if (credentials is null)
                throw new InvalidDataException("Telegram credential file is empty.");
            try { Validate(credentials); }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("Telegram credential file contains invalid credentials.", exception);
            }
            return credentials;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (clearBytes is not null) CryptographicOperations.ZeroMemory(clearBytes);
        }
    }

    public void Save(TelegramAppCredentials credentials)
    {
        Validate(credentials);

        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        RejectLink(_path);
        var clearBytes = JsonSerializer.SerializeToUtf8Bytes(credentials);
        byte[]? protectedBytes = null;
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            protectedBytes = ProtectedData.Protect(clearBytes, null, DataProtectionScope.CurrentUser);
            if (protectedBytes.Length > MaxBytes)
                throw new InvalidDataException("Telegram credential file exceeds the supported size.");
            RejectLink(temporaryPath);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(protectedBytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
            try { File.Delete(temporaryPath); } catch (IOException) { }
        }
    }

    private static void Validate(TelegramAppCredentials? credentials)
    {
        if (credentials is null || credentials.ApiId <= 0 || string.IsNullOrWhiteSpace(credentials.ApiHash)
            || credentials.ApiHash.Length > 256 || credentials.ApiHash.Any(char.IsControl))
            throw new ArgumentException("Valid Telegram API credentials are required.", nameof(credentials));
    }

    private static void RejectLink(string path)
    {
        if (TeleSelfCloud.Infrastructure.Transfers.LocalFileSystemPathGuard.ContainsReparsePoint(path))
            throw new IOException("Telegram credential path contains a reparse point.");
    }
}
