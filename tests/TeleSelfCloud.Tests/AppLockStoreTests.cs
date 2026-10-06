using System.Text.Json;
using TeleSelfCloud.Desktop;

namespace TeleSelfCloud.Tests;

public sealed class AppLockStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.AppLock", Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(_root, "app-lock.json");

    [Fact]
    public void StoresOnlyVerifierAndRequiresCorrectPassphraseForChangesAndRemoval()
    {
        Directory.CreateDirectory(_root);
        var store = new AppLockStore(PathName);

        store.Configure("correct horse battery staple", 5);
        File.WriteAllText(PathName + ".interrupted.tmp", "unfinished replacement");

        var bytes = File.ReadAllBytes(PathName);
        Assert.DoesNotContain("correct horse battery staple", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.True(store.Verify("correct horse battery staple"));
        Assert.Throws<InvalidOperationException>(() => store.Configure("replacement passphrase", 10));
        Assert.True(store.Verify("correct horse battery staple"));
        Assert.False(store.Verify("wrong passphrase"));
        Assert.Equal(5, store.IdleTimeoutMinutes);
        store.SetIdleTimeout(10);
        Assert.Equal(10, store.IdleTimeoutMinutes);
        Assert.False(store.ChangePassphrase("wrong passphrase", "another safe passphrase"));
        Assert.True(store.ChangePassphrase("correct horse battery staple", "another safe passphrase"));
        Assert.False(store.Verify("correct horse battery staple"));
        Assert.True(store.Verify("another safe passphrase"));
        Assert.False(store.Remove("wrong passphrase"));
        Assert.True(File.Exists(PathName));
        Assert.True(store.Remove("another safe passphrase"));
        Assert.False(File.Exists(PathName));
    }

    [Fact]
    public void InvalidOrUnsupportedRecordsFailClosed()
    {
        Directory.CreateDirectory(_root);
        var store = new AppLockStore(PathName);
        File.WriteAllText(PathName, "{");
        Assert.Throws<InvalidDataException>(() => store.IsConfigured);
        File.WriteAllText(PathName, JsonSerializer.Serialize(new { Version = 1, Iterations = 10, Salt = "AA==", Verifier = "AA==", IdleTimeoutMinutes = 5 }));
        Assert.Throws<InvalidDataException>(() => store.Verify("any passphrase"));
        var freshStore = new AppLockStore(System.IO.Path.Combine(_root, "new.json"));
        Assert.Throws<ArgumentException>(() => freshStore.Configure("short", 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => freshStore.Configure("long enough passphrase", 2));
    }

    [Fact]
    public void AttemptThrottleGrowsToBoundedDelayAndResetsOnSuccess()
    {
        var throttle = new AppLockAttemptThrottle();
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(TimeSpan.FromSeconds(1), throttle.RecordFailure(now));
        Assert.Equal(TimeSpan.FromSeconds(2), throttle.RecordFailure(now));
        for (var attempt = 3; attempt <= 8; attempt++) throttle.RecordFailure(now);
        Assert.Equal(TimeSpan.FromSeconds(30), throttle.RecordFailure(now));
        Assert.True(throttle.Remaining(now) <= TimeSpan.FromSeconds(30));
        throttle.Reset();
        Assert.Equal(TimeSpan.Zero, throttle.Remaining(now));
    }

    [Fact]
    public void MissingRecordLeavesAppLockUnconfigured()
    {
        Directory.CreateDirectory(_root);
        var store = new AppLockStore(PathName);
        Assert.False(store.IsConfigured);
        Assert.False(store.Verify("any passphrase"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
