using System.Security.Cryptography;
using System.Text;
using TeleSelfCloud.Infrastructure.Telegram;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Tests;

public sealed class ProtectedVaultRegistryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.ProtectedRegistry", Guid.NewGuid().ToString("N"));
    private readonly string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private readonly string scope = Guid.NewGuid().ToString("N");
    private string PathName => Path.Combine(root, "vaults.json");
    private LocalRecordCipher Cipher(string? id = null, string purpose = "vault-registry", string identity = "account:42") => new(key, id ?? scope, purpose, identity);
    private static TelegramStorageChannelInfo Channel(long chat) => new(chat, "42", "Private vault title");
    [Fact]
    public async Task PrimaryActiveMappingsAndCreationReceiptSurviveEncryptedReopenAndMutation()
    {
        using var cipher = Cipher(); var registry = new TelegramVaultRegistry(root, "42", cipher);
        var receipt = Guid.NewGuid().ToString("N"); await registry.RegisterAsync(Channel(-101) with { CreationRequestId = receipt }, true, default);
        var second = await registry.RegisterAsync(Channel(-102), true, default);
        Assert.Equal(root, registry.GetDataDirectory(second, -101)); Assert.NotEqual(root, registry.GetDataDirectory(second, -102));
        using var reopenCipher = Cipher(); var reopen = new TelegramVaultRegistry(root, "42", reopenCipher);
        var loaded = await reopen.LoadAsync(default); Assert.Equal(-101, loaded!.PrimaryChatId); Assert.Equal(-102, loaded.ActiveChatId);
        Assert.Equal(receipt, loaded.Vaults.Single(v => v.ChatId == -101).CreationRequestId);
        var back = await reopen.SelectAsync(-101, default); Assert.Equal(-101, back.ActiveChatId); Assert.Equal(2, back.Vaults.Count);
        Assert.DoesNotContain("Private vault", Encoding.UTF8.GetString(File.ReadAllBytes(PathName))); Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }
    [Theory]
    [InlineData("scope")]
    [InlineData("purpose")]
    [InlineData("identity")]
    [InlineData("account")]
    [InlineData("tag")]
    [InlineData("plain-reader")]
    public async Task ForeignOrDamagedStateCannotRegisterReplaceOrChangeSelection(string damage)
    {
        using var cipher = Cipher(); var registry = new TelegramVaultRegistry(root, "42", cipher); await registry.RegisterAsync(Channel(-101), true, default);
        if (damage == "tag") { var bytes = File.ReadAllBytes(PathName); bytes[^1] ^= 1; File.WriteAllBytes(PathName, bytes); }
        var before = File.ReadAllBytes(PathName);
        using var other = Cipher(damage == "scope" ? Guid.NewGuid().ToString("N") : null, damage == "purpose" ? "sync-report" : "vault-registry", damage == "identity" ? "account:99" : "account:42");
        var invalid = new TelegramVaultRegistry(root, damage == "account" ? "99" : "42", damage == "plain-reader" ? null : other);
        var error = await Record.ExceptionAsync(() => invalid.SelectAsync(-101, default));
        Assert.True(error is InvalidDataException or CryptographicException); Assert.Equal(before, File.ReadAllBytes(PathName));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }
    [Fact]
    public async Task PlainLegacyRegistryIsPreservedUntilExplicitMigration()
    {
        await new TelegramVaultRegistry(root, "42").RegisterAsync(Channel(-101), true, default); var before = File.ReadAllBytes(PathName);
        using var cipher = Cipher(); var protectedRegistry = new TelegramVaultRegistry(root, "42", cipher);
        await Assert.ThrowsAsync<InvalidDataException>(() => protectedRegistry.RegisterAsync(Channel(-102), true, default)); Assert.Equal(before, File.ReadAllBytes(PathName));
        Assert.Equal(-101, (await new TelegramVaultRegistry(root, "42").LoadAsync(default))!.ActiveChatId);
    }
    [Fact]
    public async Task InnerChecksumAndForeignChannelStillRejectEvenWithAuthenticatedEnvelope()
    {
        await new TelegramVaultRegistry(root, "42").RegisterAsync(Channel(-101), true, default);
        var plain = Encoding.UTF8.GetString(File.ReadAllBytes(PathName)).Replace("Private vault title", "Changed vault title"); using var cipher = Cipher();
        File.WriteAllBytes(PathName, cipher.Protect(Encoding.UTF8.GetBytes(plain))); var before = File.ReadAllBytes(PathName);
        var registry = new TelegramVaultRegistry(root, "42", cipher);
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.LoadAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.RegisterAsync(Channel(-102) with { AccountId = "99" }, true, default)); Assert.Equal(before, File.ReadAllBytes(PathName));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
