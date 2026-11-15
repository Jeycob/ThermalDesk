namespace ThermalDesk.App;

internal sealed partial class MainForm
{
    internal int ExerciseAuditPresentationForTest(string directory)
    {
        int checks = 0;
        void Check(bool condition, string reason) { checks++; if (!condition) throw new InvalidOperationException("Main presentation: " + reason); }
        ShowOffscreenForTest();
        var tabs = Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<TabControl>().Single();
        tabs.SelectedIndex = 1;
        foreach (var size in new[] { new Size(1180, 820), new Size(984, 681) })
        {
            ClientSize = size; PerformLayout(); Application.DoEvents(); RefreshData();
            var actions = tabs.TabPages[1].Controls.OfType<TableLayoutPanel>().Single().Controls.Find("CoolingActions", true).Single();
            Check(actions.Controls.Cast<Control>().Where(c => c.Visible).All(c => c.Bottom <= actions.ClientSize.Height), "wrapped cooling actions fit their autosized row");
            Check(actions.Bottom <= actions.Parent!.ClientSize.Height, "cooling actions remain inside the page");
            Check(ports.Height >= 150, "summary retains usable port rows at minimum size");
            using var image = new Bitmap(Width, Height); DrawToBitmap(image, new Rectangle(Point.Empty, Size));
            image.Save(Path.Combine(directory, $"cooling-summary-{size.Width}.png"));
        }
        Check(ports.Columns.Cast<DataGridViewColumn>().Count(c => c.Visible) == 7, "summary shows seven readable columns");
        advancedPorts.Checked = true;
        Check(ports.Columns.Cast<DataGridViewColumn>().All(c => c.Visible), "advanced columns remain available");
        advancedPorts.Checked = false;
        Check(ports.Columns[2].Visible && ports.Columns[10].Visible && ports.Columns[11].Visible && ports.Columns[12].Visible, "mode and commanded/confirmed feedback remain visible");
        var sessionBefore = session.Text;
        applyHint = "Pending · Port 2: invalid curve"; applyFailed = true; RefreshData();
        Check(session.Text == sessionBefore && profileStatus.Text.Contains("Port 2"), "validation does not replace run status");
        Check(attentionOpensCooling && attention.Visible, "pending field has a visible corrective action");
        startupWarning = "Recovered profile"; RefreshData();
        Check(!attentionOpensCooling && attention.Text.Contains("diagnostics"), "recovery action matches its diagnostics destination");
        startupWarning = "";
        Directory.CreateDirectory(configPath); // An occupied destination must fail without losing the accepted in-memory profile.
        try { poll.Value = 4; ApplyPendingSettings(); }
        finally { Directory.Delete(configPath); }
        Check(!attentionOpensCooling && profileStatus.Text.Contains("NOT SAVED"), "persistence failure is distinct from an unapplied setting");
        Check(settings.PollSeconds == 4 && persistenceFailed, "failed persistence preserves the accepted in-memory configuration");
        ApplyPendingSettings();
        Check(!persistenceFailed && !applyFailed && File.Exists(configPath), "correcting the destination clears the persistence failure");
        return checks;
    }
}
