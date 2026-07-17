using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace ManagerPaperworkSystem.WinForms;

internal sealed class DeviceActivationForm : Form
{
    private static readonly Color ActivationBackground = Color.FromArgb(220, 234, 247);
    private static readonly Color ActivationCard = Color.FromArgb(235, 244, 252);
    private static readonly Color ActivationInput = Color.White;
    private static readonly Color ActivationBorder = Color.FromArgb(139, 168, 195);
    private static readonly Color ActivationGold = Color.FromArgb(242, 140, 40);
    private static readonly Color ActivationText = Color.FromArgb(18, 50, 79);
    private static readonly Color ActivationMuted = Color.FromArgb(66, 88, 111);
    private readonly TextBox _storeGuid = WinTheme.TextBox();
    private readonly TextBox _storeName = WinTheme.TextBox();
    private readonly TextBox _storeState = WinTheme.TextBox();
    private readonly ComboBox _businessType = new()
    {
        DropDownStyle = ComboBoxStyle.DropDown,
        FlatStyle = FlatStyle.Flat,
        Font = WinTheme.BodyFont(10.5f)
    };
    private readonly TextBox _storeZip = WinTheme.TextBox();
    private readonly TextBox _licenseKey = new()
    {
        Multiline = false,
        ScrollBars = ScrollBars.None,
        WordWrap = false,
        BackColor = Color.Black,
        ForeColor = Color.FromArgb(57, 255, 20),
        BorderStyle = BorderStyle.FixedSingle,
        Font = WinTheme.BoldFont(13.5f)
    };
    private readonly TextBox _serialNumber = WinTheme.TextBox();
    private readonly Label _version = WinTheme.FixedLabel("", false, 10);
    private readonly Label _status = WinTheme.FixedLabel("Ready", false, 10);
    private readonly Button _copyRequest = WinTheme.Button("COPY ACTIVATION DETAILS", true);
    private readonly Button _copyStoreGuid = WinTheme.Button("COPY");
    private readonly Button _copyStoreName = WinTheme.Button("COPY");
    private readonly Button _copyStoreZip = WinTheme.Button("COPY");
    private readonly Button _copySerial = WinTheme.Button("COPY");
    private readonly Button _pasteLicenseKey = WinTheme.Button("PASTE KEY");
    private readonly Button _activate = WinTheme.Button("REGISTER / ACTIVATE", true);
    private readonly Button _exportRequest = WinTheme.Button("SAVE REQUEST FILE");
    private readonly Button _importLicense = WinTheme.Button("IMPORT LICENSE FILE");
    private readonly Button _cancel = WinTheme.Button("CANCEL");
    private string? _pendingLicenseText;
    private bool _settingLicenseDisplay;

    private readonly bool _addingLicensedStore;
    private readonly string[] _requiredExistingDatabases;

    public DeviceActivationForm(bool addingLicensedStore = false)
    {
        _addingLicensedStore = addingLicensedStore;
        _requiredExistingDatabases = addingLicensedStore
            ? LicensedBusinessService.Load()
                .Select(x => x.DatabaseName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();
        WinTheme.Apply(this);
        Text = addingLicensedStore
            ? "HISAB KITAB - Add Licensed Store"
            : "HISAB KITAB - License Registration (Store Level)";
        BackColor = ActivationBackground;
        ForeColor = ActivationText;
        Size = new Size(760, 920);
        MinimumSize = new Size(680, 780);
        FormBorderStyle = FormBorderStyle.Sizable;

        var identity = DeviceLicenseService.GetOrCreateIdentity();
        _serialNumber.Text = identity.DeviceId;
        _serialNumber.ReadOnly = true;
        _version.Text = Application.ProductVersion.Split('+')[0];
        var initial = LoadInitialStoreDetails();
        if (addingLicensedStore)
            initial = new InitialStoreDetails("", initial.State, initial.BusinessType, "");
        _storeName.Text = initial.StoreName;
        _storeState.Text = initial.State;
        _storeZip.Text = initial.Zip;
        _businessType.Items.AddRange(new object[] { "TBC", "LIQ" });
        _businessType.Text = initial.BusinessType;
        UpdateFormattedStoreGuid();

        if (addingLicensedStore)
        {
            _activate.Text = "ADD / UPDATE LICENSED STORES";
            _copyRequest.Text = "COPY REQUEST FOR UPDATED LICENSE";
        }

        Controls.Add(BuildLayout());
        _copyRequest.Click += (_, _) => CopyActivationRequest();
        _copyStoreGuid.Click += (_, _) => CopyStoreGuid();
        _copyStoreName.Click += (_, _) => CopyField(_storeName.Text, "Store Name");
        _copyStoreZip.Click += (_, _) => CopyField(_storeZip.Text, "Store ZIP");
        _copySerial.Click += (_, _) => CopyProtectedPcId();
        _pasteLicenseKey.Click += (_, _) => PasteLicenseKey();
        _activate.Click += (_, _) => ActivateFromLicenseKey();
        _exportRequest.Click += (_, _) => ExportRequestFile();
        _importLicense.Click += (_, _) => ImportLicenseFile();
        _cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        _storeName.TextChanged += (_, _) => UpdateFormattedStoreGuid();
        _storeState.TextChanged += (_, _) => UpdateFormattedStoreGuid();
        _storeZip.TextChanged += (_, _) => UpdateFormattedStoreGuid();
        _businessType.TextChanged += (_, _) => UpdateFormattedStoreGuid();
        _licenseKey.TextChanged += (_, _) =>
        {
            if (!_settingLicenseDisplay)
                _pendingLicenseText = null;
        };
        Shown += (_, _) => _storeName.Focus();
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = ActivationBackground,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(24, 18, 24, 18),
            AutoScroll = true
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 322));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 280));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 160));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = ActivationBackground,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty
        };
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        header.Controls.Add(new Label
        {
            Text = "HISAB KITAB WORKS",
            Dock = DockStyle.Fill,
            ForeColor = ActivationGold,
            Font = WinTheme.HeaderFont(17),
            TextAlign = ContentAlignment.BottomCenter,
            BackColor = Color.Transparent
        }, 0, 0);
        header.Controls.Add(new Label
        {
            Text = "LICENSE ACTIVATION",
            Dock = DockStyle.Fill,
            ForeColor = ActivationMuted,
            Font = WinTheme.BodyFont(9.5f),
            TextAlign = ContentAlignment.TopCenter,
            BackColor = Color.Transparent
        }, 0, 1);
        root.Controls.Add(header, 0, 0);

        root.Controls.Add(BuildRegistrationCard(), 0, 1);
        root.Controls.Add(BuildLicenseCard(), 0, 2);

        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = ActivationBackground,
            ColumnCount = 2,
            RowCount = 5,
            Margin = new Padding(0, 8, 0, 0)
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 6));
        actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 6));
        actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        StyleActivationButton(_copyRequest, ActivationGold, Color.White, true);
        _copyRequest.Text = _addingLicensedStore
            ? "COPY NEW STORE + PROTECTED PC REQUEST"
            : "COPY PROTECTED PC REQUEST";
        _copyRequest.Dock = DockStyle.Fill;
        _copyRequest.Margin = Padding.Empty;
        actions.Controls.Add(_copyRequest, 0, 0);
        actions.SetColumnSpan(_copyRequest, 2);

        StyleActivationButton(_activate, Color.FromArgb(37, 99, 235), Color.White, true);
        _activate.Text = _addingLicensedStore ? "ADD LICENSED STORE" : "ACTIVATE LICENSE";
        _activate.Dock = DockStyle.Fill;
        _activate.Margin = new Padding(0, 0, 4, 0);
        actions.Controls.Add(_activate, 0, 2);
        _cancel.Text = "CLOSE";
        StyleActivationButton(_cancel, Color.FromArgb(185, 207, 226), ActivationText, false);
        _cancel.Dock = DockStyle.Fill;
        _cancel.Margin = new Padding(4, 0, 0, 0);
        actions.Controls.Add(_cancel, 1, 2);

        StyleActivationButton(_exportRequest, ActivationCard, ActivationText, false);
        _exportRequest.Text = "SAVE REQUEST";
        _exportRequest.Dock = DockStyle.Fill;
        _exportRequest.Margin = new Padding(0, 0, 4, 0);
        actions.Controls.Add(_exportRequest, 0, 4);
        StyleActivationButton(_importLicense, ActivationCard, ActivationText, false);
        _importLicense.Text = "IMPORT LICENSE";
        _importLicense.Dock = DockStyle.Fill;
        _importLicense.Margin = new Padding(4, 0, 4, 0);
        actions.Controls.Add(_importLicense, 1, 4);
        root.Controls.Add(actions, 0, 3);
        return root;
    }

    private Control BuildRegistrationCard()
    {
        var card = ActivationPanel(18);
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(0, 0, 0, 8);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = ActivationCard, ColumnCount = 1, RowCount = 9 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(Title("STORE INFORMATION"), 0, 0);
        AddStoreGuidField(layout, 1);
        AddCopyField(layout, "STORE NAME *", _storeName, _copyStoreName, 3);
        layout.Controls.Add(BuildStateAndBusinessType(), 0, 5);
        AddCopyField(layout, "STORE ZIP *", _storeZip, _copyStoreZip, 6);
        layout.Controls.Add(Description("Copy the store details and this PC ID into the developer License Generator, or use the protected PC request below."), 0, 8);

        card.Controls.Add(layout);
        return card;
    }

    private Control BuildLicenseCard()
    {
        var card = ActivationPanel(18);
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(0, 8, 0, 0);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = ActivationCard,
            ColumnCount = 2,
            RowCount = 5
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.Controls.Add(Title(_addingLicensedStore ? "ADD STORE WITH UPDATED LICENSE" : "LICENSE REGISTRATION"), 0, 0);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 0)!, 2);
        layout.Controls.Add(BuildApplicationIdentity(), 0, 1);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 1)!, 2);
        var licenseCaption = Caption(_addingLicensedStore
            ? "UPDATED LICENSE KEY *  —  EXISTING STORES MUST REMAIN INCLUDED"
            : "LICENSE KEY *  —  PASTE THE GENERATED KEY IN THE BLACK BOX BELOW");
        licenseCaption.Dock = DockStyle.Fill;
        layout.Controls.Add(licenseCaption, 0, 2);
        StyleActivationButton(_pasteLicenseKey, Color.FromArgb(37, 99, 235), Color.White, true);
        _pasteLicenseKey.Dock = DockStyle.Fill;
        _pasteLicenseKey.Margin = new Padding(6, 0, 0, 2);
        _pasteLicenseKey.Font = WinTheme.BoldFont(8.5f);
        layout.Controls.Add(_pasteLicenseKey, 1, 2);
        StyleActivationTextBox(_licenseKey);
        _licenseKey.BackColor = Color.Black;
        _licenseKey.ForeColor = Color.FromArgb(57, 255, 20);
        _licenseKey.Font = WinTheme.BoldFont(13.5f);
        _licenseKey.Multiline = false;
        _licenseKey.ScrollBars = ScrollBars.None;
        _licenseKey.WordWrap = false;
        _licenseKey.Dock = DockStyle.Fill;
        _licenseKey.Margin = new Padding(0, 2, 0, 4);
        _licenseKey.PlaceholderText = "Paste the HKLIC2 License Key here...";
        layout.Controls.Add(_licenseKey, 0, 3);
        layout.SetColumnSpan(_licenseKey, 2);
        _status.Dock = DockStyle.Fill;
        _status.ForeColor = ActivationMuted;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(_status, 0, 4);
        layout.SetColumnSpan(_status, 2);
        card.Controls.Add(layout);
        return card;
    }

    private void AddStoreGuidField(TableLayoutPanel layout, int row)
    {
        layout.Controls.Add(Caption("STORE GUID / DATABASE NAME *"), 0, row);
        var fieldRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = ActivationCard,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        fieldRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fieldRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        _storeGuid.Dock = DockStyle.Fill;
        StyleActivationTextBox(_storeGuid);
        _storeGuid.ReadOnly = true;
        _storeGuid.BackColor = Color.Black;
        _storeGuid.ForeColor = Color.FromArgb(255, 235, 59);
        _storeGuid.Font = WinTheme.BoldFont(10.5f);
        _storeGuid.Margin = new Padding(0, 0, 6, 0);
        _copyStoreGuid.Dock = DockStyle.Fill;
        _copyStoreGuid.Margin = Padding.Empty;
        _copyStoreGuid.Padding = Padding.Empty;
        StyleActivationButton(_copyStoreGuid, ActivationGold, Color.White, true);
        _copyStoreGuid.Font = WinTheme.BoldFont(8.5f);
        fieldRow.Controls.Add(_storeGuid, 0, 0);
        fieldRow.Controls.Add(_copyStoreGuid, 1, 0);
        layout.Controls.Add(fieldRow, 0, row + 1);
    }

    private Control BuildStateAndBusinessType()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = ActivationCard,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        row.Controls.Add(Caption("STATE *"), 0, 0);
        row.Controls.Add(Caption("BUSINESS TYPE *  (TBC / LIQ)"), 1, 0);
        StyleActivationTextBox(_storeState);
        _storeState.CharacterCasing = CharacterCasing.Upper;
        _storeState.MaxLength = 2;
        _storeState.Dock = DockStyle.Fill;
        _storeState.Margin = new Padding(0, 0, 5, 0);
        _businessType.BackColor = ActivationInput;
        _businessType.ForeColor = ActivationText;
        _businessType.Dock = DockStyle.Fill;
        _businessType.Margin = new Padding(5, 0, 0, 0);
        row.Controls.Add(_storeState, 0, 1);
        row.Controls.Add(_businessType, 1, 1);
        return row;
    }

    private static void AddCopyField(TableLayoutPanel layout, string caption, TextBox input, Button copyButton, int row)
    {
        layout.Controls.Add(Caption(caption), 0, row);
        var fieldRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = ActivationCard,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        fieldRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fieldRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        StyleActivationTextBox(input);
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 0, 6, 0);
        StyleActivationButton(copyButton, Color.FromArgb(37, 99, 235), Color.White, true);
        copyButton.Dock = DockStyle.Fill;
        copyButton.Margin = Padding.Empty;
        copyButton.Font = WinTheme.BoldFont(8.5f);
        fieldRow.Controls.Add(input, 0, 0);
        fieldRow.Controls.Add(copyButton, 1, 0);
        layout.Controls.Add(fieldRow, 0, row + 1);
    }

    private Control BuildApplicationIdentity()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = ActivationInput,
            ColumnCount = 5,
            RowCount = 2,
            Padding = new Padding(10, 5, 10, 5),
            Margin = Padding.Empty,
            CellBorderStyle = TableLayoutPanelCellBorderStyle.Single
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        panel.Controls.Add(IdentityCaption("LICENSE FOR"), 0, 0);
        panel.Controls.Add(IdentityValue("HISAB KITAB WORKS", ActivationGold), 1, 0);
        panel.SetColumnSpan(panel.GetControlFromPosition(1, 0)!, 4);
        panel.Controls.Add(IdentityCaption("PC ID"), 0, 1);
        _serialNumber.Dock = DockStyle.Fill;
        _serialNumber.ForeColor = Color.FromArgb(0, 112, 48);
        _serialNumber.BackColor = Color.FromArgb(194, 255, 197);
        _serialNumber.BorderStyle = BorderStyle.None;
        _serialNumber.TextAlign = HorizontalAlignment.Left;
        panel.Controls.Add(_serialNumber, 1, 1);
        StyleActivationButton(_copySerial, Color.FromArgb(37, 99, 235), Color.White, true);
        _copySerial.Dock = DockStyle.Fill;
        _copySerial.Margin = new Padding(3, 0, 3, 0);
        _copySerial.Font = WinTheme.BoldFont(8);
        panel.Controls.Add(_copySerial, 2, 1);
        panel.Controls.Add(IdentityCaption("VERSION"), 3, 1);
        _version.Dock = DockStyle.Fill;
        _version.ForeColor = Color.FromArgb(96, 165, 250);
        _version.BackColor = ActivationInput;
        _version.TextAlign = ContentAlignment.MiddleCenter;
        panel.Controls.Add(_version, 4, 1);
        return panel;
    }

    private static Label IdentityCaption(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = ActivationMuted,
        Font = WinTheme.BoldFont(8.5f),
        TextAlign = ContentAlignment.MiddleLeft,
        BackColor = ActivationInput
    };

    private static Label IdentityValue(string text, Color color) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = color,
        Font = WinTheme.BoldFont(11),
        TextAlign = ContentAlignment.MiddleLeft,
        BackColor = ActivationInput
    };

    private static void AddField(TableLayoutPanel layout, string caption, TextBox input, int row)
    {
        layout.Controls.Add(Caption(caption), 0, row);
        StyleActivationTextBox(input);
        input.Dock = DockStyle.Fill;
        input.Margin = Padding.Empty;
        layout.Controls.Add(input, 0, row + 1);
    }

    private void CopyActivationRequest()
    {
        try
        {
            var requestText = DeviceLicenseService.CreateRequestText(
                _storeName.Text, _storeGuid.Text, _storeZip.Text, _storeState.Text, _businessType.Text,
                CurrentSubscriptionKey());
            Clipboard.SetText(requestText);
            SetStatus("Activation request copied. Send or paste it into the HISAB KITAB WORKS License Generator.", false);
            MessageBox.Show(this, "The complete activation request was copied to the clipboard.", "Request Copied", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
    }

    private void CopyStoreGuid()
    {
        var storeGuid = _storeGuid.Text.Trim();
        if (string.IsNullOrWhiteSpace(storeGuid))
        {
            SetStatus("The Store GUID/database name could not be detected. Enter the database name first.", true);
            return;
        }

        Clipboard.SetText(storeGuid);
        SetStatus($"Store GUID copied: {storeGuid}", false);
    }

    private void CopyField(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            SetStatus($"{fieldName} is empty.", true);
            return;
        }
        Clipboard.SetText(value.Trim());
        SetStatus($"{fieldName} copied.", false);
    }

    private void CopyProtectedPcId()
    {
        try
        {
            var requestText = DeviceLicenseService.CreateRequestText(
                _storeName.Text, _storeGuid.Text, _storeZip.Text, _storeState.Text, _businessType.Text,
                CurrentSubscriptionKey());
            Clipboard.SetText(requestText);
            SetStatus("Protected PC ID copied. Paste it into the PC ID field in the developer License Generator.", false);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
    }

    private void PasteLicenseKey()
    {
        try
        {
            var value = Clipboard.GetText().Trim();
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("The clipboard does not contain a License Key.");
            _pendingLicenseText = value;
            var displayKey = ActivationCodeCodec.TryExtractDisplayLicenseKey(value);
            _settingLicenseDisplay = true;
            _licenseKey.Text = displayKey ?? value;
            _settingLicenseDisplay = false;
            _licenseKey.Focus();
            _licenseKey.SelectionStart = _licenseKey.TextLength;
            SetStatus("License Key pasted. Click ACTIVATE LICENSE.", false);
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
            var activationText = _pendingLicenseText ?? _licenseKey.Text;
            if (string.IsNullOrWhiteSpace(activationText))
                throw new InvalidOperationException("Paste the License Key first.");
            if (activationText.Trim().StartsWith("HKL-", StringComparison.OrdinalIgnoreCase) &&
                !activationText.Contains("HKLIC2-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The short License Key identifies the license, but offline activation also needs its protected code. Use COPY LICENSE KEY in the generator and PASTE KEY here, or import the license file.");
            var result = DeviceLicenseService.InstallLicenseCode(
                activationText,
                _storeName.Text,
                _storeGuid.Text,
                _storeZip.Text,
                _requiredExistingDatabases,
                _addingLicensedStore);
            if (result.Status != DeviceLicenseStatus.Valid)
            {
                SetStatus(result.Message, true);
                return;
            }
            var displayKey = result.Payload is null ? "" : ActivationCodeCodec.DisplayLicenseKey(result.Payload);
            MessageBox.Show(this,
                $"{result.Message}\r\n\r\nLicense Key: {displayKey}",
                "License Activated", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
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
            DeviceLicenseService.ExportRequest(
                dialog.FileName, _storeName.Text, _storeGuid.Text, _storeZip.Text, _storeState.Text, _businessType.Text,
                CurrentSubscriptionKey());
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
            var result = DeviceLicenseService.InstallLicense(
                dialog.FileName,
                _requiredExistingDatabases,
                _addingLicensedStore ? _storeGuid.Text : "",
                _addingLicensedStore ? _storeName.Text : "");
            if (result.Status != DeviceLicenseStatus.Valid)
            {
                SetStatus(result.Message, true);
                return;
            }
            MessageBox.Show(this, result.Message, "License Activated", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
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
        ForeColor = ActivationGold,
        Font = WinTheme.BoldFont(11),
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static Label Description(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = ActivationText,
        Font = WinTheme.BodyFont(10.5f),
        TextAlign = ContentAlignment.TopLeft
    };

    private static Label Caption(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = ActivationMuted,
        Font = WinTheme.BoldFont(8.5f),
        TextAlign = ContentAlignment.BottomLeft
    };

    private static Panel ActivationPanel(int padding)
    {
        var panel = new Panel
        {
            BackColor = ActivationCard,
            Padding = new Padding(padding)
        };
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(ActivationBorder);
            e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
        };
        return panel;
    }

    private static void StyleActivationTextBox(TextBox input)
    {
        input.BackColor = ActivationInput;
        input.ForeColor = ActivationText;
        input.BorderStyle = BorderStyle.FixedSingle;
        input.Font = WinTheme.BodyFont(10.5f);
    }

    private static void StyleActivationButton(Button button, Color background, Color foreground, bool filled)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = background;
        button.ForeColor = foreground;
        button.Font = WinTheme.BoldFont(9.5f);
        button.Padding = new Padding(6, 0, 6, 0);
        button.UseVisualStyleBackColor = false;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = filled ? background : ActivationBorder;
        button.FlatAppearance.MouseOverBackColor = filled
            ? ControlPaint.Light(background, 0.08f)
            : Color.FromArgb(31, 34, 46);
        button.FlatAppearance.MouseDownBackColor = filled
            ? ControlPaint.Dark(background, 0.08f)
            : ActivationInput;
    }

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

    private string CurrentSubscriptionKey()
        => _addingLicensedStore
            ? LicenseRuntime.CurrentLicense?.LicenseKey ?? TryReadLicenseValue("LicenseKey")
            : "";

    private void UpdateFormattedStoreGuid()
    {
        _storeGuid.Text = StoreGuidFormat.Create(_storeState.Text, _storeName.Text, _businessType.Text, _storeZip.Text);
    }

    private static InitialStoreDetails LoadInitialStoreDetails()
    {
        var existingGuid = TryReadLicenseValue("StoreGuid");
        var businessName = TryReadLicenseValue("BusinessName");
        var zip = TryReadLicenseValue("StoreZip");
        var state = TryReadLicenseValue("StoreState");
        var businessType = TryReadLicenseValue("BusinessType");

        if (string.IsNullOrWhiteSpace(existingGuid))
        {
            var configuredDatabase = TryReadConfiguredDatabaseName();
            var pending = TryReadPendingStoreDetails();
            if (!string.IsNullOrWhiteSpace(configuredDatabase))
            {
                var configuredName = BusinessNameFromDatabase(configuredDatabase);
                if (!string.IsNullOrWhiteSpace(configuredName))
                    businessName = configuredName;
            }
            if (!string.IsNullOrWhiteSpace(pending.StoreName) &&
                (string.IsNullOrWhiteSpace(businessName) || NamesMatch(pending.StoreName, businessName)))
            {
                if (string.IsNullOrWhiteSpace(businessName))
                    businessName = pending.StoreName;
                zip = pending.Zip;
            }
        }

        if (StoreGuidFormat.IsValid(existingGuid))
        {
            var parts = existingGuid.Split('_');
            state = parts[0];
            businessType = parts[2];
            zip = parts[3];
        }

        return new InitialStoreDetails(
            string.IsNullOrWhiteSpace(businessName) ? "" : businessName.Trim(),
            string.IsNullOrWhiteSpace(state) ? "IL" : state.Trim().ToUpperInvariant(),
            string.IsNullOrWhiteSpace(businessType) ? "TBC" : businessType.Trim().ToUpperInvariant(),
            zip.Trim());
    }

    private static string TryReadConfiguredDatabaseName()
    {
        try
        {
            if (!File.Exists(AppBootstrap.ConnectionSettingsPath))
                return "";

            var settings = JsonSerializer.Deserialize<DatabaseConnectionSettings>(File.ReadAllText(AppBootstrap.ConnectionSettingsPath));
            if (!string.IsNullOrWhiteSpace(settings?.Database))
                return settings.Database.Trim();
            if (!string.IsNullOrWhiteSpace(settings?.ConnectionString))
                return new SqlConnectionStringBuilder(settings.ConnectionString).InitialCatalog.Trim();
        }
        catch
        {
            // A missing or older settings file should not block license registration.
        }

        return "";
    }

    private static InitialStoreDetails TryReadPendingStoreDetails()
    {
        try
        {
            var path = Path.Combine(AppBootstrap.AppDataPath, "pending_store_info.json");
            if (!File.Exists(path))
                return new InitialStoreDetails("", "", "", "");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return new InitialStoreDetails(
                root.TryGetProperty("StoreName", out var name) ? name.GetString() ?? "" : "",
                "",
                "",
                root.TryGetProperty("StoreZip", out var pendingZip) ? pendingZip.GetString() ?? "" : "");
        }
        catch { return new InitialStoreDetails("", "", "", ""); }
    }

    private static string BusinessNameFromDatabase(string databaseName)
    {
        if (StoreGuidFormat.IsValid(databaseName))
            return databaseName.Split('_')[1];
        foreach (var prefix in new[] { "HisabKitab_", "HisabWorks_", "HBStoreLedger_" })
            if (databaseName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return databaseName[prefix.Length..].Replace('_', ' ').Trim();
        return "";
    }

    private static bool NamesMatch(string? left, string? right)
        => NormalizeName(left).Equals(NormalizeName(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeName(string? value)
        => new((value ?? "").Where(char.IsLetterOrDigit).ToArray());

    private sealed record InitialStoreDetails(string StoreName, string State, string BusinessType, string Zip);

    private static string SafeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "HISAB_KITAB" : value.Replace(' ', '_');
    }
}
