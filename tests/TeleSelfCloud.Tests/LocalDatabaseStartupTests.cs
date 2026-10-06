using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class LocalDatabaseStartupTests : IDisposable
{
    private readonly string parent = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.DatabaseStartup", Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(parent, "profile");
    private const string Pass = "portable startup database passphrase";
    private string Backup(string name) => Path.Combine(parent, name + ".tsc-db-key.json");
    public LocalDatabaseStartupTests() => Directory.CreateDirectory(Root);
    private static async Task Seed(string root, string name)
    {
        await new SqliteManifestStore(Path.Combine(root, "manifests.db")).SaveAsync(new(1, "same", name, 1, new string('A', 64), 1, [new(0, 0, 1, new string('A', 64), null, false)], false, "42"), default);
        await new SqliteTransferQueueStore(Path.Combine(root, "manifests.db")).EnqueueAsync("same", name, 1, default);
        SqliteConnection.ClearAllPools();
    }
    [Fact]
    public async Task StartupMigratesSharedPrimaryAndAdditionalVaultBeforeKeyAwareStoresOpen()
    {
        var roots = new[] { Root, Path.Combine(Root, "accounts", "42"), Path.Combine(Root, "accounts", "42", "vaults", "vault-hash") };
        using var lease = LocalProfileLease.TryAcquire(Root)!;
        for (var i = 0; i < roots.Length; i++) { await Seed(roots[i], "name" + i); LocalDatabaseProtection.Request(roots[i], Backup("key" + i), Pass, lease); }
        await LocalDatabaseStartup.PrepareAsync(Root, lease, _ => throw new InvalidOperationException("Unexpected recovery"), default);
        Assert.Equal(roots, LocalDatabaseStartup.CatalogRoots(Root));
        for (var i = 0; i < roots.Length; i++)
        {
            var stores = new VaultProfileStores(roots[i], LocalDatabaseProtection.KeyForOpening(roots[i]));
            Assert.Equal("name" + i, (await stores.Manifests.LoadAsync("same", default))!.FileName);
            Assert.Single(await stores.Queue.ListAsync(default));
            await stores.Folders.ListAsync("42", default); await stores.Checkpoints.ListAccountAsync("42", default);
            await Assert.ThrowsAsync<SqliteException>(() => new VaultProfileStores(roots[i]).Manifests.ListAsync(default));
        }
        await LocalDatabaseStartup.PrepareAsync(Root, lease, _ => throw new InvalidOperationException(), default);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingKeyInvokesRecoveryBeforeMigrationAndCancelKeepsCatalog(bool recover)
    {
        await Seed(Root, "before"); using var lease = LocalProfileLease.TryAcquire(Root)!;
        LocalDatabaseProtection.Request(Root, Backup("shared"), Pass, lease); File.Delete(Path.Combine(Root, "local-db-key.dpapi"));
        var before = File.ReadAllBytes(Path.Combine(Root, "manifests.db")); var calls = 0;
        var prepare = LocalDatabaseStartup.PrepareAsync(Root, lease, root =>
        {
            calls++; Assert.Equal(Root, root); Assert.False(File.Exists(Path.Combine(root, "local-db-migration.json")));
            if (recover) LocalDatabaseProtection.Recover(root, Backup("shared"), Pass, lease);
            return Task.FromResult(recover);
        }, default);
        if (recover) { await prepare; Assert.Equal("Ready", LocalDatabaseProtection.Status(Root).Stage); }
        else { await Assert.ThrowsAsync<OperationCanceledException>(() => prepare); Assert.Equal(before, File.ReadAllBytes(Path.Combine(Root, "manifests.db"))); }
        Assert.Equal(1, calls);
    }
    [Fact]
    public async Task DamagedJournalDoesNotPromptForAHealthyKeyOrFallBackToPlaintext()
    {
        await Seed(Root, "protected"); using var lease = LocalProfileLease.TryAcquire(Root)!;
        LocalDatabaseProtection.Request(Root, Backup("shared"), Pass, lease);
        await LocalDatabaseStartup.PrepareAsync(Root, lease, _ => throw new InvalidOperationException(), default);
        var path = Path.Combine(Root, "local-db-migration.json"); var node = JsonNode.Parse(File.ReadAllText(path))!;
        var data = Convert.FromBase64String(node["data"]!.GetValue<string>()); data[0] ^= 1; node["data"] = Convert.ToBase64String(data); File.WriteAllText(path, node.ToJsonString());
        var before = File.ReadAllBytes(Path.Combine(Root, "manifests.db"));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => LocalDatabaseStartup.PrepareAsync(Root, lease, _ => throw new InvalidOperationException("Must not prompt"), default));
        Assert.ThrowsAny<CryptographicException>(() => LocalDatabaseProtection.KeyForOpening(Root)); Assert.Equal(before, File.ReadAllBytes(Path.Combine(Root, "manifests.db")));
    }
    [Fact]
    public async Task SuccessfulRecoveryCallbackWithoutExistingKeyStillBlocksStartup()
    {
        await Seed(Root, "before"); using var lease = LocalProfileLease.TryAcquire(Root)!;
        LocalDatabaseProtection.Request(Root, Backup("shared"), Pass, lease); File.Delete(Path.Combine(Root, "local-db-key.dpapi"));
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalDatabaseStartup.PrepareAsync(Root, lease, _ => Task.FromResult(true), default));
        Assert.False(File.Exists(Path.Combine(Root, "local-db-migration.json")));
        Assert.Throws<FileNotFoundException>(() => LocalDatabaseProtection.KeyForOpening(Root));
    }
    [Fact]
    public void InventoryIsBoundedAndRequestedKeyCannotOpenOrRecreateCatalog()
    {
        var accounts = Path.Combine(Root, "accounts"); Directory.CreateDirectory(accounts);
        for (var i = 0; i < 257; i++) Directory.CreateDirectory(Path.Combine(accounts, i.ToString()));
        Assert.Throws<InvalidDataException>(() => LocalDatabaseStartup.CatalogRoots(Root));
        using var lease = LocalProfileLease.TryAcquire(Root)!;
        LocalDatabaseProtection.Request(Root, Backup("shared"), Pass, lease);
        Assert.Throws<InvalidDataException>(() => LocalDatabaseProtection.KeyForOpening(Root));
        Assert.False(File.Exists(Path.Combine(Root, "manifests.db")));
    }
    [Fact]
    public async Task PlainProfilesStayPlainAndInventoryDoesNotWalkSessionOrStagingTrees()
    {
        await Seed(Root, "legacy"); var ignored = Path.Combine(Root, "accounts", "42", "tdlib", "accounts", "99");
        Directory.CreateDirectory(ignored); File.WriteAllText(Path.Combine(ignored, "local-db-policy.json"), "not a policy");
        using var lease = LocalProfileLease.TryAcquire(Root)!;
        await LocalDatabaseStartup.PrepareAsync(Root, lease, _ => throw new InvalidOperationException(), default);
        Assert.Null(LocalDatabaseProtection.KeyForOpening(Root)); Assert.Equal(2, LocalDatabaseStartup.CatalogRoots(Root).Count);
        Assert.Equal("legacy", (await new VaultProfileStores(Root).Manifests.LoadAsync("same", default))!.FileName);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EncryptedAccountMigrationDistinguishesEquivalentRetryFromMetadataConflict(bool differentMetadata)
    {
        await Seed(Root, "shared"); var account = Path.Combine(Root, "accounts", "42"); await Seed(account, differentMetadata ? "existing" : "shared");
        using var lease = LocalProfileLease.TryAcquire(Root)!;
        LocalDatabaseProtection.Request(Root, Backup("shared"), Pass, lease); LocalDatabaseProtection.Request(account, Backup("account"), Pass, lease);
        await LocalDatabaseStartup.PrepareAsync(Root, lease, _ => throw new InvalidOperationException(), default);
        var shared = new VaultProfileStores(Root, LocalDatabaseProtection.KeyForOpening(Root)); var target = new VaultProfileStores(account, LocalDatabaseProtection.KeyForOpening(account));
        await shared.Manifests.SaveAsync(new(1, "other", "other", 1, new string('B', 64), 1, [new(0, 0, 1, new string('B', 64), null, false)], false, "99"), default);
        var migration = new AccountProfileDataMigrator(shared.Manifests, target.Manifests, shared.Queue, target.Queue, shared.Checkpoints, target.Checkpoints, shared.Cache, shared.StagingRoot, target.StagingRoot);
        if (differentMetadata)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => migration.MigrateAsync("42", default));
            Assert.Equal("shared", (await shared.Manifests.LoadAsync("same", default))!.FileName);
            Assert.Equal("existing", (await target.Manifests.LoadAsync("same", default))!.FileName);
            Assert.Equal("shared", Assert.Single(await shared.Queue.ListAsync(default)).FileName);
            Assert.Equal("existing", Assert.Single(await target.Queue.ListAsync(default)).FileName);
        }
        else
        {
            await migration.MigrateAsync("42", default);
            Assert.Null(await shared.Manifests.LoadAsync("same", default));
            Assert.Equal("shared", (await target.Manifests.LoadAsync("same", default))!.FileName);
        }
        Assert.Null(await target.Manifests.LoadAsync("other", default)); Assert.NotNull(await shared.Manifests.LoadAsync("other", default));
    }

    [Fact]
    public async Task StartupProtectsAccountStorageSettingsOfflineBeforeAccountSessionOpens()
    {
        var accountRoot = Path.Combine(Root, "accounts", "42");
        Directory.CreateDirectory(accountRoot);
        await Seed(accountRoot, "account");
        var info = new TelegramStorageChannelInfo(-10042, "42", "Private storage");
        var path = Path.Combine(accountRoot, "storage-channel.json");
        File.WriteAllText(path, JsonSerializer.Serialize(info));
        using var lease = LocalProfileLease.TryAcquire(Root)!;
        LocalDatabaseProtection.Request(accountRoot, Backup("account-storage"), Pass, lease);

        await LocalDatabaseStartup.PrepareAsync(Root, lease, _ => throw new InvalidOperationException("Unexpected recovery"), default);

        var protectedBytes = File.ReadAllBytes(path);
        Assert.True(LocalRecordCipher.IsProtectedRecord(protectedBytes));
        using (var cipher = LocalDatabaseProtection.RecordCipher(accountRoot,
                   "account:42:storage-channel-settings", "account-storage-settings"))
        {
            var plaintext = cipher.Unprotect(protectedBytes);
            try { Assert.Equal(info, JsonSerializer.Deserialize<TelegramStorageChannelInfo>(plaintext)); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        await LocalDatabaseStartup.PrepareAsync(Root, lease, _ => throw new InvalidOperationException("Unexpected recovery"), default);
        Assert.Equal(protectedBytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task StartupLeavesWrongKeySettingsCiphertextUntouchedWithoutBlockingCatalog()
    {
        var accountRoot = Path.Combine(Root, "accounts", "42");
        Directory.CreateDirectory(accountRoot);
        await Seed(accountRoot, "account");
        using var lease = LocalProfileLease.TryAcquire(Root)!;
        LocalDatabaseProtection.Request(accountRoot, Backup("account-storage-wrong-key"), Pass, lease);
        var protectionId = LocalDatabaseProtection.Status(accountRoot).ProtectionId!;
        using var wrongCipher = new LocalRecordCipher(Convert.ToBase64String(Enumerable.Repeat((byte)0xD3, 32).ToArray()),
            protectionId, "account-storage-settings", "account:42:storage-channel-settings");
        var path = Path.Combine(accountRoot, "storage-channel.json");
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new TelegramStorageChannelInfo(-10042, "42", "Private storage"));
        var wrongBytes = wrongCipher.Protect(plaintext);
        CryptographicOperations.ZeroMemory(plaintext);
        File.WriteAllBytes(path, wrongBytes);

        await LocalDatabaseStartup.PrepareAsync(Root, lease,
            _ => throw new InvalidOperationException("Unexpected recovery"), default);

        Assert.Equal(wrongBytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task StartupProtectsMalformedOptionalSettingsAsOpaqueBytesWithoutBlockingCatalog()
    {
        var accountRoot = Path.Combine(Root, "accounts", "42");
        Directory.CreateDirectory(accountRoot);
        await Seed(accountRoot, "account");
        using var lease = LocalProfileLease.TryAcquire(Root)!;
        LocalDatabaseProtection.Request(accountRoot, Backup("account-storage-malformed"), Pass, lease);
        var path = Path.Combine(accountRoot, "storage-channel.json");
        var original = "not valid json"u8.ToArray();
        File.WriteAllBytes(path, original);

        await LocalDatabaseStartup.PrepareAsync(Root, lease, _ => throw new InvalidOperationException("Unexpected recovery"), default);

        var stored = File.ReadAllBytes(path);
        Assert.True(LocalRecordCipher.IsProtectedRecord(stored));
        using var cipher = LocalDatabaseProtection.RecordCipher(accountRoot,
            "account:42:storage-channel-settings", "account-storage-settings");
        var plaintext = cipher.Unprotect(stored);
        try { Assert.Equal(original, plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(parent, true); }
}
