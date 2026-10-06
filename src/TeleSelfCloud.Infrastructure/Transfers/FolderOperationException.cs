using System;
using System.IO;

namespace TeleSelfCloud.Infrastructure.Transfers;

public sealed class FolderOperationException(string message, int publishedFileCount, Exception innerException) : IOException(message, innerException)
{
	public int PublishedFileCount { get; } = publishedFileCount;
}
