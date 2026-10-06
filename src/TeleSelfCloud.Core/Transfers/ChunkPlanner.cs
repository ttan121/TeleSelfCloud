namespace TeleSelfCloud.Core.Transfers;

public static class ChunkPlanner
{
    public static IReadOnlyList<(long Offset, long Length)> Plan(long fileSize, long partSize)
    {
        if (fileSize < 0) throw new ArgumentOutOfRangeException(nameof(fileSize));
        if (partSize <= 0) throw new ArgumentOutOfRangeException(nameof(partSize));

        var parts = new List<(long Offset, long Length)>();
        if (fileSize == 0) return new[] { (Offset: 0L, Length: 0L) };
        for (long offset = 0; offset < fileSize;)
        {
            var length = Math.Min(partSize, fileSize - offset);
            parts.Add((offset, length));
            offset = checked(offset + length);
        }

        return parts;
    }
}
