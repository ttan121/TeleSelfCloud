using TeleSelfCloud.Desktop;

namespace TeleSelfCloud.Tests;

public sealed class MetadataConflictCopyTests
{
    [Theory]
    [InlineData("Metadata history is busy. Wait for the current operation and retry.")]
    [InlineData("Metadata history exceeds the safe size limit. Its file was kept.")]
    [InlineData("Metadata history is empty. Its file was kept.")]
    [InlineData("Metadata history is corrupt. Its file was kept; restore a profile backup before retrying.")]
    [InlineData("Metadata history belongs to a different file, account, or vault. Local data was kept.")]
    [InlineData("Metadata history is invalid. Its file was kept.")]
    [InlineData("Metadata resolution plan is invalid. Its file was kept.")]
    [InlineData("No competing metadata versions were saved for this file.")]
    [InlineData("The selected metadata version is no longer available. Refresh the versions and retry.")]
    [InlineData("This file has an unfinished transfer. Finish or cancel it before applying a metadata version.")]
    [InlineData("A metadata resolution is pending. Retry the saved selection before choosing another version.")]
    [InlineData("The file changed after metadata resolution started. Its plan was kept; review the new metadata before retrying.")]
    public void RecoveryErrorsAreTranslatedAndHaveAnAction(string message)
    {
        var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.MetadataCopy", Guid.NewGuid().ToString("N"));
        try
        {
            var text = new UiText();
            text.SetLanguage("vi", Path.Combine(root, "language.json"));
            Assert.Contains("hãy", text.LocalizeMessage(message).ToLowerInvariant());
            text.SetLanguage("en", Path.Combine(root, "language.json"));
            Assert.Equal(message, text.LocalizeMessage(message));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
