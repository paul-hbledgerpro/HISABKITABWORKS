namespace HisabKitabWorks.LicenseGenerator.WinForms;

internal enum PcSeatAction
{
    FirstPc,
    RenewSamePc,
    AddPaidPc,
    ReplacePc
}

internal sealed record PcSeatChoice(
    PcSeatAction Action,
    string? ReplacedPcId = null,
    bool ReleaseOtherStoreAssignments = false);

internal sealed record RegisteredPcOption(string PcId, string ComputerName, string Status, DateTime ExpiresDate)
{
    public override string ToString()
        => $"{ComputerName}  •  {PcId}  •  {Status}  •  expires {ExpiresDate:MM/dd/yyyy}";
}

internal sealed class PcSeatDecisionForm : Form
{
    private readonly ListBox _registeredPcs = new();
    private PcSeatChoice? _choice;

    private PcSeatDecisionForm(
        string requestedPcId,
        string requestedComputer,
        IReadOnlyCollection<RegisteredPcOption> assignedPcs,
        int maximumSeats)
    {
        Text = "HISAB KITAB WORKS - New PC Detected";
        Icon = AdminTheme.LoadIcon();
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        BackColor = AdminTheme.Bg;
        ForeColor = AdminTheme.Text;
        Font = AdminTheme.Body();
        Size = new Size(820, 620);
        MinimumSize = new Size(760, 560);
        MaximizeBox = false;
        MinimizeBox = false;

        var hasFreeSeat = assignedPcs.Count < maximumSeats;
        Controls.Add(BuildLayout(requestedPcId, requestedComputer, assignedPcs, maximumSeats, hasFreeSeat));
    }

    internal static PcSeatChoice? Choose(
        IWin32Window owner,
        string requestedPcId,
        string requestedComputer,
        IReadOnlyCollection<RegisteredPcOption> assignedPcs,
        int maximumSeats)
    {
        using var form = new PcSeatDecisionForm(requestedPcId, requestedComputer, assignedPcs, maximumSeats);
        return form.ShowDialog(owner) == DialogResult.OK ? form._choice : null;
    }

    private Control BuildLayout(
        string requestedPcId,
        string requestedComputer,
        IReadOnlyCollection<RegisteredPcOption> assignedPcs,
        int maximumSeats,
        bool hasFreeSeat)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = AdminTheme.Bg,
            Padding = new Padding(20),
            ColumnCount = 1,
            RowCount = 5
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 106));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));

        var header = new Panel { Dock = DockStyle.Fill, BackColor = AdminTheme.BlueDark, Padding = new Padding(20, 10, 20, 8) };
        header.Paint += (_, e) => AdminTheme.PaintGradient(e, header.ClientRectangle);
        header.Controls.Add(new Label
        {
            Text = "NEW PC ID DETECTED",
            Dock = DockStyle.Top,
            Height = 38,
            ForeColor = Color.White,
            Font = AdminTheme.Header(19),
            BackColor = Color.Transparent
        });
        header.Controls.Add(new Label
        {
            Text = "Choose whether this computer replaces an old PC or uses another paid seat.",
            Dock = DockStyle.Bottom,
            Height = 26,
            ForeColor = AdminTheme.Copper,
            Font = AdminTheme.Body(10),
            BackColor = Color.Transparent
        });
        root.Controls.Add(header, 0, 0);

        var requested = AdminTheme.Card(AdminTheme.CopperDark);
        requested.Dock = DockStyle.Fill;
        requested.Margin = new Padding(0, 10, 0, 8);
        requested.Padding = new Padding(18, 10, 18, 10);
        var requestedLayout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 1, RowCount = 3 };
        requestedLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        requestedLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        requestedLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        requestedLayout.Controls.Add(AdminTheme.Label("CUSTOMER'S NEW COMPUTER", AdminTheme.Copper, 9, true), 0, 0);
        requestedLayout.Controls.Add(AdminTheme.Label(requestedComputer, AdminTheme.BlueDark, 11, true), 0, 1);
        requestedLayout.Controls.Add(AdminTheme.Label($"PC ID: {requestedPcId}", AdminTheme.Green, 10.5f, true), 0, 2);
        requested.Controls.Add(requestedLayout);
        root.Controls.Add(requested, 0, 1);

        var listCard = AdminTheme.Card();
        listCard.Dock = DockStyle.Fill;
        listCard.Padding = new Padding(16, 10, 16, 14);
        var listLayout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, RowCount = 2 };
        listLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        listLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        listLayout.Controls.Add(AdminTheme.Label("CURRENTLY ASSIGNED PC SEATS", AdminTheme.BlueDark, 10, true), 0, 0);
        _registeredPcs.Dock = DockStyle.Fill;
        _registeredPcs.Font = AdminTheme.Body(10.5f);
        _registeredPcs.BackColor = Color.White;
        _registeredPcs.ForeColor = AdminTheme.Text;
        _registeredPcs.BorderStyle = BorderStyle.FixedSingle;
        foreach (var pc in assignedPcs)
            _registeredPcs.Items.Add(pc);
        if (_registeredPcs.Items.Count > 0)
            _registeredPcs.SelectedIndex = 0;
        listLayout.Controls.Add(_registeredPcs, 0, 1);
        listCard.Controls.Add(listLayout);
        root.Controls.Add(listCard, 0, 2);

        var seatMessage = hasFreeSeat
            ? $"{assignedPcs.Count} of {maximumSeats} paid PC seats are assigned. Adding this PC uses an available paid seat."
            : $"All {maximumSeats} paid PC seats are assigned. ADD increases the paid seat count to {maximumSeats + 1}; REPLACE keeps it at {maximumSeats}.";
        var note = AdminTheme.Label(seatMessage, hasFreeSeat ? AdminTheme.Green : AdminTheme.CopperDark, 9.5f, true);
        note.Dock = DockStyle.Fill;
        note.TextAlign = ContentAlignment.MiddleLeft;
        root.Controls.Add(note, 0, 3);

        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Bg, ColumnCount = 3 };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        var add = AdminTheme.Button("ADD AS ADDITIONAL PAID PC", true);
        var replace = AdminTheme.Button("REPLACE SELECTED PC");
        var cancel = AdminTheme.Button("CANCEL");
        add.Dock = replace.Dock = cancel.Dock = DockStyle.Fill;
        add.Click += (_, _) => Complete(new PcSeatChoice(PcSeatAction.AddPaidPc));
        replace.Click += (_, _) =>
        {
            if (_registeredPcs.SelectedItem is not RegisteredPcOption selected)
            {
                MessageBox.Show(this, "Select the old PC that this new computer will replace.", "Select PC", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this,
                    $"Replace {selected.ComputerName} ({selected.PcId}) with the new PC?\r\n\r\n" +
                    "The old PC will receive no future renewals. Because this is offline licensing, a license already installed on the old PC remains valid until its current expiration date.",
                    "Confirm PC Replacement", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                Complete(new PcSeatChoice(PcSeatAction.ReplacePc, selected.PcId));
        };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.Add(add, 0, 0);
        buttons.Controls.Add(replace, 1, 0);
        buttons.Controls.Add(cancel, 2, 0);
        root.Controls.Add(buttons, 0, 4);
        return root;
    }

    private void Complete(PcSeatChoice choice)
    {
        _choice = choice;
        DialogResult = DialogResult.OK;
        Close();
    }
}
