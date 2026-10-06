using TeleSelfCloud.Core.Transfers;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class VaultLegacyPartitionPlannerTests
{
    private const string Account = "42";
    private const long CurrentChat = -101;
    private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void PartitionsWholeManifestsAndKeepsAmbiguousRecordsOutOfCurrentVault()
    {
        var source = new[]
        {
            Manifest("current", Account, [Part(0, 1, "-101/1")]),
            Manifest("other-chat", Account, [Part(0, 1, "-102/2")]),
            Manifest("mixed", Account, [Part(0, 1, "-101/3"), Part(1, 1, "-102/4")]),
            Manifest("unbound", null, [Part(0, 1, "-101/5")]),
            Manifest("no-remote", Account, [Part(0, 1, null)]),
            Manifest("foreign-account", "43", [Part(0, 1, "-101/6")]),
            Manifest("malformed", Account, [Part(0, 1, "not-a-remote-id")]),
            Manifest("copy-old-chat", Account,
                [new PartRecord(0, 0, 1, Hash, null, false,
                    CopySource: new PartCopySource(Account, "source-owner", 0, "-102/7"))])
        };

        var plan = VaultLegacyPartitionPlanner.Create(source, Account, CurrentChat);

        Assert.True(plan.RequiresIsolation);
        Assert.Equal(["current"], plan.CurrentVaultFileIds);
        Assert.Equal(["other-chat", "mixed", "unbound", "no-remote", "malformed", "copy-old-chat"], plan.LocalRecoveryFileIds);
        Assert.Equal(["foreign-account"], plan.ForeignAccountFileIds);
        Assert.Equal(VaultLegacyManifestDisposition.MixedChats, Assert.Single(plan.Manifests, x => x.FileId == "mixed").Disposition);
        Assert.Equal(VaultLegacyManifestDisposition.OtherChat, Assert.Single(plan.Manifests, x => x.FileId == "copy-old-chat").Disposition);
        Assert.Equal(VaultLegacyManifestDisposition.MalformedRemoteReference, Assert.Single(plan.Manifests, x => x.FileId == "malformed").Disposition);
        Assert.Equal("-102/4", source[2].Parts[1].RemoteId);
        Assert.Equal("not-a-remote-id", source[6].Parts[0].RemoteId);
    }

    [Fact]
    public void RejectsDuplicateIdsAndInvalidManifestStructure()
    {
        var duplicate = Manifest("same", Account, [Part(0, 1, "-101/1")]);
        Assert.Throws<InvalidDataException>(() => VaultLegacyPartitionPlanner.Create([duplicate, duplicate], Account, CurrentChat));
        Assert.Throws<InvalidDataException>(() => VaultLegacyPartitionPlanner.Create(
            [duplicate with { TotalSha256 = "invalid" }], Account, CurrentChat));
    }

    private static FileManifest Manifest(string id, string? accountId, IReadOnlyList<PartRecord> parts) =>
        new(1, id, id + ".bin", parts.Sum(part => part.Length), Hash,
            Math.Max(1, parts.Max(part => part.Length)), parts, parts.All(part => part.Confirmed), accountId);

    private static PartRecord Part(int index, long length, string? remoteId) =>
        new(index, index, length, Hash, remoteId, !string.IsNullOrWhiteSpace(remoteId));
}
