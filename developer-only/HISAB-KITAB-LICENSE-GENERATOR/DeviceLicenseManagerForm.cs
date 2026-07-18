using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace HisabKitabWorks.LicenseGenerator.WinForms;

internal sealed class DeviceLicenseManagerForm : Form
{
    private readonly string _licensingConnectionString;
    private readonly string _server;
    private readonly string _username;
    private readonly string _password;
    private readonly bool _importRequestOnOpen;
    private readonly string? _expectedBusinessName;
    private readonly string? _expectedSubscriptionKey;
    private readonly Button _importRequest = AdminTheme.Button("PASTE PROTECTED PC REQUEST", true);
    private readonly Button _issueLicense = AdminTheme.Button("GENERATE / RENEW LICENSE KEY", true);
    private readonly Button _manageBusinesses = AdminTheme.Button("MANAGE BUSINESSES");
    private readonly Button _revokeDevice = AdminTheme.Button("REVOKE SELECTED PC");
    private readonly Button _refresh = AdminTheme.Button("REFRESH");
    private readonly TextBox _business = AdminTheme.TextBox();
    private readonly TextBox _storeGuid = AdminTheme.TextBox();
    private readonly TextBox _storeZip = AdminTheme.TextBox();
    private readonly TextBox _storeState = AdminTheme.TextBox();
    private readonly TextBox _businessType = AdminTheme.TextBox();
    private readonly TextBox _deviceId = AdminTheme.TextBox();
    private readonly Label _status = AdminTheme.Label("Paste the customer's activation details to begin.", AdminTheme.Muted, 10);
    private readonly NumericUpDown _maxDevices = new() { Minimum = 1, Maximum = 999, Value = 1, Font = AdminTheme.Body(11) };
    private readonly NumericUpDown _maxBusinesses = new() { Minimum = 1, Maximum = 999, Value = 1, Font = AdminTheme.Body(11) };
    private readonly DateTimePicker _expires = new()
    {
        Format = DateTimePickerFormat.Short,
        Font = AdminTheme.Body(11),
        MinDate = DateTime.Today,
        MaxDate = DateTime.Today.AddYears(5),
        Value = DateTime.Today.AddMonths(1)
    };
    private readonly DataGridView _devices = new();
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private DeviceLicenseRequestV2? _request;
    private SubscriptionRecord? _subscription;
    private List<DeviceRecord> _deviceRows = new();
    private bool _legacyExpiryAdjusted;
    private bool _loadingDevices;

    public DeviceLicenseManagerForm(
        string licensingConnectionString,
        string server,
        string username,
        string password,
        bool importRequestOnOpen = false,
        string? expectedBusinessName = null,
        string? expectedSubscriptionKey = null)
    {
        _licensingConnectionString = licensingConnectionString;
        _server = server;
        _username = username;
        _password = password;
        _importRequestOnOpen = importRequestOnOpen;
        _expectedBusinessName = string.IsNullOrWhiteSpace(expectedBusinessName) ? null : expectedBusinessName.Trim();
        _expectedSubscriptionKey = string.IsNullOrWhiteSpace(expectedSubscriptionKey) ? null : expectedSubscriptionKey.Trim();

        Text = "HISAB KITAB WORKS - Developer PC Activation";
        BackColor = AdminTheme.Bg;
        ForeColor = AdminTheme.Text;
        Font = AdminTheme.Body();
        Icon = AdminTheme.LoadIcon();
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        Size = new Size(1280, 900);
        MinimumSize = new Size(1120, 760);
        WindowState = FormWindowState.Normal;
        Controls.Add(BuildLayout());
        ConfigureGrid();

        _importRequest.Click += (_, _) => ImportRequest();
        _issueLicense.Click += (_, _) => IssueLicense();
        _manageBusinesses.Click += (_, _) => ManageBusinesses();
        _revokeDevice.Click += (_, _) => RevokeSelectedDevice();
        _refresh.Click += (_, _) => RefreshDevices();
        _devices.SelectionChanged += (_, _) =>
        {
            if (!_loadingDevices)
                LoadSelectedDevice();
        };
        Shown += (_, _) =>
        {
            InitializeSchema();
            if (_importRequestOnOpen)
                BeginInvoke(ImportRequest);
        };
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = AdminTheme.Bg,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(18)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 354));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));

        var header = new Panel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, Padding = new Padding(24, 10, 24, 10) };
        header.Paint += (_, e) => AdminTheme.PaintGradient(e, header.ClientRectangle);
        header.Controls.Add(new Label
        {
            Text = "DEVELOPER PC ACTIVATION",
            Dock = DockStyle.Top,
            Height = 40,
            ForeColor = Color.White,
            Font = AdminTheme.Header(21),
            BackColor = Color.Transparent
        });
        header.Controls.Add(new Label
        {
            Text = "Verify the protected customer request • renew the same PC • replace a PC • or use another paid seat",
            Dock = DockStyle.Bottom,
            Height = 24,
            ForeColor = AdminTheme.Copper,
            Font = AdminTheme.Body(10.5f),
            BackColor = Color.Transparent
        });
        root.Controls.Add(header, 0, 0);

        var top = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Bg, ColumnCount = 2, Padding = new Padding(0, 12, 0, 10) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        top.Controls.Add(BuildRequestCard(), 0, 0);
        top.Controls.Add(BuildSubscriptionCard(), 1, 0);
        root.Controls.Add(top, 0, 1);

        var gridCard = AdminTheme.Card();
        gridCard.Dock = DockStyle.Fill;
        gridCard.Padding = new Padding(12);
        var gridLayout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, RowCount = 2 };
        gridLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        gridLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var gridTitle = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 2 };
        gridTitle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        gridTitle.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        var title = AdminTheme.Label("REGISTERED COMPUTERS", AdminTheme.Copper, 11, true);
        title.Dock = DockStyle.Fill;
        _refresh.Dock = DockStyle.Fill;
        gridTitle.Controls.Add(title, 0, 0);
        gridTitle.Controls.Add(_refresh, 1, 0);
        gridLayout.Controls.Add(gridTitle, 0, 0);
        _devices.Dock = DockStyle.Fill;
        gridLayout.Controls.Add(_devices, 0, 1);
        gridCard.Controls.Add(gridLayout);
        root.Controls.Add(gridCard, 0, 2);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Bg, ColumnCount = 4, Padding = new Padding(0, 10, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _revokeDevice.Dock = DockStyle.Fill;
        _manageBusinesses.Dock = DockStyle.Fill;
        _issueLicense.Dock = DockStyle.Fill;
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(_revokeDevice, 1, 0);
        footer.Controls.Add(_manageBusinesses, 2, 0);
        footer.Controls.Add(_issueLicense, 3, 0);
        root.Controls.Add(footer, 0, 3);
        return root;
    }

    private Control BuildRequestCard()
    {
        var card = AdminTheme.Card(AdminTheme.CopperDark);
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(0, 0, 8, 0);
        card.Padding = new Padding(18, 12, 18, 12);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 2, RowCount = 10 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var requestHeading = AdminTheme.Label("CUSTOMER ACTIVATION DETAILS", AdminTheme.Copper, 10.5f, true);
        requestHeading.Dock = DockStyle.Fill;
        layout.Controls.Add(requestHeading, 0, 0);
        layout.SetColumnSpan(requestHeading, 2);
        AddRequestField(layout, "STORE GUID / DATABASE NAME", _storeGuid, 1, 0, 2);
        AddRequestField(layout, "STORE NAME", _business, 3, 0, 2);
        AddRequestField(layout, "STATE", _storeState, 5, 0);
        AddRequestField(layout, "BUSINESS TYPE", _businessType, 5, 1);
        AddRequestField(layout, "STORE ZIP", _storeZip, 7, 0);
        AddRequestField(layout, "PC ID (THIS COMPUTER)", _deviceId, 7, 1);
        _importRequest.Dock = DockStyle.Fill;
        _importRequest.Margin = new Padding(0, 6, 0, 5);
        layout.Controls.Add(_importRequest, 0, 9);
        layout.SetColumnSpan(_importRequest, 2);
        card.Controls.Add(layout);
        return card;
    }

    private static void AddRequestField(TableLayoutPanel layout, string caption, TextBox field, int row, int column, int span = 1)
    {
        var label = AdminTheme.Label(caption, AdminTheme.Muted, 8.5f, true);
        label.Dock = DockStyle.Fill;
        field.Dock = DockStyle.Fill;
        label.Margin = column == 0 ? new Padding(0, 0, span == 1 ? 8 : 0, 0) : new Padding(8, 0, 0, 0);
        field.Margin = column == 0 ? new Padding(0, 0, span == 1 ? 8 : 0, 0) : new Padding(8, 0, 0, 0);
        layout.Controls.Add(label, column, row);
        layout.Controls.Add(field, column, row + 1);
        if (span > 1)
        {
            layout.SetColumnSpan(label, span);
            layout.SetColumnSpan(field, span);
        }
    }

    private Control BuildSubscriptionCard()
    {
        var card = AdminTheme.Card();
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(8, 0, 0, 0);
        card.Padding = new Padding(18, 12, 18, 12);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 3, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = AdminTheme.Label("LICENSE & SUBSCRIPTION", AdminTheme.Copper, 10.5f, true);
        heading.Dock = DockStyle.Fill;
        layout.Controls.Add(heading, 0, 0);
        layout.SetColumnSpan(heading, 3);
        var instructions = AdminTheme.Label("1  Paste protected request\r\n2  Confirm the paid limits\r\n3  Generate the PC activation key", AdminTheme.BlueDark, 9.5f, true);
        instructions.Dock = DockStyle.Fill;
        layout.Controls.Add(instructions, 0, 1);
        layout.SetColumnSpan(instructions, 3);
        var seatsLabel = AdminTheme.Label("PAID PC SEATS", AdminTheme.Muted, 8.5f, true);
        var businessesLabel = AdminTheme.Label("APPROVED BUSINESSES", AdminTheme.Muted, 8.5f, true);
        var expiresLabel = AdminTheme.Label("SUBSCRIPTION EXPIRES", AdminTheme.Muted, 8.5f, true);
        seatsLabel.Dock = DockStyle.Fill;
        businessesLabel.Dock = DockStyle.Fill;
        expiresLabel.Dock = DockStyle.Fill;
        layout.Controls.Add(seatsLabel, 0, 2);
        layout.Controls.Add(businessesLabel, 1, 2);
        layout.Controls.Add(expiresLabel, 2, 2);
        _maxDevices.Dock = DockStyle.Top;
        _maxBusinesses.Dock = DockStyle.Top;
        _expires.Dock = DockStyle.Top;
        layout.Controls.Add(_maxDevices, 0, 3);
        layout.Controls.Add(_maxBusinesses, 1, 3);
        layout.Controls.Add(_expires, 2, 3);
        var note = AdminTheme.Label("A matching PC ID renews the same seat. A new PC ID will ask whether to replace a PC or use another paid seat.", AdminTheme.Muted, 9);
        note.Dock = DockStyle.Fill;
        layout.Controls.Add(note, 0, 4);
        layout.SetColumnSpan(note, 3);
        card.Controls.Add(layout);
        return card;
    }

    private void ConfigureGrid()
    {
        _devices.BackgroundColor = Color.White;
        _devices.ForeColor = AdminTheme.Text;
        _devices.GridColor = AdminTheme.Panel2;
        _devices.BorderStyle = BorderStyle.None;
        _devices.ReadOnly = true;
        _devices.AllowUserToAddRows = false;
        _devices.AllowUserToDeleteRows = false;
        _devices.MultiSelect = false;
        _devices.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _devices.RowHeadersVisible = false;
        _devices.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _devices.EnableHeadersVisualStyles = false;
        _devices.ColumnHeadersDefaultCellStyle.BackColor = AdminTheme.Copper;
        _devices.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _devices.ColumnHeadersDefaultCellStyle.Font = AdminTheme.Bold(9);
        _devices.DefaultCellStyle.BackColor = AdminTheme.Panel;
        _devices.DefaultCellStyle.ForeColor = AdminTheme.Text;
        _devices.DefaultCellStyle.SelectionBackColor = AdminTheme.Panel2;
        _devices.DefaultCellStyle.SelectionForeColor = AdminTheme.BlueDark;
        _devices.Columns.Add("DeviceName", "Computer");
        _devices.Columns.Add("DeviceId", "PC ID");
        _devices.Columns.Add("Status", "Status");
        _devices.Columns.Add("Activated", "Activated");
        _devices.Columns.Add("Expires", "Expires");
    }

    private void InitializeSchema()
    {
        try
        {
            using var connection = new SqlConnection(_licensingConnectionString);
            connection.Open();
            using var command = new SqlCommand(@"
IF COL_LENGTH('dbo.Licenses', 'MaxDevices') IS NULL
    ALTER TABLE dbo.Licenses ADD MaxDevices INT NOT NULL CONSTRAINT DF_Licenses_MaxDevices DEFAULT(1);

IF OBJECT_ID('dbo.LicenseDevices', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LicenseDevices
    (
        Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        CustomerId INT NOT NULL,
        LicenseId INT NOT NULL,
        DeviceId NVARCHAR(64) NOT NULL,
        InstallationId NVARCHAR(64) NOT NULL,
        DeviceName NVARCHAR(200) NOT NULL,
        DevicePublicKey NVARCHAR(MAX) NOT NULL,
        FingerprintHash NVARCHAR(128) NOT NULL,
        Status NVARCHAR(20) NOT NULL,
        ActivatedDate DATETIME2 NOT NULL,
        ExpiresDate DATETIME2 NOT NULL,
        LastLicenseIssuedDate DATETIME2 NOT NULL,
        RevokedDate DATETIME2 NULL,
        Notes NVARCHAR(500) NULL
    );
    CREATE UNIQUE INDEX UX_LicenseDevices_DeviceId ON dbo.LicenseDevices(DeviceId);
    CREATE INDEX IX_LicenseDevices_LicenseId_Status ON dbo.LicenseDevices(LicenseId, Status);
END

IF OBJECT_ID('dbo.CustomerBusinesses', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerBusinesses
    (
        Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        CustomerId INT NOT NULL,
        BusinessName NVARCHAR(200) NOT NULL,
        StoreAddress NVARCHAR(400) NULL,
        DatabaseName NVARCHAR(128) NOT NULL,
        IsPrimary BIT NOT NULL CONSTRAINT DF_CustomerBusinesses_IsPrimary DEFAULT(0),
        IsActive BIT NOT NULL CONSTRAINT DF_CustomerBusinesses_IsActive DEFAULT(1),
        CreatedUtc DATETIME2 NOT NULL CONSTRAINT DF_CustomerBusinesses_CreatedUtc DEFAULT(SYSUTCDATETIME())
    );
    CREATE UNIQUE INDEX UX_CustomerBusinesses_Customer_Database
        ON dbo.CustomerBusinesses(CustomerId, DatabaseName);
END

IF OBJECT_ID('dbo.DeviceLicenseIssueHistory', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DeviceLicenseIssueHistory
    (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        ActivationId NVARCHAR(64) NOT NULL,
        CustomerId INT NOT NULL,
        LicenseId INT NOT NULL,
        DeviceId NVARCHAR(64) NOT NULL,
        StoreGuid NVARCHAR(128) NOT NULL,
        BusinessName NVARCHAR(200) NOT NULL,
        StoreZip NVARCHAR(20) NOT NULL,
        ActivationKey NVARCHAR(MAX) NOT NULL,
        IssuedUtc DATETIME2 NOT NULL CONSTRAINT DF_DeviceLicenseIssueHistory_IssuedUtc DEFAULT(SYSUTCDATETIME()),
        ExpiresDate DATETIME2 NOT NULL,
        IssuedByComputer NVARCHAR(200) NOT NULL,
        IssueAction NVARCHAR(30) NOT NULL CONSTRAINT DF_DeviceLicenseIssueHistory_IssueAction DEFAULT('Issued'),
        ReplacedDeviceId NVARCHAR(64) NULL
    );
    CREATE UNIQUE INDEX UX_DeviceLicenseIssueHistory_ActivationId
        ON dbo.DeviceLicenseIssueHistory(ActivationId);
    CREATE INDEX IX_DeviceLicenseIssueHistory_License_Device
        ON dbo.DeviceLicenseIssueHistory(LicenseId, DeviceId, IssuedUtc DESC);
END

IF COL_LENGTH('dbo.DeviceLicenseIssueHistory', 'IssueAction') IS NULL
    ALTER TABLE dbo.DeviceLicenseIssueHistory ADD IssueAction NVARCHAR(30) NOT NULL
        CONSTRAINT DF_DeviceLicenseIssueHistory_IssueAction_Legacy DEFAULT('Issued');
IF COL_LENGTH('dbo.DeviceLicenseIssueHistory', 'ReplacedDeviceId') IS NULL
    ALTER TABLE dbo.DeviceLicenseIssueHistory ADD ReplacedDeviceId NVARCHAR(64) NULL;", connection);
            command.ExecuteNonQuery();
            SetStatus("Device licensing database is ready.", false);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not prepare device licensing: {ex.Message}", true);
            _issueLicense.Enabled = false;
        }
    }

    private void ImportRequest()
    {
        var pastedRequest = ActivationCodeDialog.PromptForRequest(this);
        if (pastedRequest is null)
            return;
        try
        {
            var request = ActivationCodeCodec.DecodeRequest(pastedRequest);
            DeviceRequestValidator.Validate(request);
            if (_expectedBusinessName is not null &&
                !string.Equals(request.BusinessName, _expectedBusinessName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"This PC request is for '{request.BusinessName}', but the selected customer is '{_expectedBusinessName}'. " +
                    "Copy a new activation request using the selected customer's exact store name.");
            if (_expectedSubscriptionKey is not null && !string.IsNullOrWhiteSpace(request.SubscriptionKey) &&
                !string.Equals(request.SubscriptionKey, _expectedSubscriptionKey, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The PC request subscription key does not match the customer selected on the main License Generator screen.");
            ValidatePastedField(_storeGuid.Text, request.StoreGuid, "Store GUID");
            ValidatePastedField(_business.Text, request.BusinessName, "Store Name");
            ValidatePastedField(_storeZip.Text, request.StoreZip, "Store ZIP");
            ValidatePastedField(_storeState.Text, request.StoreState, "State");
            ValidatePastedField(_businessType.Text, request.BusinessType, "Business Type");
            ValidatePastedField(_deviceId.Text, request.DeviceId, "PC ID");
            _request = request;
            _business.Text = request.BusinessName;
            _storeGuid.Text = request.StoreGuid;
            _storeZip.Text = request.StoreZip;
            _storeState.Text = request.StoreState;
            _businessType.Text = request.BusinessType;
            _deviceId.Text = request.DeviceId;
            LoadSubscription(request.BusinessName, request.StoreGuid, request.SubscriptionKey);
            if (_subscription is null || !string.Equals(_subscription.DatabaseName, request.StoreGuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The Store GUID does not match the database assigned to this customer subscription.");
            var existingPc = _deviceRows.Any(x => string.Equals(x.DeviceId, request.DeviceId, StringComparison.Ordinal));
            SetStatus(_legacyExpiryAdjusted
                ? "Activation request verified. The old 100-year expiry was replaced with a one-month renewal date. Confirm it, then generate the License Key."
                : existingPc
                    ? "Protected request verified. This PC ID is already registered, so generating a key will renew the same paid seat."
                    : "Protected request verified. This is a new PC ID; generating a key will ask whether to replace a PC or use another paid seat.", false);
        }
        catch (Exception ex)
        {
            _request = null;
            _subscription = null;
            SetStatus(ex.Message, true);
        }
    }

    private static void ValidatePastedField(string enteredValue, string protectedValue, string fieldName)
    {
        if (!string.IsNullOrWhiteSpace(enteredValue) &&
            !string.Equals(enteredValue.Trim(), protectedValue.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The manually entered {fieldName} does not match the protected PC request.");
    }

    private void LoadSubscription(string businessName, string storeGuid, string subscriptionKey)
    {
        using var connection = new SqlConnection(_licensingConnectionString);
        connection.Open();
        using var command = new SqlCommand(@"
SELECT c.Id CustomerId, l.Id LicenseId, c.BusinessName, l.LicenseKey,
       l.AssignedDatabases, l.MaxStores, l.MaxUsers, l.MaxDevices, l.ExpiresDate, l.EnabledServices,
       l.MonthlyReportEmail, l.MonthlyReportDay
FROM dbo.Customers c
INNER JOIN dbo.Licenses l ON l.CustomerId = c.Id
WHERE c.BusinessName = @business
  AND (l.AssignedDatabases = @storeGuid OR (@licenseKey <> '' AND l.LicenseKey = @licenseKey))
  AND l.IsActive = 1
ORDER BY l.Id DESC", connection);
        command.Parameters.AddWithValue("@business", businessName);
        command.Parameters.AddWithValue("@storeGuid", storeGuid);
        command.Parameters.AddWithValue("@licenseKey", subscriptionKey);
        using var reader = command.ExecuteReader();
        var matches = new List<SubscriptionRecord>();
        while (reader.Read())
        {
            matches.Add(new SubscriptionRecord(
                reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? "" : reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6),
                reader.GetInt32(7), reader.GetDateTime(8), reader.IsDBNull(9) ? "Accounting" : reader.GetString(9),
                reader.IsDBNull(10) ? "" : reader.GetString(10),
                reader.IsDBNull(11) ? 3 : Convert.ToInt32(reader.GetByte(11))));
        }
        if (matches.Count == 0)
            throw new InvalidOperationException($"No active customer subscription was found for '{businessName}'. Generate the customer license first.");
        _subscription = matches[0];
        EnsurePrimaryBusiness(_subscription);
        _maxDevices.Value = Math.Clamp(_subscription.MaxDevices, 1, 999);
        _maxBusinesses.Value = Math.Clamp(_subscription.MaxStores, 1, 999);
        var storedExpiry = _subscription.ExpiresDate.Date;
        _legacyExpiryAdjusted = storedExpiry > DateTime.Today.AddYears(5);
        var proposedExpiry = storedExpiry < DateTime.Today || _legacyExpiryAdjusted
            ? DateTime.Today.AddMonths(1)
            : storedExpiry;
        _expires.Value = proposedExpiry > _expires.MaxDate ? _expires.MaxDate : proposedExpiry;
        RefreshDevices();
    }

    private void EnsurePrimaryBusiness(SubscriptionRecord subscription)
    {
        using var connection = new SqlConnection(_licensingConnectionString);
        connection.Open();
        using var command = new SqlCommand(@"
IF NOT EXISTS (SELECT 1 FROM dbo.CustomerBusinesses WHERE CustomerId=@customerId AND IsPrimary=1)
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.CustomerBusinesses WHERE CustomerId=@customerId AND DatabaseName=@database)
        UPDATE dbo.CustomerBusinesses
        SET BusinessName=@business, IsPrimary=1, IsActive=1
        WHERE CustomerId=@customerId AND DatabaseName=@database;
    ELSE
        INSERT dbo.CustomerBusinesses
            (CustomerId, BusinessName, StoreAddress, DatabaseName, IsPrimary, IsActive, CreatedUtc)
        VALUES
            (@customerId, @business, '', @database, 1, 1, SYSUTCDATETIME());
END", connection);
        command.Parameters.AddWithValue("@customerId", subscription.CustomerId);
        command.Parameters.AddWithValue("@business", subscription.BusinessName);
        command.Parameters.AddWithValue("@database", subscription.DatabaseName);
        command.ExecuteNonQuery();
    }

    private void RefreshDevices()
    {
        if (_subscription is null)
            return;
        try
        {
            using var connection = new SqlConnection(_licensingConnectionString);
            connection.Open();
            using var command = new SqlCommand(@"
SELECT Id, DeviceId, InstallationId, DeviceName, DevicePublicKey, FingerprintHash,
       Status, ActivatedDate, ExpiresDate, LastLicenseIssuedDate
FROM dbo.LicenseDevices WHERE LicenseId = @licenseId ORDER BY Status, DeviceName", connection);
            command.Parameters.AddWithValue("@licenseId", _subscription.LicenseId);
            using var reader = command.ExecuteReader();
            _deviceRows = new List<DeviceRecord>();
            while (reader.Read())
            {
                _deviceRows.Add(new DeviceRecord(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.GetDateTime(7), reader.GetDateTime(8), reader.GetDateTime(9)));
            }

            _loadingDevices = true;
            try
            {
                _devices.Rows.Clear();
                foreach (var device in _deviceRows)
                {
                    var index = _devices.Rows.Add(device.DeviceName, device.DeviceId, device.Status,
                        device.ActivatedDate.ToString("MM/dd/yyyy"), device.ExpiresDate.ToString("MM/dd/yyyy"));
                    _devices.Rows[index].Tag = device;
                }
            }
            finally
            {
                _loadingDevices = false;
            }
            var seatsInUse = _deviceRows.Count(x =>
                string.Equals(x.Status, "Active", StringComparison.OrdinalIgnoreCase) &&
                x.ExpiresDate.ToUniversalTime() > DateTime.UtcNow);
            SetStatus($"{seatsInUse} of {_maxDevices.Value:0} paid PC seats are assigned for the current subscription period.", false);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
    }

    private void LoadSelectedDevice()
    {
        if (_devices.SelectedRows.Count == 0 || _devices.SelectedRows[0].Tag is not DeviceRecord selected || _subscription is null)
            return;
        _request = new DeviceLicenseRequestV2
        {
            Version = 2,
            BusinessName = _subscription.BusinessName,
            SubscriptionKey = _subscription.LicenseKey,
            StoreGuid = _subscription.DatabaseName,
            StoreZip = "",
            StoreState = "",
            BusinessType = "",
            AppVersion = "",
            DeviceId = selected.DeviceId,
            InstallationId = selected.InstallationId,
            DeviceName = selected.DeviceName,
            DevicePublicKey = selected.DevicePublicKey,
            FingerprintHash = selected.FingerprintHash
        };
        _business.Text = _subscription.BusinessName;
        _storeGuid.Text = _subscription.DatabaseName;
        _storeZip.Text = "";
        _storeState.Text = "";
        _businessType.Text = "";
        _deviceId.Text = selected.DeviceId;
    }

    private void IssueLicense()
    {
        if (_subscription is null || _request is null)
        {
            SetStatus("Paste an activation request or select an existing registered computer first.", true);
            return;
        }
        if (!SigningKeyStore.IsConfigured)
        {
            SetStatus("One-time signing setup is required. Close this window, select Set Up / Restore Key, then paste the activation request again.", true);
            MessageBox.Show(this,
                "One-time signing setup is required on this developer PC.\r\n\r\n" +
                "1. Close Developer PC Activation.\r\n" +
                "2. Select SET UP / RESTORE KEY on the main generator.\r\n" +
                "3. Restore the encrypted signing-key backup.\r\n" +
                "4. Paste the activation request again and generate the License Key.",
                "Signing Setup Required", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_expires.Value.Date < DateTime.Today)
        {
            SetStatus("Select a future subscription expiration date.", true);
            return;
        }

        try
        {
            ValidatePastedField(_storeGuid.Text, _request.StoreGuid, "Store GUID");
            ValidatePastedField(_business.Text, _request.BusinessName, "Store Name");
            ValidatePastedField(_storeZip.Text, _request.StoreZip, "Store ZIP");
            ValidatePastedField(_storeState.Text, _request.StoreState, "State");
            ValidatePastedField(_businessType.Text, _request.BusinessType, "Business Type");
            ValidatePastedField(_deviceId.Text, _request.DeviceId, "PC ID");
            var seatChoice = ChoosePcSeatAction(_request, (int)_maxDevices.Value);
            if (seatChoice is null)
            {
                SetStatus("PC activation was cancelled. No license key was generated and no database record was changed.", false);
                return;
            }
            var expiresUtc = DateTime.SpecifyKind(_expires.Value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Local).ToUniversalTime();
            var payload = BuildPayload(_subscription, _request, (int)_maxDevices.Value, (int)_maxBusinesses.Value, expiresUtc);
            var licenseJson = BuildSignedLicense(payload);
            var formattedLicense = ActivationCodeCodec.FormatLicense(payload, licenseJson);
            RegisterDeviceAndUpdateSubscription(
                _subscription, _request, (int)_maxDevices.Value, (int)_maxBusinesses.Value, expiresUtc,
                payload.ActivationId, formattedLicense, seatChoice);
            ActivationCodeDialog.ShowLicense(
                this,
                formattedLicense,
                licenseJson,
                $"{SafeFileName(_subscription.BusinessName)}_{SafeFileName(_request.DeviceName)}.hblicense");
            SetStatus("License Key generated and copied. Paste it into the customer's License Registration window.", false);
            RefreshDevices();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
    }

    private PcSeatChoice? ChoosePcSeatAction(DeviceLicenseRequestV2 request, int maximumSeats)
    {
        if (_deviceRows.Any(x => string.Equals(x.DeviceId, request.DeviceId, StringComparison.Ordinal)))
            return new PcSeatChoice(PcSeatAction.RenewSamePc);

        var assigned = _deviceRows
            .Where(x => string.Equals(x.Status, "Active", StringComparison.OrdinalIgnoreCase) &&
                        x.ExpiresDate.ToUniversalTime() > DateTime.UtcNow)
            .Select(x => new RegisteredPcOption(x.DeviceId, x.DeviceName, x.Status, x.ExpiresDate))
            .ToList();
        if (assigned.Count == 0)
            return new PcSeatChoice(PcSeatAction.FirstPc);

        return PcSeatDecisionForm.Choose(this, request.DeviceId, request.DeviceName, assigned, maximumSeats);
    }

    private void ManageBusinesses()
    {
        if (_subscription is null)
        {
            SetStatus("Paste an activation request or select the client subscription first.", true);
            return;
        }

        EnsurePrimaryBusiness(_subscription);
        using var form = new CustomerBusinessesForm(
            _licensingConnectionString,
            _subscription.CustomerId,
            _subscription.BusinessName,
            (int)_maxBusinesses.Value);
        form.ShowDialog(this);
        SetStatus("Business directory updated. Issue or renew the PC license to include the current approved list.", false);
    }

    private void RegisterDeviceAndUpdateSubscription(
        SubscriptionRecord subscription,
        DeviceLicenseRequestV2 request,
        int maxDevices,
        int maxBusinesses,
        DateTime expiresUtc,
        string activationId,
        string activationKey,
        PcSeatChoice seatChoice)
    {
        using var connection = new SqlConnection(_licensingConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        using (var owner = new SqlCommand(
                   "SELECT LicenseId FROM dbo.LicenseDevices WITH (UPDLOCK, HOLDLOCK) WHERE DeviceId=@deviceId",
                   connection, transaction))
        {
            owner.Parameters.AddWithValue("@deviceId", request.DeviceId);
            var existingLicenseId = owner.ExecuteScalar();
            if (existingLicenseId is not null && existingLicenseId != DBNull.Value &&
                Convert.ToInt32(existingLicenseId) != subscription.LicenseId)
                throw new InvalidOperationException(
                    "This protected PC ID is already registered to a different client subscription. Review that account before continuing.");
        }

        if (seatChoice.Action == PcSeatAction.ReplacePc)
        {
            using var replace = new SqlCommand(@"
UPDATE dbo.LicenseDevices
SET Status='Replaced', RevokedDate=SYSUTCDATETIME(), Notes='Replaced by PC ID ' + @newDeviceId
WHERE LicenseId=@licenseId AND DeviceId=@replacedDeviceId", connection, transaction);
            replace.Parameters.AddWithValue("@newDeviceId", request.DeviceId);
            replace.Parameters.AddWithValue("@licenseId", subscription.LicenseId);
            replace.Parameters.AddWithValue("@replacedDeviceId", seatChoice.ReplacedPcId ?? "");
            if (replace.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The selected old PC could not be replaced. Refresh the registered PC list and try again.");
        }

        using var count = new SqlCommand(@"
SELECT COUNT(*) FROM dbo.LicenseDevices WITH (UPDLOCK, HOLDLOCK)
WHERE LicenseId = @licenseId AND Status='Active'
  AND ExpiresDate > SYSUTCDATETIME() AND DeviceId <> @deviceId", connection, transaction);
        count.Parameters.AddWithValue("@licenseId", subscription.LicenseId);
        count.Parameters.AddWithValue("@deviceId", request.DeviceId);
        var otherActive = Convert.ToInt32(count.ExecuteScalar());
        if (otherActive >= maxDevices)
            throw new InvalidOperationException($"All {maxDevices} paid PC seats are already in use. Revoke an old PC or increase the paid seat limit.");

        using var updateLicense = new SqlCommand(@"
UPDATE dbo.Licenses SET MaxDevices = @maxDevices, MaxStores = @maxBusinesses, ExpiresDate = @expires
WHERE Id = @licenseId AND IsActive = 1", connection, transaction);
        updateLicense.Parameters.AddWithValue("@maxDevices", maxDevices);
        updateLicense.Parameters.AddWithValue("@maxBusinesses", maxBusinesses);
        updateLicense.Parameters.AddWithValue("@expires", expiresUtc);
        updateLicense.Parameters.AddWithValue("@licenseId", subscription.LicenseId);
        if (updateLicense.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("The subscription is no longer active.");

        using var upsert = new SqlCommand(@"
IF EXISTS (SELECT 1 FROM dbo.LicenseDevices WHERE DeviceId = @deviceId)
BEGIN
    UPDATE dbo.LicenseDevices SET CustomerId=@customerId, LicenseId=@licenseId,
        InstallationId=@installationId, DeviceName=@deviceName, DevicePublicKey=@publicKey,
        FingerprintHash=@fingerprint, Status='Active', ExpiresDate=@expires,
        LastLicenseIssuedDate=SYSUTCDATETIME(), RevokedDate=NULL
    WHERE DeviceId=@deviceId;
END
ELSE
BEGIN
    INSERT dbo.LicenseDevices
        (CustomerId, LicenseId, DeviceId, InstallationId, DeviceName, DevicePublicKey,
         FingerprintHash, Status, ActivatedDate, ExpiresDate, LastLicenseIssuedDate)
    VALUES
        (@customerId, @licenseId, @deviceId, @installationId, @deviceName, @publicKey,
         @fingerprint, 'Active', SYSUTCDATETIME(), @expires, SYSUTCDATETIME());
END", connection, transaction);
        upsert.Parameters.AddWithValue("@customerId", subscription.CustomerId);
        upsert.Parameters.AddWithValue("@licenseId", subscription.LicenseId);
        upsert.Parameters.AddWithValue("@deviceId", request.DeviceId);
        upsert.Parameters.AddWithValue("@installationId", request.InstallationId);
        upsert.Parameters.AddWithValue("@deviceName", request.DeviceName);
        upsert.Parameters.AddWithValue("@publicKey", request.DevicePublicKey);
        upsert.Parameters.AddWithValue("@fingerprint", request.FingerprintHash);
        upsert.Parameters.AddWithValue("@expires", expiresUtc);
        upsert.ExecuteNonQuery();

        using var history = new SqlCommand(@"
INSERT dbo.DeviceLicenseIssueHistory
    (ActivationId, CustomerId, LicenseId, DeviceId, StoreGuid, BusinessName, StoreZip,
     ActivationKey, ExpiresDate, IssuedByComputer, IssueAction, ReplacedDeviceId)
VALUES
    (@activationId, @customerId, @licenseId, @deviceId, @storeGuid, @businessName, @storeZip,
     @activationKey, @expires, @issuedByComputer, @issueAction, @replacedDeviceId)", connection, transaction);
        history.Parameters.AddWithValue("@activationId", activationId);
        history.Parameters.AddWithValue("@customerId", subscription.CustomerId);
        history.Parameters.AddWithValue("@licenseId", subscription.LicenseId);
        history.Parameters.AddWithValue("@deviceId", request.DeviceId);
        history.Parameters.AddWithValue("@storeGuid", request.StoreGuid);
        history.Parameters.AddWithValue("@businessName", request.BusinessName);
        history.Parameters.AddWithValue("@storeZip", request.StoreZip);
        history.Parameters.AddWithValue("@activationKey", activationKey);
        history.Parameters.AddWithValue("@expires", expiresUtc);
        history.Parameters.AddWithValue("@issuedByComputer", Environment.MachineName);
        history.Parameters.AddWithValue("@issueAction", seatChoice.Action.ToString());
        history.Parameters.AddWithValue("@replacedDeviceId", (object?)seatChoice.ReplacedPcId ?? DBNull.Value);
        history.ExecuteNonQuery();
        transaction.Commit();
    }

    private DeviceLicensePayloadV2 BuildPayload(SubscriptionRecord subscription, DeviceLicenseRequestV2 request, int maxDevices, int maxBusinesses, DateTime expiresUtc)
    {
        var businesses = LoadApprovedBusinesses(subscription);
        if (businesses.Count == 0)
            throw new InvalidOperationException("Add at least one approved business before issuing the PC license.");
        if (businesses.Count > maxBusinesses)
            throw new InvalidOperationException($"This subscription allows {maxBusinesses} business(es), but {businesses.Count} are active.");

        var licensedBusinesses = new List<LicensedBusinessPayloadV1>();
        foreach (var business in businesses)
        {
            var encrypted = EncryptConnection(business.DatabaseName, request.DevicePublicKey);
            licensedBusinesses.Add(new LicensedBusinessPayloadV1
            {
                BusinessId = business.Id,
                BusinessName = business.BusinessName,
                Address = business.StoreAddress,
                DatabaseName = business.DatabaseName,
                IsPrimary = business.IsPrimary,
                EncryptedConnectionKey = encrypted.EncryptedKey,
                EncryptedConnection = encrypted.Cipher,
                ConnectionNonce = encrypted.Nonce,
                ConnectionTag = encrypted.Tag
            });
        }

        var primary = licensedBusinesses.FirstOrDefault(x => x.IsPrimary) ?? licensedBusinesses[0];

        return new DeviceLicensePayloadV2
        {
            ActivationId = Guid.NewGuid().ToString("N"),
            LicenseKey = subscription.LicenseKey,
            CustomerId = subscription.CustomerId,
            LicenseId = subscription.LicenseId,
            BusinessName = subscription.BusinessName,
            StoreGuid = subscription.DatabaseName,
            StoreZip = request.StoreZip,
            StoreState = request.StoreState,
            BusinessType = request.BusinessType,
            AppVersion = request.AppVersion,
            DeviceId = request.DeviceId,
            InstallationId = request.InstallationId,
            DeviceName = request.DeviceName,
            DevicePublicKey = request.DevicePublicKey,
            Status = "Active",
            MaxDevices = maxDevices,
            MaxStores = maxBusinesses,
            MaxUsers = subscription.MaxUsers,
            EnabledServices = subscription.EnabledServices,
            MonthlyReportEmail = subscription.MonthlyReportEmail,
            MonthlyReportDay = Math.Clamp(subscription.MonthlyReportDay, 1, 28),
            IssuedUtc = DateTime.UtcNow.ToString("O"),
            ExpiresUtc = expiresUtc.ToString("O"),
            EncryptedConnectionKey = primary.EncryptedConnectionKey,
            EncryptedConnection = primary.EncryptedConnection,
            ConnectionNonce = primary.ConnectionNonce,
            ConnectionTag = primary.ConnectionTag,
            Businesses = licensedBusinesses
        };
    }

    private List<CustomerBusinessRecord> LoadApprovedBusinesses(SubscriptionRecord subscription)
    {
        using var connection = new SqlConnection(_licensingConnectionString);
        connection.Open();
        using var command = new SqlCommand(@"
SELECT Id, BusinessName, StoreAddress, DatabaseName, IsPrimary
FROM dbo.CustomerBusinesses
WHERE CustomerId=@customerId AND IsActive=1
ORDER BY IsPrimary DESC, BusinessName", connection);
        command.Parameters.AddWithValue("@customerId", subscription.CustomerId);
        using var reader = command.ExecuteReader();
        var businesses = new List<CustomerBusinessRecord>();
        while (reader.Read())
            businesses.Add(new CustomerBusinessRecord(reader.GetInt32(0), reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2), reader.GetString(3), reader.GetBoolean(4)));
        return businesses;
    }

    private EncryptedConnectionParts EncryptConnection(string databaseName, string devicePublicKey)
    {
        var connectionPayload = new DeviceConnectionPayload
        {
            Server = _server,
            Database = databaseName,
            Username = _username,
            Password = _password
        };
        var clearConnection = JsonSerializer.SerializeToUtf8Bytes(connectionPayload, _json);
        var aesKey = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[clearConnection.Length];
        var tag = new byte[16];
        byte[] encryptedAesKey;
        try
        {
            using (var aes = new AesGcm(aesKey, tag.Length))
                aes.Encrypt(nonce, clearConnection, cipher, tag);
            using var deviceRsa = RSA.Create();
            deviceRsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(devicePublicKey), out _);
            encryptedAesKey = deviceRsa.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aesKey);
            CryptographicOperations.ZeroMemory(clearConnection);
        }

        return new EncryptedConnectionParts(
            Convert.ToBase64String(encryptedAesKey),
            Convert.ToBase64String(cipher),
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(tag));
    }

    private string BuildSignedLicense(DeviceLicensePayloadV2 payload)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _json);
        using var rsa = RSA.Create();
        rsa.ImportRSAPrivateKey(Convert.FromBase64String(SigningKeyStore.Load()), out _);
        var signature = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return JsonSerializer.Serialize(new DeviceLicenseEnvelopeV2
        {
            Version = 2,
            Payload = Convert.ToBase64String(payloadBytes),
            Signature = Convert.ToBase64String(signature)
        }, _json);
    }

    private void RevokeSelectedDevice()
    {
        if (_devices.SelectedRows.Count == 0 || _devices.SelectedRows[0].Tag is not DeviceRecord selected)
        {
            SetStatus("Select a registered computer first.", true);
            return;
        }
        if (MessageBox.Show(this,
                $"Revoke {selected.DeviceName}?\r\n\r\nThe PC will not receive another license unless it is activated again.",
                "Revoke Device", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        try
        {
            using var connection = new SqlConnection(_licensingConnectionString);
            connection.Open();
            using var command = new SqlCommand(@"
UPDATE dbo.LicenseDevices SET Status='Revoked', RevokedDate=SYSUTCDATETIME()
WHERE Id=@id", connection);
            command.Parameters.AddWithValue("@id", selected.Id);
            command.ExecuteNonQuery();
            SetStatus($"{selected.DeviceName} was revoked. Its paid seat is now available for a replacement PC; its already-installed offline key can remain valid until {selected.ExpiresDate:MM/dd/yyyy}.", false);
            RefreshDevices();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
    }

    private void SetStatus(string message, bool error)
    {
        _status.Text = message;
        _status.ForeColor = error ? AdminTheme.Red : AdminTheme.Green;
    }

    private static string SafeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return value.Replace(' ', '_');
    }

    private sealed record SubscriptionRecord(int CustomerId, int LicenseId, string BusinessName, string LicenseKey,
        string DatabaseName, int MaxStores, int MaxUsers, int MaxDevices, DateTime ExpiresDate, string EnabledServices,
        string MonthlyReportEmail, int MonthlyReportDay);

    private sealed record DeviceRecord(int Id, string DeviceId, string InstallationId, string DeviceName,
        string DevicePublicKey, string FingerprintHash, string Status, DateTime ActivatedDate,
        DateTime ExpiresDate, DateTime LastIssuedDate);

    private sealed record CustomerBusinessRecord(int Id, string BusinessName, string StoreAddress,
        string DatabaseName, bool IsPrimary);

    private sealed record EncryptedConnectionParts(string EncryptedKey, string Cipher, string Nonce, string Tag);
}
