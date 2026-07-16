using System.Text.Json;

namespace ManagerPaperworkSystem.WinForms;

internal sealed class DeviceActivationForm : Form
{
    private readonly TextBox _storeGuid = WinTheme.TextBox();
    private readonly TextBox _storeName = WinTheme.TextBox();
    private readonly TextBox _storeZip = WinTheme.TextBox();
    private readonly TextBox _licenseKey = new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        WordWrap = true,
        BackColor = Color.White,
        ForeColor = WinTheme.Text,
        BorderStyle = BorderStyle.FixedSingle,
        Font = WinTheme.BodyFont(10.5f)
    };
    private readonly Label _serialNumber = WinTheme.FixedLabel("", false, 11, true);
    private readonly Label _version = WinTheme.FixedLabel("", false, 10);
    private readonly Label _status = WinTheme.FixedLabel("Ready", false, 10);
    private readonly Button _copyRequest = WinTheme.Button("COPY ACTIVATION DETAILS", true);
    private readonly Button _activate = WinTheme.Button("REGISTER / ACTIVATE", true);
    private readonly Button _exportRequest = WinTheme.Button("SAVE REQUEST FILE");
    private readonly Button _importLicense = WinTheme.Button("IMPORT LICENSE FILE");
    private readonly Button _cancel = WinTheme.Button("CANCEL");

    public DeviceActivationForm()
    {
        WinTheme.Apply(this);
        Text = "HISAB KITAB - License Registration (Store Level)";
        Size = new Size(800, 860);
        MinimumSize = new Size(720, 780);
        FormBorderStyle = FormBorderStyle.Sizable;

        var identity = DeviceLicenseService.GetOrCreateIdentity();
        _serialNumber.Text = identity.DeviceId;
        _version.Text = Application.ProductVersion.Split('+')[0];
        _storeName.Text = TryReadLicenseValue("BusinessName");
        _storeGuid.Text = TryReadLicenseValue("StoreGuid");
        _storeZip.Text = TryReadLicenseValue("StoreZip");

        Controls.Add(BuildLayout());
        _copyRequest.Click += (_, _) => CopyActivationRequest();
        _activate.Click += (_, _) => ActivateFromLicenseKey();
        _exportRequest.Click += (_, _) => ExportRequestFile();
        _importLicense.Click += (_, _) => ImportLicenseFile();
        _cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        Shown += (_, _) => _storeGuid.Focus();
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Bg,
            ColumnCount = 1,
            RowCount = 4,
            Padding = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));

        var header = new Panel { Dock = DockStyle.Fill, BackColor = WinTheme.BlueDark, Padding = new Padding(28, 14, 28, 10) };
        header.Paint += (_, e) => WinTheme.PaintGradient(e, header.ClientRectangle);
        header.Controls.Add(new Label
        {
            Text = "STORE LEVEL  •  CUSTOMER ACTIVATION",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(222, 235, 248),
            Font = WinTheme.BoldFont(9.5f),
            BackColor = Color.Transparent
        });
        header.Controls.Add(new Label
        {
            Text = "LICENSE REGISTRATION",
            Dock = DockStyle.Top,
            Height = 48,
            ForeColor = Color.White,
            Font = WinTheme.HeaderFont(22),
            BackColor = Color.Transparent
        });
        root.Controls.Add(header, 0, 0);

        var body = new Panel { Dock = DockStyle.Fill, BackColor = WinTheme.Bg, Padding = new Padding(24, 18, 24, 6) };
        body.Controls.Add(BuildRegistrationCard());
        root.Controls.Add(body, 0, 1);

        var statusCard = WinTheme.BorderedPanel(16);
        statusCard.Dock = DockStyle.Fill;
        statusCard.Margin = new Padding(24, 5, 24, 7);
        _status.Dock = DockStyle.Fill;
        _status.ForeColor = WinTheme.Muted;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        statusCard.Controls.Add(_status);
        root.Controls.Add(statusCard, 0, 2);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Bg,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(24, 5, 24, 7)
        };
        _cancel.Text = "CLOSE";
        _cancel.Width = 148;
        _cancel.Height = 42;
        footer.Controls.Add(_cancel);
        root.Controls.Add(footer, 0, 3);
        return root;
    }

    private Control BuildRegistrationCard()
    {
        var card = WinTheme.BorderedPanel(20);
        card.Dock = DockStyle.Fill;
        card.Margin = Padding.Empty;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, ColumnCount = 1, RowCount = 17 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        for (var index = 0; index < 3; index++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        }
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 98));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        layout.Controls.Add(Title("STORE REGISTRATION DETAILS"), 0, 0);
        layout.Controls.Add(Description("Copy these details to your software provider. Paste the License Key they return below."), 0, 1);
        AddField(layout, "STORE GUID / DATABASE NAME *", _storeGuid, 2);
        AddField(layout, "STORE NAME *", _storeName, 4);
        AddField(layout, "ZIP CODE *", _storeZip, 6);
        layout.Controls.Add(BuildApplicationIdentity(), 0, 9);

        layout.Controls.Add(Caption("LICENSE KEY *"), 0, 11);
        _licenseKey.Dock = DockStyle.Fill;
        _licenseKey.PlaceholderText = "Paste the HKLIC2 License Key here...";
        layout.Controls.Add(_licenseKey, 0, 12);

        var primaryActions = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, ColumnCount = 2, Margin = Padding.Empty };
        primaryActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        primaryActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _copyRequest.Dock = DockStyle.Fill;
        _copyRequest.Margin = new Padding(0, 0, 5, 0);
        _activate.Dock = DockStyle.Fill;
        _activate.Margin = new Padding(5, 0, 0, 0);
        primaryActions.Controls.Add(_copyRequest, 0, 0);
        primaryActions.Controls.Add(_activate, 1, 0);
        layout.Controls.Add(primaryActions, 0, 14);

        var backupActions = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, ColumnCount = 2, Margin = Padding.Empty };
        backupActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        backupActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _exportRequest.Text = "SAVE REQUEST FILE";
        _exportRequest.Dock = DockStyle.Fill;
        _exportRequest.Margin = new Padding(0, 0, 5, 0);
        _importLicense.Text = "IMPORT LICENSE FILE";
        _importLicense.Dock = DockStyle.Fill;
        _importLicense.Margin = new Padding(5, 0, 0, 0);
        backupActions.Controls.Add(_exportRequest, 0, 0);
        backupActions.Controls.Add(_importLicense, 1, 0);
        layout.Controls.Add(backupActions, 0, 16);

        card.Controls.Add(layout);
        return card;
    }

    private Control BuildApplicationIdentity()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Panel2,
            ColumnCount = 4,
            RowCount = 2,
            Padding = new Padding(12, 8, 12, 8),
            Margin = Padding.Empty,
            CellBorderStyle = TableLayoutPanelCellBorderStyle.Single
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        panel.Controls.Add(IdentityCaption("LICENSE FOR"), 0, 0);
        panel.Controls.Add(IdentityValue("HISAB KITAB WORKS", WinTheme.Copper), 1, 0);
        panel.SetColumnSpan(panel.GetControlFromPosition(1, 0)!, 3);
        panel.Controls.Add(IdentityCaption("APP SERIAL"), 0, 1);
        _serialNumber.Dock = DockStyle.Fill;
        _serialNumber.ForeColor = WinTheme.Green;
        _serialNumber.TextAlign = ContentAlignment.MiddleLeft;
        panel.Controls.Add(_serialNumber, 1, 1);
        panel.Controls.Add(IdentityCaption("VERSION"), 2, 1);
        _version.Dock = DockStyle.Fill;
        _version.ForeColor = WinTheme.Blue;
        _version.TextAlign = ContentAlignment.MiddleCenter;
        panel.Controls.Add(_version, 3, 1);
        return panel;
    }

    private static Label IdentityCaption(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = WinTheme.Muted,
        Font = WinTheme.BoldFont(8.5f),
        TextAlign = ContentAlignment.MiddleLeft,
        BackColor = WinTheme.Panel2
    };

    private static Label IdentityValue(string text, Color color) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = color,
        Font = WinTheme.BoldFont(11),
        TextAlign = ContentAlignment.MiddleLeft,
        BackColor = WinTheme.Panel2
    };

    private static void AddField(TableLayoutPanel layout, string caption, TextBox input, int row)
    {
        layout.Controls.Add(Caption(caption), 0, row);
        input.Dock = DockStyle.Fill;
        input.Margin = Padding.Empty;
        layout.Controls.Add(input, 0, row + 1);
    }

    private void CopyActivationRequest()
    {
        try
        {
            var requestText = DeviceLicenseService.CreateRequestText(_storeName.Text, _storeGuid.Text, _storeZip.Text);
            Clipboard.SetText(requestText);
            SetStatus("Activation request copied. Send or paste it into the HISAB KITAB WORKS License Generator.", false);
            MessageBox.Show(this, "The complete activation request was copied to the clipboard.", "Request Copied", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
    }

    private void ActivateFromLicenseKey()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_storeGuid.Text) || string.IsNullOrWhiteSpace(_storeName.Text) || string.IsNullOrWhiteSpace(_storeZip.Text))
                throw new InvalidOperationException("Enter the Store GUID, Store Name and ZIP code before activating.");
            if (string.IsNullOrWhiteSpace(_licenseKey.Text))
                throw new InvalidOperationException("Paste the License Key first.");
            var result = DeviceLicenseService.InstallLicenseCode(_licenseKey.Text, _storeName.Text, _storeGuid.Text, _storeZip.Text);
            if (result.Status != DeviceLicenseStatus.Valid)
            {
                SetStatus(result.Message, true);
                return;
            }
            MessageBox.Show(this, result.Message, "License Activated", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
    }

    private void ExportRequestFile()
    {
        try
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Save PC Activation Request",
                Filter = "HISAB KITAB PC Request (*.hbrequest)|*.hbrequest",
                FileName = $"{SafeFileName(_storeName.Text)}_{Environment.MachineName}.hbrequest",
                AddExtension = true,
                DefaultExt = ".hbrequest"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            DeviceLicenseService.ExportRequest(dialog.FileName, _storeName.Text, _storeGuid.Text, _storeZip.Text);
            SetStatus($"Request file saved: {dialog.FileName}", false);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
    }

    private void ImportLicenseFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import Device License",
            Filter = "HISAB KITAB Device License (*.hblicense)|*.hblicense"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            var result = DeviceLicenseService.InstallLicense(dialog.FileName);
            if (result.Status != DeviceLicenseStatus.Valid)
            {
                SetStatus(result.Message, true);
                return;
            }
            MessageBox.Show(this, result.Message, "License Activated", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
    }

    private void SetStatus(string text, bool error)
    {
        _status.Text = text;
        _status.ForeColor = error ? WinTheme.Red : WinTheme.Green;
    }

    private static Label Title(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = WinTheme.Copper,
        Font = WinTheme.BoldFont(13),
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static Label Description(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = WinTheme.Text,
        Font = WinTheme.BodyFont(10.5f),
        TextAlign = ContentAlignment.TopLeft
    };

    private static Label Caption(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = WinTheme.Muted,
        Font = WinTheme.BoldFont(9),
        TextAlign = ContentAlignment.BottomLeft
    };

    private static string TryReadLicenseValue(string propertyName)
    {
        try
        {
            if (!File.Exists(AppBootstrap.LicenseFilePath))
                return "";
            using var document = JsonDocument.Parse(File.ReadAllText(AppBootstrap.LicenseFilePath));
            return document.RootElement.TryGetProperty(propertyName, out var value) ? value.GetString() ?? "" : "";
        }
        catch
        {
            return "";
        }
    }

    private static string SafeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "HISAB_KITAB" : value.Replace(' ', '_');
    }
}
