namespace ManagerPaperworkSystem.WinForms;

internal sealed class CloudInvoiceInboxForm : Form
{
    private readonly CloudInvoiceInboxService _service;
    private readonly LicensedBusinessConnection _business;
    private readonly Label _status = new();

    public bool SyncRequested { get; private set; }

    public CloudInvoiceInboxForm(
        CloudInvoiceInboxService service,
        LicensedBusinessConnection business)
    {
        _service = service;
        _business = business;

        WinTheme.Apply(this);
        Text = "Automatic Purchase Invoice Inbox - HISAB KITAB";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(820, 520);
        MinimumSize = new Size(760, 500);
        MaximizeBox = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(28),
            RowCount = 5,
            ColumnCount = 1,
            BackColor = WinTheme.Bg
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Text = "PROTECTED PURCHASE INVOICE INBOX",
            Dock = DockStyle.Fill,
            Font = WinTheme.BoldFont(18),
            ForeColor = WinTheme.Copper,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        var addressCard = WinTheme.BorderedPanel(18);
        addressCard.Dock = DockStyle.Fill;
        var address = new TextBox
        {
            ReadOnly = true,
            Text = _business.InvoiceInboxAddress,
            Dock = DockStyle.Top,
            Height = 42,
            BackColor = Color.White,
            ForeColor = WinTheme.Blue,
            Font = WinTheme.BoldFont(13)
        };
        var copy = WinTheme.Button("COPY STORE INVOICE ADDRESS", true);
        copy.Dock = DockStyle.Bottom;
        copy.Height = 44;
        copy.Click += (_, _) =>
        {
            Clipboard.SetText(_business.InvoiceInboxAddress);
            _status.Text = "Store invoice address copied. Give this address only to this store's approved vendors.";
            _status.ForeColor = WinTheme.Green;
        };
        addressCard.Controls.Add(address);
        addressCard.Controls.Add(copy);
        root.Controls.Add(addressCard, 0, 1);

        root.Controls.Add(new Label
        {
            Text = "Vendor PDF invoices sent to this address are held in this store's private Cloudflare inbox. "
                   + "No Gmail password or Google authorization is required. Another store cannot read this inbox.",
            Dock = DockStyle.Fill,
            Font = WinTheme.BodyFont(10),
            ForeColor = WinTheme.Muted,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 2);

        _status.Dock = DockStyle.Fill;
        _status.Text = "Ready to test the protected store inbox.";
        _status.Font = WinTheme.BodyFont(9.5f);
        _status.ForeColor = WinTheme.Muted;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        root.Controls.Add(_status, 0, 3);

        var buttons = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = WinTheme.Bg
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        var test = WinTheme.Button("TEST INBOX");
        var sync = WinTheme.Button("SYNC NEW INVOICES", true);
        var close = WinTheme.Button("CLOSE");
        foreach (var button in new[] { test, sync, close })
        {
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(6, 6, 6, 6);
        }
        test.Click += async (_, _) => await TestAsync();
        sync.Click += (_, _) =>
        {
            SyncRequested = true;
            DialogResult = DialogResult.OK;
            Close();
        };
        close.Click += (_, _) => Close();
        buttons.Controls.Add(test, 0, 0);
        buttons.Controls.Add(sync, 1, 0);
        buttons.Controls.Add(close, 2, 0);
        root.Controls.Add(buttons, 0, 4);
    }

    private async Task TestAsync()
    {
        Enabled = false;
        _status.Text = "Testing the protected store inbox…";
        _status.ForeColor = WinTheme.Blue;
        try
        {
            await _service.TestAsync(_business);
            _status.Text = "Connection succeeded. This PC can access only this licensed store's invoice inbox.";
            _status.ForeColor = WinTheme.Green;
        }
        catch (Exception ex)
        {
            _status.Text = "Connection failed: " + AppBootstrap.RedactSensitiveText(ex.Message);
            _status.ForeColor = WinTheme.Red;
        }
        finally
        {
            if (!IsDisposed)
                Enabled = true;
        }
    }
}
