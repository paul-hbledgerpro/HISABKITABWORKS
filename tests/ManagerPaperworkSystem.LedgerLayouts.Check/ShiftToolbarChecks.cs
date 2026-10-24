using System.Reflection;
using ManagerPaperworkSystem.WinForms;

internal static class ShiftToolbarChecks
{
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        foreach (var theme in ThemePreferences.Choices)
        {
            typeof(ThemePreferences).GetProperty(nameof(ThemePreferences.Choice))!.SetValue(null, theme);
            using var window = new Form { ClientSize = new Size(1200, 740), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Opacity = 0 };
            var grid = WinTheme.Grid();
            grid.DataSource = new[] { new { Date = new DateTime(2026, 10, 23), Batch = "3004", Employee = "Manager", Cash = 485.50m, Card = 1250m, Drop = 480m, Payout = 5.50m, Variance = 0m }, new { Date = new DateTime(2026, 10, 22), Batch = "3003", Employee = "Manager", Cash = 375m, Card = 950m, Drop = 370m, Payout = 0m, Variance = -5m } };
            var entry = new LedgerEntryPanel(true, _ => Task.CompletedTask, () => Task.FromResult((Array.Empty<string>(), Array.Empty<string>())));
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
            foreach (var label in new[] { "Dashboard", "Record Shift Drop", "Pending Shift Drops", "Save Selected Drop", "Import Z Report" }) actions.Controls.Add(WinTheme.Button(label, label == "Record Shift Drop"));
            var toolbar = new ShiftGridToolbar();
            var developer = WinTheme.Button("Setup"); developer.Visible = false; developer.Enabled = false; toolbar.SetDeveloperSetup(developer);
            using var host = new LedgerWorkspaceHost("Shift Cash Drop", actions, grid, new Label { Text = "Shift cash drop records for selected store", Dock = DockStyle.Fill }, entry, gridToolbar: toolbar);
            window.Controls.Add(host); host.ApplyLayout(LedgerLayout.SideEntryGrid); window.Show(); Application.DoEvents();
            var search = toolbar.Controls.Find("ShiftBatchSearch", true).OfType<TextBox>().Single();
            var filter = toolbar.Controls.Find("ShiftViewFilter", true).OfType<Button>().Single();
            var health = toolbar.Controls.Find("ShiftSyncHealth", true).OfType<Button>().Single();
            var changes = 0; toolbar.ViewChanged += (_, _) => changes++;
            search.Text = "3004"; Check(toolbar.SearchText == "3004" && changes == 1, "Search updates the existing view");
            toolbar.SetSort(true, true); Check(toolbar.OldestFirst && toolbar.ByBatch && changes == 2, "Numeric sort is selected once");
            toolbar.Controls.Find("ClearBatchSearch", true).OfType<Button>().Single().PerformClick(); Check(toolbar.SearchText == "", "Clear search works");
            toolbar.SetSort(false, false);
            foreach (var layout in Enum.GetValues<LedgerLayout>())
            foreach (var width in new[] { 1200, 850 })
            {
                host.ApplyLayout(layout); window.ClientSize = new Size(width, 740); window.PerformLayout(); Application.DoEvents();
                Check(toolbar.Parent == grid.Parent && toolbar.Bottom <= grid.Top, "Toolbar stays immediately above the records in every layout");
                Check(search.Width >= 100 && filter.Height >= 30 && health.Height == filter.Height, "Search and buttons fit the narrow grid");
                var positions = new[] { search, (Control)filter, health }.Select(c => c.PointToScreen(Point.Empty)).ToArray();
                developer.Visible = true; developer.Enabled = true; Application.DoEvents();
                Check(positions.SequenceEqual(new[] { search, (Control)filter, health }.Select(c => c.PointToScreen(Point.Empty))), "Developer unlock does not move regular controls");
                developer.Visible = false; developer.Enabled = false;
                Check(toolbar.Bottom < grid.Bottom && grid.Height > 150, "Grid remains visible");
                using var picture = new Bitmap(host.Width, host.Height); host.DrawToBitmap(picture, host.ClientRectangle);
                picture.Save(Path.Combine(output, $"shift-toolbar-{layout}-{width}-{theme.Replace(" & ", "-").Replace(' ', '-')}.png"));
            }
            window.Hide();
        }
        Console.WriteLine("PASS: search/clear, numeric/date sort, aligned toolbar at 850 and 1200 pixels, all three layouts, all nine themes, developer unlock retains positions. Synthetic forms only.");
    }
    private static void Check(bool success, string reason) { if (!success) throw new Exception(reason); }
}
