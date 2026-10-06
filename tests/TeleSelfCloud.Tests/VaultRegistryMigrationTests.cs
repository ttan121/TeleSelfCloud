using System.Security.Cryptography;
using System.Text;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class VaultRegistryMigrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.RegistryMigration", Guid.NewGuid().ToString("N"));
    private readonly string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private readonly string scope = Guid.NewGuid().ToString("N");
    private string State => Path.Combine(root, "vaults.json");
    private string Journal => Path.Combine(root, "vault-registry-migration.tsc");
    private LocalRecordCipher Cipher(string identity) => new(key, scope, "vault-registry", "account:42:" + identity);
    public VaultRegistryMigrationTests() => Directory.CreateDirectory(root);
    private async Task<byte[]> LegacyAsync()
    {
        var registry = new TelegramVaultRegistry(root, "42");
        await registry.RegisterAsync(new(-101, "42", "Private primary") { CreationRequestId = "0123456789abcdef0123456789abcdef" }, true, default);
        await registry.RegisterAsync(new(-102, "42", "Private secondary"), true, default);
        // Keep exact legacy bytes, including harmless whitespace, rather than reserializing the source.
        File.AppendAllText(State, "\r\n "); return File.ReadAllBytes(State);
    }
    [Theory]
    [InlineData("Prepared")]
    [InlineData("Verified")]
    [InlineData("Switching")]
    [InlineData("AfterReplace")]
    [InlineData("Switched")]
    [InlineData("OriginalProtected")]
    [InlineData("Ready")]
    public async Task RestartAtEveryDurableBoundaryRetainsExactMappingAndVerifiedOriginal(string fault)
    {
        var original = await LegacyAsync(); using var lease = LocalProfileLease.TryAcquire(root)!;
        using var state = Cipher("state"); using var journal = Cipher("migration");
        await Assert.ThrowsAsync<IOException>(() => TelegramVaultRegistryMigration.MigrateAsync(root, "42", lease, state, journal, default,
            stage => { if (stage == fault) throw new IOException(); }));
        await TelegramVaultRegistryMigration.MigrateAsync(root, "42", lease, state, journal, default);
        TelegramVaultRegistryMigration.RequireReady(root, "42", journal);
        Assert.Equal(original, state.Unprotect(File.ReadAllBytes(State)));
        var registry = new TelegramVaultRegistry(root, "42", state); var loaded = (await registry.LoadAsync(default))!;
        Assert.Equal(-101, loaded.PrimaryChatId); Assert.Equal(-102, loaded.ActiveChatId);
        Assert.Equal(root, registry.GetDataDirectory(loaded, -101)); Assert.StartsWith(Path.Combine(root, "vaults"), registry.GetDataDirectory(loaded, -102));
        Assert.Equal("0123456789abcdef0123456789abcdef", loaded.Vaults.Single(v => v.ChatId == -101).CreationRequestId);
        var work = Assert.Single(Directory.GetDirectories(Path.Combine(root, "vault-registry-migrations")));
        Assert.Equal(original, state.Unprotect(File.ReadAllBytes(Path.Combine(work, "original.tsc"))));
        Assert.False(File.Exists(Path.Combine(work, "old-raw"))); Assert.DoesNotContain("Private", Encoding.UTF8.GetString(File.ReadAllBytes(State)));
        await registry.SelectAsync(-101, default); var updated = File.ReadAllBytes(State);
        await TelegramVaultRegistryMigration.MigrateAsync(root, "42", lease, state, journal, default);
        Assert.Equal(updated, File.ReadAllBytes(State)); Assert.Equal(-101, (await registry.LoadAsync(default))!.ActiveChatId);
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
    }
    [Theory]
    [InlineData("account")]
    [InlineData("checksum")]
    [InlineData("oversize")]
    public async Task InvalidLegacyRegistryNeverCreatesPlanOrReplacesSource(string damage)
    {
        await LegacyAsync();
        if (damage == "checksum") File.WriteAllText(State, File.ReadAllText(State).Replace("Private primary", "Changed primary"));
        if (damage == "oversize") File.WriteAllBytes(State, new byte[1024 * 1024 + 77]);
        var before = File.ReadAllBytes(State); using var lease = LocalProfileLease.TryAcquire(root)!;
        using var state = Cipher("state"); using var journal = Cipher("migration");
        await Assert.ThrowsAsync<InvalidDataException>(() => TelegramVaultRegistryMigration.MigrateAsync(root, damage == "account" ? "99" : "42", lease, state, journal, default));
        Assert.Equal(before, File.ReadAllBytes(State)); Assert.False(File.Exists(Journal));
    }
    [Theory]
    [InlineData("Verified", false)]
    [InlineData("Switching", true)]
    public async Task CancellationKeepsPlainBeforeIntentAndFinishesAfterIntent(string stage, bool committed)
    {
        var original = await LegacyAsync(); using var lease = LocalProfileLease.TryAcquire(root)!;
        using var state = Cipher("state"); using var journal = Cipher("migration"); using var stop = new CancellationTokenSource();
        var migration = TelegramVaultRegistryMigration.MigrateAsync(root, "42", lease, state, journal, stop.Token, current => { if (current == stage) stop.Cancel(); });
        if (committed) { await migration; Assert.Equal(original, state.Unprotect(File.ReadAllBytes(State))); }
        else { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => migration); Assert.Equal(original, File.ReadAllBytes(State)); }
        await TelegramVaultRegistryMigration.MigrateAsync(root, "42", lease, state, journal, default);
    }
    [Fact]
    public async Task MissingRegistryDoesNotInventPrimaryAndMissingReadyFileDoesNotRecreate()
    {
        using var lease = LocalProfileLease.TryAcquire(root)!; using var state = Cipher("state"); using var journal = Cipher("migration");
        await Assert.ThrowsAsync<InvalidDataException>(() => TelegramVaultRegistryMigration.MigrateAsync(root, "42", lease, state, journal, default));
        Assert.False(File.Exists(State)); Assert.False(File.Exists(Journal));
        await LegacyAsync(); await TelegramVaultRegistryMigration.MigrateAsync(root, "42", lease, state, journal, default);
        File.Delete(State); Assert.Throws<InvalidDataException>(() => TelegramVaultRegistryMigration.RequireReady(root, "42", journal));
        await Assert.ThrowsAsync<FileNotFoundException>(() => TelegramVaultRegistryMigration.MigrateAsync(root, "42", lease, state, journal, default)); Assert.False(File.Exists(State));
    }
    [Fact]
    public async Task HeldRegistryWriterPreventsMigrationBeforePlan()
    {
        var original = await LegacyAsync(); using var lease = LocalProfileLease.TryAcquire(root)!;
        using var state = Cipher("state"); using var journal = Cipher("migration");
        using var writer = new FileStream(State + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => TelegramVaultRegistryMigration.MigrateAsync(root, "42", lease, state, journal, default));
        Assert.Equal(original, File.ReadAllBytes(State)); Assert.False(File.Exists(Journal));
    }
    [Fact]
    public async Task CorruptOriginalArchiveKeepsRawRecoveryCopyUntilRepaired()
    {
        var original = await LegacyAsync(); using var lease = LocalProfileLease.TryAcquire(root)!;
        using var state = Cipher("state"); using var journal = Cipher("migration");
        await Assert.ThrowsAsync<IOException>(() => TelegramVaultRegistryMigration.MigrateAsync(root, "42", lease, state, journal, default, stage => { if (stage == "Switched") throw new IOException(); }));
        var work = Assert.Single(Directory.GetDirectories(Path.Combine(root, "vault-registry-migrations"))); var archive = Path.Combine(work, "original.tsc");
        var correct = File.ReadAllBytes(archive); var bad = correct.ToArray(); bad[^1] ^= 1; File.WriteAllBytes(archive, bad);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => TelegramVaultRegistryMigration.MigrateAsync(root, "42", lease, state, journal, default));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(work, "old-raw")));
        File.WriteAllBytes(archive, correct); await TelegramVaultRegistryMigration.MigrateAsync(root, "42", lease, state, journal, default);
        Assert.False(File.Exists(Path.Combine(work, "old-raw")));
    }
    [Fact]
    public async Task ForeignJournalCannotFallbackAndChangedSourceCannotSwitch()
    {
        var original = await LegacyAsync(); using var lease = LocalProfileLease.TryAcquire(root)!;
        using var state = Cipher("state"); using var journal = Cipher("migration");
        await Assert.ThrowsAsync<IOException>(() => TelegramVaultRegistryMigration.MigrateAsync(root, "42", lease, state, journal, default, stage => { if (stage == "Verified") throw new IOException(); }));
        using var wrong = new LocalRecordCipher(key, Guid.NewGuid().ToString("N"), "vault-registry", "account:42:migration");
        await Assert.ThrowsAnyAsync<CryptographicException>(() => TelegramVaultRegistryMigration.MigrateAsync(root, "42", lease, state, wrong, default)); Assert.Equal(original, File.ReadAllBytes(State));
        File.AppendAllText(State, " "); var changed = File.ReadAllBytes(State);
        await Assert.ThrowsAsync<InvalidDataException>(() => TelegramVaultRegistryMigration.MigrateAsync(root, "42", lease, state, journal, default)); Assert.Equal(changed, File.ReadAllBytes(State));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
