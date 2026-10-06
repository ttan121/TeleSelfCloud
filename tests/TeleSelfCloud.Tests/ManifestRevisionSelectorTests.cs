using TeleSelfCloud.Core.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class ManifestRevisionSelectorTests
{
    private static FileManifest Manifest() => new(1, "same-file", "original.bin", 3, new string('A', 64), 3,
        [new PartRecord(0, 0, 3, new string('A', 64), "-100500/20", true)], true, "account",
        Revision: 2, UpdatedAtUtc: DateTimeOffset.Parse("2026-10-05T00:00:00Z"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConcurrentSnapshotsConvergeForEveryTraversalOrder(bool timestamp)
    {
        var original = Manifest() with { UpdatedAtUtc = timestamp ? Manifest().UpdatedAtUtc : null };
        FileManifest[] revisions = [original with { FileName = "renamed.bin" }, original with { FolderPath = "moved" }, original with { IsInTrash = true }];
        string? winner = null;
        foreach (var first in revisions)
        foreach (var second in revisions.Where(item => item != first))
        {
            var third = revisions.Single(item => item != first && item != second);
            var selected = ManifestRevisionSelector.PreferNewest(ManifestRevisionSelector.PreferNewest(first, second), third);
            var fingerprint = ManifestRevisionSelector.PortableFingerprint(selected);
            winner ??= fingerprint;
            Assert.Equal(winner, fingerprint);
            // Replaying a complete history must leave the same selected metadata.
            foreach (var replay in revisions)
                selected = ManifestRevisionSelector.PreferNewest(selected, replay);
            Assert.Equal(winner, ManifestRevisionSelector.PortableFingerprint(selected));
        }
    }

    [Fact]
    public void LocalCacheLegacyOwnershipAndEquivalentFormattingDoNotBreakConvergence()
    {
        var original = Manifest();
        var local = original with
        {
            AccountId = null, TotalSha256 = original.TotalSha256.ToLowerInvariant(),
            UpdatedAtUtc = original.UpdatedAtUtc!.Value.ToOffset(TimeSpan.FromHours(7)),
            Parts = [original.Parts[0] with { StagingPath = "D:/local-cache/part", Sha256 = original.Parts[0].Sha256.ToLowerInvariant() }]
        };
        Assert.Equal(ManifestRevisionSelector.PortableFingerprint(original), ManifestRevisionSelector.PortableFingerprint(local));
        var remote = original with { FileName = "remote-name.bin" };
        Assert.Equal(ManifestRevisionSelector.PortableFingerprint(ManifestRevisionSelector.PreferNewest(original, remote)),
            ManifestRevisionSelector.PortableFingerprint(ManifestRevisionSelector.PreferNewest(local, remote)));
    }

    [Fact]
    public void RevisionWinsOverClockAndTimestampWinsWithinRevision()
    {
        var current = Manifest();
        var revision = current with { Revision = current.Revision + 1, UpdatedAtUtc = current.UpdatedAtUtc!.Value.AddDays(-1) };
        Assert.Same(revision, ManifestRevisionSelector.PreferNewest(current, revision));
        Assert.Same(revision, ManifestRevisionSelector.PreferNewest(revision, current));
        var time = current with { UpdatedAtUtc = current.UpdatedAtUtc!.Value.AddSeconds(1) };
        Assert.Same(time, ManifestRevisionSelector.PreferNewest(current, time));
        Assert.Same(time, ManifestRevisionSelector.PreferNewest(time, current));
    }

    [Theory]
    [InlineData("size")]
    [InlineData("hash")]
    [InlineData("account")]
    public void ContentOrOwnerMutationIsRejectedInBothDirections(string mutation)
    {
        var current = Manifest();
        var changed = mutation switch
        {
            "size" => current with { LogicalSize = 4 },
            "hash" => current with { TotalSha256 = new string('B', 64) },
            _ => current with { AccountId = "foreign-account" }
        };
        Assert.Throws<InvalidDataException>(() => ManifestRevisionSelector.PreferNewest(current, changed));
        Assert.Throws<InvalidDataException>(() => ManifestRevisionSelector.PreferNewest(changed, current));
    }

    [Fact]
    public void RemoteCommitPromotesDraftEvenWhenDraftHasLargerRevision()
    {
        var committed = Manifest();
        var draft = committed with { Committed = false, Revision = 50 };
        Assert.Same(committed, ManifestRevisionSelector.PreferNewest(draft, committed));
        Assert.Same(committed, ManifestRevisionSelector.PreferNewest(committed, draft));
    }

    [Theory]
    [InlineData("same", true)]
    [InlineData("key", true)]
    [InlineData("size", false)]
    [InlineData("hash", false)]
    [InlineData("version", false)]
    public void CiphertextCacheIsCarriedOnlyForTheSamePayload(string change, bool preserve)
    {
        var key = new PassphraseKeyEnvelope(1, "PBKDF2", 100000, "AES-GCM", "salt", "nonce", "cipher", "tag");
        var encrypted = new EncryptedPayloadDescriptor(1, 50, new string('C', 64), key, "D:/local-cache/ciphertext");
        var local = Manifest() with { Encryption = encrypted };
        var remoteEncryption = encrypted with { StagingPath = null };
        remoteEncryption = change switch
        {
            "key" => remoteEncryption with { RecoveryKey = key with { Salt = "rewrapped" } },
            "size" => remoteEncryption with { PayloadSize = 51 },
            "hash" => remoteEncryption with { PayloadSha256 = new string('D', 64) },
            "version" => remoteEncryption with { Version = 2 },
            _ => remoteEncryption
        };
        var remote = local with { Revision = 3, Encryption = remoteEncryption };
        var selected = ManifestRevisionSelector.PreferNewest(local, remote);
        Assert.Equal(preserve ? encrypted.StagingPath : null, selected.Encryption!.StagingPath);
        Assert.Equal(remoteEncryption.RecoveryKey, selected.Encryption.RecoveryKey);
    }
}
