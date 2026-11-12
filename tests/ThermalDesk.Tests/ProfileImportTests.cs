using System.Text.Json;
using ThermalDesk.Core;

static class ProfileImportTests
{
    internal static void Run(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "ThermalDesk-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var original = new Settings(); original.Panel.Enabled = true;
            original.Panel.Rotation = 90; original.Panel.UseCustomLayout = true;
            original.Panel.Widgets = WidgetLayout.Defaults(128, 480);
            original.StartLiveAtSignIn = true;
            string source = Path.Combine(folder, "v13.json"), destination = Path.Combine(folder, "settings.json");
            SettingsStore.Save(source, original); string sourceBytes = File.ReadAllText(source);
            var candidate = SettingsStore.LoadForImport(source, false);
            check(!candidate.StartLiveAtSignIn && candidate.Panel.Enabled && candidate.Panel.Rotation == 90, "import preserves LCD configuration without inheriting startup permission");
            check(SettingsStore.LoadForImport(source, true).StartLiveAtSignIn, "import preserves this PC's existing startup preference");
            check(File.ReadAllText(source) == sourceBytes, "import source remains unchanged");
            SettingsStore.Save(destination, new Settings { PollSeconds = 7 });
            string before = File.ReadAllText(destination);
            string backup = SettingsStore.BackupBeforeImport(destination, new Settings());
            SettingsStore.Save(destination, candidate); SettingsStore.Save(destination, candidate);
            check(File.ReadAllText(backup) == before && SettingsStore.Load(backup).PollSeconds == 7, "dated import backup survives later automatic saves");
            string backup2 = SettingsStore.BackupBeforeImport(destination, candidate);
            check(backup != backup2 && File.Exists(backup) && File.Exists(backup2), "every import keeps an independent backup");
            foreach (string invalid in new[] { "{}", "null", "[]", "{\"profile\":{}}", "{\"SchemaVersion\":1,\"Ports\":[]}", sourceBytes.Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 2") })
            {
                string bad = Path.Combine(folder, "invalid.json"); File.WriteAllText(bad, invalid);
                bool rejected = false;
                try { SettingsStore.LoadForImport(bad, false); } catch (Exception e) when (e is ArgumentException or InvalidDataException or JsonException) { rejected = true; }
                check(rejected, "unrelated, incomplete or invalid JSON never imports defaults");
            }
            bool missing = false;
            try { SettingsStore.LoadForImport(Path.Combine(folder, "missing.json"), false); } catch (FileNotFoundException) { missing = true; }
            check(missing, "missing import file is an error, not defaults");
            string large = Path.Combine(folder, "large.json"); File.WriteAllText(large, new string(' ', SettingsStore.MaximumProfileBytes + 1));
            bool oversized = false;
            try { SettingsStore.LoadForImport(large, false); } catch (InvalidDataException) { oversized = true; }
            check(oversized, "oversized import is rejected before parsing");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
