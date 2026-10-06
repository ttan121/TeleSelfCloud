using System.Xml.Linq;

namespace TeleSelfCloud.Tests;

public sealed class Ui7SourceTests
{
    [Fact]
    public void DesktopManifestDeclaresPerMonitorV2DpiAwareness()
    {
        var root = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "src", "TeleSelfCloud.Desktop", "TeleSelfCloud.Desktop.csproj"));
        var manifestPath = Path.Combine(root, "src", "TeleSelfCloud.Desktop", "app.manifest");
        var document = XDocument.Load(manifestPath);

        Assert.Contains("<ApplicationManifest>app.manifest</ApplicationManifest>", project, StringComparison.Ordinal);
        Assert.Equal("PerMonitorV2", document.Descendants(XName.Get("dpiAwareness", "http://schemas.microsoft.com/SMI/2016/WindowsSettings")).Single().Value);
        Assert.Equal("true/pm", document.Descendants(XName.Get("dpiAware", "http://schemas.microsoft.com/SMI/2005/WindowsSettings")).Single().Value);
    }

    [Fact]
    public void MainWindowKeepsResizableMinimumAndRecyclingVirtualization()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "TeleSelfCloud.Desktop", "MainWindow.xaml"));

        Assert.Contains("MinHeight=\"650\" MinWidth=\"760\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ResizeMode=\"NoResize\"", xaml, StringComparison.Ordinal);
        Assert.True(CountOccurrences(xaml, "VirtualizingPanel.VirtualizationMode=\"Recycling\"") >= 3);
        Assert.Contains("VirtualizingStackPanel.IsVirtualizing=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(xaml, "<ListView.ItemContainerStyle>"));
        Assert.True(CountOccurrences(xaml, "<Setter Property=\"HorizontalContentAlignment\" Value=\"Stretch\"/>") >= 3);
    }

    private static int CountOccurrences(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TeleSelfCloud.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate TeleSelfCloud.slnx.");
    }
}
