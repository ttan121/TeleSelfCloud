using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Desktop;

internal sealed record UploadSource(string FullPath, string DestinationFolder);

internal sealed record UploadSourceSelection(IReadOnlyList<UploadSource> Files, IReadOnlyList<string> Folders);

internal static class UploadSourceSelector
{
    public const int MaximumFiles = 10_000;

    public static UploadSourceSelection Expand(IEnumerable<string> selectedPaths, string destinationFolder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedPaths);
        var destinationRoot = FileManifestMetadata.NormalizeFolderPath(destinationFolder);
        var directories = new List<string>();
        var directFiles = new List<string>();
        foreach (var selected in selectedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(selected)) throw new ArgumentException("An upload path is empty.", nameof(selectedPaths));
            var fullPath = Path.GetFullPath(selected);
            if (File.Exists(fullPath)) directFiles.Add(fullPath);
            else if (Directory.Exists(fullPath)) directories.Add(fullPath);
            else throw new FileNotFoundException("A selected upload source no longer exists.", fullPath);
        }

        directories = directories.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Length).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        var roots = new List<string>();
        foreach (var directory in directories)
        {
            if (roots.Any(root => IsSameOrChildPath(directory, root))) continue;
            roots.Add(directory);
        }

        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, UploadSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            RejectReparsePoint(root);
            var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
            if (string.IsNullOrWhiteSpace(rootName)) throw new IOException("A selected folder has no usable name.");
            var remoteRoot = CombineFolder(destinationRoot, rootName);
            folders.Add(remoteRoot);
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var entry in Directory.EnumerateFileSystemEntries(current).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RejectReparsePoint(entry);
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
                        folders.Add(CombineFolder(remoteRoot, relative));
                        pending.Push(entry);
                    }
                    else
                    {
                        AddFile(entry, DestinationForFile(root, entry, remoteRoot), files);
                        if (files.Count > MaximumFiles) throw new InvalidOperationException($"A folder upload cannot contain more than {MaximumFiles:N0} files.");
                    }
                }
            }
        }

        foreach (var file in directFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePoint(file);
            if (files.ContainsKey(file)) continue;
            AddFile(file, destinationRoot, files);
            if (files.Count > MaximumFiles) throw new InvalidOperationException($"An upload batch cannot contain more than {MaximumFiles:N0} files.");
        }

        return new UploadSourceSelection(
            files.Values.OrderBy(file => file.DestinationFolder, StringComparer.OrdinalIgnoreCase)
                .ThenBy(file => Path.GetFileName(file.FullPath), StringComparer.OrdinalIgnoreCase).ToArray(),
            folders.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static string DestinationForFile(string root, string file, string remoteRoot)
    {
        var relativeFile = Path.GetRelativePath(root, file);
        var relativeDirectory = Path.GetDirectoryName(relativeFile);
        return string.IsNullOrEmpty(relativeDirectory)
            ? remoteRoot
            : CombineFolder(remoteRoot, relativeDirectory.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/'));
    }

    private static string CombineFolder(string parent, string child)
    {
        var normalizedChild = child.Replace('\\', '/');
        var combined = parent.Length == 0 ? normalizedChild : parent + "/" + normalizedChild;
        return FileManifestMetadata.NormalizeFolderPath(combined);
    }

    private static bool IsSameOrChildPath(string candidate, string parent)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static void AddFile(string path, string destination, IDictionary<string, UploadSource> files)
    {
        files.TryAdd(path, new UploadSource(path, destination));
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Folder uploads do not follow symbolic links or other reparse points.");
    }
}
