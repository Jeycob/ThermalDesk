namespace ThermalDesk.App;

internal static class AppIcon
{
    public static Icon Create()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("ThermalDesk.AppIcon.ico")
            ?? throw new InvalidOperationException("The application icon is missing.");
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }
}
