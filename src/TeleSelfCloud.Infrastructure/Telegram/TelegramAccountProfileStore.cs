using System.Globalization;
using System.Text.Json;

namespace TeleSelfCloud.Infrastructure.Telegram;

/// <summary>Resolves account-specific local paths and safely imports the legacy single-account channel setting.</summary>
public static class TelegramAccountProfileStore
{
    public static string GetDirectory(string localRoot, string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localRoot);
        if (!long.TryParse(accountId, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId) || numericId <= 0)
            throw new ArgumentException("A positive numeric Telegram account ID is required.", nameof(accountId));

        return Path.Combine(Path.GetFullPath(localRoot), "accounts", numericId.ToString(CultureInfo.InvariantCulture));
    }

    public static string GetStorageChannelSettingsPath(string localRoot, string accountId, string legacySettingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacySettingsPath);
        var profileDirectory = GetDirectory(localRoot, accountId);
        var settingsPath = Path.Combine(profileDirectory, "storage-channel.json");
        if (File.Exists(settingsPath) || !File.Exists(legacySettingsPath)) return settingsPath;

        TelegramStorageChannelInfo? legacy;
        try
        {
            legacy = JsonSerializer.Deserialize<TelegramStorageChannelInfo>(File.ReadAllText(legacySettingsPath));
        }
        catch (JsonException)
        {
            // Leave malformed legacy settings untouched; the channel service can
            // still recover a marked channel from Telegram's chat list.
            return settingsPath;
        }

        if (legacy is null || !string.Equals(legacy.AccountId, accountId, StringComparison.Ordinal))
            return settingsPath;

        Directory.CreateDirectory(profileDirectory);
        var temporaryPath = settingsPath + ".tmp";
        File.Copy(legacySettingsPath, temporaryPath, overwrite: true);
        File.Move(temporaryPath, settingsPath, overwrite: true);
        return settingsPath;
    }
}
