using TeleSelfCloud.Desktop;

namespace TeleSelfCloud.Tests;

public sealed class ExplorerFileTypeFilterTests
{
    [Theory]
    [InlineData("photo.JPEG", 1, true)]
    [InlineData("clip.MP4", 2, true)]
    [InlineData("voice.opus", 3, true)]
    [InlineData("report.PDF", 4, true)]
    [InlineData("backup.7z", 5, true)]
    [InlineData("source.CS", 6, true)]
    [InlineData("opaque.custom", 7, true)]
    [InlineData("README", 7, true)]
    [InlineData("photo.png", 2, false)]
    [InlineData("clip.mp4", 7, false)]
    [InlineData("photo.png", 0, true)]
    [InlineData("photo.png", 8, false)]
    public void MatchesStableLocalizedCategoryIndices(string fileName, int category, bool expected) =>
        Assert.Equal(expected, ExplorerFileTypeFilter.Matches(fileName, category));
}
