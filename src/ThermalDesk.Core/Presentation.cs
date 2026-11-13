namespace ThermalDesk.Core;

public enum OnlineMediaMode { Snapshot, Mjpeg }
public enum ImageFit { Contain, Cover }
public enum ScheduleDays { EveryDay, Weekdays, Weekend }

public static class WidgetRotation
{
    public static int ActiveGroup(DisplaySettings display, DateTimeOffset time)
    {
        if (!display.RotateWidgetGroups) return 0;
        var groups = display.Widgets.Where(w => w.Group > 0).Select(w => w.Group).Distinct().Order().ToArray();
        if (groups.Length == 0) return 0;
        long slot = (long)Math.Floor(time.ToUnixTimeMilliseconds() / (display.WidgetGroupSeconds * 1000d));
        return groups[(int)((slot % groups.Length + groups.Length) % groups.Length)];
    }
    public static IEnumerable<LcdWidget> Visible(DisplaySettings display, DateTimeOffset time)
    {
        int group = ActiveGroup(display, time);
        return display.RotateWidgetGroups ? display.Widgets.Where(w => w.Group == 0 || w.Group == group) : display.Widgets;
    }
}

public sealed class RgbScheduleEntry
{
    public string Time { get; set; } = "22:00";
    public ScheduleDays Days { get; set; }
    public int BrightnessPercent { get; set; }
    // Empty preserves the port's effect. A color selects a static scheduled color.
    public string Color { get; set; } = "";
}
public sealed record ScheduledLighting(int BrightnessPercent, string Color, string Description)
{
    public static ScheduledLighting Unchanged { get; } = new(100, "", "Manual profile");
}
public static class RgbSchedule
{
    public static bool Matches(ScheduleDays days, DayOfWeek day) => days switch
    { ScheduleDays.EveryDay => true, ScheduleDays.Weekdays => day is not (DayOfWeek.Saturday or DayOfWeek.Sunday), ScheduleDays.Weekend => day is DayOfWeek.Saturday or DayOfWeek.Sunday, _ => false };
    public static bool TryTime(string? text, out TimeSpan time) => TimeSpan.TryParseExact(text, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out time);
    public static void Validate(Settings settings)
    {
        if (settings.RgbScheduleEntries is null || settings.RgbScheduleEntries.Count > 24) throw new ArgumentException("RGB schedule supports at most 24 entries.");
        var slots = new HashSet<(int, TimeSpan)>();
        foreach (var entry in settings.RgbScheduleEntries)
        {
            if (entry is null || !TryTime(entry.Time, out var time) || !Enum.IsDefined(entry.Days) || entry.BrightnessPercent is < 0 or > 100 || !WidgetLayout.IsColor(entry.Color, true))
                throw new ArgumentException("RGB schedule: use HH:mm, valid days, 0–100% brightness and an optional six-digit color.");
            for (int day = 0; day < 7; day++) if (Matches(entry.Days, (DayOfWeek)day) && !slots.Add((day, time)))
                throw new ArgumentException("RGB schedule entries must not overlap at the same day and time.");
        }
        if (settings.RgbScheduleEnabled && settings.RgbScheduleEntries.Count == 0) throw new ArgumentException("Add an RGB schedule entry or disable scheduling.");
    }
    // Evaluate local wall time so restart, midnight and missed transitions recover
    // without timers writing stale state. Fan control is deliberately unrelated.
    public static ScheduledLighting At(Settings settings, DateTime localTime)
    {
        if (!settings.RgbScheduleEnabled) return ScheduledLighting.Unchanged;
        DateTime latest = DateTime.MinValue; RgbScheduleEntry? selected = null;
        for (int offset = 0; offset <= 7; offset++)
        {
            var date = localTime.Date.AddDays(-offset);
            foreach (var entry in settings.RgbScheduleEntries)
            {
                if (!Matches(entry.Days, date.DayOfWeek) || !TryTime(entry.Time, out var time)) continue;
                var candidate = date + time;
                if (candidate <= localTime && candidate > latest) { latest = candidate; selected = entry; }
            }
        }
        return selected is null ? ScheduledLighting.Unchanged : new(selected.BrightnessPercent, selected.Color,
            $"Schedule {selected.Time} · {selected.BrightnessPercent}%" + (selected.Color.Length == 0 ? "" : " · static color"));
    }
}

public static class OnlineMediaPolicy
{
    public const int MaxSources = 4;
    public const int MaxFrameBytes = 4 * 1024 * 1024;
    public const int MaxDecodedPixels = 4 * 1024 * 1024;
    public static Uri Parse(string? text)
    {
        if (text is null || text.Length > 2048 || text.Any(char.IsControl) || !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || string.IsNullOrEmpty(uri.Host))
            throw new ArgumentException("Online image needs a direct HTTP(S) URL without embedded username/password or fragment. Local cameras are allowed.");
        return uri;
    }
    public static string Key(LcdWidget widget) => $"{widget.OnlineMode}:{Parse(widget.OnlineUrl).AbsoluteUri}";
    public static IEnumerable<LcdWidget> Sources(Settings settings) => new[] { settings.Panel, settings.Aio }
        .Where(d => d.Enabled && d.UseCustomLayout && (d.Content == DisplayContent.Dashboard || d.Overlay))
        .SelectMany(d => d.Widgets).Where(w => w.Kind == WidgetKind.OnlineImage && w.OnlineUrl.Length > 0);
}
