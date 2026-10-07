namespace ManagerPaperworkSystem.WinForms;

internal sealed class ShiftDropEntryForm : Form
{
    public TextBox Batch { get; } = new() { Dock = DockStyle.Fill, MaxLength = 20 };
    public NumericUpDown Drop { get; } = new() { Dock = DockStyle.Fill, DecimalPlaces = 2, Maximum = 99999999 };
    public NumericUpDown Payout { get; } = new() { Dock = DockStyle.Fill, DecimalPlaces = 2, Maximum = 99999999 };
    public TextBox Reason { get; } = new() { Dock = DockStyle.Fill, MaxLength = 300 };
    public Button SaveButton { get; } = WinTheme.Button("OK", true);

    public ShiftDropEntryForm(string storeName)
    {
        Text = "Record Shift Drop — " + storeName;
        ClientSize = new Size(475, 285);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        WinTheme.Apply(this);
        StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
        Control[] inputs = [Batch, Drop, Payout, Reason];
        string[] labels = ["Shift / Batch Number", "Cash Drop Amount", "Payout Amount", "Payout Reason"];
        for (var i = 0; i < inputs.Length; i++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.Controls.Add(new Label { Text = labels[i], Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, i);
            inputs[i].AccessibleName = labels[i];
            inputs[i].TabIndex = i;
            layout.Controls.Add(inputs[i], 1, i);
        }
        SaveButton.TabIndex = 4;
        layout.Controls.Add(SaveButton, 1, 4);
        Controls.Add(layout);
        AcceptButton = SaveButton;
        Shown += (_, _) => Batch.Focus();
        ThemePreferences.ApplyTree(this);
    }
}
