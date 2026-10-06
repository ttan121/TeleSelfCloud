using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

if (args.Length == 1)
{
    using var lease = LocalProfileLease.TryAcquire(args[0]);
    if (lease is null) { Console.WriteLine("BUSY"); return 23; }
    Console.WriteLine("ACQUIRED");
    Console.Out.Flush();
    Console.ReadLine();
    return 0;
}

if (args.Length == 3 && args[0] == "--media-preview")
{
    using var server = new MediaStreamServer(new PreviewWorkflow(args[1]), args[2]);
    var manifest = new FileManifest(1, "crash-probe", "crash-probe.mp4", args[1].Length,
        new string('0', 64), Math.Max(1, args[1].Length),
        [new PartRecord(0, 0, args[1].Length, new string('0', 64), "-100/1", true)], true);
    Console.WriteLine(server.StartServer(manifest));
    Console.Out.Flush();
    Console.ReadLine();
    return 0;
}

return 2;

file sealed class PreviewWorkflow(string content) : ILocalFileWorkflow
{
    public Task<FileManifest> PrepareAsync(string sourcePath, string stagingRoot, long partSizeBytes, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<FileManifest>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task RestoreAsync(string fileId, string destinationPath, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(destinationPath, content, cancellationToken);
}
