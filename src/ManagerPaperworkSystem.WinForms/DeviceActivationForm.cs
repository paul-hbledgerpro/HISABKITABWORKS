using System.Text.Json;

namespace ManagerPaperworkSystem.WinForms;

internal sealed class DeviceActivationForm : Form
{
    private readonly TextBox _businessName = WinTheme.TextBox();
    private readonly TextBox _subscriptionKey = WinTheme.TextBox();
    private readonly Label _deviceId = WinTheme.FixedLabel("", false, 12, true);
    private readonly Label _deviceName = WinTheme.FixedLabel("", false, 10);
    private readonly Label _status = WinTheme.FixedLabel("Ready", false, 10);
    private readonly Button _exportRequest = WinTheme.Button("1. EXPORT PC REQUEST", true);
    private readonly Button _importLicense = WinTheme.Button("2. IMPORT DEVICE LICENSE", true);
    private readonly Button _cancel = WinTheme.Button("CANCEL");

    public DeviceActivationForm()
    {
        WinTheme.Apply(this);
        Text = "HISAB KITAB - Device License Activation";
        Size = new Size(980, 820);
        MinimumSize = new Size(860, 760);
        FormBorderStyle = FormBorderStyle.Sizable;

        var identity = DeviceLicenseService.GetOrCreateIdentity();
        _deviceId.Text = identity.DeviceId;
        _deviceName.Text = Environment.MachineName;
        _businessName.Text = TryReadLegacyBusinessName();
        _subscriptionKey.Text = TryReadLegacySubscriptionKey();

        Controls.Add(BuildLayout());
        _exportRequest.Click += (_, _) => ExportRequest();
        _importLicense.Click += (_, _) => ImportLicense();
        _cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        Shown += (_, _) => _businessName.Focus();
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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));

        var header = new Panel { Dock = DockStyle.Fill, BackColor = WinTheme.BlueDark, Padding = new Padding(36, 18, 36, 14) };
        header.Paint += (_, e) => WinTheme.PaintGradient(e, header.ClientRectangle);
        var heading = new Label
        {
            Text = "DEVICE LICENSE ACTIVATION",
            Dock = DockStyle.Top,
            Height = 46,
            ForeColor = Color.White,
            Font = WinTheme.HeaderFont(23),
            BackColor = Color.Transparent
        };
        var subtitle = new Label
        {
            Text = "Every computer requires its own signed license file.",
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Font = WinTheme.BodyFont(11),
            BackColor = Color.Transparent
        };
        header.Controls.Add(subtitle);
        header.Controls.Add(heading);
        root.Controls.Add(header, 0, 0);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Bg,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(22, 20, 22, 8)
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        body.Controls.Add(BuildRequestCard(), 0, 0);
        body.Controls.Add(BuildImportCard(), 1, 0);
        root.Controls.Add(body, 0, 1);

        var statusCard = WinTheme.BorderedPanel(16);
        statusCard.Dock = DockStyle.Fill;
        statusCard.Margin = new Padding(22, 6, 22, 8);
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
            Padding = new Padding(22, 6, 22, 8)
        };
        _cancel.Width = 150;
        _cancel.Height = 44;
        footer.Controls.Add(_cancel);
        root.Controls.Add(footer, 0, 3);
        return root;
    }

    private Control BuildRequestCard()
    {
        var card = WinTheme.BorderedPanel(22);
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(0, 0, 10, 0);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, ColumnCount = 1, RowCount = 10 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.Controls.Add(Title("STEP 1 - CREATE PC REQUEST"), 0, 0);
        layout.Controls.Add(Description("Enter the client account name shown on your subscription, then send the exported .hbrequest file to your software provider."), 0, 1);
        layout.Controls.Add(Caption("CLIENT ACCOUNT NAME"), 0, 2);
        _businessName.Dock = DockStyle.Fill;
        layout.Controls.Add(_businessName, 0, 3);
        layout.Controls.Add(Caption("SUBSCRIPTION KEY"), 0, 4);
        _subscriptionKey.Dock = DockStyle.Fill;
        _subscriptionKey.PlaceholderText = "HBL-XXXX-XXXX-XXXX";
        layout.Controls.Add(_subscriptionKey, 0, 5);
        layout.Controls.Add(Caption("THIS COMPUTER"), 0, 6);
        var computer = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel2, ColumnCount = 1, RowCount = 2, Padding = new Padding(10, 2, 10, 2) };
        _deviceName.Dock = DockStyle.Fill;
        _deviceId.Dock = DockStyle.Fill;
        _deviceId.ForeColor = WinTheme.Blue;
        computer.Controls.Add(_deviceName, 0, 0);
        computer.Controls.Add(_deviceId, 0, 1);
        layout.Controls.Add(computer, 0, 7);
        _exportRequest.Dock = DockStyle.Fill;
        _exportRequest.Margin = new Padding(0, 8, 0, 0);
        layout.Controls.Add(_exportRequest, 0, 9);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildImportCard()
    {
        var card = WinTheme.BorderedPanel(22);
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(10, 0, 0, 0);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.Controls.Add(Title("STEP 2 - IMPORT LICENSE"), 0, 0);
        layout.Controls.Add(Description("After payment and approval, import the .hblicense file created specifically for this computer."), 0, 1);
        layout.Controls.Add(new Label
        {
            Text = "The license will be rejected if it was copied from another PC, modified, revoked, or expired.",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Muted,
            Font = WinTheme.BodyFont(11),
            TextAlign = ContentAlignment.MiddleCenter
        }, 0, 2);
        _importLicense.Dock = DockStyle.Fill;
        layout.Controls.Add(_importLicense, 0, 4);
        card.Controls.Add(layout);
        return card;
    }

    private void ExportRequest()
    {
        try
        {
            var business = _businessName.Text.Trim();
            if (string.IsNullOrWhiteSpace(business))
            {
                SetStatus("Enter the client account name first.", true);
                _businessName.Focus();
                return;
            }

            using var dialog = new SaveFileDialog
            {
                Title = "Export PC License Request",
                Filter = "HISAB KITAB PC Request (*.hbrequest)|*.hbrequest",
                FileName = $"{SafeFileName(business)}_{Environment.MachineName}.hbrequest",
                AddExtension = true,
                DefaultExt = ".hbrequest"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            DeviceLicenseService.ExportRequest(dialog.FileName, business, _subscriptionKey.Text);
            SetStatus($"PC request exported successfully: {dialog.FileName}", false);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
    }

    private void ImportLicense()
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
            MessageBox.Show(this, result.Message, "Device Activated", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

    private static string TryReadLegacyBusinessName()
    {
        try
        {
            if (!File.Exists(AppBootstrap.LicenseFilePath))
                return "";
            using var document = JsonDocument.Parse(File.ReadAllText(AppBootstrap.LicenseFilePath));
            return document.RootElement.TryGetProperty("BusinessName", out var name) ? name.GetString() ?? "" : "";
        }
        catch
        {
            return "";
        }
    }

    private static string TryReadLegacySubscriptionKey()
    {
        try
        {
            if (!File.Exists(AppBootstrap.LicenseFilePath))
                return "";
            using var document = JsonDocument.Parse(File.ReadAllText(AppBootstrap.LicenseFilePath));
            return document.RootElement.TryGetProperty("LicenseKey", out var key) ? key.GetString() ?? "" : "";
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
        return value.Replace(' ', '_');
    }
}
