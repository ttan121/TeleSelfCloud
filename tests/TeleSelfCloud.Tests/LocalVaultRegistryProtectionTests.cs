using Microsoft.Data.Sqlite;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class LocalVaultRegistryProtectionTests : IDisposable
{
    private readonly string parent = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.RegistryProtection", Guid.NewGuid().ToString("N"));
    private string Shared => Path.Combine(parent, "profile");
    private string Root => TelegramAccountProfileStore.GetDirectory(Shared, "42");
    private string State => Path.Combine(Root, "vaults.json");
    private string Backup => Path.Combine(parent, "account-key.json");
    public LocalVaultRegistryProtectionTests() => Directory.CreateDirectory(Root);
    private async Task<byte[]> EnableCatalogAndSeed(LocalProfileLease lease)
    {
        await new SqliteManifestStore(Path.Combine(Root, "manifests.db")).ListAsync(default); SqliteConnection.ClearAllPools();
        LocalDatabaseProtection.Request(Root, Backup, "registry protection passphrase", lease);
        await LocalDatabaseStartup.PrepareAsync(Shared, lease, _ => throw new InvalidOperationException(), default);
        using var registry = new TelegramVaultRegistry(Root, "42"); await registry.RegisterAsync(new(-101, "42", "Private primary"), true, default);
        await registry.RegisterAsync(new(-102, "42", "Private second"), true, default); return File.ReadAllBytes(State);
    }
    [Fact]
    public async Task RequestRestartAndFactoryProtectAccountRegistryWithExistingPortableKey()
    {
        using var lease = LocalProfileLease.TryAcquire(Shared)!; var original = await EnableCatalogAndSeed(lease); var key = LocalDatabaseProtection.DatabaseKey(Root);
        using (var plain = LocalVaultRegistryProtection.OpenRegistry(Root, "42")) Assert.Equal(-102, (await plain.LoadAsync(default))!.ActiveChatId);
        await LocalVaultRegistryProtection.RequestAsync(Root, "42", lease, default); Assert.Equal(original, File.ReadAllBytes(State));
        Assert.Throws<FileNotFoundException>(() => LocalVaultRegistryProtection.OpenRegistry(Root, "42"));
        await LocalDatabaseStartup.PrepareAsync(Shared, lease, _ => throw new InvalidOperationException(), default);
        Assert.Equal(key, LocalDatabaseProtection.DatabaseKey(Root)); Assert.False(LocalCacheProtection.IsConfigured(Root));
        using var registry = LocalVaultRegistryProtection.OpenRegistry(Root, "42"); var loaded = (await registry.LoadAsync(default))!;
        Assert.Equal(-101, loaded.PrimaryChatId); Assert.Equal(-102, loaded.ActiveChatId); Assert.Equal(Root, registry.GetDataDirectory(loaded, -101));
        await registry.SelectAsync(-101, default); var after = File.ReadAllBytes(State);
        await LocalDatabaseStartup.PrepareAsync(Shared, lease, _ => throw new InvalidOperationException(), default); Assert.Equal(after, File.ReadAllBytes(State));
    }
    [Theory]
    [InlineData("policy")]
    [InlineData("journal")]
    [InlineData("missing-state")]
    [InlineData("bad-state")]
    public async Task MissingOrDamagedProtectionNeverFallsBackOrRecreates(string damage)
    {
        using var lease = LocalProfileLease.TryAcquire(Shared)!; await EnableCatalogAndSeed(lease);
        await LocalVaultRegistryProtection.RequestAsync(Root, "42", lease, default); await LocalDatabaseStartup.PrepareAsync(Shared, lease, _ => throw new InvalidOperationException(), default);
        if (damage == "policy") File.Delete(Path.Combine(Root, "vault-registry-policy.tsc"));
        if (damage == "journal") File.Delete(Path.Combine(Root, "vault-registry-migration.tsc"));
        if (damage == "missing-state") File.Delete(State);
        if (damage == "bad-state") { var bad = File.ReadAllBytes(State); bad[^1] ^= 1; File.WriteAllBytes(State, bad); }
        var before = File.Exists(State) ? File.ReadAllBytes(State) : null;
        if (damage != "bad-state") Assert.NotNull(Record.Exception(() => LocalVaultRegistryProtection.OpenRegistry(Root, "42")));
        else { using var registry = LocalVaultRegistryProtection.OpenRegistry(Root, "42"); Assert.NotNull(await Record.ExceptionAsync(() => registry.RegisterAsync(new(-103, "42", "Third"), true, default))); }
        Assert.NotNull(await Record.ExceptionAsync(() => LocalDatabaseStartup.PrepareAsync(Shared, lease, _ => throw new InvalidOperationException(), default)));
        if (before is null) Assert.False(File.Exists(State)); else Assert.Equal(before, File.ReadAllBytes(State));
    }
    [Fact]
    public async Task RecoverExistingCatalogKeyBeforeRegistryMigrationWithoutChangingScope()
    {
        using var lease = LocalProfileLease.TryAcquire(Shared)!; var original = await EnableCatalogAndSeed(lease);
        await LocalVaultRegistryProtection.RequestAsync(Root, "42", lease, default); var key = LocalDatabaseProtection.DatabaseKey(Root);
        File.Delete(Path.Combine(Root, "local-db-key.dpapi")); var recovered = false;
        await LocalDatabaseStartup.PrepareAsync(Shared, lease, root => { Assert.Equal(Root, root); recovered = true; LocalDatabaseProtection.Recover(root, Backup, "registry protection passphrase", lease); return Task.FromResult(true); }, default);
        Assert.True(recovered); Assert.Equal(key, LocalDatabaseProtection.DatabaseKey(Root));
        using var cipher = LocalDatabaseProtection.RecordCipher(Root, "account:42:state", "vault-registry"); Assert.Equal(original, cipher.Unprotect(File.ReadAllBytes(State)));
    }
    [Fact]
    public async Task ForeignAccountAndNonAccountRootCannotCreatePolicyOrOpenProtectedRegistry()
    {
        using var lease = LocalProfileLease.TryAcquire(Shared)!; var original = await EnableCatalogAndSeed(lease);
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalVaultRegistryProtection.RequestAsync(Root, "99", lease, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalVaultRegistryProtection.RequestAsync(Shared, "42", lease, default));
        Assert.False(LocalVaultRegistryProtection.IsConfigured(Root)); Assert.Equal(original, File.ReadAllBytes(State));
        await LocalVaultRegistryProtection.RequestAsync(Root, "42", lease, default); await LocalDatabaseStartup.PrepareAsync(Shared, lease, _ => throw new InvalidOperationException(), default);
        Assert.Throws<InvalidDataException>(() => LocalVaultRegistryProtection.OpenRegistry(Root, "99"));
    }
    [Fact]
    public async Task NoKeyOrNoRegistryCannotCreateRequest()
    {
        using var lease = LocalProfileLease.TryAcquire(Shared)!;
        await Assert.ThrowsAsync<FileNotFoundException>(() => LocalVaultRegistryProtection.RequestAsync(Root, "42", lease, default));
        await EnableCatalogAndSeed(lease); File.Delete(State);
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalVaultRegistryProtection.RequestAsync(Root, "42", lease, default)); Assert.False(LocalVaultRegistryProtection.IsConfigured(Root));
    }
    [Fact]
    public async Task OpenProtectedRegistryRefusesLostFileAfterFactoryAndDisposedRegistryRejectsNewWork()
    {
        using var lease = LocalProfileLease.TryAcquire(Shared)!; await EnableCatalogAndSeed(lease);
        await LocalVaultRegistryProtection.RequestAsync(Root, "42", lease, default); await LocalDatabaseStartup.PrepareAsync(Shared, lease, _ => throw new InvalidOperationException(), default);
        var registry = LocalVaultRegistryProtection.OpenRegistry(Root, "42"); File.Delete(State);
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.RegisterAsync(new(-103, "42", "Third"), true, default)); Assert.False(File.Exists(State));
        registry.Dispose(); await Assert.ThrowsAsync<ObjectDisposedException>(() => registry.LoadAsync(default));
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(parent)) Directory.Delete(parent, true); }
}
