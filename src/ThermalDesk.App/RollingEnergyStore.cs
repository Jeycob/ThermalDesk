using System.Text.Json;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal static class RollingEnergyStore
{
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThermalDesk", "energy-last24h.json");
    internal static RollingEnergyState? Load(string? filePath = null)
    {
        try
        {
            using var file = new FileStream(filePath ?? FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 1024 * 1024) return null;
            var state = JsonSerializer.Deserialize<RollingEnergyState>(file, Settings.JsonOptions);
            return state is { Valid: true } ? state : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    internal static void Save(RollingEnergyState state, string? filePath = null)
    {
        if (!state.Valid) return;
        string path = filePath ?? FilePath, temporary = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) JsonSerializer.Serialize(file, state, Settings.JsonOptions);
        File.Move(temporary, path, true);
    }
}
