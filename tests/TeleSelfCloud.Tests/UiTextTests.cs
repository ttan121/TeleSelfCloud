using TeleSelfCloud.Desktop;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TeleSelfCloud.Tests;

public sealed class UiTextTests
{
    [Fact]
    public void PdfPreviewUsesInstalledDefaultViewerAndHasBilingualCopy()
    {
        Assert.True(FilePreviewWindow.SupportsPreview("report.pdf"));
        Assert.True(FilePreviewWindow.SupportsStreamingPreview("episode.mp4"));
        Assert.True(FilePreviewWindow.SupportsStreamingPreview("voice.ogg"));
        Assert.False(FilePreviewWindow.SupportsStreamingPreview("report.pdf"));
        Assert.False(FilePreviewWindow.SupportsPreview("unknown.abc"));
        var preferencePath = Path.Combine(Path.GetTempPath(), "TeleSelfCloud-UiTextTests", Guid.NewGuid().ToString("N"), "language.json");
        try
        {
            UiText.Instance.SetLanguage("en", preferencePath);
            Assert.Equal("PDF files open in your default PDF app.", UiText.Instance.Get("preview.pdf.external"));
            Assert.Equal("Open PDF", UiText.Instance.Get("preview.pdf.open"));
            Assert.Equal("Open in default media app", UiText.Instance.Get("preview.media.open"));
            Assert.Contains("requested byte ranges", UiText.Instance.Get("preview.media.help"));
            UiText.Instance.SetLanguage("vi", preferencePath);
            Assert.Equal("Tệp PDF sẽ mở bằng ứng dụng PDF mặc định.", UiText.Instance.Get("preview.pdf.external"));
            Assert.Equal("Mở PDF", UiText.Instance.Get("preview.pdf.open"));
            Assert.Equal("Mở bằng trình phát mặc định", UiText.Instance.Get("preview.media.open"));
            Assert.Contains("Đóng cửa sổ này để dừng luồng", UiText.Instance.Get("preview.media.help"));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(preferencePath)!, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    [Fact]
    public void LiteralMainWindowStatusMessagesHaveVietnameseTranslations()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "TeleSelfCloud.slnx")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate TeleSelfCloud.slnx.");
        var source = File.ReadAllText(Path.Combine(root, "src", "TeleSelfCloud.Desktop", "MainWindow.xaml.cs"));
        var literals = Regex.Matches(source, "StatusText\\.Text\\s*=\\s*\"((?:\\\\.|[^\"\\\\])*)\"")
            .Cast<Match>()
            .Select(match => Regex.Unescape(match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(literals);

        var preferencePath = Path.Combine(Path.GetTempPath(), "TeleSelfCloud-UiTextTests", Guid.NewGuid().ToString("N"), "language.json");
        var text = UiText.Instance;
        try
        {
            text.SetLanguage("vi", preferencePath);
            foreach (var literal in literals)
                Assert.NotEqual(literal, text.LocalizeMessage(literal));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(preferencePath)!, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    [Fact]
    public void AuthenticationPromptsAndCommonErrorsAreAvailableInBothLanguages()
    {
        var preferencePath = Path.Combine(Path.GetTempPath(), "TeleSelfCloud-UiTextTests", Guid.NewGuid().ToString("N"), "language.json");
        var text = UiText.Instance;
        try
        {
            text.SetLanguage("en", preferencePath);
            Assert.Equal("Enter your phone number in international format, including the country code.", text.Get("auth.step.phone"));
            Assert.Equal("That login code is invalid or expired. Check the latest code from Telegram and try again.", text.Get("auth.error.code"));
            Assert.Equal("Upload failed for sample.bin: TDLib_ERROR", string.Format(text.Get("status.transferFailed"), text.Get("upload.upload"), "sample.bin", "TDLib_ERROR"));
            Assert.Equal("full sync completed; 2 manifest(s) updated. Last sync now.", string.Format(text.Get("status.syncCompleted"), text.Get("sync.full"), 2, "now"));
            Assert.Equal("Connected to private storage channel 'Test Channel'. full sync indexed 2 manifest(s); last sync now.", string.Format(text.Get("status.storageConnectedAndSynced"), "Test Channel", text.Get("sync.full"), 2, "now"));
            Assert.Equal("Remote status unknown", text.Get("files.status.remoteUnknown"));
            Assert.Equal("Remote status unknown", text.Get("files.stateRemoteUnknown"));
            Assert.Equal("Favorites", text.Get("files.scope.favorites"));
            Assert.Equal("Archive", text.Get("files.scope.archive"));
            Assert.Equal("Hidden", text.Get("files.scope.hidden"));
            Assert.Equal("Add to favorites", text.Get("action.addFavorite"));
            Assert.Equal("Move to archive", text.Get("action.archive"));
            Assert.Equal("Hide", text.Get("action.hide"));
            Assert.Equal("ZIP archive contents", text.Get("preview.zip.contents"));
            Assert.Equal("Folder", text.Get("preview.zip.folder"));
            Assert.Equal("Local data protection", text.Get("settings.localDataProtection"));
            Assert.Equal("The key for this session's TDLib database is protected with Windows DPAPI for this Windows user.", text.Get("security.tdlibDatabaseProtected"));
            Assert.Contains("processes running as this Windows user", text.Get("settings.localDataProtectionNote"));
            Assert.Equal("The saved Telegram storage channel could not be found or accessed. Local manifests and staged parts were preserved.", text.LocalizeMessage("The saved Telegram storage channel could not be found or accessed. Local manifests and staged parts were preserved."));
            Assert.Equal("Full sync in progress: 2 page(s), 1,234 message(s), 3 manifest(s) found, 4 file(s) indexed.", text.LocalizeMessage("Full sync in progress: 2 page(s), 1,234 message(s), 3 manifest(s) found, 4 file(s) indexed."));
            Assert.Equal("Sending a 1,234-byte Telegram document...", text.LocalizeMessage("Sending a 1,234-byte Telegram document..."));
            Assert.Equal("Connected: Test User (Standard; ID 123)", text.LocalizeMessage("Connected: Test User (Standard; ID 123)"));
            Assert.Equal("Choose an app for sample.bin...", string.Format(text.Get("status.chooseAppForFile"), "sample.bin"));
            Assert.Equal("Preparing sample.txt for its default app...", string.Format(text.Get("status.prepareFileForDefaultApp"), "sample.txt"));
            Assert.Equal("Renamed to renamed.txt.", string.Format(text.Get("status.fileRenamed"), "renamed.txt"));
            Assert.Equal("Moved file to Reports.", string.Format(text.Get("status.fileMoved"), "Reports"));
            Assert.Equal("Restored report.pdf from Trash.", string.Format(text.Get("status.fileRestoredFromTrash"), "report.pdf"));
            Assert.Equal("Moved report.pdf to Trash. Telegram file parts were kept.", string.Format(text.Get("status.fileMovedToTrash"), "report.pdf"));
            Assert.Equal("Downloaded and verified sample.bin.", string.Format(text.Get("status.downloadedAndVerified"), "sample.bin"));
            Assert.Equal("Uploaded and verified sample.bin (2 part(s)).", string.Format(text.Get("status.uploadedAndVerified"), "sample.bin", 2));
            Assert.Equal("Restored and verified sample.bin (5 bytes, SHA-256 abc123).", string.Format(text.Get("status.restoredAndVerified"), "sample.bin", 5, "abc123"));
            Assert.Equal("Preparing this account's local storage...", text.Get("status.preparingAccountStorage"));
            Assert.Equal("Enter a valid file name without a folder path.", text.LocalizeMessage("Enter a valid file name without a folder path."));
            Assert.Equal("Cancel or pause and remove this file's queued transfers before deleting it.", text.LocalizeMessage("Cancel or pause and remove this file's queued transfers before deleting it."));
            Assert.Equal("Keyboard shortcut: Ctrl+2", text.Get("shortcut.transfers"));
            Assert.Contains("Temporary upload error", string.Format(text.Get("status.queueTemporaryError"), text.Get("upload.upload").ToLowerInvariant(), "sample.bin", 1, 3, "5"));

            text.SetLanguage("vi", preferencePath);
            Assert.Equal("Hãy đăng nhập và kết nối kho riêng tư trước.", text.LocalizeMessage("Log in and connect the private storage channel first."));
            Assert.Equal("Nhập số điện thoại theo định dạng quốc tế, bao gồm mã quốc gia.", text.Get("auth.step.phone"));
            Assert.Equal("Mã đăng nhập không đúng hoặc đã hết hạn. Hãy kiểm tra mã mới nhất Telegram gửi rồi thử lại.", text.Get("auth.error.code"));
            Assert.Equal("Tải lên thất bại với sample.bin: TDLib_ERROR", string.Format(text.Get("status.transferFailed"), text.Get("upload.upload"), "sample.bin", "TDLib_ERROR"));
            Assert.Equal("Đồng bộ toàn bộ hoàn tất; đã cập nhật 2 danh mục. Lần đồng bộ gần nhất: bây giờ.", string.Format(text.Get("status.syncCompleted"), text.Get("sync.full"), 2, "bây giờ"));
            Assert.Equal("Đã kết nối kho riêng tư 'Test Channel'. Đồng bộ toàn bộ đã lập chỉ mục 2 danh mục; lần đồng bộ gần nhất: bây giờ.", string.Format(text.Get("status.storageConnectedAndSynced"), "Test Channel", text.Get("sync.full"), 2, "bây giờ"));
            Assert.Equal("Chưa xác minh trạng thái kho", text.Get("files.status.remoteUnknown"));
            Assert.Equal("Chưa xác minh trạng thái kho", text.Get("files.stateRemoteUnknown"));
            Assert.Equal("Yêu thích", text.Get("files.scope.favorites"));
            Assert.Equal("Lưu trữ", text.Get("files.scope.archive"));
            Assert.Equal("Đã ẩn", text.Get("files.scope.hidden"));
            Assert.Equal("Thêm vào yêu thích", text.Get("action.addFavorite"));
            Assert.Equal("Chuyển vào lưu trữ", text.Get("action.archive"));
            Assert.Equal("Ẩn tệp", text.Get("action.hide"));
            Assert.Equal("Nội dung tệp ZIP", text.Get("preview.zip.contents"));
            Assert.Equal("Thư mục", text.Get("preview.zip.folder"));
            Assert.Equal("Bảo vệ dữ liệu trên máy", text.Get("settings.localDataProtection"));
            Assert.Equal("Khóa cơ sở dữ liệu TDLib của phiên này được Windows DPAPI bảo vệ cho tài khoản Windows hiện tại.", text.Get("security.tdlibDatabaseProtected"));
            Assert.Contains("cùng tài khoản Windows", text.Get("settings.localDataProtectionNote"));
            Assert.Equal("Không tìm thấy hoặc không thể truy cập kênh kho Telegram đã lưu. Danh mục và các phần tệp cục bộ vẫn được giữ nguyên.", text.LocalizeMessage("The saved Telegram storage channel could not be found or accessed. Local manifests and staged parts were preserved."));
            Assert.Equal("Đồng bộ toàn bộ đang diễn ra: 2 trang, 1,234 tin nhắn, tìm thấy 3 danh mục, đã lập chỉ mục 4 tệp.", text.LocalizeMessage("Full sync in progress: 2 page(s), 1,234 message(s), 3 manifest(s) found, 4 file(s) indexed."));
            Assert.Equal("Đang gửi tài liệu Telegram dung lượng 1,234 byte...", text.LocalizeMessage("Sending a 1,234-byte Telegram document..."));
            Assert.Equal("Đã đăng nhập: Test User (gói Phổ thông; ID 123)", text.LocalizeMessage("Connected: Test User (Standard; ID 123)"));
            Assert.Equal("Chọn ứng dụng để mở sample.bin...", string.Format(text.Get("status.chooseAppForFile"), "sample.bin"));
            Assert.Equal("Đang chuẩn bị sample.txt để mở bằng ứng dụng mặc định...", string.Format(text.Get("status.prepareFileForDefaultApp"), "sample.txt"));
            Assert.Equal("Đã đổi tên thành renamed.txt.", string.Format(text.Get("status.fileRenamed"), "renamed.txt"));
            Assert.Equal("Đã chuyển tệp vào Reports.", string.Format(text.Get("status.fileMoved"), "Reports"));
            Assert.Equal("Đã khôi phục report.pdf khỏi thùng rác.", string.Format(text.Get("status.fileRestoredFromTrash"), "report.pdf"));
            Assert.Equal("Đã chuyển report.pdf vào thùng rác. Các phần tệp trên Telegram vẫn được giữ.", string.Format(text.Get("status.fileMovedToTrash"), "report.pdf"));
            Assert.Equal("Đã tải xuống và xác minh sample.bin.", string.Format(text.Get("status.downloadedAndVerified"), "sample.bin"));
            Assert.Equal("Đã tải lên và xác minh sample.bin (2 phần).", string.Format(text.Get("status.uploadedAndVerified"), "sample.bin", 2));
            Assert.Equal("Đã khôi phục và xác minh sample.bin (5 byte, SHA-256 abc123).", string.Format(text.Get("status.restoredAndVerified"), "sample.bin", 5, "abc123"));
            Assert.Equal("Đang chuẩn bị kho cục bộ riêng cho tài khoản này...", text.Get("status.preparingAccountStorage"));
            Assert.Equal("Hãy nhập tên tệp hợp lệ, không kèm đường dẫn thư mục.", text.LocalizeMessage("Enter a valid file name without a folder path."));
            Assert.Equal("Hãy hủy hoặc tạm dừng rồi xóa các tác vụ truyền tệp đang chờ trước khi xóa tệp này.", text.LocalizeMessage("Cancel or pause and remove this file's queued transfers before deleting it."));
            Assert.Equal("Phím tắt: Ctrl+2", text.Get("shortcut.transfers"));
            Assert.Contains("Lỗi tải lên tạm thời", string.Format(text.Get("status.queueTemporaryError"), text.Get("upload.upload").ToLowerInvariant(), "sample.bin", 1, 3, "5"));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(preferencePath)!, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    [Fact]
    public void FilesStatusColumnFitsRemoteStatusAndPageTitleRefreshesOnLanguageChange()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "TeleSelfCloud.slnx")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate TeleSelfCloud.slnx.");
        var xaml = File.ReadAllText(Path.Combine(root, "src", "TeleSelfCloud.Desktop", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "src", "TeleSelfCloud.Desktop", "MainWindow.xaml.cs"));

        Assert.Contains("x:Name=\"StatusColumn\" Width=\"130\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Converter={StaticResource ExplorerCompact}", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"{Binding StatusLabel}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("UpdatePageTitle();", code, StringComparison.Ordinal);
        Assert.Contains("private void UpdatePageTitle(string? page = null)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void CompositeStatusMessagesAreFullyLocalizedInBothLanguages()
    {
        var preferencePath = Path.Combine(Path.GetTempPath(), "TeleSelfCloud-UiTextTests", Guid.NewGuid().ToString("N"), "language.json");
        var text = UiText.Instance;
        try
        {
            text.SetLanguage("en", preferencePath);
            Assert.Equal("Canceled queued transfer for report.pdf. Staged data is retained locally.", string.Format(text.Get("status.queueCanceled"), "report.pdf"));
            Assert.Equal("Upload complete: report.pdf.", string.Format(text.Get("status.uploadComplete"), "report.pdf"));
            Assert.Equal("Upload checkpoint saved: report.pdf.", string.Format(text.Get("status.uploadCheckpointSaved"), "report.pdf"));
            Assert.Equal("Opened a local copy of report.pdf; edits do not sync back. Cleanup is eligible after 30 unchanged days if unused.", string.Format(text.Get("status.openedWithDefaultApp"), "report.pdf"));
            Assert.Equal("The app is not ready to create a temporary file. Close and reopen TeleSelfCloud, then try again.", text.Get("status.profileNotReady"));
            Assert.Equal("Restore failed: Access denied. The destination is replaced only after integrity checks pass.", string.Format(text.Get("status.restoreFailedPartial"), "Access denied"));

            text.SetLanguage("vi", preferencePath);
            Assert.Equal("Đã hủy tác vụ truyền tệp cho report.pdf. Dữ liệu đã chuẩn bị vẫn được giữ trên máy.", string.Format(text.Get("status.queueCanceled"), "report.pdf"));
            Assert.Equal("Tải lên hoàn tất: report.pdf.", string.Format(text.Get("status.uploadComplete"), "report.pdf"));
            Assert.Equal("Đã lưu điểm tiếp tục tải lên: report.pdf.", string.Format(text.Get("status.uploadCheckpointSaved"), "report.pdf"));
            Assert.Equal("Đã mở bản sao cục bộ của report.pdf; chỉnh sửa không đồng bộ ngược. Có thể dọn sau 30 ngày không đổi nếu không còn được dùng.", string.Format(text.Get("status.openedWithDefaultApp"), "report.pdf"));
            Assert.Equal("Ứng dụng chưa sẵn sàng để tạo tệp tạm. Hãy đóng rồi mở lại TeleSelfCloud và thử lại.", text.Get("status.profileNotReady"));
            Assert.Equal("Khôi phục thất bại: Access denied. Tệp đích chỉ được thay thế sau khi vượt qua kiểm tra toàn vẹn.", string.Format(text.Get("status.restoreFailedPartial"), "Access denied"));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(preferencePath)!, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    [Fact]
    public void UploadPipelineErrorsAreLocalizedInVietnameseStatusAndQueueDetails()
    {
        var preferencePath = Path.Combine(Path.GetTempPath(), "TeleSelfCloud-UiTextTests", Guid.NewGuid().ToString("N"), "language.json");
        var text = UiText.Instance;
        try
        {
            text.SetLanguage("vi", preferencePath);
            var error = "This transfer belongs to a different Telegram account. Switch back to its owning account to resume.";
            var localizedError = text.LocalizeMessage(error);

            Assert.Equal("Tác vụ này thuộc tài khoản Telegram khác. Hãy chuyển về đúng tài khoản để tiếp tục.", localizedError);
            Assert.Equal("Tải lên thất bại với report.pdf: " + localizedError,
                string.Format(text.Get("status.transferFailed"), text.Get("upload.upload"), "report.pdf", localizedError));
            Assert.Equal("Chưa xác định được giới hạn tải lên của tài khoản hiện tại. Đã chặn tải lên; ứng dụng không giả định tài khoản Premium hoặc không giới hạn.",
                text.LocalizeMessage("The active account upload limit is unknown. Upload is blocked; no Premium or unlimited capability is assumed."));
            Assert.Equal("Chưa xác định được giới hạn tải lên của tài khoản hiện tại. Chỉ có thể tiếp tục sau khi xác minh giới hạn.",
                text.LocalizeMessage("The active account upload limit is unknown. Resume is blocked until the capability is verified."));
            Assert.Equal("Một phần tệp đã chuẩn bị vượt quá giới hạn tải lên đã xác minh của tài khoản hiện tại.",
                text.LocalizeMessage("A staged part exceeds the active account's verified upload limit."));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(preferencePath)!, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    [Fact]
    public void MainWindowPaletteMeetsSelectedWcagContrastRatios()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "TeleSelfCloud.slnx")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate TeleSelfCloud.slnx.");
        var xaml = File.ReadAllText(Path.Combine(root, "src", "TeleSelfCloud.Desktop", "MainWindow.xaml"));
        var colors = Regex.Matches(xaml, "<SolidColorBrush x:Key=\"([^\"]+)\" Color=\"#([0-9A-Fa-f]{6})\"")
            .Cast<Match>()
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value, StringComparer.Ordinal);
        var pairs = new (string Foreground, string Background, double Minimum)[]
        {
            ("TextPrimary", "ContentBg", 4.5), ("TextPrimary", "SurfaceContainer", 4.5),
            ("TextSecondary", "ContentBg", 4.5), ("TextMuted", "ContentBg", 4.5),
            ("AccentBlue", "SurfaceContainer", 4.5), ("AccentBlue", "SelectionBackground", 4.5),
            ("AccentBlue", "SidebarHover", 4.5), ("AccentHover", "SurfaceContainer", 4.5),
            ("ErrorRed", "SurfaceContainer", 4.5), ("ErrorRed", "DangerBorder", 4.5),
            ("ContentBorder", "SurfaceContainer", 3), ("ContentBorder", "ContentBg", 3),
            ("ContentBorder", "SidebarBg", 3), ("ContentBorder", "SidebarHover", 3),
            ("OnAccent", "AccentBlue", 4.5), ("OnAccent", "AccentHover", 4.5)
        };

        static double Luminance(string hex)
        {
            var channels = Enumerable.Range(0, 3).Select(index => Convert.ToInt32(hex.Substring(index * 2, 2), 16) / 255d)
                .Select(value => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4)).ToArray();
            return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
        }

        foreach (var (foreground, background, minimum) in pairs)
        {
            var a = Luminance(colors[foreground]);
            var b = Luminance(colors[background]);
            var ratio = (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
            Assert.True(ratio >= minimum, $"{foreground}/{background} contrast {ratio:F2}:1 is below {minimum:F1}:1.");
        }
    }

    [Fact]
    public void VirtualizedRowsExposeLocalizedContentAsAutomationNames()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "TeleSelfCloud.slnx")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate TeleSelfCloud.slnx.");
        var xaml = XDocument.Load(Path.Combine(root, "src", "TeleSelfCloud.Desktop", "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (var (name, binding) in new[]
        {
            ("ManifestList", "DisplayName"),
            ("TrashManifestList", "DisplayName"),
            ("TransferList", "Title")
        })
        {
            var list = xaml.Descendants(presentation + "ListView")
                .Single(element => (string?)element.Attribute(xamlNamespace + "Name") == name);
            Assert.Contains(list.Element(presentation + "ListView.ItemContainerStyle")!
                .Descendants(presentation + "Setter"), setter =>
                    (string?)setter.Attribute("Property") == "AutomationProperties.Name" &&
                    (string?)setter.Attribute("Value") == $"{{Binding {binding}}}");
        }
    }
}
