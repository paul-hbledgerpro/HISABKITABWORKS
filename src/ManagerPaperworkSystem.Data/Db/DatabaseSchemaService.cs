using Microsoft.Data.SqlClient;
using System.Diagnostics;

namespace ManagerPaperworkSystem.Data.Db;

/// <summary>
/// Ensures all required tables exist in the connected database.
/// Call EnsureSchemaAsync() on app startup before loading any data.
/// Safe to run repeatedly - all statements use IF NOT EXISTS.
/// </summary>
public static class DatabaseSchemaService
{
    /// <summary>
    /// Checks and creates any missing tables in the given database.
    /// Also fixes known column-name mismatches from older schema versions.
    /// </summary>
    public static async Task EnsureSchemaAsync(string connectionString, string? storeName = null)
    {
        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            // Phase 1: Fix known column renames on existing tables
            await ExecuteSafe(conn, @"
                IF EXISTS (SELECT * FROM sys.tables WHERE name = 'Stores')
                BEGIN
                    IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Stores') AND name = 'StoreName')
                    AND NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Stores') AND name = 'Name')
                        EXEC sp_rename 'Stores.StoreName', 'Name', 'COLUMN';

                    IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Stores') AND name = 'StoreAddress')
                    AND NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Stores') AND name = 'Address')
                        EXEC sp_rename 'Stores.StoreAddress', 'Address', 'COLUMN';

                    IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Stores') AND name = 'CreatedDate')
                    AND NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Stores') AND name = 'CreatedUtc')
                        EXEC sp_rename 'Stores.CreatedDate', 'CreatedUtc', 'COLUMN';

                    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Stores') AND name = 'Name')
                        ALTER TABLE [Stores] ADD [Name] NVARCHAR(200) NOT NULL DEFAULT '';

                    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Stores') AND name = 'Address')
                        ALTER TABLE [Stores] ADD [Address] NVARCHAR(500) NOT NULL DEFAULT '';

                    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Stores') AND name = 'IsActive')
                        ALTER TABLE [Stores] ADD [IsActive] BIT NOT NULL DEFAULT 1;

                    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Stores') AND name = 'CreatedUtc')
                        ALTER TABLE [Stores] ADD [CreatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME();
                END");

            await ExecuteSafe(conn, @"
                IF OBJECT_ID(N'[dbo].[ShiftLogs]', N'U') IS NOT NULL
                AND COL_LENGTH(N'[dbo].[ShiftLogs]', N'PayoutReason') IS NULL
                    ALTER TABLE [dbo].[ShiftLogs] ADD [PayoutReason] NVARCHAR(300) NOT NULL DEFAULT ''");

            // Phase 2: Create all missing tables
            await ExecuteSafe(conn, @"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Stores')
                CREATE TABLE [Stores] (
                    [Id] INT IDENTITY(1,1) PRIMARY KEY,
                    [Name] NVARCHAR(200) NOT NULL DEFAULT '',
                    [Address] NVARCHAR(500) NOT NULL DEFAULT '',
                    [Phone] NVARCHAR(50) NULL,
                    [IsActive] BIT NOT NULL DEFAULT 1,
                    [CreatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                )");

            await ExecuteSafe(conn, @"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'AppSettings')
                CREATE TABLE [AppSettings] (
                    [Id] INT IDENTITY(1,1) PRIMARY KEY,
                    [StoreName] NVARCHAR(200) NOT NULL DEFAULT '',
                    [StoreAddress] NVARCHAR(500) NOT NULL DEFAULT '',
                    [DefaultStoreId] INT NOT NULL DEFAULT 1,
                    [LastStoreId] INT NOT NULL DEFAULT 1
                )");

            await ExecuteSafe(conn, @"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'UserAccounts')
                CREATE TABLE [UserAccounts] (
                    [Id] INT IDENTITY(1,1) PRIMARY KEY,
                    [StoreId] INT NOT NULL DEFAULT 1,
                    [Username] NVARCHAR(100) NOT NULL DEFAULT '',
                    [Email] NVARCHAR(200) NOT NULL DEFAULT '',
                    [PasswordHashBase64] NVARCHAR(500) NOT NULL DEFAULT '',
                    [SaltBase64] NVARCHAR(500) NOT NULL DEFAULT '',
                    [DisplayName] NVARCHAR(200) NOT NULL DEFAULT '',
                    [Role] INT NOT NULL DEFAULT 0,
                    [IsActive] BIT NOT NULL DEFAULT 1,
                    [SecurityQuestion] NVARCHAR(500) NULL,
                    [SecurityAnswerHash] NVARCHAR(500) NULL,
                    [LastLoginUtc] DATETIME2 NULL,
                    [CreatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                )");

            await ExecuteSafe(conn, @"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ShiftLogs')
                CREATE TABLE [ShiftLogs] (
                    [Id] INT IDENTITY(1,1) PRIMARY KEY,
                    [StoreId] INT NOT NULL DEFAULT 1,
                    [Date] DATE NOT NULL,
                    [Employee] NVARCHAR(200) NOT NULL DEFAULT '',
                    [ShiftNo] NVARCHAR(50) NOT NULL DEFAULT '',
                    [CashTotal] DECIMAL(18,2) NOT NULL DEFAULT 0,
                    [CardTotal] DECIMAL(18,2) NOT NULL DEFAULT 0,
                    [NetSales] DECIMAL(18,2) NOT NULL DEFAULT 0,
                    [Tax] DECIMAL(18,2) NOT NULL DEFAULT 0,
                    [CashDropReceived] DECIMAL(18,2) NOT NULL DEFAULT 0,
                    [RegisterPayout] DECIMAL(18,2) NOT NULL DEFAULT 0,
                    [PayoutReason] NVARCHAR(300) NOT NULL DEFAULT '',
                    [IsCorrection] BIT NOT NULL DEFAULT 0,
                    [CorrectsId] INT NULL,
                    [CreatedByUserId] INT NULL,
                    [CreatedByName] NVARCHAR(200) NOT NULL DEFAULT '',
                    [CreatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    [PosReportPath] NVARCHAR(500) NULL
                )");

            await ExecuteSafe(conn, @"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'CashOnHand')
                CREATE TABLE [CashOnHand] (
                    [Id] INT IDENTITY(1,1) PRIMARY KEY,
                    [StoreId] INT NOT NULL DEFAULT 1,
                    [Date] DATE NOT NULL,
                    [IsPayout] BIT NOT NULL DEFAULT 0,
                    [CashAdded] DECIMAL(18,2) NOT NULL DEFAULT 0,
                    [PayoutAmount] DECIMAL(18,2) NOT NULL DEFAULT 0,
                    [VendorId] INT NULL,
                    [PurposeId] INT NULL,
                    [Description] NVARCHAR(500) NOT NULL DEFAULT '',
                    [Reference] NVARCHAR(200) NULL,
                    [IsCorrection] BIT NOT NULL DEFAULT 0,
                    [CorrectsId] INT NULL,
                    [CreatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                )");

            await ExecuteSafe(conn, @"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'CheckPayouts')
                CREATE TABLE [CheckPayouts] (
                    [Id] INT IDENTITY(1,1) PRIMARY KEY,
                    [StoreId] INT NOT NULL DEFAULT 1,
                    [Date] DATE NOT NULL,
                    [PayTo] NVARCHAR(200) NOT NULL DEFAULT '',
                    [CheckNumber] NVARCHAR(50) NOT NULL DEFAULT '',
                    [CheckAmount] DECIMAL(18,2) NOT NULL DEFAULT 0,
                    [Cleared] BIT NOT NULL DEFAULT 0,
                    [Description] NVARCHAR(500) NOT NULL DEFAULT '',
                    [IsCorrection] BIT NOT NULL DEFAULT 0,
                    [CorrectsId] INT NULL,
                    [CreatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                )");

            await ExecuteSafe(conn, @"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Vendors')
                CREATE TABLE [Vendors] (
                    [Id] INT IDENTITY(1,1) PRIMARY KEY,
                    [StoreId] INT NOT NULL DEFAULT 1,
                    [Name] NVARCHAR(200) NOT NULL DEFAULT ''
                )");

            await ExecuteSafe(conn, @"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Purposes')
                CREATE TABLE [Purposes] (
                    [Id] INT IDENTITY(1,1) PRIMARY KEY,
                    [StoreId] INT NOT NULL DEFAULT 1,
                    [Name] NVARCHAR(200) NOT NULL DEFAULT ''
                )");

            await ExecuteSafe(conn, @"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'PurchaseInvoices')
                CREATE TABLE [PurchaseInvoices] (
                    [Id] INT IDENTITY(1,1) PRIMARY KEY,
                    [StoreId] INT NOT NULL DEFAULT 1,
                    [VendorId] INT NULL,
                    [InvoiceNumber] NVARCHAR(100) NOT NULL DEFAULT '',
                    [InvoiceDate] DATE NOT NULL,
                    [Total] DECIMAL(18,2) NOT NULL DEFAULT 0,
                    [Notes] NVARCHAR(1000) NOT NULL DEFAULT '',
                    [CreatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                )");

            await ExecuteSafe(conn, @"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'PurchaseInvoiceLines')
                CREATE TABLE [PurchaseInvoiceLines] (
                    [Id] INT IDENTITY(1,1) PRIMARY KEY,
                    [InvoiceId] INT NOT NULL,
                    [ProductName] NVARCHAR(300) NOT NULL DEFAULT '',
                    [Quantity] DECIMAL(18,2) NOT NULL DEFAULT 0,
                    [UnitCost] DECIMAL(18,2) NOT NULL DEFAULT 0,
                    [Total] DECIMAL(18,2) NOT NULL DEFAULT 0,
                    CONSTRAINT FK_InvoiceLines_Invoice FOREIGN KEY ([InvoiceId])
                        REFERENCES [PurchaseInvoices]([Id]) ON DELETE CASCADE
                )");

            await ExecuteSafe(conn, @"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ProductCosts')
                CREATE TABLE [ProductCosts] (
                    [Id] INT IDENTITY(1,1) PRIMARY KEY,
                    [StoreId] INT NOT NULL DEFAULT 1,
                    [ProductName] NVARCHAR(300) NOT NULL DEFAULT '',
                    [LatestUnitCost] DECIMAL(18,2) NOT NULL DEFAULT 0,
                    [PreviousUnitCost] DECIMAL(18,2) NOT NULL DEFAULT 0,
                    [VendorId] INT NULL,
                    [LastUpdatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                )");

            await ExecuteSafe(conn, @"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'PriceAlerts')
                CREATE TABLE [PriceAlerts] (
                    [Id] INT IDENTITY(1,1) PRIMARY KEY,
                    [StoreId] INT NOT NULL DEFAULT 1,
                    [ProductName] NVARCHAR(300) NOT NULL DEFAULT '',
                    [OldPrice] DECIMAL(18,2) NOT NULL DEFAULT 0,
                    [NewPrice] DECIMAL(18,2) NOT NULL DEFAULT 0,
                    [ChangePercent] DECIMAL(18,2) NOT NULL DEFAULT 0,
                    [VendorName] NVARCHAR(200) NOT NULL DEFAULT '',
                    [IsRead] BIT NOT NULL DEFAULT 0,
                    [CreatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                )");

            await ExecuteSafe(conn, @"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'BankStatementTransactions')
                CREATE TABLE [BankStatementTransactions] (
                    [Id] INT IDENTITY(1,1) PRIMARY KEY,
                    [StoreId] INT NOT NULL DEFAULT 1,
                    [Date] DATETIME2 NOT NULL,
                    [Description] NVARCHAR(500) NOT NULL DEFAULT '',
                    [Credit] DECIMAL(18,2) NOT NULL DEFAULT 0,
                    [Debit] DECIMAL(18,2) NOT NULL DEFAULT 0,
                    [Balance] DECIMAL(18,2) NOT NULL DEFAULT 0,
                    [Category] NVARCHAR(200) NULL,
                    [CheckNumber] NVARCHAR(50) NULL,
                    [Reference] NVARCHAR(200) NULL,
                    [ImportedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                )");

            // Phase 3: Seed default data if tables are empty
            var sName = storeName ?? "Store 1";
            await ExecuteSafe(conn, $@"
                IF NOT EXISTS (SELECT 1 FROM [Stores])
                    INSERT INTO [Stores] ([Name], [Address], [IsActive]) VALUES ('{Escape(sName)}', '', 1)");

            await ExecuteSafe(conn, $@"
                IF NOT EXISTS (SELECT 1 FROM [AppSettings])
                    INSERT INTO [AppSettings] ([StoreName], [StoreAddress], [DefaultStoreId], [LastStoreId])
                    VALUES ('{Escape(sName)}', '', 1, 1)");

            Debug.WriteLine($"DatabaseSchemaService: Schema verified for {sName}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DatabaseSchemaService.EnsureSchemaAsync failed: {ex.Message}");
            // Don't throw - the app should still try to run with whatever schema exists.
            // Individual operations will catch their own 207/208 errors.
        }
    }

    private static async Task ExecuteSafe(SqlConnection conn, string sql)
    {
        try
        {
            using var cmd = new SqlCommand(sql, conn);
            cmd.CommandTimeout = 30;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DatabaseSchemaService SQL error: {ex.Message}");
        }
    }

    private static string Escape(string value)
        => value.Replace("'", "''");
}
