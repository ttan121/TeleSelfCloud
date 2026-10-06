using TeleSelfCloud.Desktop;

namespace TeleSelfCloud.Tests;

public sealed class TelegramRecoveryCopyTests
{
    [Theory]
    [InlineData("Manifest revisions belong to different accounts. Local data was kept.")]
    [InlineData("A manifest revision changes immutable file content. Local data was kept; inspect the storage history before retrying.")]
    [InlineData("Telegram recovery history is malformed. The saved transfer was kept; no new document was sent.")]
    [InlineData("Telegram recovery history has invalid order, IDs, or storage scope. The saved transfer was kept; no new document was sent.")]
    [InlineData("Telegram recovery history did not advance. The saved transfer was kept; no new document was sent.")]
    [InlineData("Telegram returned a different storage message. The saved transfer was kept.")]
    [InlineData("Telegram returned a different document file. The saved transfer was kept.")]
    [InlineData("Telegram returned an invalid send identity. The saved transfer was kept.")]
    [InlineData("Telegram returned an invalid upload confirmation. The saved transfer was kept.")]
    [InlineData("Invalid Telegram remote message reference.")]
    [InlineData("Telegram send updates exceeded the recovery buffer. The saved transfer was kept; retry to verify the accepted document.")]
    [InlineData("A matching Telegram document is still being sent. Wait for confirmation before retrying.")]
    [InlineData("The Telegram storage document is not confirmed. Wait for sending to finish before retrying.")]
    public void ErrorsExplainRecoveryInVietnameseAndPreserveEnglish(string message)
    {
        var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.TelegramRecoveryCopy", Guid.NewGuid().ToString("N"));
        try
        {
            var text = new UiText();
            text.SetLanguage("vi", Path.Combine(root, "language.json"));
            Assert.NotEqual(message, text.LocalizeMessage(message));
            Assert.Contains("hãy", text.LocalizeMessage(message).ToLowerInvariant());
            text.SetLanguage("en", Path.Combine(root, "language.json"));
            Assert.Equal(message, text.LocalizeMessage(message));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
