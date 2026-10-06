using System.Diagnostics;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class LocalOpenedFileWorkspaceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.OpenedWorkspaceTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void OpenedCopyIsRetainedForThirtyDaysThenRemoved()
    {
        Directory.CreateDirectory(root);
        using var lease = LocalProfileLease.TryAcquire(root)!;
        var workspace = LocalOpenedFileWorkspace.Create(root, lease, "private.txt");
        File.WriteAllText(workspace.FilePath, "reconstructible output");

        LocalOpenedFileWorkspace.CleanupOrphansAt(root, lease, DateTimeOffset.UtcNow.AddDays(29));
        Assert.True(File.Exists(workspace.FilePath));

        LocalOpenedFileWorkspace.CleanupOrphansAt(root, lease, DateTimeOffset.UtcNow.AddDays(31));
        Assert.False(Directory.Exists(workspace.Directory));
    }

    [Fact]
    public void ExpiredEmptyWorkspaceLeftByInterruptedOpenIsRemoved()
    {
        Directory.CreateDirectory(root);
        using var lease = LocalProfileLease.TryAcquire(root)!;
        var workspace = LocalOpenedFileWorkspace.Create(root, lease, "private.txt");

        // Simulate a crash after the ownership marker and payload directory were created,
        // but before the opened copy was materialized.
        LocalOpenedFileWorkspace.CleanupOrphansAt(root, lease, DateTimeOffset.UtcNow.AddDays(29));
        Assert.True(Directory.Exists(workspace.Directory));

        LocalOpenedFileWorkspace.CleanupOrphansAt(root, lease, DateTimeOffset.UtcNow.AddDays(31));
        Assert.False(Directory.Exists(workspace.Directory));
    }

    [Fact]
    public void ExpiredOpenedCopyIsPreservedWhileExternalProcessHoldsFile()
    {
        Directory.CreateDirectory(root);
        using var lease = LocalProfileLease.TryAcquire(root)!;
        var workspace = LocalOpenedFileWorkspace.Create(root, lease, "private.txt");
        File.WriteAllText(workspace.FilePath, "still in use");
        using (var externalReader = new FileStream(workspace.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            LocalOpenedFileWorkspace.CleanupOrphansAt(root, lease, DateTimeOffset.UtcNow.AddDays(31));
            Assert.True(File.Exists(workspace.FilePath));
        }

        LocalOpenedFileWorkspace.CleanupOrphansAt(root, lease, DateTimeOffset.UtcNow.AddDays(31));
        Assert.False(Directory.Exists(workspace.Directory));
    }

    [Fact]
    public void RetentionRestartsAfterTheExternalCopyIsModified()
    {
        Directory.CreateDirectory(root);
        using var lease = LocalProfileLease.TryAcquire(root)!;
        var baseline = DateTimeOffset.UtcNow;
        var workspace = LocalOpenedFileWorkspace.Create(root, lease, "edited.txt");
        File.WriteAllText(workspace.FilePath, "edited external copy");
        File.SetLastWriteTimeUtc(workspace.FilePath, baseline.AddDays(5).UtcDateTime);

        LocalOpenedFileWorkspace.CleanupOrphansAt(root, lease, baseline.AddDays(31));
        Assert.True(File.Exists(workspace.FilePath));

        LocalOpenedFileWorkspace.CleanupOrphansAt(root, lease, baseline.AddDays(36));
        Assert.False(Directory.Exists(workspace.Directory));
    }

    [Fact]
    public void WorkspaceSupportsAFileNamedLikeItsOwnershipMarker()
    {
        Directory.CreateDirectory(root);
        using var lease = LocalProfileLease.TryAcquire(root)!;
        var workspace = LocalOpenedFileWorkspace.Create(root, lease, ".tsc-open-owner");
        File.WriteAllText(workspace.FilePath, "user content");

        Assert.True(File.Exists(workspace.FilePath));
        Assert.NotEqual(Path.Combine(workspace.Directory, ".tsc-open-owner"), workspace.FilePath);
        LocalOpenedFileWorkspace.Delete(root, workspace, lease);
        Assert.False(Directory.Exists(workspace.Directory));
    }

    [Fact]
    public async Task ExpiredWorkspaceJunctionNeverDeletesItsExternalTarget()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(root);
        using var lease = LocalProfileLease.TryAcquire(root)!;
        var workspace = LocalOpenedFileWorkspace.Create(root, lease, "private.txt");
        File.WriteAllText(workspace.FilePath, "reconstructible output");
        var external = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.OpenedSentinel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        var sentinel = Path.Combine(external, "keep.txt");
        File.WriteAllText(sentinel, "external data stays intact");
        var junction = Path.Combine(Path.GetDirectoryName(workspace.FilePath)!, "linked");
        try
        {
            using var createJunction = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "/c", "mklink", "/J", junction, external }
            }) ?? throw new InvalidOperationException("Could not start opened-file junction fixture creation.");
            await createJunction.WaitForExitAsync();
            Assert.Equal(0, createJunction.ExitCode);

            LocalOpenedFileWorkspace.CleanupOrphansAt(root, lease, DateTimeOffset.UtcNow.AddDays(31));
            Assert.True(Directory.Exists(workspace.Directory));
            Assert.Equal("external data stays intact", File.ReadAllText(sentinel));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction, recursive: false);
            if (Directory.Exists(external)) Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public void FileNameCannotEscapeTheOwnedWorkspace()
    {
        Directory.CreateDirectory(root);
        using var lease = LocalProfileLease.TryAcquire(root)!;
        Assert.Throws<InvalidDataException>(() => LocalOpenedFileWorkspace.Create(root, lease, ".." + Path.DirectorySeparatorChar + "outside.txt"));
        Assert.False(Directory.Exists(Path.Combine(root, "transient", "opened")));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
