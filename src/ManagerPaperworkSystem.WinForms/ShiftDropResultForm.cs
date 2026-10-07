namespace ManagerPaperworkSystem.WinForms;

internal sealed class ShiftDropResultForm : Form
{
    public ShiftDropResultForm(string caption, decimal? variance, string message)
    {
        Text = "Shift Cash Drop";
        ClientSize = new Size(480, 230);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        WinTheme.Apply(this);
        StartPosition = FormStartPosition.CenterParent;
        var title = new Label { Text = caption, Dock = DockStyle.Top, Height = 48, TextAlign = ContentAlignment.MiddleCenter };
        var text = variance is null ? message : variance > 0 ? $"OVER {variance:C2}" : variance < 0 ? $"SHORT {Math.Abs(variance.Value):C2}" : "BALANCED — $0.00";
        var result = new Label { Name = "ShiftResult", Text = text, Dock = DockStyle.Fill, Padding = new Padding(12), Font = variance.HasValue ? WinTheme.BoldFont(18) : WinTheme.BodyFont(11), TextAlign = ContentAlignment.MiddleCenter, ForeColor = variance > 0 ? WinTheme.Green : variance < 0 ? WinTheme.Red : WinTheme.Text };
        var ok = WinTheme.Button("OK", true);
        ok.Dock = DockStyle.Bottom;
        ok.DialogResult = DialogResult.OK;
        AcceptButton = ok;
        Controls.Add(result);
        Controls.Add(title);
        Controls.Add(ok);
        ThemePreferences.ApplyTree(this);
    }
}
