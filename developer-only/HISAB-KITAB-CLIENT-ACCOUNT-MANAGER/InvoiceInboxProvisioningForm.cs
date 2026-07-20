namespace HisabKitabWorks.ClientAccountManager.WinForms;

internal sealed class InvoiceInboxProvisioningForm : Form
{
    private readonly ClientAccountService _service;
    private readonly ClientAccount _account;
    private readonly TextBox _baseUrl = DeveloperTheme.TextBox();
    private readonly TextBox _adminSecret = DeveloperTheme.TextBox(true);
    private readonly TextBox _invoiceAddress = DeveloperTheme.TextBox();
    private readonly CheckBox _rememberSecret = new()
    {
        Text = "Protect and remember this admin secret for my Windows user",
        Checked = true,
        AutoSize = true,
        Font = DeveloperTheme.Body(9.5f)
    };
    private readonly Label _status = DeveloperTheme.Label(
        "Ready to provision this store's private invoice inbox.",
        false,
        DeveloperTheme.Muted);
    private readonly Button _provision = DeveloperTheme.Button("PROVISION / REFRESH INBOX", true);

    public InvoiceInboxProvisioningForm(ClientAccountService service, ClientAccount account)
    {
        _service = service;
        _account = account;
        Text = "HISAB KITAB WORKS - Store Invoice Inbox";
        Icon = DeveloperTheme.Icon();
        BackColor = DeveloperTheme.Bg;
        Font = DeveloperTheme.Body();
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(880, 650);
        MinimumSize = new Size(760, 590);
        AutoScaleMode = AutoScaleMode.Dpi;
        MaximizeBox = false;

        _invoiceAddress.ReadOnly = true;
        _invoiceAddress.BackColor = DeveloperTheme.PaleBlue;
        LoadSettings();
        Controls.Add(BuildLayout());
        Shown += (_, _) => LoadExisting();
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = DeveloperTheme.Bg,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 4
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildContent(), 0, 1);
        root.Controls.Add(BuildActions(), 0, 2);
        _status.BackColor = Color.White;
        _status.Padding = new Padding(12, 0, 12, 0);
        _status.AutoEllipsis = true;
        root.Controls.Add(_status, 0, 3);
        return root;
    }

    private Control BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 8, 20, 8) };
        panel.Paint += (_, e) => DeveloperTheme.Gradient(e, panel.ClientRectangle);
        var logo = new PictureBox
        {
            Image = DeveloperTheme.Logo(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Dock = DockStyle.Left,
            Width = 100,
            BackColor = Color.Transparent
        };
        var text = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding = new Padding(14, 5, 0, 5),
            RowCount = 2
        };
        text.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        text.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        text.Controls.Add(new Label
        {
            Text = "STORE INVOICE INBOX",
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Font = DeveloperTheme.Bold(20),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        text.Controls.Add(new Label
        {
            Text = "DEVELOPER-ONLY PROVISIONING • UNIQUE ADDRESS PER LICENSED STORE",
            ForeColor = DeveloperTheme.Orange,
            BackColor = Color.Transparent,
            Font = DeveloperTheme.Bold(10.5f),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 1);
        panel.Controls.Add(text);
        panel.Controls.Add(logo);
        return panel;
    }

    private Control BuildContent()
    {
        var card = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(24, 18, 24, 18),
            ColumnCount = 1,
            RowCount = 7
        };
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        card.Controls.Add(ReadOnlyLine("CLIENT / BUSINESS", _account.BusinessName), 0, 0);
        card.Controls.Add(ReadOnlyLine("STORE GUID", _account.StoreGuid), 0, 1);
        card.Controls.Add(Field("INVOICE SERVICE URL", _baseUrl), 0, 2);
        card.Controls.Add(Field("WORKER ADMIN SECRET", _adminSecret), 0, 3);
        card.Controls.Add(_rememberSecret, 0, 4);
        card.Controls.Add(Field("STORE INVOICE ADDRESS", _invoiceAddress), 0, 5);
        var note = DeveloperTheme.Label(
            "The client receives only this store's address and store token. The Worker admin secret remains developer-only and is protected with Windows encryption.",
            false,
            DeveloperTheme.Blue);
        note.BackColor = DeveloperTheme.PaleBlue;
        note.Padding = new Padding(12);
        card.Controls.Add(note, 0, 6);
        return card;
    }

    private Control BuildActions()
    {
        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            Padding = new Padding(0, 8, 0, 0)
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 23));
        var copy = DeveloperTheme.Button("COPY INVOICE ADDRESS");
        var close = DeveloperTheme.Button("CLOSE");
        _provision.Click += async (_, _) => await ProvisionAsync();
        copy.Click += (_, _) => CopyAddress();
        close.Click += (_, _) => Close();
        actions.Controls.Add(_provision, 0, 0);
        actions.Controls.Add(copy, 1, 0);
        actions.Controls.Add(close, 2, 0);
        return actions;
    }

    private void LoadSettings()
    {
        var settings = InvoiceInboxDeveloperSettingsStore.Load();
        _baseUrl.Text = string.IsNullOrWhiteSpace(settings.BaseUrl)
            ? InvoiceInboxDeveloperSettings.DefaultBaseUrl
            : settings.BaseUrl;
        try
        {
            _adminSecret.Text = InvoiceInboxCredentialProtector.UnprotectForWindowsUser(
                settings.ProtectedAdminSecret);
        }
        catch
        {
            _adminSecret.Clear();
        }
    }

    private void LoadExisting()
    {
        try
        {
            var existing = _service.LoadInvoiceInboxProvisioning(
                _account.CustomerId, _account.StoreGuid);
            if (existing is null)
                return;
            _invoiceAddress.Text = existing.InvoiceAddress;
            if (!string.IsNullOrWhiteSpace(existing.ApiBaseUrl))
                _baseUrl.Text = existing.ApiBaseUrl;
            SetStatus($"Provisioned for {existing.StoreGuid}. Use REFRESH to verify it with Cloudflare.", false);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
    }

    private async Task ProvisionAsync()
    {
        var adminSecret = _adminSecret.Text.Trim();
        try
        {
            ToggleBusy(true);
            SetStatus("Contacting the private Invoice Inbox service…", false);
            using var api = new InvoiceInboxApiClient(_baseUrl.Text, adminSecret);
            var stores = await api.LoadStoresAsync();
            var remote = stores.FirstOrDefault(x =>
                string.Equals(x.StoreGuid, _account.StoreGuid, StringComparison.OrdinalIgnoreCase));
            var existing = _service.LoadInvoiceInboxProvisioning(
                _account.CustomerId, _account.StoreGuid);
            InvoiceInboxProvisionResult result;

            if (remote is null)
            {
                result = await api.CreateStoreAsync(_account.BusinessName, _account.StoreGuid);
            }
            else if (existing is not null &&
                     string.Equals(existing.WorkerStoreId, remote.Id, StringComparison.Ordinal) &&
                     !string.IsNullOrWhiteSpace(existing.EncryptedStoreApiToken))
            {
                _service.SaveInvoiceInboxProvisioning(existing with
                {
                    StoreGuid = remote.StoreGuid,
                    WorkerStoreId = remote.Id,
                    InvoiceAddress = remote.EmailAddress,
                    ApiBaseUrl = _baseUrl.Text.Trim().TrimEnd('/'),
                    UpdatedUtc = DateTime.UtcNow
                });
                _invoiceAddress.Text = remote.EmailAddress;
                SaveDeveloperSettings(adminSecret);
                SetStatus("Invoice inbox matched to Cloudflare. The existing store token was preserved.", false);
                return;
            }
            else
            {
                var confirm = MessageBox.Show(
                    this,
                    $"An Invoice Inbox already exists for {_account.StoreGuid}, but this Account Manager does not have its store token.\n\n" +
                    "Rotate the token and reconnect this store? The previous store token will stop working.",
                    "Recover Store Invoice Inbox",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (confirm != DialogResult.Yes)
                {
                    SetStatus("No changes were made.", false);
                    return;
                }
                result = await api.RotateStoreTokenAsync(remote);
            }

            var encryptedToken = InvoiceInboxCredentialProtector.ProtectStoreToken(
                result.ApiToken, adminSecret);
            _service.SaveInvoiceInboxProvisioning(new InvoiceInboxProvisioning(
                _account.CustomerId,
                _account.LicenseId,
                result.Store.StoreGuid,
                result.Store.Id,
                result.Store.EmailAddress,
                encryptedToken,
                _baseUrl.Text.Trim().TrimEnd('/'),
                DateTime.UtcNow));
            _invoiceAddress.Text = result.Store.EmailAddress;
            SaveDeveloperSettings(adminSecret);
            SetStatus($"Invoice inbox ready: {result.Store.EmailAddress}", false);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
        finally
        {
            ToggleBusy(false);
        }
    }

    private void SaveDeveloperSettings(string adminSecret)
    {
        InvoiceInboxDeveloperSettingsStore.Save(
            _baseUrl.Text,
            _rememberSecret.Checked ? adminSecret : "");
    }

    private void CopyAddress()
    {
        if (string.IsNullOrWhiteSpace(_invoiceAddress.Text))
        {
            SetStatus("Provision the invoice inbox first.", true);
            return;
        }
        Clipboard.SetText(_invoiceAddress.Text.Trim());
        SetStatus("Store invoice address copied.", false);
    }

    private void ToggleBusy(bool busy)
    {
        UseWaitCursor = busy;
        _provision.Enabled = !busy;
        _baseUrl.Enabled = !busy;
        _adminSecret.Enabled = !busy;
    }

    private void SetStatus(string message, bool error)
    {
        _status.Text = message;
        _status.ForeColor = error ? DeveloperTheme.Red : DeveloperTheme.Green;
    }

    private static Control Field(string label, Control control)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        control.Dock = DockStyle.Fill;
        panel.Controls.Add(DeveloperTheme.Label(label, true), 0, 0);
        panel.Controls.Add(control, 0, 1);
        return panel;
    }

    private static Control ReadOnlyLine(string label, string value)
    {
        var text = DeveloperTheme.TextBox();
        text.ReadOnly = true;
        text.BackColor = DeveloperTheme.PaleBlue;
        text.Text = value;
        return Field(label, text);
    }
}
