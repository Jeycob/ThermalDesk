using System.Text.Json;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal static class DailyEnergyStore
{
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThermalDesk", "energy-today.json");
    internal static DailyEnergyState? Load(string? filePath = null)
    {
        try
        {
            var path = filePath ?? FilePath;
            var info = new FileInfo(path); if (!info.Exists || info.Length > 16384) return null;
            var state = JsonSerializer.Deserialize<DailyEnergyState>(File.ReadAllText(path), Settings.JsonOptions);
            return state is { Valid: true } ? state : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    internal static void Save(DailyEnergyState state, string? filePath = null)
    {
        if (!state.Valid) return;
        string path = filePath ?? FilePath, temp = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Settings.JsonOptions)); File.Move(temp, path, true);
    }
}
