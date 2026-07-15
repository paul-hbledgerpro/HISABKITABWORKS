using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Services;
using ManagerPaperworkSystem.Data.Db;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.UI.Views;

public partial class SetupWizardWindow : Window
{
    private readonly ISettingsService _settingsService;
    private readonly IAuthService _authService;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly string _connectionSettingsPath;
    private bool _connectionTested = false;

    public SetupWizardWindow(ISettingsService settingsService, IAuthService authService, IDbContextFactory<AppDbContext> dbFactory)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _authService = authService;
        _dbFactory = dbFactory;

        // Connection settings path
        var appDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hisab Kitab");
        Directory.CreateDirectory(appDataFolder);
        _connectionSettingsPath = Path.Combine(appDataFolder, "connection_settings.json");

        // Initialize combo boxes
        cmbReportType.ItemsSource = Enum.GetValues(typeof(ReportType)).Cast<ReportType>().ToList();
        cmbReportType.SelectedItem = ReportType.ShiftLog;

        cmbSecurityQuestion.ItemsSource = new[]
        {
            "What was the name of your first pet?",
            "What city were you born in?",
            "What is your mother's maiden name?",
            "What was the model of your first car?",
            "What is the name of your favorite teacher?"
        };
        cmbSecurityQuestion.SelectedIndex = 0;

        // Load existing connection settings if any
        LoadExistingConnectionSettings();
    }

    private void LoadExistingConnectionSettings()
    {
        try
        {
            if (File.Exists(_connectionSettingsPath))
            {
                var json = File.ReadAllText(_connectionSettingsPath);
                var settings = JsonSerializer.Deserialize<ConnectionSettings>(json);
                
                if (settings != null)
                {
                    if (settings.DatabaseType == "SqlServer")
                    {
                        cmbDatabaseType.SelectedIndex = 1; // SQL Server
                        txtDbServer.Text = settings.Server ?? ".\\SQLEXPRESS";
                        txtDbName.Text = settings.Database ?? "HisabKitab";
                        txtDbUsername.Text = settings.Username ?? "";
                        txtDbPassword.Password = settings.Password ?? "";
                    }
                    else
                    {
                        cmbDatabaseType.SelectedIndex = 0; // SQLite
                    }
                }
            }
        }
        catch
        {
            // Ignore errors, use defaults
        }
    }

    private void DatabaseType_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Guard: this event fires during InitializeComponent before panels exist
        if (pnlSqlite == null || pnlSqlServer == null) return;

        if (cmbDatabaseType.SelectedItem is ComboBoxItem item)
        {
            var tag = item.Tag?.ToString();
            pnlSqlite.Visibility = tag == "SQLite" ? Visibility.Visible : Visibility.Collapsed;
            pnlSqlServer.Visibility = tag == "SqlServer" ? Visibility.Visible : Visibility.Collapsed;
            _connectionTested = (tag == "SQLite"); // SQLite doesn't need testing
        }
    }

    private void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        lblError.Text = "";
        txtConnectionStatus.Text = "Testing...";
        txtConnectionStatus.Foreground = System.Windows.Media.Brushes.Yellow;

        // Validate inputs
        if (string.IsNullOrWhiteSpace(txtDbServer.Text))
        {
            txtConnectionStatus.Text = "✗ Server address required";
            txtConnectionStatus.Foreground = System.Windows.Media.Brushes.Red;
            return;
        }
        if (string.IsNullOrWhiteSpace(txtDbUsername.Text))
        {
            txtConnectionStatus.Text = "✗ Username required";
            txtConnectionStatus.Foreground = System.Windows.Media.Brushes.Red;
            return;
        }
        if (string.IsNullOrWhiteSpace(txtDbPassword.Password))
        {
            txtConnectionStatus.Text = "✗ Password required";
            txtConnectionStatus.Foreground = System.Windows.Media.Brushes.Red;
            return;
        }

        try
        {
            var connectionString = BuildSqlServerConnectionString();
            
            using var connection = new SqlConnection(connectionString);
            connection.Open();

            // Verify database exists
            using var cmd = new SqlCommand("SELECT DB_NAME()", connection);
            var dbName = cmd.ExecuteScalar()?.ToString();

            txtConnectionStatus.Text = $"✓ Connected to {dbName}!";
            txtConnectionStatus.Foreground = System.Windows.Media.Brushes.LimeGreen;
            _connectionTested = true;
        }
        catch (SqlException ex)
        {
            txtConnectionStatus.Foreground = System.Windows.Media.Brushes.Red;
            _connectionTested = false;

            if (ex.Number == 53 || ex.Number == -1)
            {
                txtConnectionStatus.Text = "✗ Cannot reach server";
                lblError.Text = $"Cannot connect to '{txtDbServer.Text}'. Verify the server is running and accessible.";
            }
            else if (ex.Number == 18456)
            {
                txtConnectionStatus.Text = "✗ Login failed";
                lblError.Text = "Invalid username or password.";
            }
            else if (ex.Number == 4060)
            {
                txtConnectionStatus.Text = "✗ Database not found";
                lblError.Text = $"Database '{txtDbName.Text}' does not exist. Create it first.";
            }
            else
            {
                txtConnectionStatus.Text = "✗ Connection failed";
                lblError.Text = ex.Message;
            }
        }
        catch (Exception ex)
        {
            txtConnectionStatus.Text = "✗ Error";
            txtConnectionStatus.Foreground = System.Windows.Media.Brushes.Red;
            lblError.Text = ex.Message;
            _connectionTested = false;
        }
    }

    private string BuildSqlServerConnectionString()
    {
        var server = txtDbServer.Text.Trim();
        var database = txtDbName.Text.Trim();
        var username = txtDbUsername.Text.Trim();
        var password = txtDbPassword.Password;

        return $"Server={server};Database={database};User Id={username};Password={password};TrustServerCertificate=True;Encrypt=True;Connection Timeout=30;";
    }

    private async void CreateDatabase_Click(object sender, RoutedEventArgs e)
    {
        lblError.Text = "";
        txtConnectionStatus.Text = "";

        var dbName = txtDbName.Text.Trim();
        if (string.IsNullOrWhiteSpace(dbName))
        {
            lblError.Text = "Database name is required.";
            return;
        }
        if (string.IsNullOrWhiteSpace(txtDbServer.Text))
        {
            lblError.Text = "Server address is required.";
            return;
        }
        if (string.IsNullOrWhiteSpace(txtDbUsername.Text))
        {
            lblError.Text = "SQL username is required.";
            return;
        }
        if (string.IsNullOrWhiteSpace(txtDbPassword.Password))
        {
            lblError.Text = "SQL password is required.";
            return;
        }

        btnCreateDb.IsEnabled = false;
        txtConnectionStatus.Text = "Checking database...";
        txtConnectionStatus.Foreground = System.Windows.Media.Brushes.Yellow;

        try
        {
            var server = txtDbServer.Text.Trim();
            var username = txtDbUsername.Text.Trim();
            var password = txtDbPassword.Password;
            var dbConnStr = $"Server={server};Database={dbName};User Id={username};Password={password};TrustServerCertificate=True;Encrypt=True;Connection Timeout=30;";

            // ── STEP 1: Try connecting directly to the database ──
            bool dbExists = false;
            bool tablesExist = false;

            try
            {
                txtConnectionStatus.Text = "Connecting to database...";
                using var testConn = new SqlConnection(dbConnStr);
                await testConn.OpenAsync();
                dbExists = true;

                // Check if tables already exist
                using var tblCmd = testConn.CreateCommand();
                tblCmd.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'AppSettings'";
                tablesExist = (int)(await tblCmd.ExecuteScalarAsync()!) > 0;
            }
            catch (SqlException ex) when (ex.Number == 4060)
            {
                // Database does not exist — we need to create it
                dbExists = false;
            }
            catch (SqlException ex) when (ex.Number == 18456)
            {
                // Login failed — database might exist but user has no access
                // Try to create user permissions after creating the database
                dbExists = false;
            }

            // ── STEP 2: If database exists, just handle tables ──
            if (dbExists)
            {
                if (tablesExist)
                {
                    txtConnectionStatus.Text = "\u2713 Database already exists with tables!";
                    txtConnectionStatus.Foreground = System.Windows.Media.Brushes.LimeGreen;
                    _connectionTested = true;
                    btnCreateDb.IsEnabled = true;
                    return;
                }
                else
                {
                    // Database exists but no tables — create them
                    txtConnectionStatus.Text = "Creating tables...";
                    using var schemaConn = new SqlConnection(dbConnStr);
                    await schemaConn.OpenAsync();
                    await CreateSchemaAsync(schemaConn);

                    txtConnectionStatus.Text = "\u2713 Tables created successfully!";
                    txtConnectionStatus.Foreground = System.Windows.Media.Brushes.LimeGreen;
                    _connectionTested = true;
                    btnCreateDb.IsEnabled = true;
                    return;
                }
            }

            // ── STEP 3: Database doesn't exist — create it from master ──
            txtConnectionStatus.Text = "Creating database on server...";
            var masterConnStr = $"Server={server};Database=master;User Id={username};Password={password};TrustServerCertificate=True;Encrypt=True;Connection Timeout=30;";

            using (var masterConn = new SqlConnection(masterConnStr))
            {
                await masterConn.OpenAsync();

                using var createCmd = masterConn.CreateCommand();
                createCmd.CommandText = $"CREATE DATABASE [{dbName}]";
                await createCmd.ExecuteNonQueryAsync();
            }

            // ── STEP 4: Wait for Azure SQL to provision the new database ──
            // Azure SQL can take 5-20 seconds after CREATE DATABASE
            txtConnectionStatus.Text = "Waiting for database to become ready...";

            SqlConnection? newDbConn = null;
            for (int attempt = 1; attempt <= 8; attempt++)
            {
                await System.Threading.Tasks.Task.Delay(attempt * 2000); // 2s, 4s, 6s, ... 16s
                txtConnectionStatus.Text = $"Connecting to new database (attempt {attempt}/8)...";
                try
                {
                    newDbConn = new SqlConnection(dbConnStr);
                    await newDbConn.OpenAsync();
                    break; // connected!
                }
                catch
                {
                    newDbConn?.Dispose();
                    newDbConn = null;
                    if (attempt == 8)
                    {
                        txtConnectionStatus.Text = "\u2717 Database created but not yet accessible";
                        txtConnectionStatus.Foreground = System.Windows.Media.Brushes.Orange;
                        lblError.Text = "Database was created on the server, but Azure is still provisioning it. Wait 30 seconds and click 'Create Database' again — it will detect the existing database and create the tables.";
                        btnCreateDb.IsEnabled = true;
                        return;
                    }
                }
            }

            // ── STEP 5: Grant permissions (Azure SQL requires explicit user creation) ──
            txtConnectionStatus.Text = "Setting up permissions...";
            try
            {
                using var grantCmd = newDbConn!.CreateCommand();
                grantCmd.CommandText = $@"
                    IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = '{username}')
                    BEGIN
                        CREATE USER [{username}] FOR LOGIN [{username}];
                    END;
                    ALTER ROLE db_owner ADD MEMBER [{username}];";
                await grantCmd.ExecuteNonQueryAsync();
            }
            catch { /* non-fatal: server admin already has full access */ }

            // ── STEP 6: Create all tables ──
            txtConnectionStatus.Text = "Creating tables...";
            using (newDbConn!)
            {
                await CreateSchemaAsync(newDbConn);
            }

            txtConnectionStatus.Text = "\u2713 Database created successfully!";
            txtConnectionStatus.Foreground = System.Windows.Media.Brushes.LimeGreen;
            _connectionTested = true;
        }
        catch (SqlException ex) when (ex.Number == 1801)
        {
            // Error 1801: Database already exists (race condition or sys.databases not visible)
            txtConnectionStatus.Text = "Database already exists, creating tables...";
            try
            {
                var dbConnStr2 = $"Server={txtDbServer.Text.Trim()};Database={dbName};User Id={txtDbUsername.Text.Trim()};Password={txtDbPassword.Password};TrustServerCertificate=True;Encrypt=True;Connection Timeout=30;";
                using var retryConn = new SqlConnection(dbConnStr2);
                await retryConn.OpenAsync();
                await CreateSchemaAsync(retryConn);

                txtConnectionStatus.Text = "\u2713 Tables created successfully!";
                txtConnectionStatus.Foreground = System.Windows.Media.Brushes.LimeGreen;
                _connectionTested = true;
            }
            catch (Exception retryEx)
            {
                txtConnectionStatus.Text = "\u2717 Failed to create tables";
                txtConnectionStatus.Foreground = System.Windows.Media.Brushes.Red;
                lblError.Text = $"Database exists but cannot create tables: {retryEx.Message}. Click 'Test Connection' first, then try again.";
            }
        }
        catch (Exception ex)
        {
            txtConnectionStatus.Text = "\u2717 Failed";
            txtConnectionStatus.Foreground = System.Windows.Media.Brushes.Red;
            lblError.Text = $"Create failed: {ex.Message}";
        }
        finally
        {
            btnCreateDb.IsEnabled = true;
        }
    }

    /// <summary>
    /// Creates the full store database schema (same tables as the API StoreDbService).
    /// </summary>
    private static async System.Threading.Tasks.Task CreateSchemaAsync(SqlConnection conn)
    {
        var commands = new[]
        {
            @"IF OBJECT_ID('dbo.PriceAlerts','U') IS NOT NULL DROP TABLE [dbo].[PriceAlerts];
              IF OBJECT_ID('dbo.ProductCosts','U') IS NOT NULL DROP TABLE [dbo].[ProductCosts];
              IF OBJECT_ID('dbo.PurchaseInvoiceLines','U') IS NOT NULL DROP TABLE [dbo].[PurchaseInvoiceLines];
              IF OBJECT_ID('dbo.PurchaseInvoices','U') IS NOT NULL DROP TABLE [dbo].[PurchaseInvoices];
              IF OBJECT_ID('dbo.CheckPayouts','U') IS NOT NULL DROP TABLE [dbo].[CheckPayouts];
              IF OBJECT_ID('dbo.CashOnHand','U') IS NOT NULL DROP TABLE [dbo].[CashOnHand];
              IF OBJECT_ID('dbo.ShiftLogs','U') IS NOT NULL DROP TABLE [dbo].[ShiftLogs];
              IF OBJECT_ID('dbo.Purposes','U') IS NOT NULL DROP TABLE [dbo].[Purposes];
              IF OBJECT_ID('dbo.Vendors','U') IS NOT NULL DROP TABLE [dbo].[Vendors];
              IF OBJECT_ID('dbo.UserAccounts','U') IS NOT NULL DROP TABLE [dbo].[UserAccounts];
              IF OBJECT_ID('dbo.Stores','U') IS NOT NULL DROP TABLE [dbo].[Stores];
              IF OBJECT_ID('dbo.AppSettings','U') IS NOT NULL DROP TABLE [dbo].[AppSettings];",

            @"CREATE TABLE [dbo].[AppSettings] (
                [Id] INT IDENTITY(1,1) PRIMARY KEY,
                [StoreName] NVARCHAR(200) NOT NULL DEFAULT '',
                [StoreAddress] NVARCHAR(400) NOT NULL DEFAULT '',
                [DefaultReportType] INT NOT NULL DEFAULT 1,
                [ScreenMode] INT NOT NULL DEFAULT 0,
                [DefaultStoreId] INT NOT NULL DEFAULT 1,
                [LastStoreId] INT NOT NULL DEFAULT 1
              );",

            @"CREATE TABLE [dbo].[Stores] (
                [Id] INT IDENTITY(1,1) PRIMARY KEY, [Name] NVARCHAR(200) NOT NULL DEFAULT '',
                [Address] NVARCHAR(400) NOT NULL DEFAULT '', [IsActive] BIT NOT NULL DEFAULT 1,
                [CreatedUtc] DATETIME2 NOT NULL DEFAULT GETUTCDATE()
              );",

            @"CREATE TABLE [dbo].[UserAccounts] (
                [Id] INT IDENTITY(1,1) PRIMARY KEY, [FirstName] NVARCHAR(80) NOT NULL DEFAULT '',
                [LastName] NVARCHAR(80) NOT NULL DEFAULT '', [Role] INT NOT NULL DEFAULT 2,
                [Username] NVARCHAR(80) NOT NULL DEFAULT '', [Email] NVARCHAR(200) NOT NULL DEFAULT '',
                [PasswordHashBase64] NVARCHAR(200) NOT NULL DEFAULT '', [SaltBase64] NVARCHAR(200) NOT NULL DEFAULT '',
                [SecurityQuestion] NVARCHAR(240) NOT NULL DEFAULT '', [SecurityAnswerHashBase64] NVARCHAR(200) NOT NULL DEFAULT '',
                [SecurityAnswerSaltBase64] NVARCHAR(200) NOT NULL DEFAULT '', [IsActive] BIT NOT NULL DEFAULT 1,
                [CreatedUtc] DATETIME2 NOT NULL DEFAULT GETUTCDATE(), [LastLoginUtc] DATETIME2 NULL, [LastChangedUtc] DATETIME2 NULL
              );
              CREATE UNIQUE INDEX IX_UserAccounts_Username ON [dbo].[UserAccounts]([Username]);
              CREATE INDEX IX_UserAccounts_Role ON [dbo].[UserAccounts]([Role]);
              CREATE INDEX IX_UserAccounts_Email ON [dbo].[UserAccounts]([Email]) WHERE [Email] <> '';",

            @"CREATE TABLE [dbo].[Vendors] (
                [Id] INT IDENTITY(1,1) PRIMARY KEY, [StoreId] INT NOT NULL DEFAULT 1,
                [Name] NVARCHAR(200) NOT NULL DEFAULT ''
              ); CREATE UNIQUE INDEX IX_Vendors_StoreId_Name ON [dbo].[Vendors]([StoreId],[Name]);",

            @"CREATE TABLE [dbo].[Purposes] (
                [Id] INT IDENTITY(1,1) PRIMARY KEY, [StoreId] INT NOT NULL DEFAULT 1,
                [Name] NVARCHAR(200) NOT NULL DEFAULT ''
              ); CREATE UNIQUE INDEX IX_Purposes_StoreId_Name ON [dbo].[Purposes]([StoreId],[Name]);",

            @"CREATE TABLE [dbo].[ShiftLogs] (
                [Id] INT IDENTITY(1,1) PRIMARY KEY, [StoreId] INT NOT NULL DEFAULT 1, [Date] DATE NOT NULL,
                [Employee] NVARCHAR(100) NOT NULL DEFAULT '', [ShiftNo] NVARCHAR(20) NOT NULL DEFAULT '',
                [CashTotal] DECIMAL(18,2) NOT NULL DEFAULT 0, [CardTotal] DECIMAL(18,2) NOT NULL DEFAULT 0,
                [NetSales] DECIMAL(18,2) NOT NULL DEFAULT 0, [Tax] DECIMAL(18,2) NOT NULL DEFAULT 0,
                [CashDropReceived] DECIMAL(18,2) NOT NULL DEFAULT 0, [RegisterPayout] DECIMAL(18,2) NOT NULL DEFAULT 0,
                [PayoutReason] NVARCHAR(300) NOT NULL DEFAULT '',
                [CreatedByUserId] INT NOT NULL DEFAULT 0, [CreatedByName] NVARCHAR(120) NOT NULL DEFAULT '',
                [IsCorrection] BIT NOT NULL DEFAULT 0, [CorrectsId] INT NULL,
                [CorrectionReason] NVARCHAR(300) NOT NULL DEFAULT '', [CreatedUtc] DATETIME2 NOT NULL DEFAULT GETUTCDATE()
              ); CREATE INDEX IX_ShiftLogs_StoreDate ON [dbo].[ShiftLogs]([StoreId],[Date]);",

            @"CREATE TABLE [dbo].[CashOnHand] (
                [Id] INT IDENTITY(1,1) PRIMARY KEY, [StoreId] INT NOT NULL DEFAULT 1, [Date] DATE NOT NULL,
                [CashAdded] DECIMAL(18,2) NOT NULL DEFAULT 0, [Reference] NVARCHAR(50) NOT NULL DEFAULT '',
                [IsPayout] BIT NOT NULL DEFAULT 0, [PayoutAmount] DECIMAL(18,2) NOT NULL DEFAULT 0,
                [VendorId] INT NULL, [PurposeId] INT NULL, [Description] NVARCHAR(400) NOT NULL DEFAULT '',
                [CreatedByUserId] INT NOT NULL DEFAULT 0, [CreatedByName] NVARCHAR(120) NOT NULL DEFAULT '',
                [IsCorrection] BIT NOT NULL DEFAULT 0, [CorrectsId] INT NULL,
                [CorrectionReason] NVARCHAR(300) NOT NULL DEFAULT '', [CreatedUtc] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                FOREIGN KEY ([VendorId]) REFERENCES [dbo].[Vendors]([Id]),
                FOREIGN KEY ([PurposeId]) REFERENCES [dbo].[Purposes]([Id])
              ); CREATE INDEX IX_CashOnHand_StoreDate ON [dbo].[CashOnHand]([StoreId],[Date]);",

            @"CREATE TABLE [dbo].[CheckPayouts] (
                [Id] INT IDENTITY(1,1) PRIMARY KEY, [StoreId] INT NOT NULL DEFAULT 1, [Date] DATE NOT NULL,
                [VendorName] NVARCHAR(200) NOT NULL DEFAULT '', [Description] NVARCHAR(400) NOT NULL DEFAULT '',
                [CheckAmount] DECIMAL(18,2) NOT NULL DEFAULT 0, [CheckNumber] NVARCHAR(50) NOT NULL DEFAULT '',
                [Cleared] BIT NOT NULL DEFAULT 0, [CreatedByUserId] INT NOT NULL DEFAULT 0,
                [CreatedByName] NVARCHAR(120) NOT NULL DEFAULT '', [IsCorrection] BIT NOT NULL DEFAULT 0,
                [CorrectsId] INT NULL, [CorrectionReason] NVARCHAR(300) NOT NULL DEFAULT '',
                [CreatedUtc] DATETIME2 NOT NULL DEFAULT GETUTCDATE()
              ); CREATE INDEX IX_CheckPayouts_StoreDate ON [dbo].[CheckPayouts]([StoreId],[Date]);",

            @"CREATE TABLE [dbo].[PurchaseInvoices] (
                [Id] INT IDENTITY(1,1) PRIMARY KEY, [StoreId] INT NOT NULL DEFAULT 1, [VendorId] INT NULL,
                [VendorName] NVARCHAR(200) NOT NULL DEFAULT '', [InvoiceNumber] NVARCHAR(100) NOT NULL DEFAULT '',
                [InvoiceDate] DATE NOT NULL, [Total] DECIMAL(18,2) NOT NULL DEFAULT 0,
                [FilePath] NVARCHAR(500) NOT NULL DEFAULT '', [Notes] NVARCHAR(500) NOT NULL DEFAULT '',
                [CreatedByUserId] INT NOT NULL DEFAULT 0, [CreatedByName] NVARCHAR(120) NOT NULL DEFAULT '',
                [CreatedUtc] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                FOREIGN KEY ([VendorId]) REFERENCES [dbo].[Vendors]([Id])
              ); CREATE INDEX IX_PurchaseInvoices_StoreDate ON [dbo].[PurchaseInvoices]([StoreId],[InvoiceDate]);",

            @"CREATE TABLE [dbo].[PurchaseInvoiceLines] (
                [Id] INT IDENTITY(1,1) PRIMARY KEY, [PurchaseInvoiceId] INT NOT NULL,
                [ProductName] NVARCHAR(260) NOT NULL DEFAULT '', [ItemCode] NVARCHAR(80) NOT NULL DEFAULT '',
                [OrdQuantity] DECIMAL(18,2) NOT NULL DEFAULT 0, [ShipQuantity] DECIMAL(18,2) NOT NULL DEFAULT 0,
                [VolumeMl] INT NULL, [Tax] DECIMAL(18,2) NULL, [Price] DECIMAL(18,2) NULL,
                [Amount] DECIMAL(18,2) NULL, [Quantity] DECIMAL(18,2) NOT NULL DEFAULT 0,
                [UnitCost] DECIMAL(18,4) NOT NULL DEFAULT 0,
                FOREIGN KEY ([PurchaseInvoiceId]) REFERENCES [dbo].[PurchaseInvoices]([Id]) ON DELETE CASCADE
              ); CREATE INDEX IX_PurchaseInvoiceLines_InvoiceId ON [dbo].[PurchaseInvoiceLines]([PurchaseInvoiceId]);",

            @"CREATE TABLE [dbo].[ProductCosts] (
                [Id] INT IDENTITY(1,1) PRIMARY KEY, [StoreId] INT NOT NULL DEFAULT 1,
                [ProductKey] NVARCHAR(260) NOT NULL DEFAULT '', [ProductName] NVARCHAR(260) NOT NULL DEFAULT '',
                [Sku] NVARCHAR(80) NOT NULL DEFAULT '', [LastUnitCost] DECIMAL(18,4) NOT NULL DEFAULT 0,
                [LastInvoiceDate] DATE NOT NULL, [LastVendorName] NVARCHAR(200) NOT NULL DEFAULT '',
                [LastInvoiceNumber] NVARCHAR(100) NOT NULL DEFAULT '', [UpdatedUtc] DATETIME2 NOT NULL DEFAULT GETUTCDATE()
              ); CREATE UNIQUE INDEX IX_ProductCosts_StoreKey ON [dbo].[ProductCosts]([StoreId],[ProductKey]);",

            @"CREATE TABLE [dbo].[PriceAlerts] (
                [Id] INT IDENTITY(1,1) PRIMARY KEY, [StoreId] INT NOT NULL DEFAULT 1,
                [ProductKey] NVARCHAR(260) NOT NULL DEFAULT '', [ProductName] NVARCHAR(260) NOT NULL DEFAULT '',
                [Sku] NVARCHAR(80) NOT NULL DEFAULT '', [OldUnitCost] DECIMAL(18,4) NOT NULL DEFAULT 0,
                [NewUnitCost] DECIMAL(18,4) NOT NULL DEFAULT 0, [Direction] INT NOT NULL DEFAULT 0,
                [AlertType] INT NOT NULL DEFAULT 0, [VendorName] NVARCHAR(200) NOT NULL DEFAULT '',
                [OtherVendorName] NVARCHAR(200) NOT NULL DEFAULT '', [InvoiceNumber] NVARCHAR(100) NOT NULL DEFAULT '',
                [InvoiceDate] DATE NOT NULL, [PurchaseInvoiceId] INT NULL,
                [IsRead] BIT NOT NULL DEFAULT 0, [ReadUtc] DATETIME2 NULL,
                [CreatedUtc] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                FOREIGN KEY ([PurchaseInvoiceId]) REFERENCES [dbo].[PurchaseInvoices]([Id])
              ); CREATE INDEX IX_PriceAlerts_StoreReadCreated ON [dbo].[PriceAlerts]([StoreId],[IsRead],[CreatedUtc]);"
        };

        foreach (var sql in commands)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        try { DialogResult = false; } catch { Close(); }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        lblError.Text = "";

        // Get database type
        var dbType = (cmbDatabaseType.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "SQLite";
        var useSqlServer = dbType == "SqlServer";

        // ==================== VALIDATE DATABASE CONNECTION ====================
        if (useSqlServer)
        {
            if (string.IsNullOrWhiteSpace(txtDbServer.Text))
            {
                lblError.Text = "Server address is required.";
                return;
            }
            if (string.IsNullOrWhiteSpace(txtDbUsername.Text))
            {
                lblError.Text = "SQL Server username is required.";
                return;
            }
            if (string.IsNullOrWhiteSpace(txtDbPassword.Password))
            {
                lblError.Text = "SQL Server password is required.";
                return;
            }
            if (!_connectionTested)
            {
                lblError.Text = "Please test the database connection first.";
                return;
            }
        }

        // ==================== VALIDATE STORE INFO ====================
        var storeName = txtStoreName.Text?.Trim() ?? "";
        var storeAddr = txtStoreAddress.Text?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(storeName))
        {
            lblError.Text = "Store name is required.";
            return;
        }

        // ==================== VALIDATE ADMIN ACCOUNT ====================
        var firstName = txtFirstName.Text?.Trim() ?? "";
        var lastName = txtLastName.Text?.Trim() ?? "";
        var username = txtUsername.Text?.Trim() ?? "";
        var password = pwd.Password ?? "";
        var password2 = pwd2.Password ?? "";
        var secQ = (cmbSecurityQuestion.SelectedItem as string) ?? "";
        var secA = (pwdSecurityAnswer.Password ?? "").Trim();

        if (string.IsNullOrWhiteSpace(username))
        {
            lblError.Text = "Admin username is required.";
            return;
        }
        if (string.IsNullOrWhiteSpace(password) || password.Length < 4)
        {
            lblError.Text = "Admin password is required (minimum 4 characters).";
            return;
        }
        if (password != password2)
        {
            lblError.Text = "Passwords do not match.";
            return;
        }
        if (string.IsNullOrWhiteSpace(secQ))
        {
            lblError.Text = "Please select a security question.";
            return;
        }
        if (string.IsNullOrWhiteSpace(secA) || secA.Length < 2)
        {
            lblError.Text = "Security answer is required.";
            return;
        }

        try
        {
            // ==================== SAVE DATABASE CONNECTION SETTINGS ====================
            var connSettings = new ConnectionSettings
            {
                DatabaseType = dbType,
                Server = txtDbServer.Text.Trim(),
                Database = txtDbName.Text.Trim(),
                Username = txtDbUsername.Text.Trim(),
                Password = txtDbPassword.Password,
                ConnectionString = useSqlServer ? BuildSqlServerConnectionString() : ""
            };

            var json = JsonSerializer.Serialize(connSettings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_connectionSettingsPath, json);

            // ==================== SAVE PENDING SETUP DATA ====================
            // Store/admin setup data is saved to a pending file.
            // After the app restarts with the correct database, it will apply this data.
            var pendingSetup = new PendingSetupData
            {
                StoreName = storeName,
                StoreAddress = storeAddr,
                DefaultReportType = (int)(cmbReportType.SelectedItem ?? ReportType.ShiftLog),
                FirstName = firstName,
                LastName = lastName,
                Username = username,
                Email = txtEmail.Text?.Trim() ?? "",
                Password = password,
                SecurityQuestion = secQ,
                SecurityAnswer = secA
            };

            var pendingPath = Path.Combine(
                Path.GetDirectoryName(_connectionSettingsPath)!,
                "pending_setup.json");
            var pendingJson = JsonSerializer.Serialize(pendingSetup, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(pendingPath, pendingJson);

            // Success - close wizard
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { DialogResult = true; } catch { }
            }));
        }
        catch (Exception ex)
        {
            lblError.Text = ex.Message;
        }
    }
}

/// <summary>
/// Connection settings saved to JSON file
/// </summary>
public class ConnectionSettings
{
    public string DatabaseType { get; set; } = "SQLite";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string ConnectionString { get; set; } = "";
}

/// <summary>
/// Pending setup data saved during fresh install, applied after restart
/// </summary>
public class PendingSetupData
{
    public string StoreName { get; set; } = "";
    public string StoreAddress { get; set; } = "";
    public int DefaultReportType { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public string SecurityQuestion { get; set; } = "";
    public string SecurityAnswer { get; set; } = "";
}
