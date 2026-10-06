using TeleSelfCloud.Desktop;

namespace TeleSelfCloud.Tests;

public sealed class FilesEmptyStateTests
{
    [Fact]
    public void EmptyFilesViewUsesLocalizedCopyForFolderAndSearchStates()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "TeleSelfCloud.Desktop", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "src", "TeleSelfCloud.Desktop", "MainWindow.xaml.cs"));

        Assert.Contains("x:Name=\"FilesEmptyText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("emptyTextKey = (_manifestItems.Any((ManifestItem item) => !item.Manifest.IsInTrash) ? \"files.folderEmpty\" : \"files.empty\")", code, StringComparison.Ordinal);
        Assert.Contains("emptyTextKey = \"files.searchEmpty\"", code, StringComparison.Ordinal);
        Assert.Contains("FilesEmptyText.Text = UiText.Instance.Get(emptyTextKey)", code, StringComparison.Ordinal);

        var text = new UiText();
        var preferencePath = Path.Combine(Path.GetTempPath(), $"TeleSelfCloud-empty-state-{Guid.NewGuid():N}", "language.json");
        try
        {
            text.SetLanguage("en", preferencePath);
            Assert.Equal("This folder is empty. Choose another folder or upload a file here.", text.Get("files.folderEmpty"));
            Assert.Equal("No files match these filters. Clear a filter or try another name.", text.Get("files.searchEmpty"));
            text.SetLanguage("vi", preferencePath);
            Assert.Equal("Thư mục này đang trống. Hãy chọn thư mục khác hoặc tải tệp lên đây.", text.Get("files.folderEmpty"));
            Assert.Equal("Không tìm thấy tệp phù hợp. Hãy xóa bộ lọc hoặc thử tên khác.", text.Get("files.searchEmpty"));
        }
        finally
        {
            var directory = Path.GetDirectoryName(preferencePath);
            if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TeleSelfCloud.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate TeleSelfCloud.slnx.");
    }
}
