using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace HisabKitabWorks.LicenseGenerator.WinForms;

internal sealed partial class MainForm : Form
{
    private const string LicensingDatabase = "HBLedgerPro_Licensing";
    private const int DefaultMaxStores = 1;
    private const int DefaultMaxUsers = 999;

    private readonly TextBox _server = AdminTheme.TextBox();
    private readonly TextBox _username = AdminTheme.TextBox();
    private readonly TextBox _password = AdminTheme.TextBox(password: true);
    private readonly TextBox _storeName = AdminTheme.TextBox();
    private readonly TextBox _ownerName = AdminTheme.TextBox();
    private readonly TextBox _email = AdminTheme.TextBox();
    private readonly TextBox _zip = AdminTheme.TextBox();
    private readonly TextBox _phone = AdminTheme.TextBox();
    private readonly NumericUpDown _maxDevices = AdminTheme.NumberBox();
    private readonly NumericUpDown _maxBusinesses = AdminTheme.NumberBox();
    private readonly Button _connect = AdminTheme.Button("CONNECT");
    private readonly Button _generate = AdminTheme.Button("GENERATE LICENSE KEY", primary: true);
    private readonly Button _lookup = AdminTheme.Button("LOOK UP");
    private readonly Button _deviceLicenses = AdminTheme.Button("PASTE ACTIVATION REQUEST", primary: true);
    private readonly Button _copyKey = AdminTheme.Button("COPY KEY");
    private readonly Button _exportLicense = AdminTheme.Button("OPEN DEVICE LICENSES");
    private readonly Button _importSigningKey = AdminTheme.Button("SET UP / RESTORE KEY");
    private readonly Button _backupSigningKey = AdminTheme.Button("BACK UP KEY");
    private readonly Label _dbStatus = AdminTheme.Label("●  Not connected", AdminTheme.Muted, 9.5f);
    private readonly Label _signingStatus = AdminTheme.Label("", AdminTheme.Muted, 9.5f);
    private readonly Label _keyValue = AdminTheme.Label("—", AdminTheme.Copper, 22, true);
    private readonly Label _databaseDetails = AdminTheme.Label("No license selected", AdminTheme.Muted, 9.5f);
    private readonly Label _statusIcon = AdminTheme.Label("\uE946", AdminTheme.Muted, 26);
    private readonly Label _statusText = AdminTheme.Label("Connect to the licensing database to begin.", AdminTheme.Muted, 10.5f);

    private Panel _resultCard = null!;
    private Panel _statusCard = null!;
    private bool _isConnected;

    public MainForm()
    {
        Text = "HISAB KITAB WORKS - Admin License Generator";
        BackColor = AdminTheme.Bg;
        ForeColor = AdminTheme.Text;
        Font = AdminTheme.Body();
        Icon = AdminTheme.LoadIcon();
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        Size = new Size(1320, 860);
        MinimumSize = new Size(1180, 760);
        WindowState = FormWindowState.Normal;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = true;
        MaximizeBox = true;

        _server.Text = "hbstoreledger-server.database.windows.net";
        _zip.MaxLength = 10;
        _generate.Enabled = false;
        _lookup.Enabled = false;
        _deviceLicenses.Enabled = false;
        _copyKey.Enabled = false;
        _exportLicense.Enabled = false;
        _backupSigningKey.Enabled = false;

        Controls.Add(BuildLayout());
        WireEvents();
        RefreshSigningKeyStatus();
    }

    private string ConnectionString(string database)
        => new SqlConnectionStringBuilder
        {
            DataSource = _server.Text.Trim(),
            InitialCatalog = database,
            UserID = _username.Text.Trim(),
            Password = _password.Text,
            TrustServerCertificate = true,
            Encrypt = true,
            ConnectTimeout = 30
        }.ConnectionString;

    private static void EnsureDeviceSeatColumn(SqlConnection connection)
    {
        using var command = new SqlCommand(@"
IF COL_LENGTH('dbo.Licenses', 'MaxDevices') IS NULL
    ALTER TABLE dbo.Licenses ADD MaxDevices INT NOT NULL CONSTRAINT DF_Licenses_MaxDevices DEFAULT(1);", connection);
        command.ExecuteNonQuery();
    }

    private void WireEvents()
    {
        _connect.Click += (_, _) => ConnectToDatabase();
        _generate.Click += (_, _) => GenerateLicense();
        _lookup.Click += (_, _) => LookupLicense();
        _deviceLicenses.Click += (_, _) => OpenDeviceLicenseManager(importRequest: true);
        _copyKey.Click += (_, _) => CopyLicenseKey();
        _exportLicense.Click += (_, _) => OpenDeviceLicenseManager();
        _importSigningKey.Click += (_, _) => ImportSigningKey();
        _backupSigningKey.Click += (_, _) => BackupSigningKey();

        foreach (var field in new[] { _server, _username, _password })
            field.TextChanged += (_, _) => MarkConnectionStale();
    }

    private void ConnectToDatabase()
    {
        if (string.IsNullOrWhiteSpace(_server.Text) ||
            string.IsNullOrWhiteSpace(_username.Text) ||
            string.IsNullOrWhiteSpace(_password.Text))
        {
            SetDatabaseStatus("Fill in Server, Username, and Password.", AdminTheme.Red);
            return;
        }

        SetBusy(true, "Connecting to the licensing database...");
        try
        {
            using var connection = new SqlConnection(ConnectionString(LicensingDatabase));
            connection.Open();
            EnsureDeviceSeatColumn(connection);
            _isConnected = true;
            _generate.Enabled = true;
            _lookup.Enabled = true;
            _deviceLicenses.Enabled = true;
            SetDatabaseStatus("Connected to licensing database", AdminTheme.Green);
            ShowSuccess("Connection successful. Enter the customer information to generate or look up a license.");
        }
        catch (Exception ex)
        {
            _isConnected = false;
            _generate.Enabled = false;
            _lookup.Enabled = false;
            _deviceLicenses.Enabled = false;
            SetDatabaseStatus($"Connection failed: {ex.Message}", AdminTheme.Red);
            ShowError($"Could not connect to the licensing database.\r\n{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void GenerateLicense()
    {
        if (!_isConnected)
        {
            ShowError("Connect to the licensing database first.");
            return;
        }

        var storeName = _storeName.Text.Trim();
        var ownerName = _ownerName.Text.Trim();
        var email = _email.Text.Trim();
        var zip = _zip.Text.Trim();
        var phone = _phone.Text.Trim();
        var maxDevices = (int)_maxDevices.Value;
        var maxBusinesses = (int)_maxBusinesses.Value;

        if (string.IsNullOrWhiteSpace(storeName))
        {
            ShowError("Enter the client account name.");
            _storeName.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(ownerName))
        {
            ShowError("Enter the owner name.");
            _ownerName.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(email))
        {
            ShowError("Enter the email address.");
            _email.Focus();
            return;
        }

        SetBusy(true, "Checking customer databases and active licenses...");
        try
        {
            var databaseSelection = FindCustomerDatabase(storeName);
            if (databaseSelection.Cancelled)
            {
                ShowInfo("License generation was cancelled.");
                return;
            }

            var databaseName = databaseSelection.DatabaseName;
            var databaseExists = databaseSelection.Exists;

            if (databaseExists && TryReuseExistingLicense(databaseName))
                return;

            var confirmation = MessageBox.Show(
                this,
                $"Database to use:\r\n\r\n{databaseName}\r\n\r\nIs this correct?",
                "Confirm Customer Database",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirmation != DialogResult.Yes)
            {
                ShowInfo("Cancelled. Verify that the store name matches the intended database.");
                return;
            }

            var licenseKey = GenerateKey();
            ClearResult();

            using var connection = new SqlConnection(ConnectionString(LicensingDatabase));
            connection.Open();

            while (true)
            {
                using var keyCheck = new SqlCommand("SELECT COUNT(*) FROM Licenses WHERE LicenseKey = @key", connection);
                keyCheck.Parameters.AddWithValue("@key", licenseKey);
                if (Convert.ToInt32(keyCheck.ExecuteScalar()) == 0)
                    break;
                licenseKey = GenerateKey();
            }

            var customerId = 0;
            var isExistingCustomer = false;
            using (var customerCheck = new SqlCommand("SELECT TOP 1 Id FROM Customers WHERE BusinessName = @name", connection))
            {
                customerCheck.Parameters.AddWithValue("@name", storeName);
                var existingId = customerCheck.ExecuteScalar();
                if (existingId is not null && existingId != DBNull.Value)
                {
                    customerId = Convert.ToInt32(existingId);
                    isExistingCustomer = true;
                }
            }

            if (isExistingCustomer)
            {
                using var update = new SqlCommand(@"
                    UPDATE Customers
                    SET OwnerName = @owner, Email = @email, Phone = @phone,
                        Notes = CASE WHEN @notes = '' THEN Notes ELSE @notes END
                    WHERE Id = @id", connection);
                update.Parameters.AddWithValue("@owner", ownerName);
                update.Parameters.AddWithValue("@email", email);
                update.Parameters.AddWithValue("@phone", phone);
                update.Parameters.AddWithValue("@notes", string.IsNullOrWhiteSpace(zip) ? "" : $"Zip: {zip}");
                update.Parameters.AddWithValue("@id", customerId);
                update.ExecuteNonQuery();

                using var deactivate = new SqlCommand("UPDATE Licenses SET IsActive = 0 WHERE CustomerId = @customerId", connection);
                deactivate.Parameters.AddWithValue("@customerId", customerId);
                deactivate.ExecuteNonQuery();
            }
            else
            {
                using var insertCustomer = new SqlCommand(@"
                    INSERT INTO Customers (BusinessName, OwnerName, Email, Phone, Notes)
                    OUTPUT INSERTED.Id
                    VALUES (@business, @owner, @email, @phone, @notes)", connection);
                insertCustomer.Parameters.AddWithValue("@business", storeName);
                insertCustomer.Parameters.AddWithValue("@owner", ownerName);
                insertCustomer.Parameters.AddWithValue("@email", email);
                insertCustomer.Parameters.AddWithValue("@phone", phone);
                insertCustomer.Parameters.AddWithValue("@notes", string.IsNullOrWhiteSpace(zip) ? "" : $"Zip: {zip}");
                customerId = Convert.ToInt32(insertCustomer.ExecuteScalar());
            }

            var expiresDate = DateTime.UtcNow.AddMonths(1);
            using (var insertLicense = new SqlCommand(@"
                INSERT INTO Licenses
                    (CustomerId, LicenseKey, MaxStores, MaxUsers, MaxDevices, MonthlyFee, IsActive, ActivatedDate, ExpiresDate, AssignedDatabases)
                VALUES
                    (@customerId, @key, @maxStores, @maxUsers, @maxDevices, 0.00, 1, GETUTCDATE(), @expires, @database)", connection))
            {
                insertLicense.Parameters.AddWithValue("@customerId", customerId);
                insertLicense.Parameters.AddWithValue("@key", licenseKey);
                insertLicense.Parameters.AddWithValue("@maxStores", maxBusinesses);
                insertLicense.Parameters.AddWithValue("@maxUsers", DefaultMaxUsers);
                insertLicense.Parameters.AddWithValue("@maxDevices", maxDevices);
                insertLicense.Parameters.AddWithValue("@expires", expiresDate);
                insertLicense.Parameters.AddWithValue("@database", databaseName);
                insertLicense.ExecuteNonQuery();
            }

            ShowLicense(licenseKey, $"Primary DB: {databaseName}  |  Customer ID: {customerId}  |  PC Seats: {maxDevices}  |  Businesses: {maxBusinesses}", canExport: true);

            if (!databaseExists)
            {
                try
                {
                    using var masterConnection = new SqlConnection(ConnectionString("master"));
                    masterConnection.Open();
                    var quotedName = databaseName.Replace("]", "]]", StringComparison.Ordinal);
                    using var createDatabase = new SqlCommand($"CREATE DATABASE [{quotedName}]", masterConnection) { CommandTimeout = 120 };
                    createDatabase.ExecuteNonQuery();
                }
                catch (Exception databaseError)
                {
                    ShowError($"The license was created, but the customer database could not be created.\r\n{databaseError.Message}\r\n\r\nCreate the database manually in SQL Server Management Studio.");
                    return;
                }
            }

            var action = isExistingCustomer
                ? "A new key was issued for the existing customer and the old keys were deactivated."
                : "The customer license was registered and the database was created.";
            ShowSuccess($"{action}\r\nPaste the customer's activation request to generate the PC License Key.");
        }
        catch (Exception ex)
        {
            ShowError($"License generation failed.\r\n{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private DatabaseSelection FindCustomerDatabase(string storeName)
    {
        var safeName = Regex.Replace(storeName, @"[^a-zA-Z0-9 ]", "").Trim();
        if (string.IsNullOrWhiteSpace(safeName))
            throw new InvalidOperationException("The store name must contain letters or numbers.");

        var underscoreName = safeName.Replace(" ", "_");
        var compactName = safeName.Replace(" ", "");
        var newDatabaseName = $"HisabKitab_{underscoreName}";

        try
        {
            var candidates = new List<string>();
            using var connection = new SqlConnection(ConnectionString("master"));
            connection.Open();
            using (var command = new SqlCommand(@"
                SELECT name FROM sys.databases
                WHERE name LIKE 'HisabKitab[_]%'
                   OR name LIKE 'HisabWorks[_]%'
                   OR name LIKE 'HBStoreLedger[_]%'
                ORDER BY name", connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                    candidates.Add(reader.GetString(0));
            }

            var prefixes = new[] { "HisabKitab_", "HisabWorks_", "HBStoreLedger_" };
            var exact = candidates.FirstOrDefault(candidate => prefixes.Any(prefix =>
                candidate.Equals($"{prefix}{safeName}", StringComparison.OrdinalIgnoreCase) ||
                candidate.Equals($"{prefix}{underscoreName}", StringComparison.OrdinalIgnoreCase)));
            if (exact is not null)
                return new DatabaseSelection(exact, true, false);

            var compact = candidates.FirstOrDefault(candidate =>
            {
                var databasePart = candidate;
                foreach (var prefix in prefixes)
                {
                    if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        databasePart = candidate[prefix.Length..];
                        break;
                    }
                }
                return databasePart.Replace(" ", "").Replace("_", "")
                    .Equals(compactName, StringComparison.OrdinalIgnoreCase);
            });
            if (compact is not null)
                return new DatabaseSelection(compact, true, false);

            var words = safeName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(word => word.Length > 2)
                .ToArray();
            var allWordMatches = candidates.Where(candidate =>
                words.Length > 0 && words.All(word => candidate.Contains(word, StringComparison.OrdinalIgnoreCase))).ToList();
            var partialMatches = candidates.Where(candidate =>
                words.Any(word => candidate.Contains(word, StringComparison.OrdinalIgnoreCase))).ToList();
            var matches = allWordMatches.Count > 0 ? allWordMatches : partialMatches;

            for (var index = 0; index < matches.Count; index++)
            {
                var choice = MessageBox.Show(
                    this,
                    $"Is this the correct customer database?\r\n\r\n{matches[index]}",
                    $"Select Database ({index + 1} of {matches.Count})",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);
                if (choice == DialogResult.Yes)
                    return new DatabaseSelection(matches[index], true, false);
                if (choice == DialogResult.Cancel)
                    return new DatabaseSelection("", false, true);
            }
        }
        catch (SqlException ex)
        {
            ShowInfo($"Could not inspect existing databases. A new database name will be used.\r\n{ex.Message}");
        }

        return new DatabaseSelection(newDatabaseName, false, false);
    }

    private bool TryReuseExistingLicense(string databaseName)
    {
        using var connection = new SqlConnection(ConnectionString(LicensingDatabase));
        connection.Open();
        using var command = new SqlCommand(@"
            SELECT TOP 1 l.LicenseKey, c.BusinessName, c.Id, l.ExpiresDate, l.MaxStores, l.MaxUsers, l.MaxDevices
            FROM Licenses l
            INNER JOIN Customers c ON l.CustomerId = c.Id
            WHERE l.AssignedDatabases = @database AND l.IsActive = 1
            ORDER BY l.Id DESC", connection);
        command.Parameters.AddWithValue("@database", databaseName);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return false;

        var existingKey = reader.GetString(0);
        var businessName = reader.GetString(1);
        var customerId = reader.GetInt32(2);
        var expiresDate = reader.GetDateTime(3);
        var maxStores = reader.IsDBNull(4) ? DefaultMaxStores : reader.GetInt32(4);
        var maxUsers = reader.IsDBNull(5) ? DefaultMaxUsers : reader.GetInt32(5);
        var maxDevices = reader.IsDBNull(6) ? 1 : reader.GetInt32(6);
        reader.Close();
        _maxDevices.Value = Math.Clamp(maxDevices, 1, 999);
        _maxBusinesses.Value = Math.Clamp(maxStores, 1, 999);

        var choice = MessageBox.Show(
            this,
            $"An active license already exists for this database.\r\n\r\n" +
            $"Business: {businessName}\r\nDatabase: {databaseName}\r\nActive Key: {existingKey}\r\n\r\n" +
            "YES = keep this subscription and open device licensing.\r\n" +
            "NO = issue a new key and deactivate the old one.",
            "Active License Found",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);

        if (choice == DialogResult.Cancel)
        {
            ShowInfo("License generation was cancelled.");
            return true;
        }
        if (choice != DialogResult.Yes)
            return false;

        ShowLicense(existingKey, $"Primary DB: {databaseName}  |  Customer ID: {customerId}  |  PC Seats: {maxDevices}  |  Businesses: {maxStores}", canExport: true);
        ShowSuccess($"The existing subscription for '{businessName}' is ready. Paste the customer's activation request to continue.");
        return true;
    }

    private void LookupLicense()
    {
        if (!_isConnected)
        {
            ShowError("Connect to the licensing database first.");
            return;
        }

        var searchName = _storeName.Text.Trim();
        if (string.IsNullOrWhiteSpace(searchName))
        {
            ShowError("Enter a client account name to look up.");
            _storeName.Focus();
            return;
        }

        SetBusy(true, "Searching customer licenses...");
        ClearResult();
        try
        {
            using var connection = new SqlConnection(ConnectionString(LicensingDatabase));
            connection.Open();
            using var command = new SqlCommand(@"
                SELECT TOP 1 c.BusinessName, c.OwnerName, c.Email, c.Id, c.Phone,
                       l.LicenseKey, l.IsActive, l.ExpiresDate, l.AssignedDatabases, l.MaxStores, l.MaxUsers, l.MaxDevices
                FROM Customers c
                INNER JOIN Licenses l ON l.CustomerId = c.Id
                WHERE c.BusinessName LIKE @name
                ORDER BY l.IsActive DESC, l.Id DESC", connection);
            command.Parameters.AddWithValue("@name", $"%{searchName}%");
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                ShowError($"No license was found for '{searchName}'. Complete the customer details and generate a new license.");
                return;
            }

            var businessName = reader.GetString(0);
            var ownerName = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var email = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var customerId = reader.GetInt32(3);
            var phone = reader.IsDBNull(4) ? "" : reader.GetString(4);
            var key = reader.GetString(5);
            var active = reader.GetBoolean(6);
            var expires = reader.GetDateTime(7);
            var database = reader.IsDBNull(8) ? "" : reader.GetString(8);
            var maxStores = reader.IsDBNull(9) ? DefaultMaxStores : reader.GetInt32(9);
            var maxUsers = reader.IsDBNull(10) ? DefaultMaxUsers : reader.GetInt32(10);
            var maxDevices = reader.IsDBNull(11) ? 1 : reader.GetInt32(11);

            _storeName.Text = businessName;
            _ownerName.Text = ownerName;
            _email.Text = email;
            _phone.Text = phone;
            _maxDevices.Value = Math.Clamp(maxDevices, 1, 999);
            _maxBusinesses.Value = Math.Clamp(maxStores, 1, 999);
            ShowLicense(
                key,
                $"Primary DB: {database}  |  Customer ID: {customerId}  |  PC Seats: {maxDevices}  |  Businesses: {maxStores}  |  Active: {(active ? "Yes" : "No")}  |  Expires: {expires:MM/dd/yyyy}",
                canExport: active);
            if (active)
                ShowSuccess($"Found the active subscription for '{businessName}'. Open Device Licenses to issue or renew a PC license.");
            else
                ShowInfo($"The latest license for '{businessName}' is inactive. Generate a fresh license key.");
        }
        catch (Exception ex)
        {
            ShowError($"License lookup failed.\r\n{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ImportSigningKey()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Set Up or Restore HISAB KITAB WORKS Signing Key",
            Filter = "Encrypted signing-key backup (*.hbsigningbackup)|*.hbsigningbackup|Signing key or legacy source (*.txt;*.pem;*.key;*.cs)|*.txt;*.pem;*.key;*.cs|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            if (Path.GetExtension(dialog.FileName).Equals(".hbsigningbackup", StringComparison.OrdinalIgnoreCase))
            {
                var password = SigningKeyPasswordForm.PromptForRestore(this);
                if (password is null)
                    return;
                SigningKeyStore.ImportEncryptedBackup(dialog.FileName, password);
            }
            else
            {
                SigningKeyStore.Import(dialog.FileName);
            }
            RefreshSigningKeyStatus();
            ShowSuccess("Signing is ready on this PC. You can now paste an activation request and generate its License Key.");
        }
        catch (Exception ex)
        {
            ShowError($"Could not import the signing key.\r\n{ex.Message}");
        }
    }

    private void BackupSigningKey()
    {
        if (!SigningKeyStore.IsConfigured)
        {
            ShowError("Set up the signing key on this PC first.");
            return;
        }

        var password = SigningKeyPasswordForm.PromptForBackup(this);
        if (password is null)
            return;
        using var dialog = new SaveFileDialog
        {
            Title = "Save Encrypted Signing-Key Backup",
            Filter = "HISAB KITAB Signing-Key Backup (*.hbsigningbackup)|*.hbsigningbackup",
            FileName = "HISAB_KITAB_SIGNING_KEY_BACKUP.hbsigningbackup",
            AddExtension = true,
            DefaultExt = ".hbsigningbackup"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            SigningKeyStore.ExportEncryptedBackup(dialog.FileName, password);
            ShowSuccess("Encrypted signing-key backup created. Keep the file and its password private. Use Set Up / Restore Key on your work PC.");
        }
        catch (Exception ex)
        {
            ShowError($"Could not create the signing-key backup.\r\n{ex.Message}");
        }
    }

    private void OpenDeviceLicenseManager(bool importRequest = false)
    {
        if (!_isConnected)
        {
            ShowError("Connect to the licensing database first.");
            return;
        }
        var expectedBusiness = string.IsNullOrWhiteSpace(_storeName.Text) ? null : _storeName.Text.Trim();
        var displayedKey = _keyValue.Text ?? "";
        var expectedKey = Regex.IsMatch(displayedKey, @"^HBL-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$")
            ? displayedKey.Trim()
            : null;
        using var form = new DeviceLicenseManagerForm(
            ConnectionString(LicensingDatabase),
            _server.Text.Trim(),
            _username.Text.Trim(),
            _password.Text,
            importRequest,
            expectedBusiness,
            expectedKey);
        form.ShowDialog(this);
    }

    private static string GenerateKey()
    {
        const string characters = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = new byte[12];
        RandomNumberGenerator.Fill(bytes);
        var key = bytes.Select(value => characters[value % characters.Length]).ToArray();
        return $"HBL-{new string(key, 0, 4)}-{new string(key, 4, 4)}-{new string(key, 8, 4)}";
    }

    private void CopyLicenseKey()
    {
        if (string.IsNullOrWhiteSpace(_keyValue.Text) || _keyValue.Text == "—")
            return;
        try
        {
            Clipboard.SetText(_keyValue.Text);
            ShowSuccess("The license key was copied to the clipboard.");
        }
        catch (Exception ex)
        {
            ShowError($"Could not copy the license key.\r\n{ex.Message}");
        }
    }

    private void ShowLicense(string key, string details, bool canExport)
    {
        _keyValue.Text = key;
        _databaseDetails.Text = details;
        _copyKey.Enabled = true;
        _exportLicense.Enabled = canExport;
        _resultCard.Tag = AdminTheme.Copper;
        _resultCard.Invalidate();
    }

    private void ClearResult()
    {
        _keyValue.Text = "—";
        _databaseDetails.Text = "No license selected";
        _copyKey.Enabled = false;
        _exportLicense.Enabled = false;
        _resultCard.Tag = AdminTheme.CopperDark;
        _resultCard.Invalidate();
    }

    private void RefreshSigningKeyStatus()
    {
        var configured = SigningKeyStore.IsConfigured;
        _signingStatus.Text = configured
            ? "●  Ready - protected for this Windows user"
            : "●  One-time setup required before issuing licenses";
        _signingStatus.ForeColor = configured ? AdminTheme.Green : AdminTheme.Muted;
        _importSigningKey.Text = configured ? "RESTORE / REPLACE KEY" : "SET UP / RESTORE KEY";
        _backupSigningKey.Enabled = configured;
    }

    private void MarkConnectionStale()
    {
        if (!_isConnected)
            return;
        _isConnected = false;
        _generate.Enabled = false;
        _lookup.Enabled = false;
        SetDatabaseStatus("Connection settings changed - reconnect", AdminTheme.Muted);
    }

    private void SetDatabaseStatus(string text, Color color)
    {
        _dbStatus.Text = $"●  {text}";
        _dbStatus.ForeColor = color;
    }

    private void SetBusy(bool busy, string? message = null)
    {
        UseWaitCursor = busy;
        _connect.Enabled = !busy;
        _generate.Enabled = !busy && _isConnected;
        _lookup.Enabled = !busy && _isConnected;
        _deviceLicenses.Enabled = !busy && _isConnected;
        _importSigningKey.Enabled = !busy;
        _backupSigningKey.Enabled = !busy && SigningKeyStore.IsConfigured;
        if (busy && !string.IsNullOrWhiteSpace(message))
            ShowInfo(message);
    }

    private void ShowError(string message) => SetStatus(message, AdminTheme.Red, "\uEA39");
    private void ShowSuccess(string message) => SetStatus(message, AdminTheme.Green, "\uE73E");
    private void ShowInfo(string message) => SetStatus(message, AdminTheme.Copper, "\uE946");

    private void SetStatus(string message, Color color, string glyph)
    {
        _statusText.Text = message;
        _statusText.ForeColor = color;
        _statusIcon.Text = glyph;
        _statusIcon.ForeColor = color;
        _statusCard.Tag = color;
        _statusCard.Invalidate();
    }

    private sealed record DatabaseSelection(string DatabaseName, bool Exists, bool Cancelled);
}
