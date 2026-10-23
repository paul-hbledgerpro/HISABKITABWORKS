using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.WinForms;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ManagerPaperworkSystem.PosImport.Tests;

public class ShiftPayoutChoiceTests
{
    [Fact]
    public async Task PendingPayeeAndPurposeSurviveMatchingAndDoNotDoubleDeductCash()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options); await db.Database.EnsureCreatedAsync();
        db.Stores.AddRange(new Store { Id = 1, Name = "First" }, new Store { Id = 2, Name = "Second" });
        db.Vendors.Add(new Vendor { StoreId = 2, Name = "Supplier" }); db.Purposes.Add(new Purpose { StoreId = 2, Name = "Supplies" }); await db.SaveChangesAsync();
        const string portal = "GALAXY SMOKE SHOP (ELGIN, IL - 60123)";
        var request = await ShiftDropWorkflow.RecordAsync(db, 1, Guid.NewGuid(), portal, "3019", 90, 10, "Supplies", 1, "Manager", false, " Supplier ");
        Assert.Equal("Pending", request.Status); Assert.Equal("Supplier", request.Payee);
        Assert.True(await db.Vendors.AnyAsync(x => x.StoreId == 1 && x.Name == "Supplier"));
        Assert.True(await db.Purposes.AnyAsync(x => x.StoreId == 1 && x.Name == "Supplies"));
        Assert.False(await db.CashOnHand.AnyAsync());
        var day = new DateOnly(2026, 10, 23); const string path = "C:\\fixture\\3019.pdf";
        db.ShiftLogs.Add(new ShiftLogEntry { StoreId = 1, Date = day, ShiftNo = "3019", CashTotal = 100, PosReportKey = "ADVENTPOS-Z|3019", PosReportPath = path,
            PosReportStoreIdentity = PortalZReportHistory.Identity(portal, day, "3019", path) });
        db.PosSalesSummaries.Add(new PosSalesSummary { StoreId = 1, ReportFrom = day, ReportTo = day, CashSales = 100 }); await db.SaveChangesAsync();
        await ShiftDropWorkflow.ApplyAsync(db, request.Id, portal); await ShiftDropWorkflow.ApplyAsync(db, request.Id, portal);
        db.ChangeTracker.Clear();
        Assert.Equal("Supplier", (await db.ShiftLogs.SingleAsync()).PayoutPayee);
        var cash = await db.CashOnHand.SingleAsync(); Assert.Equal(90, cash.CashAdded); Assert.Equal(0, cash.PayoutAmount);
        Assert.Equal(10, (await db.PosSalesSummaries.SingleAsync()).RegisterPayout);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ShiftDropWorkflow.RecordAsync(db, 1, Guid.NewGuid(), portal, "3019", 50, 5, "Other purpose", 1, "Manager", false, "Other vendor"));
        await using var verify = new AppDbContext(options);
        Assert.False(await verify.Vendors.AnyAsync(x => x.Name == "Other vendor"));
        Assert.False(await verify.Purposes.AnyAsync(x => x.Name == "Other purpose"));
    }
}
