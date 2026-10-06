using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Infrastructure.Telegram;

public sealed class TelegramRemoteManifestCatalog(ITelegramRequestClient session, IPartTransport transport, IManifestStore localStore, long chatId, string? accountId = null, IRemoteSyncCheckpointStore? checkpointStore = null, ILocalFolderStore? folderStore = null, MetadataConflictArchive? conflictArchive = null, VaultMetadataKey? metadataKey = null)
{
	private sealed record RemoteFolderStateEntry(LocalFolder? Folder, FolderTombstone? Tombstone);

	private const int MaxManifestBytes = 16777216;

	private const int MaxFolderSnapshotsPerSync = 256;

	private const int MaxFolderStateBytesPerSync = 67108864;

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
	private int _scanActive;
	private readonly HashSet<string> _observedFileIds = new(StringComparer.Ordinal);
	public IReadOnlyCollection<string> ObservedFileIds => _observedFileIds.ToArray();

	public DateTimeOffset? LastSuccessfulSyncUtc { get; private set; }

	public bool WasIncrementalSync { get; private set; }

	public int PagesRead { get; private set; }

	public int MessagesRead { get; private set; }

	public int ManifestCaptionsFound { get; private set; }

	public int FilesIndexed { get; private set; }

	public int FolderSnapshotsFound { get; private set; }

	public event EventHandler<string>? ScanStatusChanged;

	public async Task<IReadOnlyList<FileManifest>> ImportRecentAsync(CancellationToken cancellationToken, bool forceFullRescan = false)
	{
		if (Interlocked.CompareExchange(ref _scanActive, 1, 0) != 0)
			throw new InvalidOperationException("A catalog sync is already running.");
		try
		{
			LastSuccessfulSyncUtc = null;
			WasIncrementalSync = false;
			PagesRead = MessagesRead = ManifestCaptionsFound = FilesIndexed = FolderSnapshotsFound = 0;
			_observedFileIds.Clear();
			cancellationToken.ThrowIfCancellationRequested();
			return await ImportCoreAsync(cancellationToken, forceFullRescan);
		}
		finally { Volatile.Write(ref _scanActive, 0); }
	}

	private async Task<IReadOnlyList<FileManifest>> ImportCoreAsync(CancellationToken cancellationToken, bool forceFullRescan)
	{
		var remoteSyncCheckpoint = ((checkpointStore == null || string.IsNullOrWhiteSpace(accountId)) ? null : (await checkpointStore.LoadAsync(accountId, chatId, cancellationToken)));
		var checkpoint = remoteSyncCheckpoint;
		WasIncrementalSync = !forceFullRescan && checkpoint is not null && checkpoint.HighestMessageId > 0;
		long stopAtMessageId = (WasIncrementalSync ? checkpoint!.HighestMessageId : 0);
		Dictionary<string, FileManifest> imported = new Dictionary<string, FileManifest>(StringComparer.Ordinal);
		Dictionary<string, RemoteFolderStateEntry> remoteFolderStates = new Dictionary<string, RemoteFolderStateEntry>(StringComparer.OrdinalIgnoreCase);
		int totalFolderStateBytes = 0;
		long fromMessageId = 0L;
		long highestMessageIdSeen = 0L;
		bool reachedCheckpoint = false;
		while (true)
		{
			var jsonNode = (await session.ExecuteAsync(new JsonObject
			{
				["@type"] = "getChatHistory",
				["chat_id"] = chatId,
				["from_message_id"] = fromMessageId,
				["offset"] = 0,
				["limit"] = 100,
				["only_local"] = false
			}, cancellationToken))["messages"];
			cancellationToken.ThrowIfCancellationRequested();
			if (!(jsonNode is JsonArray messages))
			{
				throw new InvalidDataException("Telegram returned an invalid history page; the sync checkpoint was not advanced.");
			}
			if (messages.Count == 0)
			{
				break;
			}
			if (messages.OfType<JsonObject>().Count() != messages.Count)
			{
				throw new InvalidDataException("Telegram history returned a malformed message page; the sync checkpoint was not advanced.");
			}
			PagesRead++;
			MessagesRead += messages.Count;
			long? previousMessageId = null;
			foreach (JsonObject message in messages.OfType<JsonObject>())
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!long.TryParse(message["id"]?.ToString(), out var currentMessageId) || currentMessageId <= 0)
				{
					throw new InvalidDataException("Telegram history contained a message with an invalid ID; the sync checkpoint was not advanced.");
				}
				if (previousMessageId.HasValue)
				{
					long valueOrDefault = previousMessageId.GetValueOrDefault();
					if (currentMessageId >= valueOrDefault)
					{
						throw new InvalidDataException("Telegram history was not ordered newest-to-oldest; the sync checkpoint was not advanced.");
					}
				}
				previousMessageId = currentMessageId;
				if (fromMessageId != 0 && currentMessageId > fromMessageId)
					throw new InvalidDataException("Telegram history replayed messages newer than the requested cursor; the sync checkpoint was not advanced.");
				if (!long.TryParse(message["chat_id"]?.ToString(), out var messageChatId) || messageChatId != chatId)
					throw new InvalidDataException("Telegram history returned a message from a different storage channel; the sync checkpoint was not advanced.");
				highestMessageIdSeen = Math.Max(highestMessageIdSeen, currentMessageId);
				if (WasIncrementalSync && currentMessageId <= stopAtMessageId)
				{
					reachedCheckpoint = true;
					break;
				}
				var fields = (message["content"]?["caption"]?["text"]?.GetValue<string>())?.Split('|');
				if (fields is { Length: > 0 } && fields[0] is "TSC-MANIFEST" or "TSC-FOLDERS")
				{
					if (fields.Length != 4)
						throw new InvalidDataException("A remote catalog caption is malformed; the sync checkpoint was not advanced.");
					if (fields[0] == "TSC-MANIFEST" && fields[1] != "1" && fields[1] != "2")
						throw new InvalidDataException("The remote manifest schema version is not supported; the sync checkpoint was not advanced.");
					if ((fields[0] == "TSC-MANIFEST" && fields[1] == "2" || fields[0] == "TSC-FOLDERS" && fields[1] == "3") && metadataKey is null)
						throw new InvalidDataException("Unlock this vault with the metadata key matching its encrypted documents.");
				}
				if (fields != null && fields.Length == 4 && fields[0] == "TSC-FOLDERS")
				{
					string text = fields[1];
					if (text != "1" && text != "2" && text != "3")
					{
						throw new InvalidDataException("The remote folder-state schema version is not supported.");
					}
					if (folderStore == null || string.IsNullOrWhiteSpace(accountId))
					{
						continue;
					}
					if (!long.TryParse(message["chat_id"]?.ToString(), out var result) || result != chatId)
					{
						throw new InvalidDataException("A remote folder-state document came from a different Telegram channel.");
					}
					if (!string.Equals(fields[2], accountId, StringComparison.Ordinal))
					{
						throw new InvalidDataException("Remote folder state belongs to a different Telegram account.");
					}
					string remoteId = $"{chatId}/{currentMessageId}";
					await using Stream stateStream = await transport.DownloadPartAsync(remoteId, cancellationToken);
					byte[] array = await ReadBoundedAsync(stateStream, 16777216, cancellationToken);
					totalFolderStateBytes = checked(totalFolderStateBytes + array.Length);
					if (totalFolderStateBytes > 67108864 || FolderSnapshotsFound >= 256)
					{
						throw new InvalidDataException("Remote folder history exceeds the safe sync limits.");
					}
					if (!string.Equals(Convert.ToHexString(SHA256.HashData(array)), fields[3], StringComparison.OrdinalIgnoreCase))
					{
						throw new InvalidDataException("Remote folder state failed its Telegram caption SHA-256 check.");
					}
					if (fields[1] == "3") array = DecryptMetadata("folders", fields[2], array);
                    TelegramRemoteFolderStatePublisher.FolderStateSnapshot folderStateSnapshot;
                    try { folderStateSnapshot = JsonSerializer.Deserialize<TelegramRemoteFolderStatePublisher.FolderStateSnapshot>(array, JsonOptions) ?? throw new InvalidDataException("Telegram returned an empty remote folder state."); }
                    finally { if (fields[1] == "3") CryptographicOperations.ZeroMemory(array); }
					if (folderStateSnapshot.SchemaVersion != (fields[1] == "3" ? 2 : int.Parse(fields[1], CultureInfo.InvariantCulture)) || !string.Equals(folderStateSnapshot.AccountId, accountId, StringComparison.Ordinal) || folderStateSnapshot.Folders == null || folderStateSnapshot.Folders.Count > 100000 || (folderStateSnapshot.Tombstones?.Count ?? 0) > 100000 || folderStateSnapshot.Folders.Count + (folderStateSnapshot.Tombstones?.Count ?? 0) > 100000)
					{
						throw new InvalidDataException("Remote folder state has invalid account, schema, or entry count.");
					}
					HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					foreach (LocalFolder folder in folderStateSnapshot.Folders)
					{
						if (folder is null) throw new InvalidDataException("Remote folder state contains a null folder entry.");
						string text2 = FileManifestMetadata.NormalizeFolderPath(folder.Path);
						if (!string.Equals(folder.AccountId, accountId, StringComparison.Ordinal) || text2.Length == 0 || !string.Equals(text2, folder.Path, StringComparison.Ordinal) || folder.CreatedAtUtc == default(DateTimeOffset))
						{
							throw new InvalidDataException("Remote folder state contains an invalid folder entry.");
						}
						if (!hashSet.Add(folder.Path))
						{
							throw new InvalidDataException("Remote folder state contains duplicate or conflicting paths.");
						}
						remoteFolderStates.TryAdd(folder.Path, new RemoteFolderStateEntry(folder, null));
					}
					foreach (FolderTombstone item in folderStateSnapshot.Tombstones ?? Array.Empty<FolderTombstone>())
					{
						if (item is null) throw new InvalidDataException("Remote folder state contains a null tombstone.");
						string text3 = FileManifestMetadata.NormalizeFolderPath(item.Path);
						if (!string.Equals(item.AccountId, accountId, StringComparison.Ordinal) || text3.Length == 0 || !string.Equals(text3, item.Path, StringComparison.Ordinal) || item.DeletedAtUtc == default(DateTimeOffset))
						{
							throw new InvalidDataException("Remote folder state contains an invalid tombstone.");
						}
						if (!hashSet.Add(item.Path))
						{
							throw new InvalidDataException("Remote folder state contains duplicate or conflicting paths.");
						}
						remoteFolderStates.TryAdd(item.Path, new RemoteFolderStateEntry(null, item));
					}
					HashSet<string> hashSet2 = (folderStateSnapshot.Tombstones ?? Array.Empty<FolderTombstone>()).Select((FolderTombstone item) => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
					foreach (LocalFolder folder2 in folderStateSnapshot.Folders)
					{
						string[] array2 = folder2.Path.Split('/');
						for (int num = 1; num < array2.Length; num++)
						{
							if (hashSet2.Contains(string.Join('/', array2.Take(num))))
							{
								throw new InvalidDataException("Remote folder state marks a parent deleted while retaining an active child.");
							}
						}
					}
					if (remoteFolderStates.Count > 100000)
					{
						throw new InvalidDataException("Remote folder history exceeds the safe unique-path limit.");
					}
					FolderSnapshotsFound++;
				}
				else
				{
					if (fields == null || fields.Length != 4 || fields[0] != "TSC-MANIFEST" || (fields[1] != "1" && fields[1] != "2"))
					{
						continue;
					}
                    ManifestCaptionsFound++;
					long value = currentMessageId;
					if (!long.TryParse(message["chat_id"]?.ToString(), out var result2) || result2 != chatId)
					{
						continue;
					}
					string remoteId2 = $"{chatId}/{value}";
					await using Stream download = await transport.DownloadPartAsync(remoteId2, cancellationToken);
					byte[] array3 = await ReadBoundedAsync(download, 16777216, cancellationToken);
					if (!string.Equals(Convert.ToHexString(SHA256.HashData(array3)), fields[3], StringComparison.OrdinalIgnoreCase))
					{
						throw new InvalidDataException("A remote manifest failed its Telegram caption SHA-256 check.");
					}
					if (fields[1] == "2") array3 = DecryptMetadata("manifest", fields[2], array3);
                    FileManifest manifest;
                    try { manifest = JsonSerializer.Deserialize<FileManifest>(array3, JsonOptions) ?? throw new InvalidDataException("Telegram returned an empty remote manifest."); }
                    finally { if (fields[1] == "2") CryptographicOperations.ZeroMemory(array3); }
					ManifestValidator.ValidateStructure(manifest);
					foreach (var part in manifest.Parts)
					{
						if (part.RemoteId is null || TelegramRemoteMessageId.Parse(part.RemoteId).ChatId != chatId)
							throw new InvalidDataException("A remote manifest part points outside the active storage channel.");
					}
					manifest = manifest with
					{
						Parts = manifest.Parts.Select((PartRecord part) => part with
						{
							StagingPath = null
						}).ToArray(),
						Encryption = ((manifest.Encryption is null) ? null : manifest.Encryption with
						{
							StagingPath = null
						})
					};
					if (!manifest.Committed || !string.Equals(manifest.FileId, fields[2], StringComparison.Ordinal))
					{
						throw new InvalidDataException("A remote manifest caption does not match its committed manifest.");
					}
					if (!string.IsNullOrWhiteSpace(accountId))
					{
						if (!string.IsNullOrWhiteSpace(manifest.AccountId) && !string.Equals(manifest.AccountId, accountId, StringComparison.Ordinal))
						{
							throw new InvalidDataException("A remote manifest is bound to a different Telegram account than the active storage channel.");
						}
						manifest = manifest with
						{
							AccountId = accountId
						};
					}
					var fileManifest = ((!imported.TryGetValue(manifest.FileId, out var value2)) ? (await localStore.LoadAsync(manifest.FileId, cancellationToken)) : value2);
					var fileManifest2 = fileManifest;
					if (fileManifest2 is not null && !string.IsNullOrWhiteSpace(accountId) && !string.IsNullOrWhiteSpace(fileManifest2.AccountId) && !string.Equals(fileManifest2.AccountId, accountId, StringComparison.Ordinal))
					{
						throw new InvalidDataException("A remote manifest ID conflicts with a local manifest owned by a different Telegram account.");
					}
					FileManifest fileManifest3 = ManifestRevisionSelector.PreferNewest(fileManifest2, manifest);
					if (fileManifest2 is not null && conflictArchive is not null)
						await conflictArchive.RecordCompetingAsync(fileManifest2, manifest, cancellationToken);
					if (string.IsNullOrWhiteSpace(fileManifest3.AccountId) && !string.IsNullOrWhiteSpace(accountId))
					{
						fileManifest3 = fileManifest3 with
						{
							AccountId = accountId
						};
					}
					if (fileManifest2 is null || fileManifest3 != fileManifest2)
					{
						await localStore.SaveAsync(fileManifest3, cancellationToken);
					}
					imported[manifest.FileId] = fileManifest3;
					_observedFileIds.Add(manifest.FileId);
					FilesIndexed = imported.Count;
				}
			}
			ScanStatusChanged?.Invoke(this, $"{(WasIncrementalSync ? "Incremental" : "Full")} sync in progress: {PagesRead} page(s), {MessagesRead:N0} message(s), {ManifestCaptionsFound} manifest(s) found, {FilesIndexed} file(s) indexed.");
			if (reachedCheckpoint)
			{
				break;
			}
			// offset=0 is inclusive: a page containing only the already-seen oldest anchor is the end.
			if (fromMessageId != 0 && messages.Count == 1 && previousMessageId == fromMessageId) break;
			if (!long.TryParse(messages.LastOrDefault()?["id"]?.ToString(), out var result3) || result3 <= 0 || (fromMessageId != 0L && result3 >= fromMessageId))
			{
				throw new InvalidDataException("Telegram history pagination stopped advancing; the sync checkpoint was not advanced.");
			}
			fromMessageId = result3;
		}
		cancellationToken.ThrowIfCancellationRequested();
		if (folderStore != null && !string.IsNullOrWhiteSpace(accountId) && remoteFolderStates.Count > 0)
		{
			await folderStore.MergeRemoteAsync(accountId, from item in remoteFolderStates.Values
				where item.Folder is not null
				select item.Folder!, from item in remoteFolderStates.Values
				where item.Tombstone is not null
				select item.Tombstone!, cancellationToken);
		}
		long highestMessageId = Math.Max(checkpoint?.HighestMessageId ?? 0, highestMessageIdSeen);
		cancellationToken.ThrowIfCancellationRequested();
		DateTimeOffset completedAt = DateTimeOffset.UtcNow;
		if (checkpointStore != null && !string.IsNullOrWhiteSpace(accountId))
		{
			await checkpointStore.SaveAsync(accountId, chatId, new RemoteSyncCheckpoint(highestMessageId, completedAt), CancellationToken.None);
		}
		LastSuccessfulSyncUtc = completedAt;
		ScanStatusChanged?.Invoke(this, $"{(WasIncrementalSync ? "Incremental" : "Full")} sync completed: {PagesRead} page(s), {MessagesRead:N0} message(s), {ManifestCaptionsFound} manifest(s) found, {FilesIndexed} file(s) indexed.");
		return imported.Values.ToArray();
	}

    private byte[] DecryptMetadata(string kind, string identity, byte[] bytes)
    {
        if (metadataKey is null) throw new InvalidDataException("Unlock this vault with the metadata key matching its encrypted documents.");
        if (metadataKey.AccountId != accountId || metadataKey.ChatId != chatId) throw new InvalidDataException("The metadata key belongs to another account or vault.");
        return metadataKey.Unprotect(kind, identity, bytes);
    }
	private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
	{
		using MemoryStream buffer = new MemoryStream();
		byte[] chunk = new byte[65536];
		int num;
		while ((num = await stream.ReadAsync(chunk, cancellationToken)) > 0)
		{
			if (buffer.Length + num > maxBytes)
			{
				throw new InvalidDataException("The remote manifest exceeds the safe parsing limit.");
			}
			await buffer.WriteAsync(chunk.AsMemory(0, num), cancellationToken);
		}
		return buffer.ToArray();
	}
}


