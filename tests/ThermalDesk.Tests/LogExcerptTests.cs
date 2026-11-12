using System.Text;
using System.Text.Json;
using ThermalDesk.Core;

internal static class LogExcerptTests
{
    internal static void Run(Action<bool, string> check)
    {
        string root = Directory.CreateTempSubdirectory("thermaldesk-log-tests-").FullName;
        try
        {
            string path = Path.Combine(root, "example.log");
            File.WriteAllText(path, string.Join('\n', Enumerable.Range(1, 30).Select(i => "Line " + i)) + "\n");
            var tail = BoundedLogTail.Read(path);
            check(tail.Text.Split('\n').Length == 10 && tail.Text.StartsWith("Line 21\n") && tail.Text.EndsWith("Line 30"), "tail selects ten newest lines, not trailing empty line");
            File.WriteAllText(path, "old\nnew\n", Encoding.Unicode);
            check(BoundedLogTail.Read(path).Text == "old\nnew", "UTF-16 LE BOM and line endings decoded");
            File.WriteAllText(path, "old\r\nżółć\r\n", Encoding.BigEndianUnicode);
            check(BoundedLogTail.Read(path).Text == "old\nżółć", "UTF-16 BE and Unicode text decoded");
            using (var huge = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                huge.SetLength(8 * 1024 * 1024); huge.Position = huge.Length - 12; huge.Write(Encoding.UTF8.GetBytes("\nLAST ENTRY\n"));
            }
            check(BoundedLogTail.Read(path).Text.EndsWith("LAST ENTRY"), "large log reads bounded suffix, not full file");
            File.WriteAllText(path, new string('x', 100000));
            check(BoundedLogTail.Read(path).Text.Length <= 400, "huge single line cannot create an oversized excerpt");
            string sensitive = "\u001b[31mERROR\u001b[0m --api-key abcdef password=verysecret Authorization: Bearer abc.xyz\n\"access_token\":\"tokenvalue\"";
            string clean = BoundedLogTail.Clean(sensitive);
            check(!clean.Contains("abcdef") && !clean.Contains("verysecret") && !clean.Contains("abc.xyz") && !clean.Contains("tokenvalue") && !clean.Contains('\u001b'), "common credentials and ANSI controls redacted");
            check(BoundedLogTail.Read(Path.Combine(root, "missing.log")).Text == "", "missing log is unavailable, not a stale sample");
            check(!BoundedLogTail.IsLocalPath(@"\\server\share\file.log") && !BoundedLogTail.IsLocalPath("relative.log") && !BoundedLogTail.IsLocalPath(@"\\.\PhysicalDrive0"), "network, relative and device paths rejected");
            if (OperatingSystem.IsWindows()) check(!BoundedLogTail.IsLocalPath(@"C:\test.txt:stream") && !BoundedLogTail.IsLocalPath(@"C:\NUL.txt"), "alternate data stream and device alias rejected");
            File.WriteAllText(path, "one\ntwo\nthree");
            var widget = new LcdWidget { Source = WidgetSource.TextFileLog, TextTemplate = "{value}", LogFilePath = path, LogLines = 2 };
            var profile = new Settings(); profile.Aio.Enabled = profile.Aio.UseCustomLayout = true; profile.Aio.Widgets = [widget];
            profile.Validate();
            var telemetry = new Telemetry(DateTimeOffset.UtcNow, null, null, null, null, null)
            { LogExcerpts = new Dictionary<string, LogExcerpt> { [LogWidget.Key(widget)] = BoundedLogTail.Read(path) } };
            check(WidgetLayout.Format(widget, telemetry) == "two\nthree", "widget uses requested last-line count");
            check(!JsonSerializer.Serialize(telemetry, Settings.JsonOptions).Contains("three"), "diagnostic telemetry does not export log content");
            var copy = profile.Copy();
            check(copy.Aio.Widgets[0].LogLines == 2 && copy.Aio.Widgets[0].LogFilePath == path && copy.LogSampleSeconds == 5, "log options round-trip in profiles");
            check(!LogWidget.NeedsProcesses(profile), "manual file tail does not require process enumeration");
            widget.Source = WidgetSource.TopGpuProcessLog;
            check(LogWidget.NeedsProcesses(profile), "automatic process log requests sampling");
            profile.Aio.Enabled = false;
            check(!LogWidget.Selected(profile).Any() && !LogWidget.NeedsProcesses(profile), "disabled display makes no file/process requests");
            foreach (var source in new[] { WidgetSource.TextFileLog, WidgetSource.TopCpuProcessLog, WidgetSource.TopGpuProcessLog })
            {
                widget.Source = source; widget.Kind = WidgetKind.HorizontalGauge;
                bool rejected = false; try { profile.Validate(); } catch (ArgumentException) { rejected = true; }
                check(rejected, "log source cannot be a gauge: " + source);
            }
            widget.Kind = WidgetKind.Text; widget.Source = WidgetSource.TextFileLog; widget.LogLines = 11;
            bool invalid = false; try { profile.Validate(); } catch (ArgumentException) { invalid = true; }
            check(invalid, "unbounded line count rejected");
            check(ReferenceEquals(Settings.JsonOptions, Settings.JsonOptions), "serializer options/metadata cache is shared");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
