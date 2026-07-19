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
                AND COL_LENGTH(N'[dbo].[ShiftLogs]', N'PayoutReason') IS NULL
                BEGIN
                    ALTER TABLE [dbo].[ShiftLogs] ADD [PayoutReason] NVARCHAR(300) NOT NULL DEFAULT '';
                END";
            await shiftCmd.ExecuteNonQueryAsync(ct);

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
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
