using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.Data.Services;
using ManagerPaperworkSystem.WinForms;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ManagerPaperworkSystem.PosImport.Tests;

public sealed class ClosedMonthStartupTests
{
    [SqlServerFact]
    public async Task StartupRepairsOnlyEditableLegacyNullsIncludingCorrectionGuards()
    {
        var name = "HK_Startup_Test_" + Guid.NewGuid().ToString("N");
        var cs = $"Server={Environment.GetEnvironmentVariable("HK_TEST_SQL_SERVER")};Database={name};Integrated Security=True;TrustServerCertificate=True;Pooling=False";
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options);
        var month = LedgerMonthService.Month(DateOnly.FromDateTime(DateTime.Today)).AddMonths(-1);
        try
        {
            await db.Database.EnsureCreatedAsync(); await DbInitializer.InitializeAsync(db);
            var first = await db.Stores.Select(x => x.Id).FirstAsync();
            var other = new Store { Name = "Other fixture" }; db.Stores.Add(other); await db.SaveChangesAsync();
            var original = new ShiftLogEntry { StoreId = first, Date = month, ShiftNo = "Closed", CashTotal = 100 };
            db.ShiftLogs.Add(original); await db.SaveChangesAsync();
            db.ShiftLogs.AddRange(new ShiftLogEntry { StoreId = first, Date = month.AddMonths(1), ShiftNo = "Correction", CorrectsId = original.Id, IsCorrection = true },
                new ShiftLogEntry { StoreId = first, Date = month.AddMonths(1), ShiftNo = "Open" },
                new ShiftLogEntry { StoreId = other.Id, Date = month, ShiftNo = "Other" });
            db.PosSalesSummaries.Add(new PosSalesSummary { StoreId = first, ReportFrom = month, ReportTo = month });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.ShiftLogs ALTER COLUMN Employee nvarchar(200) NULL; UPDATE dbo.ShiftLogs SET Employee=NULL;");
            await LedgerMonthService.SetClosedAsync(db, first, month, true, "Owner");
            await DatabaseSchemaService.EnsureSchemaAsync(cs); await DbInitializer.InitializeAsync(db);
            using var connection = new SqlConnection(cs); await connection.OpenAsync();
            using var query = new SqlCommand("SELECT COUNT(*) FROM dbo.ShiftLogs WHERE (ShiftNo IN ('Closed','Correction') AND Employee IS NULL) OR (ShiftNo IN ('Open','Other') AND Employee='')", connection);
            Assert.Equal(4, Convert.ToInt32(await query.ExecuteScalarAsync()));
            // Startup must not weaken the guard for corrections posted in a later month.
            var blocked = await Assert.ThrowsAsync<SqlException>(() => db.ShiftLogs.Where(x => x.ShiftNo == "Correction").ExecuteUpdateAsync(s => s.SetProperty(x => x.Employee, "Changed")));
            Assert.True(DatabaseStartupFailurePolicy.IsClosedPeriod(blocked));
        }
        finally
        {
            if (!name.StartsWith("HK_Startup_Test_") || name.Length != 48) throw new Exception("Unexpected fixture database name.");
            await db.Database.EnsureDeletedAsync();
        }
    }

    [SqlServerFact]
    public async Task RepeatedStartupAfterMonthClosePreservesRecordsAndClosedMonthGuards()
    {
        var name = "HK_Startup_Test_" + Guid.NewGuid().ToString("N");
        var cs = $"Server={Environment.GetEnvironmentVariable("HK_TEST_SQL_SERVER")};Database={name};Integrated Security=True;TrustServerCertificate=True;Pooling=False";
        AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options);
        await using var db = Db();
        var month = LedgerMonthService.Month(DateOnly.FromDateTime(DateTime.Today)).AddMonths(-1);
        try
        {
            await db.Database.EnsureCreatedAsync();
            await DatabaseSchemaService.EnsureSchemaAsync(cs); await DbInitializer.InitializeAsync(db);
            var first = await db.Stores.Select(x => x.Id).FirstAsync();
            var other = new Store { Name = "Other fixture store" }; db.Stores.Add(other); await db.SaveChangesAsync();
            db.ShiftLogs.Add(new ShiftLogEntry { StoreId = first, Date = month, ShiftNo = "3004", Employee = "Manager", CashTotal = 110, CashDropReceived = 100, RegisterPayout = 10 });
            db.PosSalesSummaries.Add(new PosSalesSummary { StoreId = first, ReportFrom = month, ReportTo = month, CashSales = 110 });
            db.CashOnHand.Add(new CashOnHandEntry { StoreId = first, Date = month, CashAdded = 100, Reference = "Fixture" });
            await db.SaveChangesAsync();
            await LedgerMonthService.SetClosedAsync(db, first, month, true, "Owner");
            async Task<string> Snapshot()
            {
                using var connection = new SqlConnection(cs); await connection.OpenAsync();
                using var command = new SqlCommand("SELECT (SELECT * FROM dbo.ShiftLogs FOR JSON PATH, INCLUDE_NULL_VALUES) AS Shifts,(SELECT * FROM dbo.CashOnHand FOR JSON PATH, INCLUDE_NULL_VALUES) AS Cash,(SELECT * FROM dbo.PosSalesSummaries FOR JSON PATH, INCLUDE_NULL_VALUES) AS Summaries,(SELECT * FROM dbo.LedgerMonths FOR JSON PATH, INCLUDE_NULL_VALUES) AS Months FOR JSON PATH", connection);
                return (string)(await command.ExecuteScalarAsync())!;
            }
            var before = await Snapshot();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await DatabaseSchemaService.EnsureSchemaAsync(cs);
                await using var nextSession = Db(); await DbInitializer.InitializeAsync(nextSession);
            }
            Assert.Equal(before, await Snapshot());
            var blocked = await Assert.ThrowsAsync<SqlException>(() => db.ShiftLogs.Where(x => x.StoreId == first).ExecuteUpdateAsync(s => s.SetProperty(x => x.CashDropReceived, 1)));
            Assert.Contains(blocked.Errors.Cast<SqlError>(), x => x.Number == 51120);
            Assert.True(DatabaseStartupFailurePolicy.IsClosedPeriod(new InvalidOperationException("Business upgrade failed", new AggregateException(blocked))));
            Assert.False(DatabaseStartupFailurePolicy.IsClosedPeriod(new InvalidOperationException("Unrelated failure")));
            // The next month is still writable, and another store's same month stays independent.
            db.ShiftLogs.AddRange(new ShiftLogEntry { StoreId = first, Date = month.AddMonths(1), ShiftNo = "3005" }, new ShiftLogEntry { StoreId = other.Id, Date = month, ShiftNo = "3004" });
            await db.SaveChangesAsync();
            Assert.Equal(3, await db.ShiftLogs.CountAsync());
        }
        finally
        {
            if (!name.StartsWith("HK_Startup_Test_") || name.Length != 48) throw new Exception("Unexpected fixture database name.");
            await db.Database.EnsureDeletedAsync();
        }
    }
}
