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
                    [LastStoreId] INT NOT NULL DEFAULT 1,
                    [SmsGatewayEnabled] BIT NOT NULL DEFAULT 0,
                    [SmsGatewayUrl] NVARCHAR(500) NOT NULL DEFAULT '',
                    [SmsGatewayUsername] NVARCHAR(200) NOT NULL DEFAULT '',
                    [SmsGatewayPasswordEncrypted] VARBINARY(MAX) NOT NULL DEFAULT 0x
                );
                IF COL_LENGTH('dbo.AppSettings', 'SmsGatewayEnabled') IS NULL ALTER TABLE dbo.AppSettings ADD SmsGatewayEnabled BIT NOT NULL DEFAULT 0;
                IF COL_LENGTH('dbo.AppSettings', 'SmsGatewayUrl') IS NULL ALTER TABLE dbo.AppSettings ADD SmsGatewayUrl NVARCHAR(500) NOT NULL DEFAULT '';
                IF COL_LENGTH('dbo.AppSettings', 'SmsGatewayUsername') IS NULL ALTER TABLE dbo.AppSettings ADD SmsGatewayUsername NVARCHAR(200) NOT NULL DEFAULT '';
                IF COL_LENGTH('dbo.AppSettings', 'SmsGatewayPasswordEncrypted') IS NULL ALTER TABLE dbo.AppSettings ADD SmsGatewayPasswordEncrypted VARBINARY(MAX) NOT NULL DEFAULT 0x;");

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
                    [ProductKey] NVARCHAR(260) NOT NULL DEFAULT '',
                    [ProductName] NVARCHAR(260) NOT NULL DEFAULT '',
                    [Sku] NVARCHAR(80) NOT NULL DEFAULT '',
                    [OldUnitCost] DECIMAL(18,4) NOT NULL DEFAULT 0,
                    [NewUnitCost] DECIMAL(18,4) NOT NULL DEFAULT 0,
                    [Direction] INT NOT NULL DEFAULT 0,
                    [AlertType] INT NOT NULL DEFAULT 0,
                    [VendorName] NVARCHAR(200) NOT NULL DEFAULT '',
                    [OtherVendorName] NVARCHAR(200) NOT NULL DEFAULT '',
                    [InvoiceNumber] NVARCHAR(100) NOT NULL DEFAULT '',
                    [InvoiceDate] DATE NOT NULL DEFAULT CONVERT(date, SYSUTCDATETIME()),
                    [PurchaseInvoiceId] INT NULL,
                    [IsRead] BIT NOT NULL DEFAULT 0,
                    [ReadUtc] DATETIME2 NULL,
                    [CreatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                )");

            // Older HISAB KITAB databases used OldPrice/NewPrice and did not have the
            // complete price-alert metadata. These upgrades only add missing columns;
            // legacy columns and rows are intentionally left intact for WPF compatibility.
            await EnsureColumnAsync(conn, "PriceAlerts", "ProductKey",
                "NVARCHAR(260) NOT NULL DEFAULT ''",
                "UPDATE [dbo].[PriceAlerts] SET [ProductKey] = UPPER(LTRIM(RTRIM([ProductName]))) WHERE [ProductKey] = ''");
            await EnsureColumnAsync(conn, "PriceAlerts", "Sku", "NVARCHAR(80) NOT NULL DEFAULT ''");
            await EnsureColumnAsync(conn, "PriceAlerts", "OldUnitCost",
                "DECIMAL(18,4) NOT NULL DEFAULT 0",
                "IF COL_LENGTH(N'[dbo].[PriceAlerts]', N'OldPrice') IS NOT NULL UPDATE [dbo].[PriceAlerts] SET [OldUnitCost] = CONVERT(DECIMAL(18,4), [OldPrice])");
            await EnsureColumnAsync(conn, "PriceAlerts", "NewUnitCost",
                "DECIMAL(18,4) NOT NULL DEFAULT 0",
                "IF COL_LENGTH(N'[dbo].[PriceAlerts]', N'NewPrice') IS NOT NULL UPDATE [dbo].[PriceAlerts] SET [NewUnitCost] = CONVERT(DECIMAL(18,4), [NewPrice])");
            await EnsureColumnAsync(conn, "PriceAlerts", "Direction", "INT NOT NULL DEFAULT 0");
            await EnsureColumnAsync(conn, "PriceAlerts", "AlertType", "INT NOT NULL DEFAULT 0");
            await EnsureColumnAsync(conn, "PriceAlerts", "OtherVendorName", "NVARCHAR(200) NOT NULL DEFAULT ''");
            await EnsureColumnAsync(conn, "PriceAlerts", "InvoiceNumber", "NVARCHAR(100) NOT NULL DEFAULT ''");
            await EnsureColumnAsync(conn, "PriceAlerts", "InvoiceDate",
                "DATE NOT NULL DEFAULT CONVERT(date, SYSUTCDATETIME())",
                "UPDATE [dbo].[PriceAlerts] SET [InvoiceDate] = CONVERT(date, [CreatedUtc]) WHERE [CreatedUtc] IS NOT NULL");
            await EnsureColumnAsync(conn, "PriceAlerts", "PurchaseInvoiceId", "INT NULL");
            await EnsureColumnAsync(conn, "PriceAlerts", "ReadUtc", "DATETIME2 NULL");

            await ExecuteSafe(conn, @"
                IF OBJECT_ID(N'[dbo].[PriceAlerts]', N'U') IS NOT NULL
                AND NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE object_id = OBJECT_ID(N'[dbo].[PriceAlerts]')
                    AND name = N'IX_PriceAlerts_StoreReadCreated')
                    CREATE INDEX [IX_PriceAlerts_StoreReadCreated]
                    ON [dbo].[PriceAlerts] ([StoreId], [IsRead], [CreatedUtc])");

            // Payroll and scheduling are optional licensed services. Their tables are
            // additive so older WPF clients safely ignore them.
            await ExecuteSafe(conn, @"
                IF OBJECT_ID(N'[dbo].[Employees]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [dbo].[Employees] (
                        [Id] INT IDENTITY(1,1) PRIMARY KEY,
                        [StoreId] INT NOT NULL DEFAULT 1,
                        [EmployeeNumber] NVARCHAR(30) NOT NULL DEFAULT '',
                        [FirstName] NVARCHAR(80) NOT NULL DEFAULT '',
                        [MiddleInitial] NVARCHAR(10) NOT NULL DEFAULT '',
                        [LastName] NVARCHAR(80) NOT NULL DEFAULT '',
                        [Address] NVARCHAR(300) NOT NULL DEFAULT '',
                        [City] NVARCHAR(100) NOT NULL DEFAULT '',
                        [State] NVARCHAR(20) NOT NULL DEFAULT '',
                        [Zip] NVARCHAR(20) NOT NULL DEFAULT '',
                        [Phone] NVARCHAR(40) NOT NULL DEFAULT '',
                        [Email] NVARCHAR(200) NOT NULL DEFAULT '',
                        [EncryptedSsn] VARBINARY(MAX) NOT NULL DEFAULT 0x,
                        [SsnLast4] NVARCHAR(4) NOT NULL DEFAULT '',
                        [PayRate] DECIMAL(18,4) NOT NULL DEFAULT 0,
                        [PayType] INT NOT NULL DEFAULT 1,
                        [PayFrequency] INT NOT NULL DEFAULT 2,
                        [IsOvertimeEligible] BIT NOT NULL DEFAULT 1,
                        [HolidayMultiplier] DECIMAL(8,4) NOT NULL DEFAULT 1.5,
                        [WorkState] NVARCHAR(20) NOT NULL DEFAULT 'IL',
                        [ResidenceState] NVARCHAR(20) NOT NULL DEFAULT 'IL',
                        [HireDate] DATE NOT NULL DEFAULT CONVERT(date, SYSUTCDATETIME()),
                        [TerminationDate] DATE NULL,
                        [IsActive] BIT NOT NULL DEFAULT 1,
                        [FederalFilingStatus] INT NOT NULL DEFAULT 1,
                        [FederalMultipleJobs] BIT NOT NULL DEFAULT 0,
                        [FederalDependentsCredit] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [FederalOtherIncome] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [FederalDeductions] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [FederalExtraWithholding] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [FederalExempt] BIT NOT NULL DEFAULT 0,
                        [StateFilingStatus] NVARCHAR(50) NOT NULL DEFAULT 'Single',
                        [StateAllowances] INT NOT NULL DEFAULT 0,
                        [StateAdditionalAllowances] INT NOT NULL DEFAULT 0,
                        [StateDeductions] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [StateCredits] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [StateExtraWithholding] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [StateExempt] BIT NOT NULL DEFAULT 0,
                        [StateFormDataJson] NVARCHAR(4000) NOT NULL DEFAULT '{}',
                        [IllinoisLine1Allowances] INT NOT NULL DEFAULT 0,
                        [IllinoisLine2Allowances] INT NOT NULL DEFAULT 0,
                        [IllinoisExtraWithholding] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [W4OnFile] BIT NOT NULL DEFAULT 0,
                        [StateWithholdingOnFile] BIT NOT NULL DEFAULT 0,
                        [EmergencyContactName] NVARCHAR(200) NOT NULL DEFAULT '',
                        [EmergencyContactPhone] NVARCHAR(40) NOT NULL DEFAULT '',
                        [CreatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                        [UpdatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                    );
                    CREATE UNIQUE INDEX [IX_Employees_Store_EmployeeNumber]
                        ON [dbo].[Employees] ([StoreId], [EmployeeNumber]);
                    CREATE INDEX [IX_Employees_Store_Active_LastName]
                        ON [dbo].[Employees] ([StoreId], [IsActive], [LastName]);
                END");

            await EnsureColumnAsync(conn, "Employees", "ResidenceState",
                "NVARCHAR(20) NOT NULL DEFAULT 'IL'",
                "UPDATE [dbo].[Employees] SET [ResidenceState] = CASE WHEN LEN(LTRIM(RTRIM([State]))) = 2 THEN UPPER(LTRIM(RTRIM([State]))) ELSE UPPER(LTRIM(RTRIM([WorkState]))) END WHERE [ResidenceState] = 'IL'");
            await EnsureColumnAsync(conn, "Employees", "StateFilingStatus", "NVARCHAR(50) NOT NULL DEFAULT 'Single'");
            await EnsureColumnAsync(conn, "Employees", "StateAllowances",
                "INT NOT NULL DEFAULT 0",
                "UPDATE [dbo].[Employees] SET [StateAllowances] = [IllinoisLine1Allowances] WHERE [StateAllowances] = 0 AND [IllinoisLine1Allowances] <> 0");
            await EnsureColumnAsync(conn, "Employees", "StateAdditionalAllowances",
                "INT NOT NULL DEFAULT 0",
                "UPDATE [dbo].[Employees] SET [StateAdditionalAllowances] = [IllinoisLine2Allowances] WHERE [StateAdditionalAllowances] = 0 AND [IllinoisLine2Allowances] <> 0");
            await EnsureColumnAsync(conn, "Employees", "StateDeductions", "DECIMAL(18,2) NOT NULL DEFAULT 0");
            await EnsureColumnAsync(conn, "Employees", "StateCredits", "DECIMAL(18,2) NOT NULL DEFAULT 0");
            await EnsureColumnAsync(conn, "Employees", "StateExtraWithholding",
                "DECIMAL(18,2) NOT NULL DEFAULT 0",
                "UPDATE [dbo].[Employees] SET [StateExtraWithholding] = [IllinoisExtraWithholding] WHERE [StateExtraWithholding] = 0 AND [IllinoisExtraWithholding] <> 0");
            await EnsureColumnAsync(conn, "Employees", "StateExempt", "BIT NOT NULL DEFAULT 0");
            await EnsureColumnAsync(conn, "Employees", "StateFormDataJson", "NVARCHAR(4000) NOT NULL DEFAULT '{}'");

            await ExecuteSafe(conn, @"
                IF OBJECT_ID(N'[dbo].[EmployeeDocuments]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [dbo].[EmployeeDocuments] (
                        [Id] INT IDENTITY(1,1) PRIMARY KEY,
                        [StoreId] INT NOT NULL,
                        [EmployeeId] INT NOT NULL,
                        [DocumentType] INT NOT NULL,
                        [FileName] NVARCHAR(260) NOT NULL DEFAULT '',
                        [ContentType] NVARCHAR(120) NOT NULL DEFAULT 'application/octet-stream',
                        [EncryptedContent] VARBINARY(MAX) NOT NULL,
                        [CreatedByName] NVARCHAR(120) NOT NULL DEFAULT '',
                        [CreatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                        CONSTRAINT [FK_EmployeeDocuments_Employees] FOREIGN KEY ([EmployeeId])
                            REFERENCES [dbo].[Employees]([Id]) ON DELETE CASCADE
                    );
                    CREATE INDEX [IX_EmployeeDocuments_Employee_Type_Created]
                        ON [dbo].[EmployeeDocuments] ([EmployeeId], [DocumentType], [CreatedUtc]);
                END");

            await ExecuteSafe(conn, @"
                IF OBJECT_ID(N'[dbo].[ScheduleShifts]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [dbo].[ScheduleShifts] (
                        [Id] INT IDENTITY(1,1) PRIMARY KEY,
                        [StoreId] INT NOT NULL,
                        [EmployeeId] INT NOT NULL,
                        [ShiftDate] DATE NOT NULL,
                        [StartTime] TIME NOT NULL,
                        [EndTime] TIME NOT NULL,
                        [UnpaidBreakMinutes] INT NOT NULL DEFAULT 0,
                        [Status] INT NOT NULL DEFAULT 0,
                        [Notes] NVARCHAR(500) NOT NULL DEFAULT '',
                        [UpdatedByName] NVARCHAR(120) NOT NULL DEFAULT '',
                        [UpdatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                        CONSTRAINT [FK_ScheduleShifts_Employees] FOREIGN KEY ([EmployeeId])
                            REFERENCES [dbo].[Employees]([Id])
                    );
                    CREATE INDEX [IX_ScheduleShifts_Store_Date_Employee]
                        ON [dbo].[ScheduleShifts] ([StoreId], [ShiftDate], [EmployeeId]);
                END");

            await ExecuteSafe(conn, @"
                IF OBJECT_ID(N'[dbo].[PayrollRuns]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [dbo].[PayrollRuns] (
                        [Id] INT IDENTITY(1,1) PRIMARY KEY,
                        [StoreId] INT NOT NULL,
                        [PeriodStart] DATE NOT NULL,
                        [PeriodEnd] DATE NOT NULL,
                        [PayDate] DATE NOT NULL,
                        [PayFrequency] INT NOT NULL,
                        [TaxYear] INT NOT NULL,
                        [Status] INT NOT NULL DEFAULT 0,
                        [TaxRuleSetId] NVARCHAR(100) NOT NULL DEFAULT '',
                        [TaxRuleVersion] NVARCHAR(40) NOT NULL DEFAULT '',
                        [TaxRuleSha256] NVARCHAR(64) NOT NULL DEFAULT '',
                        [TaxRuleSources] NVARCHAR(1000) NOT NULL DEFAULT '',
                        [TaxRulesVerifiedUtc] DATETIME2 NULL,
                        [CreatedByName] NVARCHAR(120) NOT NULL DEFAULT '',
                        [CreatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                        [ApprovedByName] NVARCHAR(120) NOT NULL DEFAULT '',
                        [ApprovedUtc] DATETIME2 NULL,
                        [FinalizedByName] NVARCHAR(120) NOT NULL DEFAULT '',
                        [FinalizedUtc] DATETIME2 NULL
                    );
                    CREATE INDEX [IX_PayrollRuns_Store_Period]
                        ON [dbo].[PayrollRuns] ([StoreId], [PeriodStart], [PeriodEnd]);
                END");

            await EnsureColumnAsync(conn, "PayrollRuns", "TaxRuleSetId", "NVARCHAR(100) NOT NULL DEFAULT ''");
            await EnsureColumnAsync(conn, "PayrollRuns", "TaxRuleVersion", "NVARCHAR(40) NOT NULL DEFAULT ''");
            await EnsureColumnAsync(conn, "PayrollRuns", "TaxRuleSha256", "NVARCHAR(64) NOT NULL DEFAULT ''");
            await EnsureColumnAsync(conn, "PayrollRuns", "TaxRuleSources", "NVARCHAR(1000) NOT NULL DEFAULT ''");
            await EnsureColumnAsync(conn, "PayrollRuns", "TaxRulesVerifiedUtc", "DATETIME2 NULL");

            await ExecuteSafe(conn, @"
                IF OBJECT_ID(N'[dbo].[EmployeePeriodHours]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [dbo].[EmployeePeriodHours] (
                        [Id] INT IDENTITY(1,1) PRIMARY KEY,
                        [StoreId] INT NOT NULL,
                        [EmployeeId] INT NOT NULL,
                        [PeriodStart] DATE NOT NULL,
                        [PeriodEnd] DATE NOT NULL,
                        [RegularHours] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [OvertimeHours] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [HolidayHours] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [Notes] NVARCHAR(500) NOT NULL DEFAULT '',
                        [UpdatedByName] NVARCHAR(120) NOT NULL DEFAULT '',
                        [UpdatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                        CONSTRAINT [FK_EmployeePeriodHours_Employees] FOREIGN KEY ([EmployeeId])
                            REFERENCES [dbo].[Employees]([Id])
                    );
                    CREATE UNIQUE INDEX [IX_EmployeePeriodHours_Store_Employee_Period]
                        ON [dbo].[EmployeePeriodHours] ([StoreId], [EmployeeId], [PeriodStart], [PeriodEnd]);
                END");

            await ExecuteSafe(conn, @"
                IF OBJECT_ID(N'[dbo].[PayrollEntries]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [dbo].[PayrollEntries] (
                        [Id] INT IDENTITY(1,1) PRIMARY KEY,
                        [PayrollRunId] INT NOT NULL,
                        [EmployeeId] INT NOT NULL,
                        [EmployeeName] NVARCHAR(200) NOT NULL DEFAULT '',
                        [PayRate] DECIMAL(18,4) NOT NULL DEFAULT 0,
                        [PayType] INT NOT NULL DEFAULT 0,
                        [ScheduledHours] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [RegularHours] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [OvertimeHours] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [HolidayHours] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [BonusPay] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [RegularPay] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [OvertimePay] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [HolidayPay] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [CashAdvanceDeduction] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [OtherDeduction] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [GrossPay] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [FederalWithholding] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [SocialSecurityWithholding] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [MedicareWithholding] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [StateWithholding] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [WorkState] NVARCHAR(2) NOT NULL DEFAULT '',
                        [StateTaxRuleId] NVARCHAR(100) NOT NULL DEFAULT '',
                        [StateTaxRuleVersion] NVARCHAR(40) NOT NULL DEFAULT '',
                        [NetPay] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [GrossPayYtd] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [FederalWithholdingYtd] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [SocialSecurityWithholdingYtd] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [MedicareWithholdingYtd] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [StateWithholdingYtd] DECIMAL(18,2) NOT NULL DEFAULT 0,
                        [CheckNumber] NVARCHAR(50) NOT NULL DEFAULT '',
                        [OverrideReason] NVARCHAR(500) NOT NULL DEFAULT '',
                        CONSTRAINT [FK_PayrollEntries_PayrollRuns] FOREIGN KEY ([PayrollRunId])
                            REFERENCES [dbo].[PayrollRuns]([Id]) ON DELETE CASCADE
                    );
                    CREATE UNIQUE INDEX [IX_PayrollEntries_Run_Employee]
                        ON [dbo].[PayrollEntries] ([PayrollRunId], [EmployeeId]);
                END
                IF COL_LENGTH('dbo.PayrollEntries', 'PayType') IS NULL ALTER TABLE dbo.PayrollEntries ADD PayType INT NOT NULL DEFAULT 0;
                IF COL_LENGTH('dbo.PayrollEntries', 'RegularPay') IS NULL ALTER TABLE dbo.PayrollEntries ADD RegularPay DECIMAL(18,2) NOT NULL DEFAULT 0;
                IF COL_LENGTH('dbo.PayrollEntries', 'OvertimePay') IS NULL ALTER TABLE dbo.PayrollEntries ADD OvertimePay DECIMAL(18,2) NOT NULL DEFAULT 0;
                IF COL_LENGTH('dbo.PayrollEntries', 'HolidayPay') IS NULL ALTER TABLE dbo.PayrollEntries ADD HolidayPay DECIMAL(18,2) NOT NULL DEFAULT 0;");

            await EnsureColumnAsync(conn, "PayrollEntries", "WorkState", "NVARCHAR(2) NOT NULL DEFAULT ''");
            await EnsureColumnAsync(conn, "PayrollEntries", "StateTaxRuleId", "NVARCHAR(100) NOT NULL DEFAULT ''");
            await EnsureColumnAsync(conn, "PayrollEntries", "StateTaxRuleVersion", "NVARCHAR(40) NOT NULL DEFAULT ''");

            await ExecuteSafe(conn, @"
                IF OBJECT_ID(N'[dbo].[PayrollAuditEntries]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [dbo].[PayrollAuditEntries] (
                        [Id] INT IDENTITY(1,1) PRIMARY KEY,
                        [StoreId] INT NOT NULL,
                        [PayrollRunId] INT NOT NULL,
                        [PayrollEntryId] INT NULL,
                        [Action] NVARCHAR(80) NOT NULL DEFAULT '',
                        [Details] NVARCHAR(1000) NOT NULL DEFAULT '',
                        [PerformedByName] NVARCHAR(120) NOT NULL DEFAULT '',
                        [PerformedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                    );
                    CREATE INDEX [IX_PayrollAudit_Run_Performed]
                        ON [dbo].[PayrollAuditEntries] ([PayrollRunId], [PerformedUtc]);
                END");

            await ExecuteSafe(conn, @"
                IF OBJECT_ID(N'[dbo].[ScheduleNotifications]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [dbo].[ScheduleNotifications] (
                        [Id] INT IDENTITY(1,1) PRIMARY KEY,
                        [StoreId] INT NOT NULL,
                        [EmployeeId] INT NOT NULL,
                        [ScheduleFrom] DATE NOT NULL,
                        [ScheduleTo] DATE NOT NULL,
                        [PhoneNumber] NVARCHAR(40) NOT NULL DEFAULT '',
                        [MessageText] NVARCHAR(2000) NOT NULL DEFAULT '',
                        [Status] NVARCHAR(30) NOT NULL DEFAULT 'Pending',
                        [GatewayResponse] NVARCHAR(1000) NOT NULL DEFAULT '',
                        [CreatedByName] NVARCHAR(120) NOT NULL DEFAULT '',
                        [CreatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                        [SentUtc] DATETIME2 NULL
                    );
                    CREATE INDEX [IX_ScheduleNotifications_Store_Period_Employee]
                        ON [dbo].[ScheduleNotifications] ([StoreId], [ScheduleFrom], [EmployeeId]);
                END");

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
                    [IsMatched] BIT NOT NULL DEFAULT 0,
                    [MatchReference] NVARCHAR(200) NOT NULL DEFAULT '',
                    [IncludeInProfitLoss] BIT NOT NULL DEFAULT 0,
                    [ImportedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                )");

            await EnsureColumnAsync(conn, "BankStatementTransactions", "IsMatched", "BIT NOT NULL CONSTRAINT DF_BankStatementTransactions_IsMatched DEFAULT 0");
            await EnsureColumnAsync(conn, "BankStatementTransactions", "MatchReference", "NVARCHAR(200) NOT NULL CONSTRAINT DF_BankStatementTransactions_MatchReference DEFAULT ''");
            await EnsureColumnAsync(conn, "BankStatementTransactions", "IncludeInProfitLoss", "BIT NOT NULL CONSTRAINT DF_BankStatementTransactions_IncludeInProfitLoss DEFAULT 0");

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

    private static async Task EnsureColumnAsync(
        SqlConnection conn,
        string table,
        string column,
        string definition,
        string? afterAddSql = null)
    {
        var safeTable = table.Replace("]", "]]", StringComparison.Ordinal);
        var safeColumn = column.Replace("]", "]]", StringComparison.Ordinal);
        var afterAdd = string.IsNullOrWhiteSpace(afterAddSql)
            ? ""
            : $"EXEC sys.sp_executesql N'{afterAddSql.Replace("'", "''", StringComparison.Ordinal)}';";

        await ExecuteSafe(conn, $@"
            IF OBJECT_ID(N'[dbo].[{safeTable}]', N'U') IS NOT NULL
            AND COL_LENGTH(N'[dbo].[{safeTable}]', N'{safeColumn}') IS NULL
            BEGIN
                ALTER TABLE [dbo].[{safeTable}] ADD [{safeColumn}] {definition};
                {afterAdd}
            END");
    }

    private static string Escape(string value)
        => value.Replace("'", "''");
}
