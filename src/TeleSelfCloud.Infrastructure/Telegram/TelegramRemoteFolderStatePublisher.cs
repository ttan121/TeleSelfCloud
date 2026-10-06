using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Telegram;

public sealed class TelegramRemoteFolderStatePublisher(ILocalFolderStore folderStore, TelegramFileTransport transport, string temporaryDirectory, TeleSelfCloud.Infrastructure.Transfers.VaultMetadataKey? metadataKey = null)
{
	internal sealed record FolderStateSnapshot(int SchemaVersion, string AccountId, IReadOnlyList<LocalFolder> Folders, IReadOnlyList<FolderTombstone>? Tombstones = null);

	private const int MaxStateBytes = 16777216;

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

	public async Task PublishAsync(string accountId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(accountId, "accountId");
		if (accountId.Contains('|'))
		{
			throw new ArgumentException("Account ID cannot contain the storage caption delimiter.", "accountId");
		}
		IReadOnlyList<LocalFolder> folders = await folderStore.ListAsync(accountId, cancellationToken);
		IReadOnlyList<FolderTombstone> readOnlyList = await folderStore.ListTombstonesAsync(accountId, cancellationToken);
		if (folders.Count == 0 && readOnlyList.Count == 0)
		{
			return;
		}
		byte[] array = JsonSerializer.SerializeToUtf8Bytes(new FolderStateSnapshot(2, accountId, folders, readOnlyList), JsonOptions);
		if (array.Length > 16777216)
		{
			throw new InvalidDataException("Remote folder state exceeds the safe size limit.");
		}
		if (metadataKey is not null)
		{
			if (metadataKey.AccountId != accountId || metadataKey.ChatId != transport.ChatId) throw new InvalidDataException("The metadata key belongs to another account or vault.");
			var plaintext = array;
			try { array = await metadataKey.ProtectForReplayAsync(Path.Combine(temporaryDirectory, "protected-outbox"), "folders", accountId, plaintext, cancellationToken); }
			finally { CryptographicOperations.ZeroMemory(plaintext); }
		}
		string text = Convert.ToHexString(SHA256.HashData(array));
		string caption = "TSC-FOLDERS|" + (metadataKey is null ? "2" : "3") + "|" + accountId + "|" + text;
		Directory.CreateDirectory(temporaryDirectory);
		string path = Path.Combine(temporaryDirectory, $"folder-state-{Guid.NewGuid():N}.json");
		try
		{
			await File.WriteAllBytesAsync(path, array, cancellationToken);
			await transport.UploadStorageDocumentAsync(path, caption, cancellationToken);
		}
		finally
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
	}
}
