using Microsoft.Data.Sqlite;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class LocalDatabaseProgressTests : IDisposable
{
    private readonly string parent = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.LocalProgress", Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(parent, "profile");
    private const string Pass = "safe progress recovery passphrase";
    public LocalDatabaseProgressTests() => Directory.CreateDirectory(Root);
    private static async Task Seed(string root)
    {
        await new SqliteManifestStore(Path.Combine(root, "manifests.db")).SaveAsync(new(1, "id", "data.bin", 1, new string('A', 64), 1, [new(0, 0, 1, new string('A', 64), null, false)], false, "42"), default);
        SqliteConnection.ClearAllPools();
    }
    private sealed class Observer(Action<LocalDatabaseStartupProgress> action) : IProgress<LocalDatabaseStartupProgress>
    { public void Report(LocalDatabaseStartupProgress value) => action(value); }
    [Theory]
    [InlineData("Inventory", false)]
    [InlineData("Checking", false)]
    [InlineData("Inspecting", false)]
    [InlineData("Copying", false)]
    [InlineData("Encrypting", false)]
    [InlineData("Verified", false)]
    [InlineData("Switching", true)]
    [InlineData("ProtectingOriginals", true)]
    [InlineData("Ready", true)]
    public async Task StopAtReportedStepExitsBeforeStoresAndPreservesOrCompletesCatalog(string step, bool ready)
    {
        await Seed(Root); var before = File.ReadAllBytes(Path.Combine(Root, "manifests.db")); using var lease = LocalProfileLease.TryAcquire(Root)!;
        LocalDatabaseProtection.Request(Root, Path.Combine(parent, "backup.json"), Pass, lease);
        using var stop = new CancellationTokenSource(); var seen = new List<LocalDatabaseStartupProgress>();
        var observer = new Observer(value => { seen.Add(value); if (value.Stage == step) stop.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalDatabaseStartup.PrepareAsync(Root, lease, _ => throw new InvalidOperationException(), stop.Token, observer));
        Assert.Contains(seen, p => p.Stage == step);
        Assert.All(seen, p => { Assert.Equal(Root, p.Root); Assert.Equal(p.Stage == "Inventory" ? 0 : 1, p.CatalogIndex); Assert.Equal(p.Stage == "Inventory" ? 0 : 1, p.CatalogCount); });
        if (ready) Assert.Equal("Ready", LocalDatabaseProtection.Status(Root).Stage);
        else { Assert.Equal(before, File.ReadAllBytes(Path.Combine(Root, "manifests.db"))); Assert.Throws<InvalidDataException>(() => LocalDatabaseProtection.KeyForOpening(Root)); }
        await LocalDatabaseStartup.PrepareAsync(Root, lease, _ => throw new InvalidOperationException(), default);
        Assert.Equal("data.bin", (await new VaultProfileStores(Root, LocalDatabaseProtection.KeyForOpening(Root)).Manifests.LoadAsync("id", default))!.FileName);
    }
    [Fact]
    public async Task LateStopDoesNotStartNextConfiguredCatalogAndCountsExcludePlainProfiles()
    {
        var account = Path.Combine(Root, "accounts", "42"); var plain = Path.Combine(Root, "accounts", "99");
        await Seed(Root); await Seed(account); await Seed(plain); var originalAccount = File.ReadAllBytes(Path.Combine(account, "manifests.db"));
        using var lease = LocalProfileLease.TryAcquire(Root)!;
        LocalDatabaseProtection.Request(Root, Path.Combine(parent, "shared.json"), Pass, lease); LocalDatabaseProtection.Request(account, Path.Combine(parent, "account.json"), Pass, lease);
        using var stop = new CancellationTokenSource(); var seen = new List<LocalDatabaseStartupProgress>();
        var observer = new Observer(value => { seen.Add(value); if (value.Stage == "Switching") stop.Cancel(); });
        await Assert.ThrowsAsync<OperationCanceledException>(() => LocalDatabaseStartup.PrepareAsync(Root, lease, _ => throw new InvalidOperationException(), stop.Token, observer));
        Assert.Equal("Ready", LocalDatabaseProtection.Status(Root).Stage); Assert.Equal("Requested", LocalDatabaseProtection.Status(account).Stage);
        Assert.Equal(originalAccount, File.ReadAllBytes(Path.Combine(account, "manifests.db")));
        Assert.All(seen, p => { Assert.Equal(p.Stage == "Inventory" ? 0 : 2, p.CatalogCount); Assert.Equal(p.Stage == "Inventory" ? 0 : 1, p.CatalogIndex); });
    }
    [Fact]
    public async Task ThrowingProgressObserversCannotStrandSwitchOrArchives()
    {
        await Seed(Root); using var lease = LocalProfileLease.TryAcquire(Root)!; LocalDatabaseProtection.Request(Root, Path.Combine(parent, "backup.json"), Pass, lease);
        await LocalDatabaseStartup.PrepareAsync(Root, lease, _ => throw new InvalidOperationException(), default, new Observer(_ => throw new IOException("observer failed")));
        Assert.Equal("Ready", LocalDatabaseProtection.Status(Root).Stage);
        Assert.NotNull(await new VaultProfileStores(Root, LocalDatabaseProtection.KeyForOpening(Root)).Manifests.LoadAsync("id", default));
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(Root, "local-db-migrations"), "old-*", SearchOption.AllDirectories), p => !p.EndsWith(".tscenc"));
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(parent, true); }
}
