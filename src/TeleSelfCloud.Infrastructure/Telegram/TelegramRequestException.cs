using System.Globalization;
using System.Text.RegularExpressions;

namespace TeleSelfCloud.Infrastructure.Telegram;

/// <summary>TDLib request or message-send failure with retry metadata when the server provides it.</summary>
public sealed class TelegramRequestException : IOException
{
    private static readonly Regex RetryAfterPattern = new(
        @"(?:retry\s+after|FLOOD_WAIT[_\s:]+)(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public int? ErrorCode { get; }
    public TimeSpan? RetryAfter { get; }

    private TelegramRequestException(int? errorCode, string message, TimeSpan? retryAfter)
        : base(message)
    {
        ErrorCode = errorCode;
        RetryAfter = retryAfter;
    }

    public static TelegramRequestException From(int? errorCode, string? detail)
    {
        var safeMessage = errorCode == 406
            ? "Telegram rejected the operation (error 406); server details are intentionally hidden."
            : string.IsNullOrWhiteSpace(detail) ? "Telegram request failed." : detail.Trim();
        TimeSpan? retryAfter = null;
        if (errorCode is 420 or 429 && !string.IsNullOrWhiteSpace(detail))
        {
            var match = RetryAfterPattern.Match(detail);
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                retryAfter = TimeSpan.FromSeconds(seconds);
        }
        return new TelegramRequestException(errorCode, safeMessage, retryAfter);
    }

    public bool IsTransient => ErrorCode is 420 or 429 or 500 or 502 or 503 or 504 ||
        ErrorCode == 400 && (Message.Contains("network", StringComparison.OrdinalIgnoreCase) ||
                             Message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
                             Message.Contains("timeout", StringComparison.OrdinalIgnoreCase));
}
