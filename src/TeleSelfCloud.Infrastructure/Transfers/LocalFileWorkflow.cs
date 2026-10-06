using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Infrastructure.Transfers;

public sealed class LocalFileWorkflow(
    ITransferCoordinator transferCoordinator,
    IManifestStore manifestStore,
    IStagedPartAssembler assembler,
    LocalStagingContentStore? stagingContentStore = null) : ILocalFileWorkflow
{
    public async Task<FileManifest> PrepareAsync(string sourcePath, string stagingRoot, long partSizeBytes, CancellationToken cancellationToken)
    {
        var manifest = await transferCoordinator.PrepareAsync(sourcePath, stagingRoot, partSizeBytes, cancellationToken);
        if (stagingContentStore is not null)
            foreach (var part in manifest.Parts)
                if (part.StagingPath is not null)
                    await stagingContentStore.ProtectInPlaceAsync(part.StagingPath, StagingFileIdentity.Part(manifest.FileId, part.Index),
                        part.Length, part.Sha256, cancellationToken);
        await manifestStore.SaveAsync(manifest, cancellationToken);
        return manifest;
    }

    public Task<IReadOnlyList<FileManifest>> ListAsync(CancellationToken cancellationToken) =>
        manifestStore.ListAsync(cancellationToken);

    public async Task RestoreAsync(string fileId, string destinationPath, CancellationToken cancellationToken)
    {
        var manifest = await manifestStore.LoadAsync(fileId, cancellationToken)
            ?? throw new FileNotFoundException("Local transfer manifest was not found.", fileId);
        await assembler.AssembleAsync(manifest, destinationPath, cancellationToken);
    }
}
