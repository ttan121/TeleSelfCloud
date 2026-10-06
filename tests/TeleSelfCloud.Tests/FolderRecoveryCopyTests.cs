using TeleSelfCloud.Desktop;

namespace TeleSelfCloud.Tests;

public sealed class FolderRecoveryCopyTests
{
    [Theory]
    [InlineData("The pending folder plan changed. Review it again before continuing.", "xem lại")]
    [InlineData("The pending folder plan changed. Review it again before stopping remaining steps.", "xem lại")]
    [InlineData("The stopped folder archive differs from its pending plan. Both files were kept.", "Giữ cả hai")]
    [InlineData("A folder change is already running for this account and vault.", "Hãy chờ")]
    [InlineData("Finish the pending folder change before starting another one.", "Tiếp tục thay đổi thư mục")]
    [InlineData("A file in the pending folder change is missing. Its recovery plan was kept.", "giữ nguyên")]
    [InlineData("A file changed while its folder operation was pending. Sync and resolve the conflict before retrying.", "xung đột")]
    [InlineData("The folder contents changed while its operation was pending. Sync and resolve the conflict before retrying.", "xung đột")]
    [InlineData("The pending folder operation is corrupt; its recovery file was kept.", "bản sao lưu")]
    [InlineData("Cancel and remove active transfers before changing their folder.", "lượt truyền")]
    [InlineData("Another download is using this destination. Wait for it to finish or pause it before retrying.", "đích này")]
    [InlineData("The download recovery record is invalid. Its files were kept; check the local backup before retrying.", "giữ nguyên")]
    public void RecoveryErrorsAreActionableInBothLanguages(string message, string detail)
    {
        var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.FolderCopy", Guid.NewGuid().ToString("N"));
        try
        {
            var text = new UiText();
            text.SetLanguage("vi", Path.Combine(root, "language.json"));
            Assert.Contains(detail, text.LocalizeMessage(message));
            Assert.Contains("Tiếp tục", text.Get("folder.resume"));
            text.SetLanguage("en", Path.Combine(root, "language.json"));
            Assert.Equal(message, text.LocalizeMessage(message));
            Assert.Equal("Resume folder change", text.Get("folder.resume"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
