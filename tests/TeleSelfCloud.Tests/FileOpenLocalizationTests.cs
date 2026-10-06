using System.Runtime.ExceptionServices;
using TeleSelfCloud.Desktop;

namespace TeleSelfCloud.Tests;

public sealed class FileOpenLocalizationTests
{
    [Theory]
    [InlineData("opening this file", "Could not open ", "Không thể mở ", "mở tệp này")]
    [InlineData("previewing", "Could not preview ", "Không thể xem trước ", "xem trước")]
    public void FileErrorDialogAndStatusCauseUseTheSelectedLanguage(string action, string prefix, string translatedPrefix, string vietnameseAction)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.FileOpenLocalization", Guid.NewGuid().ToString("N"));
            try
            {
                var preference = Path.Combine(root, "language.json");
                var english = $"Connect private storage or download all file parts before {action}.";
                UiText.Instance.SetLanguage("vi", preference);
                var translated = $"Hãy kết nối kho riêng tư hoặc tải đủ các phần tệp trước khi {vietnameseAction}.";
                Assert.Equal(translated, UiText.Instance.LocalizeMessage(english));
                var status = new LocalizedTextBlock { Text = prefix + "report.bin: " + UiText.Instance.LocalizeMessage(english) };
                Assert.Equal(translatedPrefix + "report.bin: " + translated, status.Text);
                UiText.Instance.SetLanguage("en", preference);
                Assert.Equal(english, UiText.Instance.LocalizeMessage(english));
                status = new LocalizedTextBlock { Text = prefix + "report.bin: " + UiText.Instance.LocalizeMessage(english) };
                Assert.Equal(prefix + "report.bin: " + english, status.Text);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Localization fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
