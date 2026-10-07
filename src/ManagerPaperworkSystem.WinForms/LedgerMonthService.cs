using System.Data;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Db;
using Microsoft.EntityFrameworkCore;
namespace ManagerPaperworkSystem.WinForms;

internal static class LedgerMonthService
{
    public static DateOnly Month(DateOnly date) => new(date.Year, date.Month, 1);
    public static decimal BalanceThrough(IEnumerable<CashOnHandEntry> rows, DateOnly through) => ManagerPaperworkSystem.Core.Services.CashBalanceCalculator.BalanceThrough(rows, through);
    public static decimal OpeningForRange(IEnumerable<CashOnHandEntry> rows, DateOnly from) => ManagerPaperworkSystem.Core.Services.CashBalanceCalculator.OpeningForRange(rows, from);
    public static async Task SetOpeningAsync(AppDbContext db, int storeId, DateOnly month, decimal amount, int userId, string userName)
    {
        if (amount < 0 || decimal.Round(amount, 2) != amount) throw new InvalidOperationException("Enter a nonnegative opening cash amount with at most two decimals.");
        month = Month(month);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var period = await db.LedgerMonths.SingleOrDefaultAsync(x => x.StoreId == storeId && x.Month == month);
        if (period?.IsClosed == true) throw new InvalidOperationException("This month is closed.");
        if (period?.OpeningCash is not null) throw new InvalidOperationException("Opening cash is already recorded for this store and month. Use an owner correction rather than entering it again.");
        if (period is null) { period = new LedgerMonth { StoreId = storeId, Month = month }; db.LedgerMonths.Add(period); }
        var existing = await db.CashOnHand.Where(x => x.StoreId == storeId && x.Date == month && x.Reference == "CARRY_FORWARD").ToListAsync();
        if (existing.Count > 0) throw new InvalidOperationException("A carry-forward entry already exists. Review it in Cash On Hand rather than adding a second opening balance.");
        period.OpeningCash = amount; period.OpeningBy = userName;
        db.CashOnHand.Add(new CashOnHandEntry { StoreId = storeId, Date = month, CashAdded = amount, Reference = "CARRY_FORWARD", Description = "Opening cash for " + month.ToString("MMMM yyyy"), CreatedByUserId = userId, CreatedByName = userName });
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    public static async Task SetClosedAsync(AppDbContext db, int storeId, DateOnly month, bool closed, string actor)
    {
        month = Month(month);
        if (closed && month >= Month(DateOnly.FromDateTime(DateTime.Today))) throw new InvalidOperationException("Close completed months only. The current month stays open for entries.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        if (closed)
        {
            if (await db.PendingShiftDrops.AnyAsync(x => x.StoreId == storeId && (x.Status == "Pending" || x.Status == "Attention"))) throw new InvalidOperationException("Review pending shift drops before closing the month.");
            var end = month.AddMonths(1);
            var shifts = await db.ShiftLogs.Where(x => x.StoreId == storeId && x.Date >= month && x.Date < end).Select(x => x.Date).Distinct().ToListAsync();
            var summaries = await db.PosSalesSummaries.Where(x => x.StoreId == storeId && x.ReportFrom >= month && x.ReportTo < end).Select(x => x.ReportFrom).Distinct().ToListAsync();
            if (shifts.Except(summaries).Any() || summaries.Except(shifts).Any()) throw new InvalidOperationException("Some dates have a Z report without a daily summary, or a summary without a Z report. Complete those imports before closing.");
        }
        var period = await db.LedgerMonths.SingleOrDefaultAsync(x => x.StoreId == storeId && x.Month == month);
        if (period is null) { period = new LedgerMonth { StoreId = storeId, Month = month }; db.LedgerMonths.Add(period); }
        period.IsClosed = closed; period.ClosedUtc = closed ? DateTime.UtcNow : null; period.ClosedBy = actor;
        db.ActivityLogs.Add(new ActivityLogEntry { StoreId = storeId, OccurredUtc = DateTime.UtcNow, Action = closed ? "Month closed" : "Month reopened", EntityType = "LedgerMonth", EntityId = period.Id, Description = $"{month:MMMM yyyy}: {actor}" });
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
}
