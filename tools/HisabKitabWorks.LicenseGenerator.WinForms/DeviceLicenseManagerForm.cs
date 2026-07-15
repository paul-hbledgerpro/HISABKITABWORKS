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
    private readonly Button _importRequest = AdminTheme.Button("IMPORT .HBREQUEST", true);
    private readonly Button _issueLicense = AdminTheme.Button("ISSUE / RENEW LICENSE", true);
    private readonly Button _revokeDevice = AdminTheme.Button("REVOKE SELECTED PC");
    private readonly Button _refresh = AdminTheme.Button("REFRESH");
    private readonly Label _business = AdminTheme.Label("No request loaded", AdminTheme.Text, 13, true);
    private readonly Label _device = AdminTheme.Label("—", AdminTheme.Muted, 10);
    private readonly Label _deviceId = AdminTheme.Label("—", AdminTheme.Copper, 11, true);
    private readonly Label _status = AdminTheme.Label("Import a PC request to begin.", AdminTheme.Muted, 10);
    private readonly NumericUpDown _maxDevices = new() { Minimum = 1, Maximum = 999, Value = 1, Font = AdminTheme.Body(11) };
    private readonly DateTimePicker _expires = new() { Format = DateTimePickerFormat.Short, Font = AdminTheme.Body(11) };
    private readonly DataGridView _devices = new();
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private DeviceLicenseRequestV2? _request;
    private SubscriptionRecord? _subscription;
    private List<DeviceRecord> _deviceRows = new();

    public DeviceLicenseManagerForm(string licensingConnectionString, string server, string username, string password)
    {
        _licensingConnectionString = licensingConnectionString;
        _server = server;
        _username = username;
        _password = password;

        Text = "HISAB KITAB WORKS - Device License Manager";
        BackColor = AdminTheme.Bg;
        ForeColor = AdminTheme.Text;
        Font = AdminTheme.Body();
        Icon = AdminTheme.LoadIcon();
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1220, 820);
        MinimumSize = new Size(1060, 700);
        Controls.Add(BuildLayout());
        ConfigureGrid();

        _importRequest.Click += (_, _) => ImportRequest();
        _issueLicense.Click += (_, _) => IssueLicense();
        _revokeDevice.Click += (_, _) => RevokeSelectedDevice();
        _refresh.Click += (_, _) => RefreshDevices();
        _devices.SelectionChanged += (_, _) => LoadSelectedDevice();
        Shown += (_, _) => InitializeSchema();
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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 200));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));

        var header = new Panel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, Padding = new Padding(24, 12, 24, 12) };
        header.Paint += (_, e) => AdminTheme.PaintGradient(e, header.ClientRectangle);
        header.Controls.Add(new Label
        {
            Text = "DEVICE LICENSE MANAGER",
            Dock = DockStyle.Top,
            Height = 40,
            ForeColor = AdminTheme.Text,
            Font = AdminTheme.Header(21),
            BackColor = Color.Transparent
        });
        header.Controls.Add(new Label
        {
            Text = "One signed license per paid computer seat",
            Dock = DockStyle.Bottom,
            Height = 24,
            ForeColor = AdminTheme.Copper,
            Font = AdminTheme.Body(10.5f),
            BackColor = Color.Transparent
        });
        root.Controls.Add(header, 0, 0);

        var top = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Bg, ColumnCount = 2, Padding = new Padding(0, 12, 0, 10) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
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
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _importRequest.Dock = DockStyle.Fill;
        _revokeDevice.Dock = DockStyle.Fill;
        _issueLicense.Dock = DockStyle.Fill;
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(_importRequest, 1, 0);
        footer.Controls.Add(_revokeDevice, 2, 0);
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
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(AdminTheme.Label("CURRENT PC REQUEST", AdminTheme.Copper, 10, true), 0, 0);
        _business.Dock = DockStyle.Fill;
        _device.Dock = DockStyle.Fill;
        _deviceId.Dock = DockStyle.Fill;
        layout.Controls.Add(_business, 0, 1);
        layout.Controls.Add(_device, 0, 2);
        layout.Controls.Add(_deviceId, 0, 3);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildSubscriptionCard()
    {
        var card = AdminTheme.Card();
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(8, 0, 0, 0);
        card.Padding = new Padding(18, 12, 18, 12);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 2, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = AdminTheme.Label("SUBSCRIPTION LIMITS", AdminTheme.Copper, 10, true);
        heading.Dock = DockStyle.Fill;
        layout.Controls.Add(heading, 0, 0);
        layout.SetColumnSpan(heading, 2);
        layout.Controls.Add(AdminTheme.Label("PAID PC SEATS", AdminTheme.Muted, 8.5f, true), 0, 1);
        layout.Controls.Add(AdminTheme.Label("SUBSCRIPTION EXPIRES", AdminTheme.Muted, 8.5f, true), 1, 1);
        _maxDevices.Dock = DockStyle.Top;
        _expires.Dock = DockStyle.Top;
        layout.Controls.Add(_maxDevices, 0, 2);
        layout.Controls.Add(_expires, 1, 2);
        card.Controls.Add(layout);
        return card;
    }

    private void ConfigureGrid()
    {
        _devices.BackgroundColor = AdminTheme.Panel;
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
        _devices.ColumnHeadersDefaultCellStyle.ForeColor = Color.Black;
        _devices.ColumnHeadersDefaultCellStyle.Font = AdminTheme.Bold(9);
        _devices.DefaultCellStyle.BackColor = AdminTheme.Panel;
        _devices.DefaultCellStyle.ForeColor = AdminTheme.Text;
        _devices.DefaultCellStyle.SelectionBackColor = AdminTheme.Panel2;
        _devices.DefaultCellStyle.SelectionForeColor = AdminTheme.Text;
        _devices.Columns.Add("DeviceName", "Computer");
        _devices.Columns.Add("DeviceId", "Device ID");
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
END", connection);
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
        using var dialog = new OpenFileDialog
        {
            Title = "Import HISAB KITAB PC Request",
            Filter = "HISAB KITAB PC Request (*.hbrequest)|*.hbrequest|JSON files (*.json)|*.json"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            var request = JsonSerializer.Deserialize<DeviceLicenseRequestV2>(File.ReadAllText(dialog.FileName), _json)
                ?? throw new InvalidOperationException("The selected PC request is invalid.");
            DeviceRequestValidator.Validate(request);
            _request = request;
            _business.Text = request.BusinessName;
            _device.Text = request.DeviceName;
            _deviceId.Text = request.DeviceId;
            LoadSubscription(request.BusinessName, request.SubscriptionKey);
            SetStatus("PC request verified. Confirm seats and expiration, then issue the license.", false);
        }
        catch (Exception ex)
        {
            _request = null;
            _subscription = null;
            SetStatus(ex.Message, true);
        }
    }

    private void LoadSubscription(string businessName, string subscriptionKey)
    {
        using var connection = new SqlConnection(_licensingConnectionString);
        connection.Open();
        using var command = new SqlCommand(@"
SELECT c.Id CustomerId, l.Id LicenseId, c.BusinessName, l.LicenseKey,
       l.AssignedDatabases, l.MaxStores, l.MaxUsers, l.MaxDevices, l.ExpiresDate
FROM dbo.Customers c
INNER JOIN dbo.Licenses l ON l.CustomerId = c.Id
WHERE c.BusinessName = @business AND l.LicenseKey = @licenseKey AND l.IsActive = 1
ORDER BY l.Id DESC", connection);
        command.Parameters.AddWithValue("@business", businessName);
        command.Parameters.AddWithValue("@licenseKey", subscriptionKey);
        using var reader = command.ExecuteReader();
        var matches = new List<SubscriptionRecord>();
        while (reader.Read())
        {
            matches.Add(new SubscriptionRecord(
                reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? "" : reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6),
                reader.GetInt32(7), reader.GetDateTime(8)));
        }
        if (matches.Count == 0)
            throw new InvalidOperationException($"No active customer subscription was found for '{businessName}'. Generate the customer license first.");
        _subscription = matches[0];
        _maxDevices.Value = Math.Clamp(_subscription.MaxDevices, 1, 999);
        _expires.Value = _subscription.ExpiresDate < DateTime.Today ? DateTime.Today.AddMonths(1) : _subscription.ExpiresDate;
        RefreshDevices();
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

            _devices.Rows.Clear();
            foreach (var device in _deviceRows)
            {
                var index = _devices.Rows.Add(device.DeviceName, device.DeviceId, device.Status,
                    device.ActivatedDate.ToString("MM/dd/yyyy"), device.ExpiresDate.ToString("MM/dd/yyyy"));
                _devices.Rows[index].Tag = device;
            }
            var seatsInUse = _deviceRows.Count(x => x.ExpiresDate.ToUniversalTime() > DateTime.UtcNow);
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
            DeviceId = selected.DeviceId,
            InstallationId = selected.InstallationId,
            DeviceName = selected.DeviceName,
            DevicePublicKey = selected.DevicePublicKey,
            FingerprintHash = selected.FingerprintHash
        };
        _business.Text = _subscription.BusinessName;
        _device.Text = selected.DeviceName;
        _deviceId.Text = selected.DeviceId;
    }

    private void IssueLicense()
    {
        if (_subscription is null || _request is null)
        {
            SetStatus("Import a PC request or select an existing registered computer first.", true);
            return;
        }
        if (!SigningKeyStore.IsConfigured)
        {
            SetStatus("Import the private signing key in the main License Generator first.", true);
            return;
        }
        if (_expires.Value.Date < DateTime.Today)
        {
            SetStatus("Select a future subscription expiration date.", true);
            return;
        }

        try
        {
            var expiresUtc = DateTime.SpecifyKind(_expires.Value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Local).ToUniversalTime();
            using var dialog = new SaveFileDialog
            {
                Title = "Save Device License",
                Filter = "HISAB KITAB Device License (*.hblicense)|*.hblicense",
                FileName = $"{SafeFileName(_subscription.BusinessName)}_{SafeFileName(_request.DeviceName)}.hblicense",
                AddExtension = true,
                DefaultExt = ".hblicense"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            RegisterDeviceAndUpdateSubscription(_subscription, _request, (int)_maxDevices.Value, expiresUtc);
            var payload = BuildPayload(_subscription, _request, (int)_maxDevices.Value, expiresUtc);
            File.WriteAllText(dialog.FileName, BuildSignedLicense(payload));
            SetStatus($"Device license issued successfully: {dialog.FileName}", false);
            RefreshDevices();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
    }

    private void RegisterDeviceAndUpdateSubscription(SubscriptionRecord subscription, DeviceLicenseRequestV2 request, int maxDevices, DateTime expiresUtc)
    {
        using var connection = new SqlConnection(_licensingConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        using var count = new SqlCommand(@"
SELECT COUNT(*) FROM dbo.LicenseDevices WITH (UPDLOCK, HOLDLOCK)
WHERE LicenseId = @licenseId AND ExpiresDate > SYSUTCDATETIME() AND DeviceId <> @deviceId", connection, transaction);
        count.Parameters.AddWithValue("@licenseId", subscription.LicenseId);
        count.Parameters.AddWithValue("@deviceId", request.DeviceId);
        var otherActive = Convert.ToInt32(count.ExecuteScalar());
        if (otherActive >= maxDevices)
            throw new InvalidOperationException($"All {maxDevices} paid PC seats are already in use. Revoke an old PC or increase the paid seat limit.");

        using var updateLicense = new SqlCommand(@"
UPDATE dbo.Licenses SET MaxDevices = @maxDevices, ExpiresDate = @expires
WHERE Id = @licenseId AND IsActive = 1", connection, transaction);
        updateLicense.Parameters.AddWithValue("@maxDevices", maxDevices);
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
        transaction.Commit();
    }

    private DeviceLicensePayloadV2 BuildPayload(SubscriptionRecord subscription, DeviceLicenseRequestV2 request, int maxDevices, DateTime expiresUtc)
    {
        var connectionPayload = new DeviceConnectionPayload
        {
            Server = _server,
            Database = subscription.DatabaseName,
            Username = _username,
            Password = _password
        };
        var clearConnection = JsonSerializer.SerializeToUtf8Bytes(connectionPayload, _json);
        var aesKey = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[clearConnection.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(aesKey, tag.Length))
            aes.Encrypt(nonce, clearConnection, cipher, tag);
        using var deviceRsa = RSA.Create();
        deviceRsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(request.DevicePublicKey), out _);
        var encryptedAesKey = deviceRsa.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256);
        CryptographicOperations.ZeroMemory(aesKey);
        CryptographicOperations.ZeroMemory(clearConnection);

        return new DeviceLicensePayloadV2
        {
            LicenseKey = subscription.LicenseKey,
            CustomerId = subscription.CustomerId,
            LicenseId = subscription.LicenseId,
            BusinessName = subscription.BusinessName,
            DeviceId = request.DeviceId,
            InstallationId = request.InstallationId,
            DeviceName = request.DeviceName,
            DevicePublicKey = request.DevicePublicKey,
            Status = "Active",
            MaxDevices = maxDevices,
            MaxStores = subscription.MaxStores,
            MaxUsers = subscription.MaxUsers,
            IssuedUtc = DateTime.UtcNow.ToString("O"),
            ExpiresUtc = expiresUtc.ToString("O"),
            EncryptedConnectionKey = Convert.ToBase64String(encryptedAesKey),
            EncryptedConnection = Convert.ToBase64String(cipher),
            ConnectionNonce = Convert.ToBase64String(nonce),
            ConnectionTag = Convert.ToBase64String(tag)
        };
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
            SetStatus($"{selected.DeviceName} was revoked and cannot be renewed. Its seat remains reserved until the current license expires on {selected.ExpiresDate:MM/dd/yyyy}.", false);
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
        string DatabaseName, int MaxStores, int MaxUsers, int MaxDevices, DateTime ExpiresDate);

    private sealed record DeviceRecord(int Id, string DeviceId, string InstallationId, string DeviceName,
        string DevicePublicKey, string FingerprintHash, string Status, DateTime ActivatedDate,
        DateTime ExpiresDate, DateTime LastIssuedDate);
}
