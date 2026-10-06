using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;

namespace TeleSelfCloud.Infrastructure.Telegram;

/// <summary>Places the active TDLib database under the recognized account profile.</summary>
[SupportedOSPlatform("windows")]
public static class TelegramSessionProfileStore
{
    private sealed record SessionPointer(string AccountId, string SessionDirectoryName);
    private sealed record SignInPointer(string SessionDirectoryName);
    private sealed record PendingMove(string AccountId, string SourceDirectory, string TargetDirectoryName);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const int MaximumPointerBytes = 16 * 1024;
    private static readonly byte[] ProtectedPointerMagic = "TSCPTR01"u8.ToArray();
    private static readonly byte[] ProtectedPointerEntropy = "TeleSelfCloud.TelegramSessionPointers.v1"u8.ToArray();

    public static string ResolveForRestore(string localRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localRoot);
        var root = Path.GetFullPath(localRoot);
        var pendingPath = Path.Combine(root, "telegram-session-move.pending.json");
        var pending = ReadPointerIfPresent<PendingMove>(pendingPath);
        if (pending is not null)
        {
            if (!IsValidAccountId(pending.AccountId) || !IsSafeSessionName(pending.TargetDirectoryName) ||
                string.IsNullOrWhiteSpace(pending.SourceDirectory) || !Path.IsPathFullyQualified(pending.SourceDirectory))
                throw new InvalidDataException("The pending Telegram session move record is invalid.");
            var target = GetAccountSessionDirectory(root, pending.AccountId, pending.TargetDirectoryName);
            if (Directory.Exists(target)) return target;
        }

        var signInPointerPath = Path.Combine(root, "telegram-signin-session.json");
        var signInPointer = ReadPointerIfPresent<SignInPointer>(signInPointerPath);
        if (signInPointer is not null)
        {
            if (!IsSafeSessionName(signInPointer.SessionDirectoryName))
                throw new InvalidDataException("The Telegram sign-in session pointer is invalid.");
            var signInDirectory = Path.Combine(root, "telegram-login", signInPointer.SessionDirectoryName);
            if (Directory.Exists(signInDirectory)) return signInDirectory;
        }

        var pointerPath = Path.Combine(root, "telegram-active-session.json");
        var pointer = ReadPointerIfPresent<SessionPointer>(pointerPath);
        if (pointer is not null)
        {
            if (!IsValidAccountId(pointer.AccountId) || !IsSafeSessionName(pointer.SessionDirectoryName))
                throw new InvalidDataException("The active Telegram session pointer is invalid.");
            var active = GetAccountSessionDirectory(root, pointer.AccountId, pointer.SessionDirectoryName);
            if (Directory.Exists(active)) return active;
            throw new InvalidDataException("The active Telegram session pointer refers to a missing session directory.");
        }

        return Path.Combine(root, "telegram-account");
    }

    public static string ResolveForRestore(string localRoot, string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        var root = Path.GetFullPath(localRoot);
        var sessionsRoot = Path.GetFullPath(Path.Combine(TelegramAccountProfileStore.GetDirectory(root, accountId), "tdlib"));
        var active = Path.GetFullPath(ResolveForRestore(root));
        if (string.Equals(Path.GetDirectoryName(active), sessionsRoot, StringComparison.OrdinalIgnoreCase) &&
            IsSafeSessionName(Path.GetFileName(active)))
            return active;

        var sessions = Directory.Exists(sessionsRoot) ? Directory.GetDirectories(sessionsRoot) : Array.Empty<string>();
        var populatedSessions = sessions
            .Where(session => Directory.EnumerateFiles(session, "*", SearchOption.AllDirectories).Any())
            .ToArray();
        return populatedSessions.Length switch
        {
            1 => populatedSessions[0],
            0 when sessions.Length == 0 => throw new DirectoryNotFoundException($"No TDLib session exists for account {accountId}."),
            0 => throw new InvalidOperationException($"No populated TDLib session exists for account {accountId}."),
            _ => throw new InvalidOperationException($"The active TDLib session for account {accountId} is ambiguous.")
        };
    }

    public static string CreateSignInDirectory(string localRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localRoot);
        var root = Path.GetFullPath(localRoot);
        var sessionName = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(root, "telegram-login", sessionName);
        Directory.CreateDirectory(directory);
        AtomicWrite(Path.Combine(root, "telegram-signin-session.json"),
            JsonSerializer.Serialize(new SignInPointer(sessionName), JsonOptions));
        return directory;
    }

    public static bool IsAccountProfileDirectory(string localRoot, string directory)
    {
        var root = Path.GetFullPath(Path.Combine(localRoot, "accounts")) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(directory);
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
               candidate.Contains($"{Path.DirectorySeparatorChar}tdlib{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    public static string MoveToAccountProfile(string localRoot, string accountId, string sourceDirectory, bool persistForRestore = true)
    {
        var root = Path.GetFullPath(localRoot);
        var profileRoot = TelegramAccountProfileStore.GetDirectory(root, accountId);
        var sessionsRoot = Path.Combine(profileRoot, "tdlib");
        var sessionName = Guid.NewGuid().ToString("N");
        var target = GetAccountSessionDirectory(root, accountId, sessionName);
        Directory.CreateDirectory(sessionsRoot);

        var pendingPath = Path.Combine(root, "telegram-session-move.pending.json");
        AtomicWrite(pendingPath, JsonSerializer.Serialize(new PendingMove(accountId, Path.GetFullPath(sourceDirectory), sessionName), JsonOptions));
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException("The TDLib session directory disappeared before account isolation.");
        Directory.Move(Path.GetFullPath(sourceDirectory), target);
        if (persistForRestore) PersistCurrentSession(root, accountId, target);
        File.Delete(pendingPath);
        var signInPointer = Path.Combine(root, "telegram-signin-session.json");
        if (File.Exists(signInPointer)) File.Delete(signInPointer);
        return target;
    }

    public static void PersistCurrentSession(string localRoot, string accountId, string sessionDirectory)
    {
        var root = Path.GetFullPath(localRoot);
        var sessionsRoot = Path.GetFullPath(Path.Combine(TelegramAccountProfileStore.GetDirectory(root, accountId), "tdlib")) + Path.DirectorySeparatorChar;
        var fullSessionPath = Path.GetFullPath(sessionDirectory);
        if (!fullSessionPath.StartsWith(sessionsRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The TDLib session is outside the account's private session directory.");
        var sessionName = Path.GetFileName(fullSessionPath);
        if (!IsSafeSessionName(sessionName)) throw new InvalidDataException("The account TDLib session name is invalid.");
        AtomicWrite(Path.Combine(root, "telegram-active-session.json"),
            JsonSerializer.Serialize(new SessionPointer(accountId, sessionName), JsonOptions));
    }

    public static void PersistSignInSession(string localRoot, string sessionDirectory)
    {
        var root = Path.GetFullPath(localRoot);
        var loginRoot = Path.GetFullPath(Path.Combine(root, "telegram-login"));
        var fullSessionPath = Path.GetFullPath(sessionDirectory);
        if (!string.Equals(Path.GetDirectoryName(fullSessionPath), loginRoot, StringComparison.OrdinalIgnoreCase) ||
            !IsSafeSessionName(Path.GetFileName(fullSessionPath)))
            throw new InvalidOperationException("The Telegram sign-in session is outside its private session directory.");
        AtomicWrite(Path.Combine(root, "telegram-signin-session.json"),
            JsonSerializer.Serialize(new SignInPointer(Path.GetFileName(fullSessionPath)), JsonOptions));
    }

    public static void ClearCurrentSessionPointer(string localRoot, string accountId)
    {
        var path = Path.Combine(Path.GetFullPath(localRoot), "telegram-active-session.json");
        if (TryRead<SessionPointer>(path, out var pointer) &&
            string.Equals(pointer.AccountId, accountId, StringComparison.Ordinal))
            File.Delete(path);
    }

    public static void ClearSignInSessionPointer(string localRoot, string sessionDirectory)
    {
        var path = Path.Combine(Path.GetFullPath(localRoot), "telegram-signin-session.json");
        if (TryRead<SignInPointer>(path, out var pointer) &&
            string.Equals(pointer.SessionDirectoryName, Path.GetFileName(Path.GetFullPath(sessionDirectory)), StringComparison.OrdinalIgnoreCase))
            File.Delete(path);
    }

    public static void DiscardSessionDirectory(string localRoot, string? accountId, string sessionDirectory)
    {
        var fullSessionPath = Path.GetFullPath(sessionDirectory);
        if (accountId != null && IsAccountProfileDirectory(localRoot, fullSessionPath))
        {
            var sessionsRoot = Path.GetFullPath(Path.Combine(TelegramAccountProfileStore.GetDirectory(localRoot, accountId), "tdlib"));
            if (!string.Equals(Path.GetDirectoryName(fullSessionPath), sessionsRoot, StringComparison.OrdinalIgnoreCase) ||
                !IsSafeSessionName(Path.GetFileName(fullSessionPath)))
                throw new InvalidOperationException("The Telegram session directory is not a valid account session.");
            if (Directory.Exists(fullSessionPath)) Directory.Delete(fullSessionPath, recursive: true);
            ClearCurrentSessionPointer(localRoot, accountId);
            return;
        }

        var loginRoot = Path.GetFullPath(Path.Combine(localRoot, "telegram-login"));
        if (!string.Equals(Path.GetDirectoryName(fullSessionPath), loginRoot, StringComparison.OrdinalIgnoreCase) ||
            !IsSafeSessionName(Path.GetFileName(fullSessionPath)))
            throw new InvalidOperationException("The Telegram sign-in session directory is not valid.");
        if (Directory.Exists(fullSessionPath)) Directory.Delete(fullSessionPath, recursive: true);
        ClearSignInSessionPointer(localRoot, fullSessionPath);
    }

    private static string GetAccountSessionDirectory(string localRoot, string accountId, string sessionName) =>
        Path.Combine(TelegramAccountProfileStore.GetDirectory(localRoot, accountId), "tdlib", sessionName);

    private static bool IsSafeSessionName(string value) =>
        value is { Length: 32 } && value.All(character => Uri.IsHexDigit(character));

    private static bool IsValidAccountId(string? accountId) =>
        long.TryParse(accountId, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0;

    private static T? ReadPointerIfPresent<T>(string path) where T : class
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A Telegram session pointer cannot be a filesystem link.");
        if ((attributes & FileAttributes.Directory) != 0)
            throw new InvalidDataException("A Telegram session pointer path cannot be a directory.");

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > MaximumPointerBytes)
                throw new InvalidDataException("A Telegram session pointer has an invalid size.");
            var bytes = new byte[MaximumPointerBytes + 1];
            try
            {
                var length = 0;
                while (length < bytes.Length)
                {
                    var read = stream.Read(bytes, length, bytes.Length - length);
                    if (read == 0) break;
                    length += read;
                }
                if (length > MaximumPointerBytes)
                    throw new InvalidDataException("A Telegram session pointer exceeds the allowed size.");
                var jsonBytes = DecodePointerBytes(bytes.AsSpan(0, length));
                try
                {
                    return JsonSerializer.Deserialize<T>(jsonBytes, JsonOptions)
                        ?? throw new InvalidDataException("A Telegram session pointer is empty.");
                }
                finally { CryptographicOperations.ZeroMemory(jsonBytes); }
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("A Telegram session pointer is malformed.", exception);
            }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException("A Telegram session pointer cannot be decrypted for this Windows user.", exception);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (IOException exception)
        {
            throw new InvalidDataException("A Telegram session pointer cannot be read safely.", exception);
        }
    }

    private static bool TryRead<T>(string path, out T value) where T : class
    {
        try
        {
            if (ReadPointerIfPresent<T>(path) is { } parsed)
            {
                value = parsed;
                return true;
            }
        }
        catch (IOException) { }
        catch (JsonException) { }
        catch (CryptographicException) { }
        value = null!;
        return false;
    }

    private static void AtomicWrite(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A Telegram session pointer cannot be replaced through a filesystem link.");
            if ((attributes & FileAttributes.Directory) != 0)
                throw new InvalidDataException("A Telegram session pointer path cannot be a directory.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var jsonBytes = Encoding.UTF8.GetBytes(content);
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = ProtectedData.Protect(jsonBytes, ProtectedPointerEntropy, DataProtectionScope.CurrentUser);
            var bytes = new byte[ProtectedPointerMagic.Length + protectedBytes.Length];
            try
            {
                ProtectedPointerMagic.CopyTo(bytes, 0);
                protectedBytes.CopyTo(bytes, ProtectedPointerMagic.Length);
                if (bytes.Length > MaximumPointerBytes)
                    throw new InvalidDataException("A protected Telegram session pointer exceeds the supported size.");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(jsonBytes);
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static byte[] DecodePointerBytes(ReadOnlySpan<byte> bytes)
    {
        if (!bytes.StartsWith(ProtectedPointerMagic)) return bytes.ToArray();
        if (bytes.Length <= ProtectedPointerMagic.Length)
            throw new InvalidDataException("A protected Telegram session pointer is empty.");
        return ProtectedData.Unprotect(bytes[ProtectedPointerMagic.Length..].ToArray(), ProtectedPointerEntropy, DataProtectionScope.CurrentUser);
    }
}
