namespace HisabKitabWorks.LicenseGenerator.WinForms;

internal sealed class ActivationCodeDialog : Form
{
    private readonly TextBox _text = new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        BackColor = AdminTheme.Bg,
        ForeColor = AdminTheme.Text,
        BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Consolas", 10),
        Dock = DockStyle.Fill
    };
    private readonly FlowLayoutPanel _actions = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.RightToLeft,
        WrapContents = false,
        BackColor = AdminTheme.Panel,
        Padding = new Padding(0, 8, 0, 0)
    };

    private ActivationCodeDialog(string title, string instruction)
    {
        Text = title;
        BackColor = AdminTheme.Bg;
        ForeColor = AdminTheme.Text;
        Font = AdminTheme.Body();
        Icon = AdminTheme.LoadIcon();
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(920, 650);
        MinimumSize = new Size(760, 520);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Bg, Padding = new Padding(20), RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.Controls.Add(new Label
        {
            Text = instruction,
            Dock = DockStyle.Fill,
            ForeColor = AdminTheme.Text,
            Font = AdminTheme.Body(11),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        root.Controls.Add(_text, 0, 1);
        root.Controls.Add(_actions, 0, 2);
        Controls.Add(root);
    }

    public static string? PromptForRequest(IWin32Window owner)
    {
        using var form = new ActivationCodeDialog(
            "Paste Customer Activation Details",
            "Paste the complete activation details copied from the customer's HISAB KITAB registration window. It contains the Store GUID, Business Name, ZIP Code, App Serial Number, and protected PC identity.");
        var continueButton = AdminTheme.Button("CONTINUE", true);
        var pasteButton = AdminTheme.Button("PASTE FROM CLIPBOARD");
        var loadButton = AdminTheme.Button("LOAD REQUEST FILE");
        var cancelButton = AdminTheme.Button("CANCEL");
        foreach (var button in new[] { continueButton, pasteButton, loadButton, cancelButton })
        {
            button.Width = 185;
            form._actions.Controls.Add(button);
        }
        continueButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(form._text.Text))
            {
                MessageBox.Show(form, "Paste the customer activation details first.", "Activation Details Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            form.DialogResult = DialogResult.OK;
        };
        pasteButton.Click += (_, _) =>
        {
            if (Clipboard.ContainsText())
                form._text.Text = Clipboard.GetText();
        };
        loadButton.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Load HISAB KITAB PC Request",
                Filter = "HISAB KITAB PC Request (*.hbrequest)|*.hbrequest|Text files (*.txt)|*.txt|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog(form) == DialogResult.OK)
                form._text.Text = File.ReadAllText(dialog.FileName);
        };
        cancelButton.Click += (_, _) => form.DialogResult = DialogResult.Cancel;
        form.Shown += (_, _) =>
        {
            if (Clipboard.ContainsText() && Clipboard.GetText().Contains("HKREQ2-", StringComparison.OrdinalIgnoreCase))
                form._text.Text = Clipboard.GetText();
            form._text.Focus();
        };
        return form.ShowDialog(owner) == DialogResult.OK ? form._text.Text : null;
    }

    public static void ShowLicense(
        IWin32Window owner,
        string formattedLicense,
        string licenseJson,
        string suggestedFileName)
    {
        using var form = new ActivationCodeDialog(
            "Generated HISAB KITAB License Key",
            "Copy the complete License Key below and paste it into the customer's HISAB KITAB License Registration window.");
        form._text.Text = formattedLicense;
        form._text.ReadOnly = true;
        var copyButton = AdminTheme.Button("COPY LICENSE KEY", true);
        var saveButton = AdminTheme.Button("SAVE LICENSE FILE");
        var closeButton = AdminTheme.Button("CLOSE");
        foreach (var button in new[] { copyButton, saveButton, closeButton })
        {
            button.Width = 190;
            form._actions.Controls.Add(button);
        }
        copyButton.Click += (_, _) =>
        {
            Clipboard.SetText(formattedLicense);
            MessageBox.Show(form, "The complete License Key was copied.", "License Key Copied", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        saveButton.Click += (_, _) =>
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Save Device License",
                Filter = "HISAB KITAB Device License (*.hblicense)|*.hblicense",
                FileName = suggestedFileName,
                AddExtension = true,
                DefaultExt = ".hblicense"
            };
            if (dialog.ShowDialog(form) == DialogResult.OK)
                File.WriteAllText(dialog.FileName, licenseJson);
        };
        closeButton.Click += (_, _) => form.Close();
        form.Shown += (_, _) =>
        {
            Clipboard.SetText(formattedLicense);
            form._text.SelectAll();
            form._text.Focus();
        };
        form.ShowDialog(owner);
    }
}
