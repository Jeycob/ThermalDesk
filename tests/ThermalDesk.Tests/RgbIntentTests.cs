using ThermalDesk.Core;

internal static class RgbIntentTests
{
    public static void Run(Action<bool, string> check)
    {
        void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); } catch (ArgumentException) { rejected = true; }
            check(rejected, name);
        }

        var settings = new Settings { RgbEnabled = true };
        Reject(settings.Validate, "RGB master without ON/OFF intent remains invalid");
        settings.Ports[0].RgbOffRequested = true;
        settings.Validate(); settings.ValidateLiveSelection();
        check(true, "An explicit-OFF-only profile is a valid lighting-only live session");
        check(!settings.CoolingEnabled && settings.Ports.All(p => p.Mode == ControlMode.Preserve),
            "Explicit OFF does not enable cooling or change fan ownership");
        settings.StartLiveAtSignIn = true; settings.Validate();
        var restored = settings.Copy(); restored.ValidateLiveSelection();
        check(restored.StartLiveAtSignIn && restored.Ports[0].RgbOffRequested && !restored.Ports[0].Rgb,
            "An OFF-only sign-in profile survives serialization and validation");

        var sample = new Telemetry(DateTimeOffset.UtcNow, 40, 40, 10, 10, 25);
        check(Lighting.OutputFrame(restored.Ports[0], "FFFFFF", 0, sample).All(c => c == new LedColor()),
            "A restored OFF-only profile emits black, not the fallback color");
        restored.Ports[0].Rgb = true; restored.Ports[0].RgbColor = "123456";
        restored.Ports[0].RgbBrightness = 100; restored.ValidateLiveSelection();
        check(Lighting.OutputFrame(restored.Ports[0], "FFFFFF", 0, sample).All(c => c == new LedColor(0x12, 0x34, 0x56)),
            "ON takes precedence when both persisted intent flags are present");

        settings.RgbEnabled = false;
        settings.Validate();
        Reject(settings.ValidateLiveSelection, "A disabled master cannot start hardware solely from saved OFF intent");
        check(settings.Ports[0].RgbOffRequested, "Global OFF preserves saved per-port intent for the next enable");
        check(settings.StartLiveAtSignIn && !settings.HasLiveSelection,
            "An idle profile preserves sign-in preference without claiming a live device");
        var profilePath = Path.Combine(Path.GetTempPath(), "thermaldesk-rgb-idle-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            SettingsStore.Save(profilePath, settings);
            var idle = SettingsStore.LoadForStartup(profilePath);
            check(!idle.Recovered && idle.Settings.StartLiveAtSignIn && !idle.Settings.HasLiveSelection && idle.Settings.Ports[0].RgbOffRequested,
                "Saved idle sign-in profile reloads normally, not through damaged-profile recovery");
        }
        finally { if (File.Exists(profilePath)) File.Delete(profilePath); }
        settings.RgbEnabled = true; settings.ValidateLiveSelection();
        check(true, "Re-enabling the RGB master restores valid explicit-OFF selection");
        settings.Ports[0].RgbOffRequested = false;
        Reject(settings.ValidateLiveSelection, "Legacy false/Unchanged ports do not silently acquire hardware ownership");
        settings.RgbEnabled = false; settings.Validate();
        check(settings.Ports.All(p => !Lighting.IsManaged(p)), "A default unchanged profile remains unowned");

        settings.Ports[0].RgbOffRequested = true; settings.Ports[0].LedCount = 63;
        Reject(settings.Validate, "Explicit OFF does not bypass LED count bounds");
        settings.Ports[0].LedCount = 12; settings.Ports[0].Mode = ControlMode.Fixed;
        Reject(settings.Validate, "Explicit OFF does not bypass fan wiring checks");
    }
}
