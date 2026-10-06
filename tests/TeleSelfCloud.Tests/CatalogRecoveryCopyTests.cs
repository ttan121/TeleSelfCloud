using TeleSelfCloud.Desktop;

namespace TeleSelfCloud.Tests;

public sealed class CatalogRecoveryCopyTests
{
    [Theory]
    [InlineData("A catalog sync is already running.", "Hãy chờ")]
    [InlineData("Telegram history replayed messages newer than the requested cursor; the sync checkpoint was not advanced.", "Điểm đồng bộ được giữ nguyên")]
    [InlineData("Telegram history returned a message from a different storage channel; the sync checkpoint was not advanced.", "kho khác")]
    [InlineData("A remote catalog caption is malformed; the sync checkpoint was not advanced.", "Điểm đồng bộ được giữ nguyên")]
    [InlineData("The remote manifest schema version is not supported; the sync checkpoint was not advanced.", "bản cập nhật")]
    [InlineData("A remote manifest part points outside the active storage channel.", "ngoài kho")]
    [InlineData("Remote folder state contains a null folder entry.", "thiếu dữ liệu")]
    [InlineData("Remote folder state contains a null tombstone.", "bản ghi xóa")]
    [InlineData("Manifest contains a null part entry.", "bản sao hợp lệ")]
    public void RecoveryErrorsExplainActionInVietnameseAndPreserveEnglish(string message, string expectedDetail)
    {
        var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.CopyTests", Guid.NewGuid().ToString("N"));
        try
        {
            var text = new UiText();
            text.SetLanguage("vi", Path.Combine(root, "language.json"));
            Assert.Contains(expectedDetail, text.LocalizeMessage(message));
            text.SetLanguage("en", Path.Combine(root, "language.json"));
            Assert.Equal(message, text.LocalizeMessage(message));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
