using System.ComponentModel;
using System.Drawing.Imaging;
using ThermalDesk.Core;

namespace ThermalDesk.App;

// Offline UI regressions. Only in-memory profiles and fixture telemetry are used.
internal static class EditorRegressionChecks
{
    public static int Run(string directory)
    {
        int checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException("Editor regression: " + message); }
        var telemetry = new Telemetry(DateTimeOffset.UtcNow, 54, 43, 20, 10, 38);

        var pump = new PortSettings { Port = 1, Role = PortRole.Pump, Mode = ControlMode.Curve, MinimumDuty = 35, FixedDuty = 40 };
        using (var form = new PortEditorForm(pump, () => telemetry))
        {
            var limits = form.ReadLimitsForTest();
            Check(limits.Minimum == 60 && limits.FixedMinimum == 60, "opening an already-configured pump establishes both numeric floors");
            Check(limits.Settings.MinimumDuty == 60 && limits.Settings.FixedDuty == 60, "displayed pump values agree with the enforced floor");
            Check(pump.MinimumDuty == 35 && pump.FixedDuty == 40, "opening an editor does not mutate its input profile");
        }

        var fan = new PortSettings { Port = 1, Role = PortRole.Fan, Mode = ControlMode.Curve, MinimumDuty = 60, Curve = [new(20, 30), new(80, 90), new(100, 30)] };
        var effective = CurveCanvas.EffectiveCurve(fan);
        Check(effective.Contains(new CurvePoint(50, 60)), "rising curve retains the exact crossing of the minimum");
        Check(effective.Contains(new CurvePoint(90, 60)), "falling curve retains the exact crossing of the minimum");
        Check(effective.All(p => p.Duty >= 60) && effective[0].Temperature == 0 && effective[^1].Temperature == 100, "effective curve covers the full chart and clamps low values");
        fan.Mode = ControlMode.Fixed; fan.FixedDuty = 40;
        Check(CurveCanvas.EffectiveCurve(fan).All(p => p.Duty == 60), "fixed preview includes the configured minimum just like FanSafety");
        fan.Mode = ControlMode.Curve;
        using (var host = new Form { ClientSize = new(600, 350), StartPosition = FormStartPosition.Manual, Location = new(-20000, -20000), ShowInTaskbar = false })
        {
            int edits = 0;
            var chart = new CurveCanvas(() => fan, (index, point) => { fan.Curve[index] = point; edits++; }, () => telemetry);
            host.Controls.Add(chart); host.Show(); host.PerformLayout(); Application.DoEvents();
            var point = chart.PointForTest(1);
            chart.MouseDownForTest(point); chart.MouseMoveForTest(new(point.X + 8, point.Y - 8), MouseButtons.Left);
            Check(edits == 1, "normal curve dragging changes one point");
            chart.Capture = false;
            chart.MouseMoveForTest(new(point.X + 20, point.Y - 20), MouseButtons.Left);
            Check(edits == 1, "lost mouse capture ends curve dragging even if a stale button state remains");
            point = chart.PointForTest(1); chart.MouseDownForTest(point);
            chart.MouseMoveForTest(new(point.X + 10, point.Y + 10), MouseButtons.None);
            chart.MouseMoveForTest(new(point.X + 20, point.Y + 20), MouseButtons.Left);
            Check(edits == 1, "a released button cannot leave the curve attached to the pointer");
            fan.Mode = ControlMode.Fixed; chart.MouseDownForTest(point); chart.MouseMoveForTest(new(point.X + 5, point.Y + 5), MouseButtons.Left);
            Check(edits == 1, "fixed mode does not allow editing inactive curve points by dragging");
            fan.Mode = ControlMode.Curve;
            using var bitmap = new Bitmap(host.Width, host.Height); host.DrawToBitmap(bitmap, new Rectangle(Point.Empty, host.Size));
            bitmap.Save(Path.Combine(directory, "editor-effective-curve.png"), ImageFormat.Png);
        }

        var page1 = new LcdWidget { Name = "Visible CPU", Group = 1, X = 30, Y = 30, Width = 150, Height = 50 };
        var page2 = new LcdWidget { Name = "Hidden GPU", Group = 2, X = 30, Y = 30, Width = 150, Height = 50, Source = WidgetSource.GpuTemperature };
        var always = new LcdWidget { Name = "Clock", Group = 0, X = 240, Y = 30, Width = 150, Height = 50, Source = WidgetSource.Clock, ValueFormat = "HH:mm", TextTemplate = "{value}" };
        var layout = new DisplaySettings { UseCustomLayout = true, RotateWidgetGroups = true, Widgets = [page1, page2, always] };
        using (var editor = new WidgetDesignerForm("Page regression", layout, 480, 128, () => telemetry))
        {
            editor.RenderForTest(Path.Combine(directory, "editor-pages.png"));
            editor.SelectPageForTest(1); editor.SelectWidgetForTest(page1.Id); editor.ClickSelectedForTest();
            Check(editor.SelectedIdForTest == page1.Id && editor.ActivePageForTest == 1, "hit testing cannot select an overlapping widget on an invisible page");
            editor.SelectWidgetForTest(always.Id); editor.ClickSelectedForTest();
            Check(editor.SelectedIdForTest == always.Id && editor.ActivePageForTest == 1, "selecting an always-visible widget preserves the explicit preview page");
            editor.SelectPageForTest(2); editor.SelectWidgetForTest(page2.Id); editor.ClickSelectedForTest();
            Check(editor.SelectedIdForTest == page2.Id && editor.ActivePageForTest == 2, "the same geometry selects the correct widget on page two");
            editor.SelectPageForTest(-1);
            Check(editor.ActivePageForTest == -1, "all-pages preview is an explicit selectable state");
            Check(editor.Result.RotateWidgetGroups, "preview-page selection does not change physical page rotation");
        }

        var widget = new LcdWidget(); var inspector = new WidgetProperties(widget);
        bool Has(string name) => TypeDescriptor.GetProperties(inspector).Find(name, false) is not null;
        Check(Has(nameof(WidgetProperties.Source)) && !Has(nameof(WidgetProperties.OnlineUrl)) && !Has(nameof(WidgetProperties.Minimum)) && !Has(nameof(WidgetProperties.HistoryMinutes)), "text inspector omits unrelated image, gauge and graph properties");
        inspector.Kind = WidgetKind.RadialSegments;
        Check(Has(nameof(WidgetProperties.Segments)) && Has(nameof(WidgetProperties.Minimum)) && !Has(nameof(WidgetProperties.LogFile)), "segmented gauge exposes its own controls");
        inspector.Kind = WidgetKind.LineGraph;
        Check(Has(nameof(WidgetProperties.HistoryMinutes)) && !Has(nameof(WidgetProperties.Segments)) && !Has(nameof(WidgetProperties.Minimum)), "auto-scaled graph hides gauge controls and fixed bounds");
        inspector.AutoScale = false;
        Check(Has(nameof(WidgetProperties.Minimum)) && Has(nameof(WidgetProperties.Maximum)), "manual graph scale exposes numeric bounds");
        inspector.Source = WidgetSource.TextFileLog;
        Check(widget.Kind == WidgetKind.Text && Has(nameof(WidgetProperties.LogFile)) && Has(nameof(WidgetProperties.LastLines)), "local log source switches to text and exposes log controls");
        inspector.Source = WidgetSource.TopGpuProcessLog;
        Check(!Has(nameof(WidgetProperties.LogFile)) && Has(nameof(WidgetProperties.LastLines)), "automatic log hides the ignored manual file path");
        inspector.Kind = WidgetKind.OnlineImage;
        Check(Has(nameof(WidgetProperties.OnlineUrl)) && Has(nameof(WidgetProperties.OnlineRefreshSeconds)) && !Has(nameof(WidgetProperties.Source)) && !Has(nameof(WidgetProperties.LastLines)), "online image inspector omits ignored sensor and log settings");
        inspector.OnlineMode = OnlineMediaMode.Mjpeg;
        Check(Has(nameof(WidgetProperties.OnlineMaxFps)) && !Has(nameof(WidgetProperties.OnlineRefreshSeconds)), "MJPEG exposes FPS rather than snapshot polling");
        var nameProperty = TypeDescriptor.GetProperties(inspector)[nameof(WidgetProperties.Name)]!;
        nameProperty.SetValue(inspector, "Edited through inspector");
        Check(widget.Name == "Edited through inspector", "filtered property descriptors still edit the selected widget");
        return checks;
    }
}
