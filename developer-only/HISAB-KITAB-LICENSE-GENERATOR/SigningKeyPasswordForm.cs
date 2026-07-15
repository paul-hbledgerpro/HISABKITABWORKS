namespace HisabKitabWorks.LicenseGenerator.WinForms;

internal sealed class SigningKeyPasswordForm : Form
{
    private readonly TextBox _password = AdminTheme.TextBox(password: true);
    private readonly TextBox _confirm = AdminTheme.TextBox(password: true);
    private readonly Label _error = AdminTheme.Label("", AdminTheme.Red, 9);
    private readonly bool _requireConfirmation;

    private SigningKeyPasswordForm(bool requireConfirmation)
    {
        _requireConfirmation = requireConfirmation;
        Text = requireConfirmation ? "Protect Signing-Key Backup" : "Unlock Signing-Key Backup";
        BackColor = AdminTheme.Bg;
        ForeColor = AdminTheme.Text;
        Font = AdminTheme.Body();
        Icon = AdminTheme.LoadIcon();
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(500, requireConfirmation ? 310 : 240);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = AdminTheme.Bg,
            Padding = new Padding(22),
            ColumnCount = 1,
            RowCount = requireConfirmation ? 5 : 4
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        if (requireConfirmation)
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));

        var instructions = AdminTheme.Label(
            requireConfirmation
                ? "Create a password for this encrypted backup. You will need the same password on your work PC."
                : "Enter the password used when this encrypted backup was created.",
            AdminTheme.Text, 10);
        instructions.Dock = DockStyle.Fill;
        root.Controls.Add(instructions, 0, 0);
        root.Controls.Add(BuildPasswordField("BACKUP PASSWORD", _password), 0, 1);
        var nextRow = 2;
        if (requireConfirmation)
        {
            root.Controls.Add(BuildPasswordField("CONFIRM PASSWORD", _confirm), 0, nextRow);
            nextRow++;
        }

        _error.Dock = DockStyle.Fill;
        root.Controls.Add(_error, 0, nextRow);

        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = AdminTheme.Bg };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var cancel = AdminTheme.Button("CANCEL");
        var ok = AdminTheme.Button(requireConfirmation ? "CREATE BACKUP" : "RESTORE KEY", true);
        cancel.Dock = DockStyle.Fill;
        ok.Dock = DockStyle.Fill;
        cancel.DialogResult = DialogResult.Cancel;
        ok.Click += (_, _) => ValidateAndAccept();
        buttons.Controls.Add(cancel, 0, 0);
        buttons.Controls.Add(ok, 1, 0);
        root.Controls.Add(buttons, 0, nextRow + 1);

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(root);
    }

    public static string? PromptForBackup(IWin32Window owner)
    {
        using var form = new SigningKeyPasswordForm(requireConfirmation: true);
        return form.ShowDialog(owner) == DialogResult.OK ? form._password.Text : null;
    }

    public static string? PromptForRestore(IWin32Window owner)
    {
        using var form = new SigningKeyPasswordForm(requireConfirmation: false);
        return form.ShowDialog(owner) == DialogResult.OK ? form._password.Text : null;
    }

    private void ValidateAndAccept()
    {
        if (_password.Text.Length < 12)
        {
            _error.Text = "Use at least 12 characters.";
            return;
        }
        if (_requireConfirmation && !string.Equals(_password.Text, _confirm.Text, StringComparison.Ordinal))
        {
            _error.Text = "The passwords do not match.";
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    private static Control BuildPasswordField(string caption, TextBox input)
    {
        var field = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Bg, RowCount = 2 };
        field.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        field.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var label = AdminTheme.Label(caption, AdminTheme.Muted, 8.5f, true);
        label.Dock = DockStyle.Fill;
        input.Dock = DockStyle.Fill;
        field.Controls.Add(label, 0, 0);
        field.Controls.Add(input, 0, 1);
        return field;
    }
}
