using System.Reflection;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Desktop;
using Xunit.Abstractions;

namespace TeleSelfCloud.Tests;

public sealed class ThumbnailPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task MissingLargeLocalSourceDoesNotAllocateTheWholePayload()
    {
        var path = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.MissingThumbnail", Guid.NewGuid().ToString("N"), "missing.part");
        const int size = 32 * 1024 * 1024;
        var hash = new string('a', 64);
        var manifest = new FileManifest(1, "missing-fixture", "missing.png", size, hash, size,
            [new PartRecord(0, 0, size, hash, null, false, path)], false);
        var decode = typeof(ExplorerThumbnail).GetMethod("Decode", BindingFlags.Static | BindingFlags.NonPublic)!;
        var measured = await Task.Run(() =>
        {
            void Attempt()
            {
                try { Assert.Null(decode.Invoke(null, [manifest, CancellationToken.None])); }
                catch (TargetInvocationException ex) when (ex.InnerException is IOException) { }
            }
            Attempt(); // Exclude one-time runtime/JIT initialization.
            var before = GC.GetAllocatedBytesForCurrentThread();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Attempt();
            return new { AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before, ElapsedMs = timer.Elapsed.TotalMilliseconds };
        });
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(measured));
        var metrics = Environment.GetEnvironmentVariable("TSC_UI_METRICS_PATH");
        if (!string.IsNullOrEmpty(metrics)) { Directory.CreateDirectory(Path.GetDirectoryName(metrics)!); File.WriteAllText(metrics, System.Text.Json.JsonSerializer.Serialize(measured)); }
        Assert.True(measured.AllocatedBytes < 1024 * 1024, $"Missing thumbnail allocated {measured.AllocatedBytes:N0} bytes.");
    }
}
