using System.IO.Compression;

namespace TeleSelfCloud.Infrastructure.Transfers;

public sealed record ZipPreviewEntry(string Name, long UncompressedSize, bool IsDirectory);

public static class ZipPreviewReader
{
    public const int MaximumEntries = 5_000;
    public const int MaximumNameLength = 2_048;
    public const long MaximumArchiveBytes = 100L * 1024 * 1024;

    public static IReadOnlyList<ZipPreviewEntry> Read(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("The ZIP file was not found.", path);
        if (info.Length > MaximumArchiveBytes) throw new InvalidDataException("The ZIP file exceeds the safe preview size limit.");

        using var archive = ZipFile.OpenRead(info.FullName);
        if (archive.Entries.Count > MaximumEntries)
            throw new InvalidDataException($"The ZIP contains more than {MaximumEntries:N0} entries and cannot be previewed safely.");
        var entries = new List<ZipPreviewEntry>(archive.Entries.Count);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName;
            if (name.Length > MaximumNameLength) name = name[..MaximumNameLength] + "…";
            entries.Add(new ZipPreviewEntry(name, entry.Length, entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')));
        }
        return entries;
    }
}
