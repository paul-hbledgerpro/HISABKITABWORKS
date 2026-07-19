using ManagerPaperworkSystem.Core.Services;

namespace ManagerPaperworkSystem.WinForms;

internal sealed class InvoiceEmailSetupForm : Form
{
    private readonly InvoiceEmailSyncService _service;
    private readonly string _storeKey;
    private readonly ComboBox _provider = WinTheme.ComboBox();
    private readonly TextBox _server = WinTheme.TextBox();
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 993 };
    private readonly CheckBox _ssl = new() { Text = "Use encrypted SSL connection", Checked = true, AutoSize = true };
    private readonly TextBox _email = WinTheme.TextBox();
    private readonly TextBox _password = WinTheme.TextBox();
    private readonly TextBox _folder = WinTheme.TextBox();
    private readonly DateTimePicker _invoiceMonth = WinTheme.DatePicker();
    private readonly CheckBox _enabled = new() { Text = "Automatically check this store's email for PDF invoices", AutoSize = true };
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill };

    public DateOnly? RequestedInvoiceMonth { get; private set; }

    public InvoiceEmailSetupForm(
        IAppPaths paths,
        InvoiceEmailSyncService service,
        string storeKey)
    {
        _ = paths;
        _service = service;
        _storeKey = storeKey;

        WinTheme.Apply(this);
        Text = "Invoice Email Automation - HISAB KITAB";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(760, 700);
        MinimumSize = new Size(720, 660);
        MaximizeBox = false;

        _provider.Items.AddRange(new object[] { "Gmail", "Microsoft 365 / Outlook", "Yahoo", "Custom IMAP" });
        _password.UseSystemPasswordChar = true;
        _folder.Text = "INBOX";
        _invoiceMonth.Format = DateTimePickerFormat.Custom;
        _invoiceMonth.CustomFormat = "MMMM yyyy";
        _invoiceMonth.ShowUpDown = true;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            ColumnCount = 1,
            RowCount = 4,
            BackColor = WinTheme.Bg
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        Controls.Add(root);

        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = WinTheme.Bg };
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        heading.Controls.Add(new Label
        {
            Text = "AUTOMATIC PURCHASE INVOICE INBOX",
            Dock = DockStyle.Fill,
            Font = WinTheme.BoldFont(18),
            ForeColor = WinTheme.Copper,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        heading.Controls.Add(new Label
        {
            Text = "HISAB KITAB downloads PDF attachments without deleting or marking the client's email as read.",
            Dock = DockStyle.Fill,
            Font = WinTheme.BodyFont(9.5f),
            ForeColor = WinTheme.Muted,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 1);
        root.Controls.Add(heading, 0, 0);

        var card = WinTheme.BorderedPanel(18);
        card.Dock = DockStyle.Fill;
        root.Controls.Add(card, 0, 1);
        var form = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 8,
            BackColor = WinTheme.Panel
        };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 7; i++)
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        form.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(form);

        AddRow(form, 0, "EMAIL PROVIDER", _provider);
        AddRow(form, 1, "IMAP SERVER", _server);
        AddRow(form, 2, "IMAP PORT", _port);
        AddRow(form, 3, "CLIENT EMAIL ADDRESS", _email);
        AddRow(form, 4, "APP PASSWORD", _password);
        AddRow(form, 5, "MAIL FOLDER", _folder);
        AddRow(form, 6, "BACKFILL INVOICE MONTH", _invoiceMonth);

        var options = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(4, 8, 4, 4),
            BackColor = WinTheme.Panel
        };
        options.Controls.Add(_ssl);
        options.Controls.Add(_enabled);
        form.Controls.Add(options, 0, 7);
        form.SetColumnSpan(options, 2);

        _status.Text = "For Gmail, use a Google App Password—not the normal mailbox password.";
        _status.ForeColor = WinTheme.Muted;
        _status.Font = WinTheme.BodyFont(9);
        _status.TextAlign = ContentAlignment.MiddleLeft;
        root.Controls.Add(_status, 0, 2);

        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = WinTheme.Bg };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
        var test = WinTheme.Button("TEST & SAVE");
        var save = WinTheme.Button("SYNC NEW INVOICES", true);
        var syncMonth = WinTheme.Button("SYNC SELECTED MONTH", true);
        buttons.Controls.Add(test, 0, 0);
        buttons.Controls.Add(save, 1, 0);
        buttons.Controls.Add(syncMonth, 2, 0);
        root.Controls.Add(buttons, 0, 3);

        _provider.SelectedIndexChanged += (_, _) => ApplyProviderDefaults();
        test.Click += async (_, _) => await SaveAsync(testOnly: true);
        save.Click += async (_, _) => await SaveAsync(testOnly: false, syncSelectedMonth: false);
        syncMonth.Click += async (_, _) => await SaveAsync(testOnly: false, syncSelectedMonth: true);

        LoadSettings();
    }

    private void LoadSettings()
    {
        var settings = _service.GetSettings(_storeKey);
        _provider.SelectedItem = settings.Provider;
        if (_provider.SelectedIndex < 0)
            _provider.SelectedIndex = 0;
        _server.Text = settings.ImapServer;
        _port.Value = Math.Clamp(settings.ImapPort, 1, 65535);
        _ssl.Checked = settings.UseSsl;
        _email.Text = settings.EmailAddress;
        _password.Text = settings.PasswordOrAppPassword;
        _folder.Text = string.IsNullOrWhiteSpace(settings.MailFolder) ? "INBOX" : settings.MailFolder;
        _enabled.Checked = settings.Enabled;
    }

    private void ApplyProviderDefaults()
    {
        switch (_provider.Text)
        {
            case "Gmail":
                _server.Text = "imap.gmail.com";
                _port.Value = 993;
                _ssl.Checked = true;
                _status.Text = "Gmail: turn on two-step verification, then use a 16-character Google App Password.";
                break;
            case "Microsoft 365 / Outlook":
                _server.Text = "outlook.office365.com";
                _port.Value = 993;
                _ssl.Checked = true;
                _status.Text = "Microsoft 365: the account must allow IMAP and an app password. OAuth-only tenants require developer setup.";
                break;
            case "Yahoo":
                _server.Text = "imap.mail.yahoo.com";
                _port.Value = 993;
                _ssl.Checked = true;
                _status.Text = "Yahoo: create an app password in Yahoo account security.";
                break;
            default:
                _status.Text = "Enter the IMAP settings supplied by the client's email provider.";
                break;
        }
    }

    private async Task SaveAsync(bool testOnly, bool syncSelectedMonth = false)
    {
        var settings = new InvoiceEmailSyncSettings
        {
            Enabled = _enabled.Checked,
            Provider = _provider.Text,
            ImapServer = _server.Text.Trim(),
            ImapPort = (int)_port.Value,
            UseSsl = _ssl.Checked,
            EmailAddress = _email.Text.Trim(),
            PasswordOrAppPassword = _password.Text,
            MailFolder = string.IsNullOrWhiteSpace(_folder.Text) ? "INBOX" : _folder.Text.Trim(),
            LastSuccessfulSyncUtc = _service.GetSettings(_storeKey).LastSuccessfulSyncUtc,
            ProcessedAttachmentHashes = _service.GetSettings(_storeKey).ProcessedAttachmentHashes
        };

        if (!settings.Enabled)
        {
            _service.SaveSettings(_storeKey, settings);
            DialogResult = DialogResult.Cancel;
            Close();
            return;
        }

        Enabled = false;
        _status.Text = "Testing the encrypted email connection…";
        _status.ForeColor = WinTheme.Blue;
        try
        {
            await _service.TestConnectionAsync(settings);
            _service.SaveSettings(_storeKey, settings);
            _status.Text = "Email connection succeeded and the settings were saved securely on this PC.";
            _status.ForeColor = WinTheme.Green;
            if (!testOnly)
            {
                RequestedInvoiceMonth = syncSelectedMonth
                    ? new DateOnly(_invoiceMonth.Value.Year, _invoiceMonth.Value.Month, 1)
                    : null;
                DialogResult = DialogResult.OK;
                Close();
            }
        }
        catch (Exception ex)
        {
            _status.Text = $"Connection failed: {AppBootstrap.RedactSensitiveText(ex.Message)}";
            _status.ForeColor = WinTheme.Red;
        }
        finally
        {
            if (!IsDisposed)
                Enabled = true;
        }
    }

    private static void AddRow(TableLayoutPanel form, int row, string label, Control control)
    {
        form.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            Font = WinTheme.BoldFont(9),
            ForeColor = WinTheme.Text,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(4, 8, 4, 8);
        form.Controls.Add(control, 1, row);
    }
}
