using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Infrastructure.Telegram;

/// <summary>Offline migration of an existing account registry. Caller owns profile lease and scoped ciphers.</summary>
public static class TelegramVaultRegistryMigration
{
    private static LocalRecordMigration Migration(string accountDirectory, string accountId) => new(
        "vaults.json", "vault-registry-migration.tsc", "vault-registry-migrations", 1024 * 1024,
        new TelegramVaultRegistry(accountDirectory, accountId).ValidatePlaintext, null);

    public static void RequireReady(string accountDirectory, string accountId, LocalRecordCipher journalCipher) =>
        Migration(accountDirectory, accountId).RequireReady(accountDirectory, journalCipher);

    public static Task MigrateAsync(string accountDirectory, string accountId, LocalProfileLease lease,
        LocalRecordCipher stateCipher, LocalRecordCipher journalCipher, CancellationToken token, Action<string>? checkpoint = null) =>
        Migration(accountDirectory, accountId).MigrateAsync(accountDirectory, lease, stateCipher, journalCipher, token, checkpoint);
}
