using System.Reflection;
using ManagerPaperworkSystem.WinForms;

internal static class ThemeChecks
{
    private static void Select(string name) => typeof(ThemePreferences).GetProperty(nameof(ThemePreferences.Choice))!.SetValue(null, name);
    private static void Check(bool result, string message) { if (!result) throw new Exception(message); }
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var path = Path.Combine(output, "appearance-test.txt");
        Check(ThemePreferences.Choices.Count == 9 && ThemePreferences.Choices.Distinct().Count() == 9, "Exactly nine theme options");
        foreach (var choice in ThemePreferences.Choices)
        {
            ThemePreferences.SaveChoice(path, choice); Check(ThemePreferences.ReadChoice(path) == choice, "Theme survives reload: " + choice);
        }
        ThemePreferences.SaveChoice(path, "Same as Windows"); Check(ThemePreferences.ReadChoice(path) == "Follow Windows", "Windows alias preserves existing choice");
        File.WriteAllText(path, "unrecognized theme"); Check(ThemePreferences.ReadChoice(path) == "Light", "Invalid preference falls back");
        File.Delete(path); Check(ThemePreferences.ReadChoice(path) == "Light", "Missing preference falls back");
        foreach (var palette in ThemePalette.Named)
        {
            foreach (var (fg, bg, name) in new[] { (palette.Text, palette.Surface, "body"), (palette.Muted, palette.Surface, "secondary"), (palette.OnAccent, palette.Accent, "button"), (palette.OnHeader, palette.Header, "header"), (palette.OnSelection, palette.Selection, "selection"), (palette.Positive, palette.Selection, "selected over"), (palette.Negative, palette.Selection, "selected short") })
                Check(Contrast(fg, bg) >= 4.5, $"{palette.Name}: {name} contrast {Contrast(fg, bg):F2}");
        }
        foreach (var shift in new[] { true, false })
        {
            Select("Light");
            using var form = new Form { ClientSize = new Size(1280, 800), BackColor = Color.White, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Opacity = 0 };
            var menu = new MenuStrip { BackColor = WinTheme.BlueDark, ForeColor = Color.White };
            var choiceClicked = ""; var appearance = ThemePreferences.CreateMenu(value => choiceClicked = value); menu.Items.Add(appearance);
            Check(appearance.DropDownItems.Count == 9, "Menu exposes nine items");
            foreach (ToolStripMenuItem item in appearance.DropDownItems) { item.PerformClick(); Check(choiceClicked == (string)item.Tag!, "Menu routes exact theme choice"); }
            var nav = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 190, BackColor = WinTheme.BlueDark, Padding = new Padding(8) };
            var selected = WinTheme.Button("Shift Cash Drop", true); selected.Width = 168;
            var inactive = WinTheme.Button("Cash On Hand"); inactive.BackColor = WinTheme.BlueDark; inactive.ForeColor = Color.White; inactive.Width = 168;
            var navLabel = new Label { Text = "HISAB KITAB", ForeColor = Color.White, Width = 170, Height = 44 };
            nav.Controls.AddRange([navLabel, selected, inactive]);
            var grid = WinTheme.Grid(); grid.DataSource = new[] { new { Date = new DateTime(2026, 10, 23), Batch = "3004", Employee = "Manager", Cash = 485.50m, Card = 1250m, Drop = 480m, Payout = 5.50m, Variance = 0m }, new { Date = new DateTime(2026, 10, 22), Batch = "3003", Employee = "Manager", Cash = 375m, Card = 950m, Drop = 370m, Payout = 0m, Variance = -5m } };
            var entry = new LedgerEntryPanel(shift, _ => Task.CompletedTask, () => Task.FromResult((new[] { "Supplier" }, new[] { "Supplies" })));
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
            foreach (var text in new[] { "Dashboard", shift ? "Record Shift Drop" : "Record Payout", "Refresh" }) actions.Controls.Add(WinTheme.Button(text, text.StartsWith("Record")));
            var details = new Panel { Dock = DockStyle.Fill, BackColor = Color.White }; details.Controls.Add(new Label { Text = "Selected shift details", Dock = DockStyle.Top, ForeColor = WinTheme.Text });
            var toolbar = shift ? new ShiftGridToolbar() : null;
            using var host = new LedgerWorkspaceHost(shift ? "Shift Cash Drop" : "Cash On Hand", actions, grid, new Label { Text = "Records for selected store", Dock = DockStyle.Fill }, entry, details: shift ? details : null, gridToolbar: toolbar);
            form.Controls.Add(host); form.Controls.Add(nav); form.Controls.Add(menu); host.ApplyLayout(LedgerLayout.SideEntryGrid); form.Show(); Application.DoEvents();
            var amount = entry.Controls.Find("LedgerAmount", true).OfType<NumericUpDown>().Single(); amount.Value = 480; var originalAmountBack = amount.BackColor;
            grid.Columns["Cash"].DefaultCellStyle.Format = "N2";
            foreach (var choice in ThemePreferences.Choices.Concat(ThemePreferences.Choices.Reverse()))
            {
                Select(choice); ThemePreferences.ApplyTree(form); Application.DoEvents();
                Check(amount.Value == 480 && grid.Columns["Cash"].DefaultCellStyle.Format == "N2", "Theme change preserves draft and currency formatting");
                Check(grid.Rows.Count == 2 && grid.Columns["Batch"].HeaderText == "Batch", "Theme change preserves records");
                if (ThemePreferences.Palette is { } palette)
                {
                    Check(selected.BackColor == palette.Accent && selected.ForeColor == palette.OnAccent, choice + " primary button");
                    Check(inactive.BackColor == palette.Navigation && inactive.ForeColor == Color.White && navLabel.ForeColor == Color.White, choice + " navigation");
                    Check(grid.ColumnHeadersDefaultCellStyle.BackColor == palette.Header && grid.DefaultCellStyle.SelectionBackColor == palette.Selection, choice + " grid");
                    Check(amount.BackColor == palette.Input && amount.ForeColor == palette.Text, choice + " amount input");
                    foreach (var v in new[] { -5m, 5m })
                    {
                        using var result = new ShiftDropResultForm("Batch 3004", v, "Saved");
                        var label = result.Controls.Find("ShiftResult", true).Single();
                        Check(label.ForeColor == (v < 0 ? palette.Negative : palette.Positive), choice + " over/short meaning");
                    }
                    using var notes = new WhatsNewForm(ReleaseHighlights.Load()); ThemePreferences.ApplyTree(notes);
                    var noteText = notes.Controls.Find("ReleaseHighlights", true).Single();
                    Check(Contrast(noteText.ForeColor, noteText.BackColor) >= 4.5, choice + " release notice readability");
                }
                typeof(ToolStripDropDownItem).GetMethod("OnDropDownShow", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(appearance, [EventArgs.Empty]);
                Check(appearance.DropDownItems.OfType<ToolStripMenuItem>().Count(i => i.Checked) == 1 && appearance.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Checked).Tag?.ToString() == choice, "Current theme checked");
                using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(output, (shift ? "shift-" : "cash-") + choice.Replace(" & ", "-").Replace(' ', '-') + ".png"));
            }
            Select("Light"); ThemePreferences.ApplyTree(form);
            Check(selected.BackColor == WinTheme.Copper && selected.ForeColor == Color.White && amount.BackColor == originalAmountBack, $"Original Light restored after every palette: {selected.BackColor}, {selected.ForeColor}, {amount.BackColor}");
            Check(grid.DefaultCellStyle.SelectionBackColor == WinTheme.Blue, "Original selection restored");
            form.Hide();
        }
        Console.WriteLine("PASS: nine choices and saved reloads, legacy Windows preference, invalid/missing fallback, menu selection, palette contrast, both cash layouts, repeated live theme switching, draft/format retention, over/short colors, release notice and Light restoration. No client settings or data changed.");
    }
    private static double Contrast(Color a, Color b)
    {
        static double Lum(Color c) { static double Channel(byte b) { var v = b / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); } return .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B); }
        var x = Lum(a); var y = Lum(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
}
