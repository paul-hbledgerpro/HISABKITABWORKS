using ManagerPaperworkSystem.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.Data.Services;

public static class DbInitializer
{
    public static async Task InitializeAsync(AppDbContext db, CancellationToken ct = default)
    {
        // Verify we can connect to the database
        if (!await db.Database.CanConnectAsync(ct))
        {
            throw new Exception("Cannot connect to database. Please ensure the database server is running, the database exists, and the saved connection settings are correct.");
        }

        // Check if core tables exist
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        bool tablesExist;
        try
        {
            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'AppSettings'";
            tablesExist = (int)(await checkCmd.ExecuteScalarAsync(ct) ?? 0) > 0;
        }
        finally
        {
            await conn.CloseAsync();
        }

        if (!tablesExist)
        {
            // Database exists but tables haven't been created yet.
            // Create all tables from the EF model automatically.
            await db.Database.EnsureCreatedAsync(ct);
        }

        // Run migrations for new columns (safe to run multiple times)
        await RunMigrationsAsync(db, ct);

        // Ensure Settings row exists
        if (!await db.Settings.AnyAsync(ct))
        {
            db.Settings.Add(new ManagerPaperworkSystem.Core.Models.AppSettings());
            await db.SaveChangesAsync(ct);
        }

        // Ensure default store exists
        await DbMigrator.EnsureDefaultStoreAsync(db, ct);
    }

    /// <summary>
    /// Adds new columns to existing tables. Safe to run on every startup.
    /// Skips gracefully if tables don't exist yet (fresh database before schema creation).
    /// </summary>
    private static async Task RunMigrationsAsync(AppDbContext db, CancellationToken ct)
    {
        if (db.Database.IsSqlite())
        {
            await RunSqliteMigrationsAsync(db, ct);
            return;
        }

        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        try
        {
            // Migration: Add Email column to UserAccounts if missing
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                IF OBJECT_ID(N'[dbo].[UserAccounts]', N'U') IS NOT NULL
                AND COL_LENGTH(N'[dbo].[UserAccounts]', N'Email') IS NULL
                BEGIN
                    ALTER TABLE [dbo].[UserAccounts] ADD [Email] NVARCHAR(200) NOT NULL DEFAULT '';
                    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_UserAccounts_Email' AND object_id = OBJECT_ID(N'[dbo].[UserAccounts]'))
                        CREATE INDEX IX_UserAccounts_Email ON [dbo].[UserAccounts]([Email]) WHERE [Email] <> '';
                END";
            await cmd.ExecuteNonQueryAsync(ct);

            using var shiftCmd = conn.CreateCommand();
            shiftCmd.CommandText = @"
                IF OBJECT_ID(N'[dbo].[ShiftLogs]', N'U') IS NOT NULL
                BEGIN
                    IF COL_LENGTH(N'[dbo].[ShiftLogs]', N'PayoutReason') IS NULL
                        ALTER TABLE [dbo].[ShiftLogs] ADD [PayoutReason] NVARCHAR(300) NOT NULL DEFAULT '';
                    IF COL_LENGTH(N'[dbo].[ShiftLogs]', N'PosSalesSummaryId') IS NULL
                        ALTER TABLE [dbo].[ShiftLogs] ADD [PosSalesSummaryId] INT NULL;
                    IF COL_LENGTH(N'[dbo].[ShiftLogs]', N'PosReportKey') IS NULL
                        ALTER TABLE [dbo].[ShiftLogs] ADD [PosReportKey] NVARCHAR(200) NOT NULL DEFAULT '';
                    IF COL_LENGTH(N'[dbo].[ShiftLogs]', N'PosReportPath') IS NULL
                        ALTER TABLE [dbo].[ShiftLogs] ADD [PosReportPath] NVARCHAR(500) NOT NULL DEFAULT '';
                    IF COL_LENGTH(N'[dbo].[ShiftLogs]', N'CorrectionReason') IS NULL
                        ALTER TABLE [dbo].[ShiftLogs] ADD [CorrectionReason] NVARCHAR(300) NOT NULL DEFAULT '';

                    -- Legacy databases and older import paths can contain NULL in
                    -- columns that the current EF model reads as required strings.
                    -- Normalize them before any dashboard query materializes rows.
                    UPDATE [dbo].[ShiftLogs]
                    SET [Employee] = COALESCE([Employee], ''),
                        [ShiftNo] = COALESCE([ShiftNo], ''),
                        [PayoutReason] = COALESCE([PayoutReason], ''),
                        [PosReportKey] = COALESCE([PosReportKey], ''),
                        [PosReportPath] = COALESCE([PosReportPath], ''),
                        [CreatedByName] = COALESCE([CreatedByName], ''),
                        [CreatedByUserId] = COALESCE([CreatedByUserId], 0),
                        [CorrectionReason] = COALESCE([CorrectionReason], '');
                    IF NOT EXISTS (
                        SELECT 1 FROM sys.indexes
                        WHERE name = 'UX_ShiftLogs_PosSalesSummaryId'
                          AND object_id = OBJECT_ID(N'[dbo].[ShiftLogs]'))
                        CREATE UNIQUE INDEX [UX_ShiftLogs_PosSalesSummaryId]
                            ON [dbo].[ShiftLogs] ([PosSalesSummaryId])
                            WHERE [PosSalesSummaryId] IS NOT NULL;
                    IF NOT EXISTS (
                        SELECT 1 FROM sys.indexes
                        WHERE name = 'UX_ShiftLogs_Store_PosReportKey'
                          AND object_id = OBJECT_ID(N'[dbo].[ShiftLogs]'))
                        CREATE UNIQUE INDEX [UX_ShiftLogs_Store_PosReportKey]
                            ON [dbo].[ShiftLogs] ([StoreId], [PosReportKey])
                            WHERE [PosReportKey] <> '';
                END";
            await shiftCmd.ExecuteNonQueryAsync(ct);

            using var posSummaryMigrationCmd = conn.CreateCommand();
            posSummaryMigrationCmd.CommandText = @"
                IF OBJECT_ID(N'[dbo].[PosSalesSummaries]', N'U') IS NOT NULL
                BEGIN
                    IF COL_LENGTH(N'[dbo].[PosSalesSummaries]', N'CashDropReceived') IS NULL
                        ALTER TABLE [dbo].[PosSalesSummaries] ADD [CashDropReceived] DECIMAL(18,2) NOT NULL DEFAULT 0;
                    IF COL_LENGTH(N'[dbo].[PosSalesSummaries]', N'RegisterPayout') IS NULL
                        ALTER TABLE [dbo].[PosSalesSummaries] ADD [RegisterPayout] DECIMAL(18,2) NOT NULL DEFAULT 0;
                    IF COL_LENGTH(N'[dbo].[PosSalesSummaries]', N'PayoutReason') IS NULL
                        ALTER TABLE [dbo].[PosSalesSummaries] ADD [PayoutReason] NVARCHAR(300) NOT NULL DEFAULT '';
                    IF COL_LENGTH(N'[dbo].[PosSalesSummaries]', N'IsReconciled') IS NULL
                        ALTER TABLE [dbo].[PosSalesSummaries] ADD [IsReconciled] BIT NOT NULL DEFAULT 0;
                    IF COL_LENGTH(N'[dbo].[PosSalesSummaries]', N'ReconciledByUserId') IS NULL
                        ALTER TABLE [dbo].[PosSalesSummaries] ADD [ReconciledByUserId] INT NULL;
                    IF COL_LENGTH(N'[dbo].[PosSalesSummaries]', N'ReconciledByName') IS NULL
                        ALTER TABLE [dbo].[PosSalesSummaries] ADD [ReconciledByName] NVARCHAR(120) NOT NULL DEFAULT '';
                    IF COL_LENGTH(N'[dbo].[PosSalesSummaries]', N'ReconciledUtc') IS NULL
                        ALTER TABLE [dbo].[PosSalesSummaries] ADD [ReconciledUtc] DATETIME2 NULL;
                END";
            await posSummaryMigrationCmd.ExecuteNonQueryAsync(ct);

            using var settingsCmd = conn.CreateCommand();
            settingsCmd.CommandText = @"
                IF OBJECT_ID(N'[dbo].[AppSettings]', N'U') IS NOT NULL
                BEGIN
                    IF COL_LENGTH(N'[dbo].[AppSettings]', N'DefaultReportType') IS NULL
                        ALTER TABLE [dbo].[AppSettings] ADD [DefaultReportType] INT NOT NULL DEFAULT 1;
                    IF COL_LENGTH(N'[dbo].[AppSettings]', N'ScreenMode') IS NULL
                        ALTER TABLE [dbo].[AppSettings] ADD [ScreenMode] INT NOT NULL DEFAULT 0;
                    IF COL_LENGTH(N'[dbo].[AppSettings]', N'AccountantEmail') IS NULL
                        ALTER TABLE [dbo].[AppSettings] ADD [AccountantEmail] NVARCHAR(254) NOT NULL DEFAULT '';
                    IF COL_LENGTH(N'[dbo].[AppSettings]', N'AutoEmailBankStatementOnFifth') IS NULL
                        ALTER TABLE [dbo].[AppSettings] ADD [AutoEmailBankStatementOnFifth] BIT NOT NULL DEFAULT 0;
                END";
            await settingsCmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private static async Task RunSqliteMigrationsAsync(AppDbContext db, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        try
        {
            using var tableCmd = conn.CreateCommand();
            tableCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ShiftLogs'";
            if (Convert.ToInt32(await tableCmd.ExecuteScalarAsync(ct) ?? 0) == 0)
                return;

            using var columnCmd = conn.CreateCommand();
            columnCmd.CommandText = "PRAGMA table_info('ShiftLogs')";
            var hasPayoutReason = false;
            await using (var reader = await columnCmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    if (string.Equals(reader["name"]?.ToString(), "PayoutReason", StringComparison.OrdinalIgnoreCase))
                    {
                        hasPayoutReason = true;
                        break;
                    }
                }
            }

            if (!hasPayoutReason)
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE ShiftLogs ADD COLUMN PayoutReason TEXT NOT NULL DEFAULT ''";
                await alterCmd.ExecuteNonQueryAsync(ct);
            }
            await EnsureSqliteColumnAsync(conn, "ShiftLogs", "PosSalesSummaryId", "INTEGER NULL", ct);
            await EnsureSqliteColumnAsync(conn, "ShiftLogs", "PosReportKey", "TEXT NOT NULL DEFAULT ''", ct);
            await EnsureSqliteColumnAsync(conn, "ShiftLogs", "PosReportPath", "TEXT NOT NULL DEFAULT ''", ct);
            await EnsureSqliteColumnAsync(conn, "ShiftLogs", "CorrectionReason", "TEXT NOT NULL DEFAULT ''", ct);
            using (var normalizeShiftCmd = conn.CreateCommand())
            {
                normalizeShiftCmd.CommandText = @"
                    UPDATE ShiftLogs
                    SET Employee = COALESCE(Employee, ''),
                        ShiftNo = COALESCE(ShiftNo, ''),
                        PayoutReason = COALESCE(PayoutReason, ''),
                        PosReportKey = COALESCE(PosReportKey, ''),
                        PosReportPath = COALESCE(PosReportPath, ''),
                        CreatedByName = COALESCE(CreatedByName, ''),
                        CreatedByUserId = COALESCE(CreatedByUserId, 0),
                        CorrectionReason = COALESCE(CorrectionReason, '')";
                await normalizeShiftCmd.ExecuteNonQueryAsync(ct);
            }
            using (var shiftIndexCmd = conn.CreateCommand())
            {
                shiftIndexCmd.CommandText =
                    "CREATE UNIQUE INDEX IF NOT EXISTS UX_ShiftLogs_PosSalesSummaryId ON ShiftLogs (PosSalesSummaryId)";
                await shiftIndexCmd.ExecuteNonQueryAsync(ct);
            }
            using (var reportIndexCmd = conn.CreateCommand())
            {
                reportIndexCmd.CommandText =
                    "CREATE UNIQUE INDEX IF NOT EXISTS UX_ShiftLogs_Store_PosReportKey ON ShiftLogs (StoreId, PosReportKey) WHERE PosReportKey <> ''";
                await reportIndexCmd.ExecuteNonQueryAsync(ct);
            }

            using var settingsColumnCmd = conn.CreateCommand();
            settingsColumnCmd.CommandText = "PRAGMA table_info('AppSettings')";
            var settingsColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var reader = await settingsColumnCmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    settingsColumns.Add(reader["name"]?.ToString() ?? "");
            }
            if (!settingsColumns.Contains("AccountantEmail"))
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE AppSettings ADD COLUMN AccountantEmail TEXT NOT NULL DEFAULT ''";
                await alterCmd.ExecuteNonQueryAsync(ct);
            }
            if (!settingsColumns.Contains("DefaultReportType"))
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE AppSettings ADD COLUMN DefaultReportType INTEGER NOT NULL DEFAULT 1";
                await alterCmd.ExecuteNonQueryAsync(ct);
            }
            if (!settingsColumns.Contains("ScreenMode"))
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE AppSettings ADD COLUMN ScreenMode INTEGER NOT NULL DEFAULT 0";
                await alterCmd.ExecuteNonQueryAsync(ct);
            }
            if (!settingsColumns.Contains("AutoEmailBankStatementOnFifth"))
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE AppSettings ADD COLUMN AutoEmailBankStatementOnFifth INTEGER NOT NULL DEFAULT 0";
                await alterCmd.ExecuteNonQueryAsync(ct);
            }

            // SQLite is used by local/test installations. Ensure the consolidated
            // POS report tables are added even when EnsureCreated ran in an older build.
            using var posSummaryCmd = conn.CreateCommand();
            posSummaryCmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS PosSalesSummaries (
                    Id INTEGER NOT NULL CONSTRAINT PK_PosSalesSummaries PRIMARY KEY AUTOINCREMENT,
                    StoreId INTEGER NOT NULL DEFAULT 1,
                    ReportFrom TEXT NOT NULL,
                    ReportTo TEXT NOT NULL,
                    SourceSystem TEXT NOT NULL DEFAULT 'AdventPOS',
                    ReportedStoreName TEXT NOT NULL DEFAULT '',
                    SourceFileName TEXT NOT NULL DEFAULT '',
                    SourceFilePath TEXT NOT NULL DEFAULT '',
                    SourceFileSha256 TEXT NOT NULL DEFAULT '',
                    TenderTransactionCount INTEGER NOT NULL DEFAULT 0,
                    GrossAmountReceived TEXT NOT NULL DEFAULT '0',
                    GiftCardRedeemed TEXT NOT NULL DEFAULT '0',
                    NonRevenueReceived TEXT NOT NULL DEFAULT '0',
                    NonRevenueReturned TEXT NOT NULL DEFAULT '0',
                    NonRevenueAmount TEXT NOT NULL DEFAULT '0',
                    GrossSales TEXT NOT NULL DEFAULT '0',
                    Taxes TEXT NOT NULL DEFAULT '0',
                    NetSales TEXT NOT NULL DEFAULT '0',
                    TaxableSales TEXT NOT NULL DEFAULT '0',
                    NonTaxableSales TEXT NOT NULL DEFAULT '0',
                    RoundingOffset TEXT NOT NULL DEFAULT '0',
                    CashSales TEXT NOT NULL DEFAULT '0',
                    CardSales TEXT NOT NULL DEFAULT '0',
                    CustomerTransactionCount INTEGER NOT NULL DEFAULT 0,
                    CustomerAverageSale TEXT NOT NULL DEFAULT '0',
                    UserLoginCount INTEGER NOT NULL DEFAULT 0,
                    DeleteVoidCount INTEGER NOT NULL DEFAULT 0,
                    NoSaleCount INTEGER NOT NULL DEFAULT 0,
                    VoidDeleteAmount TEXT NOT NULL DEFAULT '0',
                    TotalDiscount TEXT NOT NULL DEFAULT '0',
                    DepartmentQuantity TEXT NOT NULL DEFAULT '0',
                    DepartmentSales TEXT NOT NULL DEFAULT '0',
                    DepartmentCost TEXT NOT NULL DEFAULT '0',
                    DepartmentProfit TEXT NOT NULL DEFAULT '0',
                    DepartmentProfitPercent TEXT NOT NULL DEFAULT '0',
                    CashDropReceived TEXT NOT NULL DEFAULT '0',
                    RegisterPayout TEXT NOT NULL DEFAULT '0',
                    PayoutReason TEXT NOT NULL DEFAULT '',
                    IsReconciled INTEGER NOT NULL DEFAULT 0,
                    ReconciledByUserId INTEGER NULL,
                    ReconciledByName TEXT NOT NULL DEFAULT '',
                    ReconciledUtc TEXT NULL,
                    ImportedByUserId INTEGER NOT NULL DEFAULT 0,
                    ImportedByName TEXT NOT NULL DEFAULT '',
                    ImportedUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_PosSalesSummaries_Store_Period
                    ON PosSalesSummaries (StoreId, ReportFrom, ReportTo);
                CREATE UNIQUE INDEX IF NOT EXISTS UX_PosSalesSummaries_Store_Hash
                    ON PosSalesSummaries (StoreId, SourceFileSha256);

                CREATE TABLE IF NOT EXISTS PosSalesTenderLines (
                    Id INTEGER NOT NULL CONSTRAINT PK_PosSalesTenderLines PRIMARY KEY AUTOINCREMENT,
                    PosSalesSummaryId INTEGER NOT NULL,
                    TenderType TEXT NOT NULL DEFAULT '',
                    TransactionCount INTEGER NOT NULL DEFAULT 0,
                    Amount TEXT NOT NULL DEFAULT '0',
                    CONSTRAINT FK_PosSalesTenderLines_Summaries FOREIGN KEY (PosSalesSummaryId)
                        REFERENCES PosSalesSummaries (Id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_PosSalesTenderLines_Summary
                    ON PosSalesTenderLines (PosSalesSummaryId);

                CREATE TABLE IF NOT EXISTS PosSalesHourlyLines (
                    Id INTEGER NOT NULL CONSTRAINT PK_PosSalesHourlyLines PRIMARY KEY AUTOINCREMENT,
                    PosSalesSummaryId INTEGER NOT NULL,
                    TimePeriod TEXT NOT NULL DEFAULT '',
                    TransactionCount INTEGER NOT NULL DEFAULT 0,
                    Amount TEXT NOT NULL DEFAULT '0',
                    CONSTRAINT FK_PosSalesHourlyLines_Summaries FOREIGN KEY (PosSalesSummaryId)
                        REFERENCES PosSalesSummaries (Id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_PosSalesHourlyLines_Summary
                    ON PosSalesHourlyLines (PosSalesSummaryId);

                CREATE TABLE IF NOT EXISTS PosSalesDepartmentLines (
                    Id INTEGER NOT NULL CONSTRAINT PK_PosSalesDepartmentLines PRIMARY KEY AUTOINCREMENT,
                    PosSalesSummaryId INTEGER NOT NULL,
                    Department TEXT NOT NULL DEFAULT '',
                    Quantity TEXT NOT NULL DEFAULT '0',
                    Sales TEXT NOT NULL DEFAULT '0',
                    Cost TEXT NOT NULL DEFAULT '0',
                    Profit TEXT NOT NULL DEFAULT '0',
                    ProfitPercent TEXT NOT NULL DEFAULT '0',
                    SalesPercent TEXT NOT NULL DEFAULT '0',
                    CONSTRAINT FK_PosSalesDepartmentLines_Summaries FOREIGN KEY (PosSalesSummaryId)
                        REFERENCES PosSalesSummaries (Id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_PosSalesDepartmentLines_Summary
                    ON PosSalesDepartmentLines (PosSalesSummaryId);";
            await posSummaryCmd.ExecuteNonQueryAsync(ct);

            await EnsureSqliteColumnAsync(conn, "PosSalesSummaries", "CashDropReceived", "TEXT NOT NULL DEFAULT '0'", ct);
            await EnsureSqliteColumnAsync(conn, "PosSalesSummaries", "RegisterPayout", "TEXT NOT NULL DEFAULT '0'", ct);
            await EnsureSqliteColumnAsync(conn, "PosSalesSummaries", "PayoutReason", "TEXT NOT NULL DEFAULT ''", ct);
            await EnsureSqliteColumnAsync(conn, "PosSalesSummaries", "IsReconciled", "INTEGER NOT NULL DEFAULT 0", ct);
            await EnsureSqliteColumnAsync(conn, "PosSalesSummaries", "ReconciledByUserId", "INTEGER NULL", ct);
            await EnsureSqliteColumnAsync(conn, "PosSalesSummaries", "ReconciledByName", "TEXT NOT NULL DEFAULT ''", ct);
            await EnsureSqliteColumnAsync(conn, "PosSalesSummaries", "ReconciledUtc", "TEXT NULL", ct);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private static async Task EnsureSqliteColumnAsync(
        System.Data.Common.DbConnection connection,
        string tableName,
        string columnName,
        string definition,
        CancellationToken ct)
    {
        using var tableCheck = connection.CreateCommand();
        tableCheck.CommandText =
            $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{tableName.Replace("'", "''")}'";
        if (Convert.ToInt32(await tableCheck.ExecuteScalarAsync(ct) ?? 0) == 0)
            return;

        using var columnCheck = connection.CreateCommand();
        columnCheck.CommandText = $"PRAGMA table_info('{tableName.Replace("'", "''")}')";
        await using (var reader = await columnCheck.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (string.Equals(reader["name"]?.ToString(), columnName, StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText =
            $"ALTER TABLE \"{tableName.Replace("\"", "\"\"")}\" ADD COLUMN \"{columnName.Replace("\"", "\"\"")}\" {definition}";
        await alter.ExecuteNonQueryAsync(ct);
    }
}
