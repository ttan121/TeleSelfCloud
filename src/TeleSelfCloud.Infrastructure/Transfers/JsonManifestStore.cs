using System.Text.Json;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

public sealed class JsonManifestStore(string rootDirectory) : IManifestStore
{
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task SaveAsync(FileManifest manifest, CancellationToken cancellationToken)
    {
        ManifestValidator.ValidateStructure(manifest);
        Directory.CreateDirectory(rootDirectory);
        var path = GetPath(manifest.FileId);
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            await JsonSerializer.SerializeAsync(stream, manifest, _options, cancellationToken);
        File.Move(temporary, path, true);
    }

    public async Task<FileManifest?> LoadAsync(string fileId, CancellationToken cancellationToken)
    {
        var path = GetPath(fileId);
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var manifest = await JsonSerializer.DeserializeAsync<FileManifest>(stream, _options, cancellationToken);
        if (manifest is not null) ManifestValidator.ValidateStructure(manifest);
        return manifest;
    }

    public async Task<IReadOnlyList<FileManifest>> ListAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(rootDirectory)) return Array.Empty<FileManifest>();
        var manifests = new List<FileManifest>();
        foreach (var path in Directory.EnumerateFiles(rootDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            var manifest = await JsonSerializer.DeserializeAsync<FileManifest>(stream, _options, cancellationToken);
            if (manifest is not null)
            {
                ManifestValidator.ValidateStructure(manifest);
                manifests.Add(manifest);
            }
        }
        return manifests.OrderByDescending(m => m.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public Task DeleteManyAsync(IEnumerable<string> fileIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var id in fileIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = GetPath(id);
            if (File.Exists(path)) File.Delete(path);
        }
        return Task.CompletedTask;
    }

    private string GetPath(string fileId)
    {
        if (string.IsNullOrWhiteSpace(fileId) || fileId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Invalid manifest identifier.", nameof(fileId));
        return Path.Combine(rootDirectory, fileId + ".json");
    }
}
