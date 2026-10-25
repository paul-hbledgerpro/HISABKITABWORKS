using System.Reflection;
using ManagerPaperworkSystem.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--invoices") { InvoiceImportChecks.RunAsync(Path.GetFullPath(args[1]), args.Skip(2).ToArray()).GetAwaiter().GetResult(); return; }
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length == 2 && args[0] == "--cost-periods") { CostPeriodChecks.Run(Path.GetFullPath(args[1])); return; }
        typeof(ThemePreferences).GetProperty(nameof(ThemePreferences.Choice))!.SetValue(null, "Light");
        if (args.Length == 2 && args[0] == "--toolbar") { ShiftToolbarChecks.Run(Path.GetFullPath(args[1])); return; }
        if (args.Length == 2 && args[0] == "--themes") { ThemeChecks.Run(Path.GetFullPath(args[1])); return; }
        var output = Path.GetFullPath(args.Single()); Directory.CreateDirectory(output);
        WorkflowChecks.Run(output);
        var notes = ReleaseHighlights.Load();
        Assert(notes.Version == typeof(WinTheme).Assembly.GetName().Version!.ToString(3) && notes.Features.Length > 0, "Release highlights match the update");
        using (var notice = new WhatsNewForm(notes))
        {
            Assert(notice.AcceptButton is Button { Text: "OK", DialogResult: DialogResult.OK }, "Release notice has OK button");
            notice.ShowInTaskbar = false; notice.StartPosition = FormStartPosition.Manual;
            notice.Location = new Point(-32000, -32000); notice.Opacity = 0;
            notice.Show(); notice.PerformLayout();
            using var capture = new Bitmap(notice.Width, notice.Height);
            notice.DrawToBitmap(capture, new Rectangle(Point.Empty, capture.Size));
            capture.Save(Path.Combine(output, "whats-new-v182.png"));
            notice.Hide();
        }
        var prefs = Path.Combine(output, "preferences-" + Guid.NewGuid().ToString("N"));
        Assert(LedgerLayoutPreferences.Load("db-a", 1, "Shift Cash Drop", prefs) == LedgerLayout.SideEntryGrid, "Default must be side entry");
        LedgerLayoutPreferences.Save("db-a", 1, "Shift Cash Drop", LedgerLayout.GuidedGrid, prefs);
        Assert(LedgerLayoutPreferences.Load("db-a", 1, "Shift Cash Drop", prefs) == LedgerLayout.GuidedGrid, "Saved preference");
        Assert(LedgerLayoutPreferences.Load("db-a", 1, "Cash On Hand", prefs) == LedgerLayout.SideEntryGrid, "Independent sections");
        Assert(LedgerLayoutPreferences.Load("db-a", 2, "Shift Cash Drop", prefs) == LedgerLayout.SideEntryGrid, "Independent users");
        Assert(LedgerLayoutPreferences.Load("db-b", 1, "Shift Cash Drop", prefs) == LedgerLayout.SideEntryGrid, "Independent databases");

        foreach (var isShift in new[] { true, false })
        {
            typeof(ThemePreferences).GetProperty(nameof(ThemePreferences.Choice))!.SetValue(null, "Light");
            var submissions = new List<LedgerEntryDraft>();
            Task<(string[], string[])> Choices() => Task.FromResult((new[] { " Supplier ", "supplier", "Local Services" }, new[] { "Repairs", "Supplies" }));
            using var entry = new LedgerEntryPanel(isShift, draft => { submissions.Add(draft); return Task.CompletedTask; }, Choices);
            var grid = WinTheme.Grid(); grid.DataSource = new[] { new { Id = 1, Date = DateTime.Today, Batch = "3019", Cash = 100m, Drop = 0m } };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill }; actions.Controls.Add(WinTheme.Button("Dashboard")); actions.Controls.Add(WinTheme.Button(isShift ? "Record Shift Drop" : "Record Payout", true));
            foreach (Button button in actions.Controls) button.Width = 190;
            using var host = new LedgerWorkspaceHost(isShift ? "Shift Cash Drop" : "Cash On Hand", actions, grid, new Label { Text = "Records", Dock = DockStyle.Fill }, entry);
            host.Size = new Size(1200, 780); host.BindingContext = new BindingContext(); host.CreateControl();
            var amount = (NumericUpDown)entry.Controls.Find("LedgerAmount", true).Single(); amount.Value = 90;
            var vendor = (ComboBox)entry.Controls.Find("LedgerPayee", true).Single();
            var purpose = (ComboBox)entry.Controls.Find("LedgerPurpose", true).Single();
            vendor.Text = "Supplier"; purpose.Text = "Supplies";
            entry.RefreshChoicesAsync().GetAwaiter().GetResult();
            Assert(vendor.Items.Count == 2 && vendor.Text == "Supplier", "Choices deduplicate without losing typed value");
            Assert(vendor.DropDownStyle == ComboBoxStyle.DropDown && purpose.DropDownStyle == ComboBoxStyle.DropDown, "Both lists accept new names");
            var batchBox = entry.Controls.Find("LedgerBatch", true).OfType<ComboBox>().FirstOrDefault();
            if (batchBox is not null) batchBox.Text = "3019";
            if (isShift) ((NumericUpDown)entry.Controls.Find("LedgerRegisterPayout", true).Single()).Value = 10;
            foreach (var layout in Enum.GetValues<LedgerLayout>())
            {
                host.ApplyLayout(layout); host.PerformLayout();
                Assert(grid.Parent is not null && !grid.IsDisposed && grid.Height > 100, "Full grid remains visible");
                Assert(amount.Value == 90 && vendor.Text == "Supplier" && purpose.Text == "Supplies", "Layout change preserves draft");
                foreach (var dark in new[] { false, true })
                {
                    typeof(ThemePreferences).GetProperty(nameof(ThemePreferences.Choice))!.SetValue(null, dark ? "Dark" : "Light"); ThemePreferences.ApplyTree(host);
                    using var bitmap = new Bitmap(host.Width, host.Height); host.DrawToBitmap(bitmap, host.ClientRectangle);
                    bitmap.Save(Path.Combine(output, $"{(isShift ? "shift" : "cash")}-{layout}-{dark}.png"));
                }
            }
            // Guided review must not post. The second activation posts exactly once.
            host.ApplyLayout(LedgerLayout.GuidedGrid);
            var submit = typeof(LedgerEntryPanel).GetMethod("SubmitAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            ((Task)submit.Invoke(entry, null)!).GetAwaiter().GetResult(); Assert(submissions.Count == 0, "Review does not save");
            ((Task)submit.Invoke(entry, null)!).GetAwaiter().GetResult(); Assert(submissions.Count == 1, "Confirmation saves once");
            Assert(submissions[0].Payee == "Supplier" && submissions[0].Reason == "Supplies", "Final combo text submitted");
        }
        Console.WriteLine("PASS: side defaults; separate user/section/database preferences; editable shared choices; draft/grid retained in all layouts; guided review/confirm; light/dark offscreen rendering. Application not launched.");
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
}
