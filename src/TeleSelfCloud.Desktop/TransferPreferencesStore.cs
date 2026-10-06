using System.IO;
using System.Text.Json;

namespace TeleSelfCloud.Desktop;

internal sealed class TransferPreferencesStore(string path)
{
    private sealed record Preferences(int MaxConcurrentTransfers);
    private readonly string _path = Path.GetFullPath(path);

    public int LoadMaxConcurrentTransfers()
    {
        try
        {
            if (!File.Exists(_path)) return 1;
            var preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(_path));
            return preferences?.MaxConcurrentTransfers is >= 1 and <= 3 ? preferences.MaxConcurrentTransfers : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return 1;
        }
    }

    public void SaveMaxConcurrentTransfers(int value)
    {
        if (value is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(value));
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Preferences(value)));
        File.Move(temp, _path, true);
    }
}
