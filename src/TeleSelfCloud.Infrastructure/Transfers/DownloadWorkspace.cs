using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

/// <summary>Owns resumable download files without overwriting legacy destination-side sidecars.</summary>
public sealed class DownloadWorkspace : IDisposable
{
    private const string Owner = "TeleSelfCloud.Download/v1";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private sealed record State(int Version, string Owner, string Destination, string Fingerprint, string Id);
    private readonly IDisposable lease;
    private readonly string markerPath;
    public string CheckpointPath { get; }
    public string EncryptedPayloadPath { get; }

    private DownloadWorkspace(IDisposable lease, string markerPath, string directory, State state)
    {
        this.lease = lease;
        this.markerPath = markerPath;
        CheckpointPath = Path.Combine(directory, ".tsc-download-" + state.Id + ".partial");
        EncryptedPayloadPath = Path.Combine(directory, ".tsc-download-" + state.Id + ".cipher");
    }

    public static async Task<DownloadWorkspace> OpenAsync(FileManifest manifest, string destinationPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var destinationHash = Hash(destination.ToUpperInvariant());
        var fingerprint = Hash(JsonSerializer.Serialize(new
        {
            manifest.FileId, manifest.AccountId, manifest.LogicalSize, manifest.TotalSha256,
            Encryption = manifest.Encryption is null ? null : manifest.Encryption with { StagingPath = null },
            Parts = manifest.Parts.OrderBy(part => part.Index).Select(part => new { part.Index, part.Offset, part.Length, part.Sha256, part.RemoteId })
        }, Options));
        var marker = Path.Combine(directory, ".tsc-download-" + destinationHash + "-" + fingerprint + ".json");
        var lease = AcquireDestinationLease(destination);
        DownloadWorkspace? workspace = null;
        try
        {
            State state;
            var created = !File.Exists(marker);
            if (!created)
            {
                await using var input = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                if (input.Length > 32768) throw InvalidState();
                try { state = await JsonSerializer.DeserializeAsync<State>(input, Options, token) ?? throw InvalidState(); }
                catch (JsonException ex) { throw new InvalidDataException("The download recovery record is invalid. Its files were kept; check the local backup before retrying.", ex); }
                if (state.Version != 1 || state.Owner != Owner || !string.Equals(state.Destination, destination, StringComparison.OrdinalIgnoreCase) ||
                    state.Fingerprint != fingerprint || !Guid.TryParseExact(state.Id, "N", out _)) throw InvalidState();
            }
            else
            {
                state = new State(1, Owner, destination, fingerprint, Guid.NewGuid().ToString("N"));
                var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Options);
                if (bytes.Length > 32768) throw InvalidState();
                var temporary = marker + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
                    {
                        await output.WriteAsync(bytes, token);
                        await output.FlushAsync(token);
                        output.Flush(flushToDisk: true);
                    }
                    token.ThrowIfCancellationRequested();
                    File.Move(temporary, marker); // Never overwrite an unrecognized marker.
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            workspace = new DownloadWorkspace(lease, marker, directory, state);
            if (created)
            {
                // Copy legacy output read-only; the coordinator verifies every reusable part.
                // Keep the original even after success: a filename alone cannot prove ownership.
                var legacy = manifest.Encryption is null ? destination + ".partial" :
                    File.Exists(destination + ".encrypted-payload") ? destination + ".encrypted-payload" : destination + ".encrypted-payload.partial";
                if (File.Exists(legacy))
                {
                    await using var input = new FileStream(legacy, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
                    await using var output = new FileStream(workspace.CheckpointPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
                    var buffer = new byte[128 * 1024];
                    var remaining = Math.Min(input.Length, manifest.TransferSize);
                    while (remaining > 0)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
                        if (read == 0) break;
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                        remaining -= read;
                    }
                    await output.FlushAsync(token);
                }
            }
            return workspace;
        }
        catch { if (workspace is not null) workspace.Dispose(); else lease.Dispose(); throw; }
    }

    public void Complete()
    {
        // Only these paths were allocated by this record. Never delete legacy sidecars.
        File.Delete(CheckpointPath);
        File.Delete(EncryptedPayloadPath);
        File.Delete(markerPath);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static IDisposable AcquireDestinationLease(string destinationPath)
    {
        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        try { return new FileStream(Path.Combine(directory, ".tsc-download-" + Hash(destination.ToUpperInvariant()) + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
        { throw new InvalidOperationException("Another download is using this destination. Wait for it to finish or pause it before retrying.", ex); }
    }
    private static InvalidDataException InvalidState() => new("The download recovery record is invalid. Its files were kept; check the local backup before retrying.");
    public void Dispose() => lease.Dispose();
}
