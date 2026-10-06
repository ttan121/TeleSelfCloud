using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeleSelfCloud.Desktop;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class LegacyIsolationMetadataKeyTests : IDisposable
{
    private readonly string parent = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.LegacyMetadataKey", Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(parent, "retained");
    private string Target => Path.Combine(parent, "isolated");
    private const string PolicyName = "metadata-key-policy.json";
    private const string RecordName = "metadata-key.dpapi.json";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchingScopeCopiesOriginalRecordsAndCanResumeAPartialCopy(bool partial)
    {
        using var key = VaultMetadataKey.Create("42", -100);
        VaultMetadataKeyStore.Save(Source, key);
        var original = Snapshot(Source);
        if (partial)
        {
            Directory.CreateDirectory(Target);
            File.Copy(Path.Combine(Source, PolicyName), Path.Combine(Target, PolicyName));
        }
        Copy(Source, Target, "42", -100);
        Copy(Source, Target, "42", -100);

        using var copied = VaultMetadataKeyStore.Load(Target, "42", -100);
        Assert.NotNull(copied);
        Assert.Equal(key.KeyId, copied.KeyId);
        Assert.Equal(key.ScopeProof(), copied.ScopeProof());
        AssertRecordsEqual(original, Source);
        AssertRecordsEqual(original, Target);
        Assert.Empty(Directory.GetFiles(Target, "*.tmp"));
    }

    [Fact]
    public void AuthenticatedKeyForAnotherChatStaysInRetainedRootWithoutCreatingTargetRecords()
    {
        using var key = VaultMetadataKey.Create("42", -100);
        VaultMetadataKeyStore.Save(Source, key);
        var original = Snapshot(Source);

        Copy(Source, Target, "42", -200);
        Copy(Source, Target, "42", -200);

        Assert.False(Directory.Exists(Target));
        AssertRecordsEqual(original, Source);
        using var retained = VaultMetadataKeyStore.Load(Source, "42", -100);
        Assert.NotNull(retained);
        Assert.Equal(key.ScopeProof(), retained.ScopeProof());
        Assert.Throws<InvalidDataException>(() => VaultMetadataKeyStore.Load(Source, "42", -200));
    }

    [Fact]
    public void UnconfiguredRetainedRootNeedsNoKeyCopy()
    {
        Copy(Source, Target, "42", -200);
        Assert.False(Directory.Exists(Source));
        Assert.False(Directory.Exists(Target));
    }

    [Theory]
    [InlineData("missing-policy", -100)]
    [InlineData("missing-policy", -200)]
    [InlineData("missing-record", -100)]
    [InlineData("missing-record", -200)]
    [InlineData("bad-policy-json", -200)]
    [InlineData("bad-record-json", -200)]
    [InlineData("wrong-account", -200)]
    [InlineData("changed-record-chat", -200)]
    [InlineData("changed-policy-chat", -200)]
    [InlineData("bad-protected-key", -200)]
    [InlineData("bad-proof", -200)]
    [InlineData("different-policy-key", -200)]
    public void BrokenOrForeignRecordsCannotBeIgnoredEvenForDifferentChat(string damage, long requestedChat)
    {
        using var key = VaultMetadataKey.Create(damage == "wrong-account" ? "99" : "42", -100);
        VaultMetadataKeyStore.Save(Source, key);
        var record = Path.Combine(Source, RecordName);
        var policy = Path.Combine(Source, PolicyName);
        switch (damage)
        {
            case "missing-policy": File.Delete(policy); break;
            case "missing-record": File.Delete(record); break;
            case "bad-policy-json": File.WriteAllText(policy, "broken policy"); break;
            case "bad-record-json": File.WriteAllText(record, "broken key"); break;
            case "changed-record-chat": Change(record, "chatId", -101L); break;
            case "changed-policy-chat": Change(policy, "chatId", -101L); break;
            case "bad-protected-key": Change(record, "protectedKey", Convert.ToBase64String(new byte[32])); break;
            case "bad-proof": Change(record, "proof", Convert.ToBase64String(new byte[32])); break;
            case "different-policy-key": Change(policy, "keyId", Guid.NewGuid().ToString("N")); break;
        }
        var retained = Snapshot(Source);

        var error = Record.Exception(() => Copy(Source, Target, "42", requestedChat));

        Assert.NotNull(error);
        Assert.True(error is InvalidDataException or FileNotFoundException or JsonException or CryptographicException, error.ToString());
        Assert.False(Directory.Exists(Target));
        AssertRecordsEqual(retained, Source);
    }

    [Fact]
    public void ConflictingTargetRecordFailsBeforeCopyingTheOtherRecord()
    {
        using var sourceKey = VaultMetadataKey.Create("42", -100);
        using var targetKey = VaultMetadataKey.Create("42", -100);
        VaultMetadataKeyStore.Save(Source, sourceKey);
        VaultMetadataKeyStore.Save(Target, targetKey);
        File.Delete(Path.Combine(Target, PolicyName));
        var source = Snapshot(Source);
        var target = Snapshot(Target);

        Assert.Throws<InvalidDataException>(() => Copy(Source, Target, "42", -100));

        Assert.False(File.Exists(Path.Combine(Target, PolicyName)));
        AssertRecordsEqual(source, Source);
        AssertRecordsEqual(target, Target);
    }

    private static void Copy(string source, string target, string account, long chat)
    {
        try
        {
            typeof(MainWindow).GetMethod("CopyValidatedMetadataKey", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [source, target, account, chat]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); }
    }

    private static void Change<T>(string path, string name, T value)
    {
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json[name] = JsonValue.Create(value);
        File.WriteAllText(path, json.ToJsonString());
    }

    private static Dictionary<string, byte[]> Snapshot(string root) => new[] { PolicyName, RecordName }
        .Where(name => File.Exists(Path.Combine(root, name)))
        .ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(root, name)));

    private static void AssertRecordsEqual(Dictionary<string, byte[]> expected, string root)
    {
        var actual = Snapshot(root);
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (name, bytes) in expected) Assert.Equal(bytes, actual[name]);
    }

    public void Dispose() { if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true); }
}
