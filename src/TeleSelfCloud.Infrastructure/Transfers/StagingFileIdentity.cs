namespace TeleSelfCloud.Infrastructure.Transfers;

public static class StagingFileIdentity
{
    public static string Part(string fileId, int index) =>
        $"file:{Validate(fileId)}:part:{(index >= 0 ? index : throw new ArgumentOutOfRangeException(nameof(index)))}";

    public static string EncryptedPayload(string fileId) => $"file:{Validate(fileId)}:encrypted-payload";

    private static string Validate(string fileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        if (fileId.Length > 256) throw new ArgumentException("The staging file ID exceeds its supported length.", nameof(fileId));
        return fileId;
    }
}
