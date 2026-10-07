using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.Data.Services;
using ManagerPaperworkSystem.WinForms;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ManagerPaperworkSystem.PosImport.Tests;

public class LedgerWorkflowTests
{
    [Fact]
    public void Opening_replaces_prior_balance_and_register_payout_is_not_deducted_twice()
    {
        var rows = new[] {
            new CashOnHandEntry { Date=new(2026,9,1), CashAdded=900, Reference="CARRY_FORWARD" },
            new CashOnHandEntry { Date=new(2026,9,30), CashAdded=100 },
            new CashOnHandEntry { Date=new(2026,10,1), CashAdded=500, Reference="CARRY_FORWARD" },
            new CashOnHandEntry { Date=new(2026,10,1), CashAdded=90, Reference="SHIFTLOG:1" },
            new CashOnHandEntry { Date=new(2026,10,2), PayoutAmount=20, IsPayout=true }
        };
        Assert.Equal(1000, LedgerMonthService.BalanceThrough(rows,new(2026,9,30)));
        Assert.Equal(500, LedgerMonthService.OpeningForRange(rows,new(2026,10,1)));
        Assert.Equal(570, LedgerMonthService.BalanceThrough(rows,new(2026,10,31)));
    }

    [SqlServerFact]
    public async Task Sql_server_workflow_is_store_scoped_atomic_repeatable_and_closed_month_safe()
    {
        // Explicit opt-in: this test creates and removes only its own random database.
        var server=Environment.GetEnvironmentVariable("HK_TEST_SQL_SERVER");
        if(string.IsNullOrWhiteSpace(server)) return;
        var name="HK_Ledger_Test_"+Guid.NewGuid().ToString("N");
        var connection=$"Server={server};Database={name};Integrated Security=True;TrustServerCertificate=True;Pooling=False";
        AppDbContext Db()=>new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        const string portal="GALAXY SMOKE SHOP (ELGIN, IL - 60123)";
        var day=new DateOnly(2026,9,28);
        var config=Guid.NewGuid();
        ShiftLogEntry Shift(int store,string batch,string source)=>new() {
            StoreId=store,Date=day,ShiftNo=batch,CashTotal=100,PosReportKey=$"ADVENTPOS-Z|{day}|{batch}|{source}",PosReportPath="C:\\fixture\\"+batch+".pdf",
            PosReportStoreIdentity=PortalZReportHistory.Identity(source,day,batch,"C:\\fixture\\"+batch+".pdf")
        };
        await using var db=Db();
        try {
            await db.Database.EnsureCreatedAsync();
            // Exercise the deployed-schema upgrade path, as well as repeat initialization.
            await db.Database.ExecuteSqlRawAsync("DROP TABLE dbo.PendingShiftDrops; DROP TABLE dbo.LedgerMonths; DROP TABLE dbo.ShiftPayoutRollups;");
            await LedgerWorkflowSchema.EnsureAsync(db); await LedgerWorkflowSchema.EnsureAsync(db);
            db.Stores.AddRange(new Store{Name="Galaxy"},new Store{Name="Other"}); await db.SaveChangesAsync();
            db.ShiftLogs.AddRange(Shift(1,"1551",portal),Shift(2,"1551","ELGIN SMOKE SHOP"),Shift(1,"1552","ELGIN SMOKE SHOP"));
            db.PosSalesSummaries.AddRange(new PosSalesSummary{StoreId=1,ReportFrom=day,ReportTo=day,CashSales=200},new PosSalesSummary{StoreId=2,ReportFrom=day,ReportTo=day,CashSales=100});
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var request=await ShiftDropWorkflow.RecordAsync(db,1,config,"GALAXY SMOKE SHOP (ELGIN,IL)","1551",90,10,"Supplies",1,"Owner",false);
            Assert.Equal("Applied",request.Status); Assert.Equal(0,request.Variance);
            await ShiftDropWorkflow.ApplyAsync(db,request.Id,"GALAXY SMOKE SHOP (ELGIN,IL)"); // retry must not add cash/payout again
            db.ChangeTracker.Clear();
            var summary=await db.PosSalesSummaries.SingleAsync(x=>x.StoreId==1);
            Assert.Equal(90,summary.CashDropReceived); Assert.Equal(10,summary.RegisterPayout); Assert.Contains("1551: Supplies",summary.PayoutReason);
            Assert.Equal(0,(await db.ShiftLogs.SingleAsync(x=>x.StoreId==2)).CashDropReceived);
            Assert.Equal(0,(await db.PosSalesSummaries.SingleAsync(x=>x.StoreId==2)).RegisterPayout);
            Assert.Equal(90,(await db.ShiftLogs.SingleAsync(x => x.StoreId == 1 && x.ShiftNo == "1551")).CashDropReceived);
            Assert.Equal(90,(await db.CashOnHand.SingleAsync()).CashAdded); Assert.Equal(0,(await db.CashOnHand.SingleAsync()).PayoutAmount);
            var waiting=await ShiftDropWorkflow.RecordAsync(db,1,config,"GALAXY SMOKE SHOP (ELGIN,IL)","1552",85,10,"Ice",1,"Owner",false);
            Assert.Equal("Pending",waiting.Status); Assert.Equal(3,await db.ShiftLogs.CountAsync());
            waiting.NotifiedUtc=DateTime.UtcNow;await db.SaveChangesAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(()=>ShiftDropWorkflow.RecordAsync(db,1,config,portal,"1552",1,0,"",1,"Owner",false)); db.ChangeTracker.Clear();
            await Assert.ThrowsAsync<InvalidOperationException>(()=>ShiftDropWorkflow.ApplyAsync(db,waiting.Id,"ELGIN SMOKE SHOP")); db.ChangeTracker.Clear();
            db.ShiftLogs.Add(Shift(1,"1552",portal)); await db.SaveChangesAsync();
            await ShiftDropWorkflow.ApplyAsync(db,waiting.Id,"GALAXY SMOKE SHOP (ELGIN,IL)"); db.ChangeTracker.Clear();
            Assert.Equal(-5,(await db.PendingShiftDrops.SingleAsync(x=>x.Id==waiting.Id)).Variance);
            Assert.Null((await db.PendingShiftDrops.SingleAsync(x=>x.Id==waiting.Id)).NotifiedUtc);
            summary=await db.PosSalesSummaries.SingleAsync(x=>x.StoreId==1);
            Assert.Equal(20,summary.RegisterPayout); Assert.Equal(175,summary.CashDropReceived);
            Assert.Contains("1552: Ice",summary.PayoutReason);
            var firstShift=await db.ShiftLogs.SingleAsync(x=>x.StoreId==1 && x.ShiftNo=="1551");
            db.ShiftLogs.Add(new ShiftLogEntry {StoreId=1,Date=day,ShiftNo="1551",CashTotal=100,CashDropReceived=90,RegisterPayout=10,PayoutReason="Corrected supplies",IsCorrection=true,CorrectsId=firstShift.Id,CorrectionReason="Test"});await db.SaveChangesAsync();
            await CashDropRollupService.SyncDateAsync(db,1,day); Assert.Equal(20,summary.RegisterPayout);Assert.Equal(175,summary.CashDropReceived);Assert.Contains("Corrected supplies",summary.PayoutReason);
            // Separately edited summary must roll back the shift and pending record too.
            summary.RegisterPayout=35; summary.PayoutReason="Separate edit";await db.SaveChangesAsync();
            await CashDropRollupService.SyncDateAsync(db,1,day);
            Assert.Equal(35,summary.RegisterPayout);Assert.False(summary.IsReconciled);
            db.ShiftLogs.Add(Shift(1,"1553",portal));await db.SaveChangesAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(()=>ShiftDropWorkflow.RecordAsync(db,1,config,portal,"1553",80,20,"Delivery",1,"Owner",false));db.ChangeTracker.Clear();
            Assert.Equal(0,(await db.ShiftLogs.SingleAsync(x=>x.StoreId==1 && x.ShiftNo=="1553")).CashDropReceived);
            Assert.False(await db.PendingShiftDrops.AnyAsync(x=>x.Batch=="1553")); Assert.Equal(2,await db.CashOnHand.CountAsync());
            summary=await db.PosSalesSummaries.SingleAsync(x=>x.StoreId==1);var applied=await db.ShiftPayoutRollups.SingleAsync();summary.RegisterPayout=applied.AppliedAmount;summary.PayoutReason=applied.AppliedReason;await db.SaveChangesAsync();
            await LedgerMonthService.SetOpeningAsync(db,1,new(2026,10,1),500,1,"Owner");
            await Assert.ThrowsAsync<InvalidOperationException>(()=>LedgerMonthService.SetOpeningAsync(db,1,new(2026,10,1),600,1,"Owner")); db.ChangeTracker.Clear();
            Assert.Equal(500,LedgerMonthService.BalanceThrough(await db.CashOnHand.Where(x=>x.StoreId==1).ToListAsync(),new(2026,10,31)));
            await LedgerMonthService.SetClosedAsync(db,1,day,true,"Owner");db.ChangeTracker.Clear();
            Assert.True(await db.LedgerMonths.AnyAsync(x=>x.IsClosed && x.Month.AddMonths(1)>day));
            await Assert.ThrowsAnyAsync<Exception>(()=>db.ShiftLogs.Where(x=>x.StoreId==1).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.CashDropReceived,1)));
            await Assert.ThrowsAnyAsync<Exception>(()=>db.CashOnHand.Where(x=>x.StoreId==1 && x.Date==day).ExecuteDeleteAsync());
            await Assert.ThrowsAnyAsync<Exception>(()=>db.PosSalesSummaries.Where(x=>x.StoreId==1).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.CashDropReceived,1)));
            db.ShiftLogs.Add(Shift(1,"999",portal));await Assert.ThrowsAsync<DbUpdateException>(()=>db.SaveChangesAsync());db.ChangeTracker.Clear();
            db.ShiftLogs.Add(new ShiftLogEntry {StoreId=1,Date=day.AddMonths(1),ShiftNo="1551",IsCorrection=true,CorrectsId=firstShift.Id});
            await Assert.ThrowsAsync<DbUpdateException>(()=>db.SaveChangesAsync());db.ChangeTracker.Clear();
            // Other stores remain writable; reopening restores permitted edits.
            await db.ShiftLogs.Where(x=>x.StoreId==2).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.CashDropReceived,50));
            await LedgerMonthService.SetClosedAsync(db,1,day,false,"Owner");
            await ShiftDropWorkflow.RecordAsync(db,1,config,portal,"1553",110,0,"",1,"Owner",false);
            Assert.Equal(10,(await db.PendingShiftDrops.SingleAsync(x=>x.Batch=="1553")).Variance);
            db.ShiftLogs.Add(Shift(1,"1554",portal));await db.SaveChangesAsync();
            var zero=await ShiftDropWorkflow.RecordAsync(db,1,config,portal,"1554",0,0,"",1,"Owner",false);
            Assert.Equal("Applied",zero.Status);Assert.Equal(-100,zero.Variance);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>ShiftDropWorkflow.RecordAsync(db,1,config,portal,"1554",0,0,"",1,"Owner",false));
        }
        finally {if(!name.StartsWith("HK_Ledger_Test_") || name.Length!=47) throw new Exception("Unexpected test database name.");await db.Database.EnsureDeletedAsync();}
    }
}

internal sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HK_TEST_SQL_SERVER")))
            Skip="Set HK_TEST_SQL_SERVER to an isolated development SQL Server instance.";
    }
}
