using System.Reflection;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class PreviewTemporaryScopeTests
{
    [Fact]
    public void CleanupRemovesOwnedPreviewArtifactsAndRejectsExternalOrParentDirectories()
    {
        var profileRoot = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.PreviewScopeTests", Guid.NewGuid().ToString("N"));
        var external = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.PreviewScopeTests", Guid.NewGuid().ToString("N"));
        var cleanup = typeof(MainWindow).Assembly.GetType("TeleSelfCloud.Desktop.LocalPreviewWorkspace")!
            .GetMethod("Delete", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
        Directory.CreateDirectory(profileRoot);
        Directory.CreateDirectory(external);
        string owned;
        using var lease = LocalProfileLease.TryAcquire(profileRoot)!;
        owned = LocalPreviewWorkspace.Create(profileRoot, lease);
        try
        {
            File.WriteAllText(Path.Combine(owned, "preview.txt"), "preview bytes");
            File.WriteAllText(Path.Combine(external, "keep.txt"), "external bytes");
            cleanup.Invoke(null, [profileRoot, external, lease]);
            Assert.Equal("external bytes", File.ReadAllText(Path.Combine(external, "keep.txt")));
            cleanup.Invoke(null, [profileRoot, Path.Combine(profileRoot, "transient", "preview"), lease]);
            Assert.True(Directory.Exists(owned));
            cleanup.Invoke(null, [profileRoot, owned, lease]);
            Assert.False(Directory.Exists(owned));
        }
        finally
        {
            if (Directory.Exists(owned)) Directory.Delete(owned, true);
            if (Directory.Exists(external)) Directory.Delete(external, true);
            lease.Dispose();
            if (Directory.Exists(profileRoot)) Directory.Delete(profileRoot, recursive: true);
        }
    }
}
