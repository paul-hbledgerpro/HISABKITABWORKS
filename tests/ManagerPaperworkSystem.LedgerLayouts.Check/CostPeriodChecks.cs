using System.Reflection;
using System.Runtime.CompilerServices;
using ManagerPaperworkSystem.WinForms;

internal static class CostPeriodChecks
{
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        // Invoke only the independent period-control builder. Do not construct/load the
        // real MainForm, which would start application services and scheduled work.
        var owner = (MainForm)RuntimeHelpers.GetUninitializedObject(typeof(MainForm));
        var builder = typeof(MainForm).GetMethod("AddCostPeriodBar", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var dark in new[] { false, true })
        {
            typeof(ThemePreferences).GetProperty(nameof(ThemePreferences.Choice))!.SetValue(null, dark ? "Dark" : "Light");
            using var form = new Form { ClientSize = new Size(1000, 400), Location = new Point(-32000, -32000), StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, Opacity = 0 };
            var grid = WinTheme.Grid(); grid.DataSource = new[] { new { Product = "Fixture product", InvoiceDate = DateTime.Today, UnitCost = 12.00m } };
            DateOnly from = default, to = default;
            Func<DateOnly, DateOnly, Task> refresh = (f, t) => { from = f; to = t; return Task.CompletedTask; };
            var host = (Control)builder.Invoke(owner, [grid, refresh])!; form.Controls.Add(host);
            ThemePreferences.ApplyTree(form); form.Show(); Application.DoEvents();
            var period = (ComboBox)host.Controls.Find("CostPeriod", true).Single();
            foreach (var required in new[] { "Current Month", "Previous Month", "Current Year", "Previous Year", "Custom" })
                if (!period.Items.Contains(required)) throw new Exception("Missing period " + required);
            if (period.Text != "Current Month") throw new Exception("Unexpected initial period");
            period.SelectedItem = "Previous Year";
            if (from != new DateOnly(DateTime.Today.Year - 1, 1, 1) || to != new DateOnly(DateTime.Today.Year - 1, 12, 31)) throw new Exception("Year callback bounds");
            period.SelectedItem = "Previous Month";
            var first = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
            if (from != first.AddMonths(-1) || to != first.AddDays(-1)) throw new Exception("Month callback bounds");
            if (period.Bottom > period.Parent!.Height || grid.Top < period.Parent.Bottom || grid.Height < 250) throw new Exception("Period/grid layout overlaps");
            using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(output, "cost-period-" + (dark ? "dark" : "light") + ".png")); form.Hide();
        }
        Console.WriteLine("PASS: required period choices, invoice-date month/year callbacks, separate bar and grid, light/dark render. Main application not constructed or launched.");
    }
}
