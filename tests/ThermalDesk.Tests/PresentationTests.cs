using ThermalDesk.Core;
using System.Text.Json;

internal static class PresentationTests
{
    public static void Run(Action<bool, string> check)
    {
        void Reject(Action action, string label) { try { action(); } catch (ArgumentException) { check(true, label); return; } check(false, label); }
        var zero = DateTimeOffset.UnixEpoch;
        var d = new DisplaySettings { RotateWidgetGroups = true, WidgetGroupSeconds = 5, Widgets =
            [new() { Name = "Always", Group = 0 }, new() { Name = "First", Group = 1 }, new() { Name = "Last", Group = 7 }] };
        check(WidgetRotation.ActiveGroup(d, zero) == 1, "Rotation starts with the first existing page");
        check(WidgetRotation.ActiveGroup(d, zero.AddSeconds(4.999)) == 1 && WidgetRotation.ActiveGroup(d, zero.AddSeconds(5)) == 7, "Rotation obeys the exact interval");
        check(WidgetRotation.ActiveGroup(d, zero.AddSeconds(10)) == 1, "Rotation wraps sparse page numbers");
        check(WidgetRotation.Visible(d, zero.AddSeconds(5)).Select(w => w.Name).SequenceEqual(["Always", "Last"]), "Static widgets remain on every page");
        check(WidgetRotation.ActiveGroup(d, zero.AddSeconds(-1)) == 7, "Rotation handles negative clock offsets");
        d.RotateWidgetGroups = false; check(WidgetRotation.Visible(d, zero).Count() == 3, "Disabled rotation preserves legacy all-visible layouts");
        var copy = d.Copy(); check(copy.WidgetGroupSeconds == 5 && copy.Widgets[2].Group == 7, "Pages survive profile serialization");
        copy.WidgetGroupSeconds = 0; Reject(() => WidgetLayout.Validate(copy, 480, 480), "Invalid interval rejected");
        copy = d.Copy(); copy.Widgets[0].Group = 17; Reject(() => WidgetLayout.Validate(copy, 480, 480), "Page limit enforced");
        var s = new Settings { RgbScheduleEnabled = true, RgbScheduleEntries =
            [new() { Time = "08:00", BrightnessPercent = 100 }, new() { Time = "22:00", BrightnessPercent = 0 }] };
        s.Validate();
        check(RgbSchedule.At(s, new(2026, 11, 13, 23, 0, 0)).BrightnessPercent == 0, "Night lights off");
        check(RgbSchedule.At(s, new(2026, 11, 14, 7, 59, 0)).BrightnessPercent == 0, "Night carries across midnight / restart");
        check(RgbSchedule.At(s, new(2026, 11, 14, 8, 0, 0)).BrightnessPercent == 100, "Morning exact boundary");
        s.RgbScheduleEntries[0].Days = ScheduleDays.Weekdays;
        check(RgbSchedule.At(s, new(2026, 11, 14, 9, 0, 0)).BrightnessPercent == 0, "Weekday morning does not activate on Saturday");
        s.RgbScheduleEntries.Add(new() { Days = ScheduleDays.Weekend, Time = "10:00", BrightnessPercent = 30, Color = "AA0011" });
        s.Validate(); var scheduled = RgbSchedule.At(s, new(2026, 11, 14, 10, 0, 0));
        check(scheduled.BrightnessPercent == 30 && scheduled.Color == "AA0011", "Weekend color and dimming");
        var p = new PortSettings { Port = 1, Rgb = true, RgbBrightness = 50, RgbColor = "FFFFFF" };
        var t = new Telemetry(zero, 40, 40, 5, 5, 25);
        check(Lighting.OutputFrame(p, "FFFFFF", 0, t, new(0, "", "Night")).All(c => c == new LedColor(0, 0, 0)), "Schedule explicitly sends dark frames");
        check(Lighting.OutputFrame(p, "FFFFFF", 0, t, new(50, "FF0000", "Dim")).All(c => c.Red == 64 && c.Green == 0), "Schedule scales port brightness, not fan duty");
        p.Rgb = false; p.RgbOffRequested = true;
        check(Lighting.OutputFrame(p, "FFFFFF", 0, t, scheduled).All(c => c == new LedColor(0, 0, 0)), "Port OFF takes precedence over schedule");
        s.RgbScheduleEntries.Add(new() { Time = "22:00", BrightnessPercent = 20 }); Reject(() => s.Validate(), "Duplicate schedule transitions rejected");
        s.RgbScheduleEntries.RemoveAt(s.RgbScheduleEntries.Count - 1);
        s.RgbScheduleEntries[0].Time = "99:00"; Reject(() => s.Validate(), "Bad clock input rejected");
        foreach (var url in new[] { "file:///C:/private.jpg", "ftp://camera/image.jpg", "http://user:pass@camera/image.jpg", "javascript:alert(1)", "http://camera/image#fragment" })
            Reject(() => OnlineMediaPolicy.Parse(url), "Unsafe image URL rejected: " + url.Split(':')[0]);
        check(OnlineMediaPolicy.Parse("http://192.168.0.160/snapshot.jpg").Host == "192.168.0.160", "LAN webcam supported");
        check(OnlineMediaPolicy.Parse("https://example.test/live.jpg?token=private").Scheme == "https", "Explicit HTTPS snapshot token URL supported without logging it");
        var legacy = JsonSerializer.Deserialize<Settings>("{}", Settings.JsonOptions)!;
        check(!legacy.Panel.RotateWidgetGroups && !legacy.RgbScheduleEnabled && !legacy.LastSessionWasLive, "Old profiles do not silently arm new features");
        var before = new Settings { CoolingEnabled = true, CoolingWiringConfirmed = true };
        before.Ports[0].Mode = ControlMode.Fixed; before.Ports[0].Role = PortRole.Fan;
        string fans = JsonSerializer.Serialize(before.Ports, Settings.JsonOptions);
        _ = RgbSchedule.At(before, DateTime.Now);
        check(JsonSerializer.Serialize(before.Ports, Settings.JsonOptions) == fans, "Schedule resolution never modifies fan settings");
    }
}
