using TeleSelfCloud.Desktop;

namespace TeleSelfCloud.Tests;

public sealed class UploadSourceSelectorTests
{
    [Fact]
    public void MultipleFilesTargetCurrentFolderAndDuplicatePathsAreIgnored()
    {
        var root = NewDirectory();
        try
        {
            var first = Path.Combine(root, "one.txt");
            var second = Path.Combine(root, "two.txt");
            File.WriteAllText(first, "one"); File.WriteAllText(second, "two");

            var selection = UploadSourceSelector.Expand([first, second, first], "Work/Notes");

            Assert.Equal(2, selection.Files.Count);
            Assert.All(selection.Files, file => Assert.Equal("Work/Notes", file.DestinationFolder));
            Assert.Empty(selection.Folders);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FolderExpansionPreservesNestedAndEmptyDirectories()
    {
        var root = NewDirectory();
        try
        {
            var source = Path.Combine(root, "Project");
            Directory.CreateDirectory(Path.Combine(source, "src", "nested"));
            Directory.CreateDirectory(Path.Combine(source, "empty"));
            var file = Path.Combine(source, "src", "nested", "main.cs");
            File.WriteAllText(file, "class Main {}");

            var selection = UploadSourceSelector.Expand([source, file], "Incoming");

            var uploaded = Assert.Single(selection.Files);
            Assert.Equal(file, uploaded.FullPath);
            Assert.Equal("Incoming/Project/src/nested", uploaded.DestinationFolder);
            Assert.Equal(new[] { "Incoming/Project", "Incoming/Project/empty", "Incoming/Project/src", "Incoming/Project/src/nested" }, selection.Folders);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MissingSelectionFailsBeforeProducingAnUploadPlan()
    {
        var root = NewDirectory();
        try
        {
            var missing = Path.Combine(root, "missing.bin");
            Assert.Throws<FileNotFoundException>(() => UploadSourceSelector.Expand([missing], ""));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "TeleSelfCloud.UploadSelection", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
