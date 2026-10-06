using System.Security.Cryptography;
using TeleSelfCloud.Infrastructure.Telegram;

namespace TeleSelfCloud.Infrastructure.Transfers;

/// <summary>Only fixed product text or explicitly known protocol tokens may be persisted.</summary>
public static class SafeFailure
{
    public const string Unknown = "Transfer failed. Check the connection and saved source/destination, then retry. Details were omitted for privacy.";
    public const string Storage = "Local storage failed. Check free space and access to the saved source/destination, then retry. Details were omitted for privacy.";
    public const string Integrity = "Saved or remote data failed validation. Keep the profile and check recovery before retrying. Details were omitted for privacy.";
    public const string Crypto = "A protected key or document could not be opened. Recover the correct key before retrying. Details were omitted for privacy.";
    public const string Access = "Access was denied. Check access to the saved source/destination and retry. Details were omitted for privacy.";
    public const string Telegram = "Telegram rejected the operation. Check the connection, account and vault, then retry. Server details were omitted for privacy.";
    private static readonly HashSet<string> Fixed = new(StringComparer.Ordinal)
    {
        Unknown, Storage, Integrity, Crypto, Access, Telegram,
        "The app closed before this transfer finished. Resume it when ready.",
        "The metadata key belongs to another account or vault.",
        "Recover this vault's metadata key before publishing. Its protected files were kept.",
        "The file must be relinked to its original source before this upload can continue.",
        "The source file changed after staging. Prepare it again before uploading."
    };
    private static readonly HashSet<string> TelegramTokens = new(StringComparer.Ordinal)
    {
        "FILE_PART_INVALID", "FILE_PARTS_INVALID", "FILE_TOO_BIG", "CHAT_WRITE_FORBIDDEN", "CHANNEL_PRIVATE",
        "SESSION_REVOKED", "AUTH_KEY_UNREGISTERED", "USER_DEACTIVATED", "NETWORK_ERROR", "CONNECTION_TIMEOUT"
    };
    public static string Describe(Exception error) => error switch
    {
        TelegramRequestException telegram when TelegramTokens.Contains(telegram.Message) => telegram.Message,
        TelegramRequestException => Telegram,
        CryptographicException => Crypto,
        InvalidDataException => Integrity,
        UnauthorizedAccessException => Access,
        IOException => Storage,
        _ => Unknown
    };
    public static string? Normalize(string? error)
    {
        if (error is null) return null;
        if (error.Length > 2048) return Unknown;
        if (string.IsNullOrWhiteSpace(error)) return null;
        return Fixed.Contains(error) || TelegramTokens.Contains(error) ? error : Unknown;
    }
    public static string Code(Exception error) => error switch
    {
        TelegramRequestException => "TELEGRAM_FAILURE",
        CryptographicException => "KEY_OR_AUTHENTICATION_FAILURE",
        InvalidDataException => "DATA_VALIDATION_FAILURE",
        UnauthorizedAccessException => "ACCESS_DENIED",
        IOException => "LOCAL_STORAGE_FAILURE",
        OperationCanceledException => "CANCELED",
        _ => "UNEXPECTED_FAILURE"
    };
}
