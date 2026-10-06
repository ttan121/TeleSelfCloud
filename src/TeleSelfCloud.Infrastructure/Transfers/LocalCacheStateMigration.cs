using System.Text.Json;

namespace TeleSelfCloud.Infrastructure.Transfers;

/// <summary>Offline migration of one fixed cache record. Caller owns profile lease and existing ciphers.</summary>
public static class LocalCacheStateMigration
{
    private static readonly LocalRecordMigration Migration = new("local-cache-verifications.json", "cache-record-migration.tsc", "cache-record-migrations", LocalRecordCipher.MaxPlaintextBytes, Validate, "{}"u8.ToArray());
    public static void RequireReady(string root, LocalRecordCipher journalCipher) => Migration.RequireReady(root, journalCipher);
    public static Task MigrateAsync(string root, LocalProfileLease lease, LocalRecordCipher stateCipher, LocalRecordCipher journalCipher,
        CancellationToken token, Action<string>? checkpoint = null) => Migration.MigrateAsync(root, lease, stateCipher, journalCipher, token, checkpoint);
    private static void Validate(byte[] plain)
    {
        if (plain.Length > LocalRecordCipher.MaxPlaintextBytes) throw new InvalidDataException("The cache source exceeds its migration limit.");
        var state = JsonSerializer.Deserialize<Dictionary<string, LocalCacheVerification>>(plain) ?? throw new InvalidDataException("The cache source is empty.");
        if (state.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is null || p.Value.FileId != p.Key || p.Value.LogicalSize < 0 || !HashValid(p.Value.ManifestFingerprint) ||
            !Enum.IsDefined(p.Value.State) || p.Value.PartCount < 1 || p.Value.ValidParts < 0 || p.Value.ValidParts > p.Value.PartCount || p.Value.Parts is null || p.Value.Parts.Count != p.Value.PartCount || p.Value.Parts.Any(s => s is null)))
            throw new InvalidDataException("The cache source failed validation. Its file was kept.");
    }
    private static bool HashValid(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
