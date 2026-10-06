using TeleSelfCloud.Infrastructure.Telegram;

namespace TeleSelfCloud.Tests;

public sealed class TelegramSessionProfileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud-SessionPointer", Guid.NewGuid().ToString("N"));

    [Fact]
    public void MalformedActivePointerFailsClosedWithoutSelectingLegacySession()
    {
        Directory.CreateDirectory(_root);
        var legacy = Path.Combine(_root, "telegram-account");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "session.dat"), "legacy session to preserve");

        var pointerPath = Path.Combine(_root, "telegram-active-session.json");
        var corruptPointer = "{ definitely not a session pointer"u8.ToArray();
        File.WriteAllBytes(pointerPath, corruptPointer);

        var error = Assert.Throws<InvalidDataException>(() => TelegramSessionProfileStore.ResolveForRestore(_root));

        Assert.Contains("pointer", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(corruptPointer, File.ReadAllBytes(pointerPath));
        Assert.Equal("legacy session to preserve", File.ReadAllText(Path.Combine(legacy, "session.dat")));
    }

    [Fact]
    public void OversizedActivePointerIsRejectedAndPreserved()
    {
        Directory.CreateDirectory(_root);
        var pointerPath = Path.Combine(_root, "telegram-active-session.json");
        var bytes = new byte[16 * 1024 + 1];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(pointerPath, bytes);

        Assert.Throws<InvalidDataException>(() => TelegramSessionProfileStore.ResolveForRestore(_root));
        Assert.Equal(bytes, File.ReadAllBytes(pointerPath));
    }

    [Fact]
    public void DirectoryAtActivePointerPathDoesNotMasqueradeAsAMissingPointer()
    {
        Directory.CreateDirectory(_root);
        var pointerPath = Path.Combine(_root, "telegram-active-session.json");
        Directory.CreateDirectory(pointerPath);

        Assert.Throws<InvalidDataException>(() => TelegramSessionProfileStore.ResolveForRestore(_root));
        Assert.True(Directory.Exists(pointerPath));
    }

    [Fact]
    public void MissingPointersRemainCompatibleWithLegacySingleAccountSession()
    {
        Directory.CreateDirectory(_root);
        var legacy = Path.Combine(_root, "telegram-account");
        Directory.CreateDirectory(legacy);

        Assert.Equal(Path.GetFullPath(legacy), Path.GetFullPath(TelegramSessionProfileStore.ResolveForRestore(_root)));
    }

    [Fact]
    public void NewSignInPointerIsDpapiProtectedAndStillResolvesAndClears()
    {
        Directory.CreateDirectory(_root);
        var session = TelegramSessionProfileStore.CreateSignInDirectory(_root);
        var pointerPath = Path.Combine(_root, "telegram-signin-session.json");
        var pointerBytes = File.ReadAllBytes(pointerPath);

        Assert.StartsWith("TSCPTR01", System.Text.Encoding.ASCII.GetString(pointerBytes));
        Assert.DoesNotContain(Path.GetFileName(session), System.Text.Encoding.UTF8.GetString(pointerBytes));
        Assert.Equal(Path.GetFullPath(session), Path.GetFullPath(TelegramSessionProfileStore.ResolveForRestore(_root)));

        TelegramSessionProfileStore.ClearSignInSessionPointer(_root, session);
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "telegram-account")),
            Path.GetFullPath(TelegramSessionProfileStore.ResolveForRestore(_root)));
        Assert.True(Directory.Exists(session));
    }

    [Fact]
    public void TamperedDpapiPointerFailsClosedAndPreservesBytes()
    {
        Directory.CreateDirectory(_root);
        var session = TelegramSessionProfileStore.CreateSignInDirectory(_root);
        var pointerPath = Path.Combine(_root, "telegram-signin-session.json");
        var bytes = File.ReadAllBytes(pointerPath);
        bytes[^1] ^= 0x40;
        File.WriteAllBytes(pointerPath, bytes);

        Assert.Throws<InvalidDataException>(() => TelegramSessionProfileStore.ResolveForRestore(_root));
        Assert.Equal(bytes, File.ReadAllBytes(pointerPath));
        Assert.True(Directory.Exists(session));
    }

    [Fact]
    public void NewActivePointerIsDpapiProtectedAndRestoresAccountSession()
    {
        Directory.CreateDirectory(_root);
        var signInSession = TelegramSessionProfileStore.CreateSignInDirectory(_root);
        File.WriteAllText(Path.Combine(signInSession, "session.dat"), "isolated fixture");
        var accountSession = TelegramSessionProfileStore.MoveToAccountProfile(_root, "404", signInSession);
        var pointerPath = Path.Combine(_root, "telegram-active-session.json");
        var pointerBytes = File.ReadAllBytes(pointerPath);

        Assert.StartsWith("TSCPTR01", System.Text.Encoding.ASCII.GetString(pointerBytes));
        Assert.DoesNotContain("404", System.Text.Encoding.UTF8.GetString(pointerBytes));
        Assert.Equal(Path.GetFullPath(accountSession), Path.GetFullPath(TelegramSessionProfileStore.ResolveForRestore(_root)));

        TelegramSessionProfileStore.ClearCurrentSessionPointer(_root, "404");
        Assert.True(Directory.Exists(accountSession));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
