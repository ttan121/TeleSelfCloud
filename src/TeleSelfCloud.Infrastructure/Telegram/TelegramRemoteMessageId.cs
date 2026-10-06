using System.Globalization;

namespace TeleSelfCloud.Infrastructure.Telegram;

public readonly record struct TelegramRemoteMessageId(long ChatId, long MessageId)
{
    public static TelegramRemoteMessageId Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var separator = value.IndexOf('/');
        if (separator <= 0 || separator != value.LastIndexOf('/') || separator == value.Length - 1)
            throw new InvalidDataException("Invalid Telegram remote message reference.");
        var chatText = value[..separator];
        var messageText = value[(separator + 1)..];
        if (!long.TryParse(chatText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var chatId) || chatId == 0 ||
            !long.TryParse(messageText, NumberStyles.None, CultureInfo.InvariantCulture, out var messageId) || messageId <= 0 ||
            chatId.ToString(CultureInfo.InvariantCulture) != chatText || messageId.ToString(CultureInfo.InvariantCulture) != messageText)
            throw new InvalidDataException("Invalid Telegram remote message reference.");
        return new TelegramRemoteMessageId(chatId, messageId);
    }
}
