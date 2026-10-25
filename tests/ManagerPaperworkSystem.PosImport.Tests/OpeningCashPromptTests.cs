using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.Data.Services;
using ManagerPaperworkSystem.WinForms;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ManagerPaperworkSystem.PosImport.Tests;

public sealed class OpeningCashPromptTests
{
    [Fact]
    public async Task OnlyMonthCloseArmsReminderAndDismissalSurvivesANewSession()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var current = LedgerMonthService.Month(DateOnly.FromDateTime(DateTime.Today));
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Stores.AddRange(new Store { Id = 1, Name = "First" }, new Store { Id = 2, Name = "Other" }); await db.SaveChangesAsync();
            Assert.False(await LedgerMonthService.TryClaimOpeningPromptAsync(db, 1, current));
            await LedgerMonthService.SetClosedAsync(db, 1, current.AddMonths(-1), true, "Owner");
            Assert.False(await LedgerMonthService.TryClaimOpeningPromptAsync(db, 2, current));
            Assert.True(await LedgerMonthService.TryClaimOpeningPromptAsync(db, 1, current));
        }
        await using (var nextSession = new AppDbContext(options))
        {
            Assert.False(await LedgerMonthService.TryClaimOpeningPromptAsync(nextSession, 1, current));
            await LedgerMonthService.SetClosedAsync(nextSession, 1, current.AddMonths(-1), false, "Owner");
            await LedgerMonthService.SetClosedAsync(nextSession, 1, current.AddMonths(-1), true, "Owner");
            Assert.False(await LedgerMonthService.TryClaimOpeningPromptAsync(nextSession, 1, current));
            Assert.Empty(await nextSession.CashOnHand.ToListAsync()); // Dismissal does not create a zero balance.
            await LedgerMonthService.SetOpeningAsync(nextSession, 1, current, 120, 1, "Owner");
            Assert.Equal(120, (await nextSession.CashOnHand.SingleAsync()).CashAdded);
        }
    }

    [Fact]
    public async Task UpgradeDoesNotPromptForOldClosedMonthsAndIsRepeatable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE LedgerMonths(Id INTEGER PRIMARY KEY,StoreId INTEGER,Month TEXT,IsClosed INTEGER,ClosedUtc TEXT,ClosedBy TEXT,OpeningCash TEXT,OpeningBy TEXT);");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO LedgerMonths VALUES(1,1,'2026-09-01',1,NULL,'Owner',NULL,'');");
        await LedgerWorkflowSchema.EnsureAsync(db); await LedgerWorkflowSchema.EnsureAsync(db);
        var legacy = await db.LedgerMonths.SingleAsync(); Assert.False(legacy.OpeningPromptPending); Assert.Null(legacy.OpeningPromptShownUtc);
    }
}
