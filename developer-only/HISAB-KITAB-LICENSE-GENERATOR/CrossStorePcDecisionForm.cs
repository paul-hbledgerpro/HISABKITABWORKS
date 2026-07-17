namespace HisabKitabWorks.LicenseGenerator.WinForms;

internal sealed class CrossStorePcDecisionForm : Form
{
    private bool? _releaseOtherAssignments;

    private CrossStorePcDecisionForm(
        string pcId,
        string targetStoreGuid,
        string targetBusiness,
        IReadOnlyCollection<OtherStorePcAssignment> assignments)
    {
        Text = "HISAB KITAB WORKS - PC Registration Decision";
        Icon = AdminTheme.LoadIcon();
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        BackColor = AdminTheme.Bg;
        ForeColor = AdminTheme.Text;
        Font = AdminTheme.Body();
        Size = new Size(850, 610);
        MinimumSize = new Size(780, 560);
        MaximizeBox = false;
        MinimizeBox = false;
        Controls.Add(BuildLayout(pcId, targetStoreGuid, targetBusiness, assignments));
    }

    internal static bool? Choose(
        IWin32Window owner,
        string pcId,
        string targetStoreGuid,
        string targetBusiness,
        IReadOnlyCollection<OtherStorePcAssignment> assignments)
    {
        using var form = new CrossStorePcDecisionForm(pcId, targetStoreGuid, targetBusiness, assignments);
        return form.ShowDialog(owner) == DialogResult.OK ? form._releaseOtherAssignments : null;
    }

    private Control BuildLayout(
        string pcId,
        string targetStoreGuid,
        string targetBusiness,
        IReadOnlyCollection<OtherStorePcAssignment> assignments)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = AdminTheme.Bg,
            Padding = new Padding(20),
            ColumnCount = 1,
            RowCount = 5
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));

        var header = new Panel { Dock = DockStyle.Fill, BackColor = AdminTheme.BlueDark, Padding = new Padding(22, 10, 22, 8) };
        header.Paint += (_, e) => AdminTheme.PaintGradient(e, header.ClientRectangle);
        header.Controls.Add(new Label
        {
            Text = "PC ID ALREADY REGISTERED",
            Dock = DockStyle.Top,
            Height = 40,
            ForeColor = Color.White,
            Font = AdminTheme.Header(19),
            BackColor = Color.Transparent
        });
        header.Controls.Add(new Label
        {
            Text = "Choose whether this PC also belongs to the new store or moves from its existing store.",
            Dock = DockStyle.Bottom,
            Height = 28,
            ForeColor = AdminTheme.Copper,
            Font = AdminTheme.Body(10),
            BackColor = Color.Transparent
        });
        root.Controls.Add(header, 0, 0);

        var target = AdminTheme.Card(AdminTheme.CopperDark);
        target.Dock = DockStyle.Fill;
        target.Margin = new Padding(0, 10, 0, 8);
        target.Padding = new Padding(18, 10, 18, 10);
        var targetLayout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, RowCount = 3 };
        targetLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        targetLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        targetLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        targetLayout.Controls.Add(AdminTheme.Label("REQUESTED REGISTRATION", AdminTheme.Copper, 9, true), 0, 0);
        targetLayout.Controls.Add(AdminTheme.Label($"{targetBusiness}  •  {targetStoreGuid}", AdminTheme.BlueDark, 10.5f, true), 0, 1);
        targetLayout.Controls.Add(AdminTheme.Label($"PC ID: {pcId}", AdminTheme.Green, 10.5f, true), 0, 2);
        target.Controls.Add(targetLayout);
        root.Controls.Add(target, 0, 1);

        var existing = AdminTheme.Card();
        existing.Dock = DockStyle.Fill;
        existing.Padding = new Padding(16, 10, 16, 12);
        var existingLayout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, RowCount = 2 };
        existingLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        existingLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        existingLayout.Controls.Add(AdminTheme.Label("CURRENT STORE ASSIGNMENT(S)", AdminTheme.BlueDark, 10, true), 0, 0);
        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            ForeColor = AdminTheme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = AdminTheme.Body(10.5f)
        };
        foreach (var assignment in assignments)
            list.Items.Add($"{assignment.BusinessName}  •  {assignment.StoreGuid}  •  expires {assignment.ExpiresDate:MM/dd/yyyy}");
        existingLayout.Controls.Add(list, 0, 1);
        existing.Controls.Add(existingLayout);
        root.Controls.Add(existing, 0, 2);

        var explanation = AdminTheme.Label(
            "ADD keeps the existing registration and counts this PC under the requested store too.  MOVE / REPLACE releases the existing store assignment and keeps only the requested store assignment.",
            AdminTheme.Muted, 9.5f, true);
        explanation.Dock = DockStyle.Fill;
        root.Controls.Add(explanation, 0, 3);

        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Bg, ColumnCount = 3 };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        var add = AdminTheme.Button("ADD PC TO THIS STORE", true);
        var move = AdminTheme.Button("MOVE / REPLACE EXISTING");
        var cancel = AdminTheme.Button("CANCEL");
        add.Dock = move.Dock = cancel.Dock = DockStyle.Fill;
        add.Click += (_, _) => Complete(false);
        move.Click += (_, _) =>
        {
            if (MessageBox.Show(this,
                    "Move this PC registration to the requested Store GUID?\r\n\r\n" +
                    "The existing store will receive no future renewals for this PC. Any offline license already installed remains valid until its expiration date.",
                    "Confirm Move / Replace", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                Complete(true);
        };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.Add(add, 0, 0);
        buttons.Controls.Add(move, 1, 0);
        buttons.Controls.Add(cancel, 2, 0);
        root.Controls.Add(buttons, 0, 4);
        return root;
    }

    private void Complete(bool releaseOtherAssignments)
    {
        _releaseOtherAssignments = releaseOtherAssignments;
        DialogResult = DialogResult.OK;
        Close();
    }
}
