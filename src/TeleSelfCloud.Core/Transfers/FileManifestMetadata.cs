using System;
using System.IO;
using System.Linq;

namespace TeleSelfCloud.Core.Transfers;

public static class FileManifestMetadata
{
	public static FileManifest Rename(FileManifest manifest, string fileName, DateTimeOffset? updatedAtUtc = null)
	{
		if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
		{
			throw new ArgumentException("Enter a valid file name without a folder path.", "fileName");
		}
		return Revise(manifest, manifest with
		{
			FileName = fileName
		}, updatedAtUtc);
	}

	public static FileManifest Move(FileManifest manifest, string folderPath, DateTimeOffset? updatedAtUtc = null)
	{
		string folderPath2 = NormalizeFolderPath(folderPath);
		return Revise(manifest, manifest with
		{
			FolderPath = folderPath2
		}, updatedAtUtc);
	}

	public static FileManifest SetTrashed(FileManifest manifest, bool inTrash, DateTimeOffset? updatedAtUtc = null)
	{
		return Revise(manifest, manifest with
		{
			IsInTrash = inTrash
		}, updatedAtUtc);
	}

	public static FileManifest SetFavorite(FileManifest manifest, bool favorite, DateTimeOffset? updatedAtUtc = null)
	{
		return Revise(manifest, manifest with
		{
			IsFavorite = favorite
		}, updatedAtUtc);
	}

	public static FileManifest SetArchived(FileManifest manifest, bool archived, DateTimeOffset? updatedAtUtc = null)
	{
		return Revise(manifest, manifest with
		{
			IsArchived = archived
		}, updatedAtUtc);
	}

	public static FileManifest SetHidden(FileManifest manifest, bool hidden, DateTimeOffset? updatedAtUtc = null)
	{
		return Revise(manifest, manifest with
		{
			IsHidden = hidden
		}, updatedAtUtc);
	}

	public static string NormalizeFolderPath(string? folderPath)
	{
		if (string.IsNullOrWhiteSpace(folderPath))
		{
			return string.Empty;
		}
		string text = folderPath.Trim().Replace('\\', '/');
		if (text.StartsWith('/') || Path.IsPathRooted(text))
		{
			throw new ArgumentException("Folder paths must be relative to the storage root.", "folderPath");
		}
		text = text.TrimEnd('/');
		string[] array = text.Split('/');
		if (array.Any((string segment) =>
		{
			bool flag = string.IsNullOrWhiteSpace(segment);
			if (!flag)
			{
				bool flag2 = ((segment == "." || segment == "..") ? true : false);
				flag = flag2;
			}
			return flag || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0;
		}))
		{
			throw new ArgumentException("Folder path contains an invalid name.", "folderPath");
		}
		return string.Join('/', array);
	}

	private static FileManifest Revise(FileManifest manifest, FileManifest revised, DateTimeOffset? updatedAtUtc)
	{
		ManifestValidator.ValidateStructure(manifest);
		if (!manifest.Committed)
		{
			throw new InvalidOperationException("Only committed files can receive remote metadata updates.");
		}
		FileManifest fileManifest = revised with
		{
			Revision = checked(manifest.Revision + 1),
			UpdatedAtUtc = (updatedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime()
		};
		ManifestValidator.ValidateStructure(fileManifest);
		return fileManifest;
	}
}
