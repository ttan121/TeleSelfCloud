using System.IO;
using System.Text.Json;

namespace TeleSelfCloud.Desktop;

internal sealed class UiPreferencesStore(string path)
{
    private sealed record Preferences(bool KeepLogin, bool DarkMode, bool ExplorerIcons = false);
    private readonly string _path = Path.GetFullPath(path);

    public (bool KeepLogin, bool DarkMode, bool ExplorerIcons) Load()
    {
        try
        {
            if (!File.Exists(_path)) return (false, false, false);
            var preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(_path));
            return (preferences?.KeepLogin ?? false, preferences?.DarkMode ?? false, preferences?.ExplorerIcons ?? false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (false, false, false);
        }
    }

    public void Save(bool keepLogin, bool darkMode, bool explorerIcons)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new Preferences(keepLogin, darkMode, explorerIcons)));
        File.Move(temporaryPath, _path, true);
    }
}
