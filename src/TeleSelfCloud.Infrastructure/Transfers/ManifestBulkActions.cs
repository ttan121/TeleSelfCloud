using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

public sealed class ManifestBulkActions(IManifestStore manifestStore, Func<FileManifest, CancellationToken, Task> publishRevision) : IFileBulkActions
{
	public async Task<IReadOnlyList<BulkFileActionResult>> ExecuteAsync(IEnumerable<string> fileIds, BulkFileAction action, string? destinationFolder, string accountId, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(fileIds, "fileIds");
		ArgumentException.ThrowIfNullOrWhiteSpace(accountId, "accountId");
		if ((uint)action > 1u)
		{
			throw new ArgumentOutOfRangeException("action");
		}
		string[] array = fileIds.Where((string value) => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
		int num = array.Length;
		if ((num > 500 || num == 0) ? true : false)
		{
			throw new ArgumentOutOfRangeException("fileIds", "Select between 1 and 500 files.");
		}
		var destination = ((action == BulkFileAction.Move) ? FileManifestMetadata.NormalizeFolderPath(destinationFolder) : null);
		List<BulkFileActionResult> results = new List<BulkFileActionResult>(array.Length);
		string[] array2 = array;
		foreach (string id in array2)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				FileManifest fileManifest = (await manifestStore.LoadAsync(id, cancellationToken)) ?? throw new FileNotFoundException("The selected manifest does not exist.", id);
				if (!fileManifest.Committed)
				{
					throw new InvalidOperationException("Only committed files support bulk actions.");
				}
				if (!string.Equals(fileManifest.AccountId, accountId, StringComparison.Ordinal))
				{
					throw new InvalidOperationException("The selected file belongs to a different account.");
				}
				if (fileManifest.IsInTrash)
				{
					throw new InvalidOperationException("Files in Trash cannot be moved by this action.");
				}
				FileManifest revised = ((action == BulkFileAction.Move) ? FileManifestMetadata.Move(fileManifest, destination!) : FileManifestMetadata.SetTrashed(fileManifest, inTrash: true));
				if (action != BulkFileAction.Move || !string.Equals(fileManifest.FolderPath, destination, StringComparison.Ordinal))
				{
					await publishRevision(revised, cancellationToken);
					await manifestStore.SaveAsync(revised, CancellationToken.None);
				}
				results.Add(new BulkFileActionResult(id, Succeeded: true, null));
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex2)
			{
				results.Add(new BulkFileActionResult(id, Succeeded: false, ex2.Message));
			}
		}
		return results;
	}
}


