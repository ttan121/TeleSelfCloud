using System.Diagnostics;
using System.Reflection;
using TeleSelfCloud.Infrastructure.Transfers;
using TeleSelfCloud.Desktop;

namespace TeleSelfCloud.Tests;

public sealed class LocalProfileLeaseTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.ProfileLeaseTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SameProfileIsExclusiveAndLockContentsAreNeverTruncated()
    {
        Directory.CreateDirectory(root);
        var lockPath = Path.Combine(root, ".profile.lock");
        File.WriteAllText(lockPath, "keep this content");
        using (var lease = LocalProfileLease.TryAcquire(root))
        {
            Assert.NotNull(lease);
            Assert.Null(LocalProfileLease.TryAcquire(Path.Combine(root, ".").ToUpperInvariant()));
            using var separate = LocalProfileLease.TryAcquire(Path.Combine(root, "other"));
            Assert.NotNull(separate);
            lease.Dispose();
            lease.Dispose();
            using var reopened = LocalProfileLease.TryAcquire(root);
            Assert.NotNull(reopened);
        }
        Assert.Equal("keep this content", File.ReadAllText(lockPath));
    }

    [Fact]
    public async Task ProfileAliasThroughJunctionCannotBypassExclusiveLease()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(root);
        var junction = Path.Combine(root, "profile-alias");
        try
        {
            using var lease = LocalProfileLease.TryAcquire(root);
            Assert.NotNull(lease);
            using var createJunction = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "/c", "mklink", "/J", junction, root }
            }) ?? throw new InvalidOperationException("Could not start junction fixture creation.");
            await createJunction.WaitForExitAsync();
            Assert.Equal(0, createJunction.ExitCode);

            Assert.Throws<InvalidDataException>(() => LocalProfileLease.TryAcquire(junction));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction, recursive: false);
        }
    }

    [Fact]
    public async Task LockPathJunctionIsRejectedWithoutTouchingItsTarget()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(root);
        var target = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.ProfileLeaseLockTarget", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "keep.txt");
        File.WriteAllText(sentinel, "external data stays intact");
        var junction = Path.Combine(root, ".profile.lock");
        try
        {
            using var createJunction = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "/c", "mklink", "/J", junction, target }
            }) ?? throw new InvalidOperationException("Could not start junction fixture creation.");
            await createJunction.WaitForExitAsync();
            Assert.Equal(0, createJunction.ExitCode);

            Assert.Throws<InvalidDataException>(() => LocalProfileLease.TryAcquire(root));
            Assert.Equal("external data stays intact", File.ReadAllText(sentinel));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction, recursive: false);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public async Task OtherProcessCannotAcquireOwnedProfile()
    {
        using var owned = LocalProfileLease.TryAcquire(root);
        using var child = StartProbe();
        try
        {
            Assert.Equal("BUSY", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(23, child.ExitCode);
        }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
    }

    [Fact]
    public async Task ProcessTerminationReleasesOwnershipWithoutDeletingLockFile()
    {
        using var child = StartProbe();
        try
        {
            Assert.Equal("ACQUIRED", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Null(LocalProfileLease.TryAcquire(root));
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(File.Exists(Path.Combine(root, ".profile.lock")));
            using var reopened = LocalProfileLease.TryAcquire(root);
            Assert.NotNull(reopened);
        }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
    }

    [Fact]
    public void StartupConflictCopyGivesRecoveryDirectionInBothLanguages()
    {
        var text = new UiText();
        text.SetLanguage("vi", Path.Combine(root, "language.json"));
        Assert.Contains("cửa sổ đang chạy", text.Get("startup.profileInUse"));
        text.SetLanguage("en", Path.Combine(root, "language.json"));
        Assert.Contains("running window", text.Get("startup.profileInUse"));
    }

    private Process StartProbe()
    {
        var configuration = typeof(LocalProfileLeaseTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var probe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../TeleSelfCloud.ProfileLeaseProbe/bin", configuration, "net10.0/TeleSelfCloud.ProfileLeaseProbe.dll"));
        Assert.True(File.Exists(probe), "Build the profile lease probe alongside the test project.");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(probe);
        start.ArgumentList.Add(root);
        return Process.Start(start)!;
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
