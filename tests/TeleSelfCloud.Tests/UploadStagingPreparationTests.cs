using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class UploadStagingPreparationTests
{
    [Fact]
    public async Task CancelledPreparationRemovesOnlyItsUnownedAttemptDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "tsc-prepare-cleanup-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(staging);
        var source = Path.Combine(root, "source.bin");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FileTransferCoordinator().PrepareAsync(source, staging, 2, cancellation.Token));

        Assert.Empty(Directory.GetFileSystemEntries(staging));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task SuccessfulPreparationKeepsManifestOwnedStaging()
    {
        var root = Path.Combine(Path.GetTempPath(), "tsc-prepare-owned-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.bin");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);

        var manifest = await new FileTransferCoordinator().PrepareAsync(source, staging, 2, default);

        Assert.All(manifest.Parts, part => Assert.True(File.Exists(part.StagingPath)));
        Assert.Single(Directory.GetDirectories(staging));
        Directory.Delete(root, recursive: true);
    }
}
