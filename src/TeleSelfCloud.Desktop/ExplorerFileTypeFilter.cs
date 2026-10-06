using System.IO;

namespace TeleSelfCloud.Desktop;

/// <summary>Stable extension groups used by the Explorer's file-type filter.</summary>
public static class ExplorerFileTypeFilter
{
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".bmp", ".gif", ".heic", ".jpeg", ".jpg", ".png", ".svg", ".tif", ".tiff", ".webp" };
    private static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
        { ".3gp", ".avi", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg", ".mpg", ".webm" };
    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
        { ".aac", ".flac", ".m4a", ".mp3", ".ogg", ".opus", ".wav", ".wma" };
    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
        { ".csv", ".doc", ".docx", ".md", ".odt", ".pdf", ".ppt", ".pptx", ".rtf", ".txt", ".xls", ".xlsx" };
    private static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase)
        { ".7z", ".bz2", ".gz", ".rar", ".tar", ".zip" };
    private static readonly HashSet<string> Code = new(StringComparer.OrdinalIgnoreCase)
        { ".c", ".cc", ".cpp", ".cs", ".css", ".go", ".h", ".hpp", ".html", ".java", ".js", ".json", ".kt", ".php", ".py", ".rs", ".sh", ".sql", ".ts", ".tsx", ".xml", ".yaml", ".yml" };

    public static bool Matches(string fileName, int selectedIndex)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (selectedIndex == 0) return true;
        var extension = Path.GetExtension(fileName);
        return selectedIndex switch
        {
            1 => Images.Contains(extension),
            2 => Video.Contains(extension),
            3 => Audio.Contains(extension),
            4 => Documents.Contains(extension),
            5 => Archives.Contains(extension),
            6 => Code.Contains(extension),
            7 => !IsKnown(extension),
            _ => false
        };
    }

    private static bool IsKnown(string extension) => Images.Contains(extension) || Video.Contains(extension) ||
        Audio.Contains(extension) || Documents.Contains(extension) || Archives.Contains(extension) || Code.Contains(extension);
}
