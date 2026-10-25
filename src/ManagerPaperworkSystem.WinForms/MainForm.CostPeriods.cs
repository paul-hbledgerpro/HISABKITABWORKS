namespace ManagerPaperworkSystem.WinForms;

internal sealed partial class MainForm
{
    private Control AddCostPeriodBar(Control body, Func<DateOnly, DateOnly, Task> refresh)
    {
        var host = new Panel { Dock = DockStyle.Fill };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(4), WrapContents = false, AutoScroll = true };
        var period = CreateStandardPeriodCombo(); period.Name = "CostPeriod"; period.Width = 155;
        var range = new Label { AutoSize = true, Padding = new Padding(8, 7, 0, 0) };
        var from = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var to = DateTime.Today;
        var prior = period.Text; var changing = false;
        void ShowRange() => range.Text = $"Invoice dates: {from:d} – {to:d}";
        ShowRange();
        period.SelectedIndexChanged += async (_, _) =>
        {
            if (changing) return;
            if (!TryResolveStandardPeriod(period.Text, from, to, out var selectedFrom, out var selectedTo))
            {
                changing = true; period.SelectedItem = prior; changing = false; return;
            }
            from = selectedFrom; to = selectedTo; prior = period.Text; ShowRange();
            period.Enabled = false;
            try { await refresh(DateOnly.FromDateTime(from), DateOnly.FromDateTime(to)); }
            catch (Exception ex) { MessageBox.Show(this, AppBootstrap.RedactSensitiveText(ex.Message), "Cost History", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { if (!period.IsDisposed) period.Enabled = true; }
        };
        bar.Controls.Add(new Label { Text = "Period", AutoSize = true, Padding = new Padding(0, 7, 4, 0) });
        bar.Controls.Add(period); bar.Controls.Add(range);
        body.Dock = DockStyle.Fill; host.Controls.Add(body); host.Controls.Add(bar);
        return host;
    }
}
