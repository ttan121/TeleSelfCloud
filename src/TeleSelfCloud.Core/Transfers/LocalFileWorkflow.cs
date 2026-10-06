namespace TeleSelfCloud.Core.Transfers;

public interface ILocalFileWorkflow
{
    Task<FileManifest> PrepareAsync(string sourcePath, string stagingRoot, long partSizeBytes, CancellationToken cancellationToken);
    Task<IReadOnlyList<FileManifest>> ListAsync(CancellationToken cancellationToken);
    Task RestoreAsync(string fileId, string destinationPath, CancellationToken cancellationToken);
}

public interface IStagedPartAssembler
{
    Task AssembleAsync(FileManifest manifest, string destinationPath, CancellationToken cancellationToken);
}
