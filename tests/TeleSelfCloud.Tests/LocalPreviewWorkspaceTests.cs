using System.Diagnostics;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class LocalPreviewWorkspaceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.PreviewWorkspaceTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void StartupCleanupRemovesOnlyOwnedPreviewDirectories()
    {
        Directory.CreateDirectory(root);
        string owned;
        using (var firstLease = LocalProfileLease.TryAcquire(root)!)
        {
            owned = LocalPreviewWorkspace.Create(root, firstLease);
            File.WriteAllText(Path.Combine(owned, "preview.txt"), "reconstructible scratch");
        }

        var previewRoot = Path.Combine(root, "transient", "preview");
        var unowned = Path.Combine(previewRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unowned);
        File.WriteAllText(Path.Combine(unowned, "keep.txt"), "unowned data");

        using (var nextLease = LocalProfileLease.TryAcquire(root)!)
            LocalPreviewWorkspace.CleanupOrphans(root, nextLease);

        Assert.False(Directory.Exists(owned));
        Assert.Equal("unowned data", File.ReadAllText(Path.Combine(unowned, "keep.txt")));
    }

    [Fact]
    public void PreviewWorkspaceRequiresTheMatchingLiveProfileLease()
    {
        Directory.CreateDirectory(root);
        var otherRoot = root + "-other";
        using (var lease = LocalProfileLease.TryAcquire(otherRoot)!)
            Assert.Throws<InvalidOperationException>(() => LocalPreviewWorkspace.Create(root, lease));
        Directory.Delete(otherRoot, recursive: true);
    }

    [Fact]
    public async Task ReparsePointInsideOwnedPreviewIsPreservedWithoutFollowingIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(root);
        string owned;
        using (var initialLease = LocalProfileLease.TryAcquire(root)!)
            owned = LocalPreviewWorkspace.Create(root, initialLease);

        var external = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.PreviewSentinel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        var sentinel = Path.Combine(external, "keep.txt");
        File.WriteAllText(sentinel, "external data stays intact");
        var junction = Path.Combine(owned, "linked");
        try
        {
            using var createJunction = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "/c", "mklink", "/J", junction, external }
            }) ?? throw new InvalidOperationException("Could not start preview junction fixture creation.");
            await createJunction.WaitForExitAsync();
            Assert.Equal(0, createJunction.ExitCode);

            using var nextLease = LocalProfileLease.TryAcquire(root)!;
            LocalPreviewWorkspace.CleanupOrphans(root, nextLease);

            Assert.True(Directory.Exists(owned));
            Assert.Equal("external data stays intact", File.ReadAllText(sentinel));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction, recursive: false);
            if (Directory.Exists(external)) Directory.Delete(external, recursive: true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
