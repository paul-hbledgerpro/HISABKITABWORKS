using System.Data;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.WinForms;

internal static class ShiftDropWorkflow
{
    public static void Validate(string batch, decimal drop, decimal payout, string reason)
    {
        if (!long.TryParse(batch, out var number) || number <= 0 || batch.Length > 20) throw new InvalidOperationException("Enter a valid batch number.");
        if (drop < 0 || payout < 0 || decimal.Round(drop, 2) != drop || decimal.Round(payout, 2) != payout) throw new InvalidOperationException("Enter positive amounts with at most two decimal places.");
        if (reason.Length > 300 || payout > 0 && string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Enter the payout reason (up to 300 characters).");
    }

    public static async Task<PendingShiftDrop> RecordAsync(AppDbContext db, int storeId, Guid configurationId, string portalStore, string batch, decimal drop, decimal payout, string reason, int userId, string userName, bool allowReplace)
    {
        batch = long.TryParse(batch, out var number) ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : batch;
        Validate(batch, drop, payout, reason);
        await PortalZReportHistory.LoadAsync(db, storeId, portalStore, CancellationToken.None, batchNumber: batch);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var request = await db.PendingShiftDrops.SingleOrDefaultAsync(x => x.StoreId == storeId && x.Batch == batch && (x.Status == "Pending" || x.Status == "Attention"));
        if (request is not null) throw new InvalidOperationException("This batch already has a waiting entry. Open Pending Shift Drops to review or cancel it first.");
        request = new PendingShiftDrop { StoreId = storeId, ConfigurationId = configurationId, PortalStoreName = portalStore, Batch = batch, Drop = drop, Payout = payout, Reason = reason, UserId = userId, UserName = userName };
        db.PendingShiftDrops.Add(request);
        await ApplyCoreAsync(db, request, portalStore, allowReplace);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return request;
    }

    public static async Task ApplyAsync(AppDbContext db, int requestId, string portalStore)
    {
        db.ChangeTracker.Clear();
        var pending = await db.PendingShiftDrops.AsNoTracking().SingleAsync(x => x.Id == requestId);
        if (pending.Status != "Pending") return;
        await PortalZReportHistory.LoadAsync(db, pending.StoreId, portalStore, CancellationToken.None, batchNumber: pending.Batch);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var request = await db.PendingShiftDrops.SingleAsync(x => x.Id == requestId);
        if (request.Status != "Pending") return;
        await ApplyCoreAsync(db, request, portalStore, false);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private static async Task ApplyCoreAsync(AppDbContext db, PendingShiftDrop request, string portalStore, bool allowReplace)
    {
        if (PortalStoreIsolationPolicy.Normalize(request.PortalStoreName) != PortalStoreIsolationPolicy.Normalize(portalStore)) throw new InvalidOperationException("The saved portal store changed. Review this pending entry before applying it.");
        var candidates = await db.ShiftLogs.Where(x => x.StoreId == request.StoreId && x.ShiftNo == request.Batch && !x.IsCorrection && x.PosSalesSummaryId == null).ToListAsync();
        var matches = candidates.Where(x => PortalZReportHistory.MatchesIdentity(x.PosReportStoreIdentity, portalStore, x.Date, x.ShiftNo, x.PosReportPath)).ToList();
        if (matches.Count == 0) return;
        if (matches.Count != 1) throw new InvalidOperationException("More than one report matches this batch. Select the dated shift in Shift Cash Drop.");
        var shift = matches[0];
        if (!PortalZReportHistory.MatchesIdentity(shift.PosReportStoreIdentity, portalStore, shift.Date, shift.ShiftNo, shift.PosReportPath)) throw new InvalidOperationException("The report identity needs review before recording cash.");
        if (await db.ShiftLogs.AnyAsync(x => x.StoreId == request.StoreId && x.CorrectsId == shift.Id)) throw new InvalidOperationException("This shift has a correction. Use the existing correction workflow.");
        if (await db.LedgerMonths.AnyAsync(x => x.StoreId == request.StoreId && x.Month == new DateOnly(shift.Date.Year, shift.Date.Month, 1) && x.IsClosed)) throw new InvalidOperationException("This shift belongs to a closed month. Ask an owner to reopen that month.");
        if (!allowReplace && (shift.CashDropReceived != 0 || shift.RegisterPayout != 0 || shift.PayoutReason.Length > 0 || await db.PendingShiftDrops.AnyAsync(x => x.StoreId == request.StoreId && x.ShiftId == shift.Id && x.Status == "Applied"))) throw new InvalidOperationException("This shift already has entered cash information. Review it before replacing amounts.");
        shift.CashDropReceived = request.Drop; shift.RegisterPayout = request.Payout; shift.PayoutReason = request.Reason;
        await db.SaveChangesAsync();
        await CashDropRollupService.SyncDateAsync(db, request.StoreId, shift.Date, request.UserId, request.UserName, requirePayoutConsistency: true);
        var reference = "SHIFTLOG:" + shift.Id;
        var cashRows = await db.CashOnHand.Where(x => x.StoreId == request.StoreId && x.Reference == reference).ToListAsync();
        if (cashRows.Any(x => x.IsCorrection) || cashRows.Count > 1) throw new InvalidOperationException("The linked cash entry has corrections requiring review.");
        var cash = cashRows.SingleOrDefault();
        if (cash is not null && await db.CashOnHand.AnyAsync(x => x.StoreId == request.StoreId && x.CorrectsId == cash.Id)) throw new InvalidOperationException("The linked cash entry has corrections requiring review.");
        if (cash is null && request.Drop != 0) { cash = new CashOnHandEntry { StoreId = request.StoreId, Reference = reference }; db.CashOnHand.Add(cash); }
        if (cash is not null) { cash.Date = shift.Date; cash.CashAdded = request.Drop; cash.PayoutAmount = 0; cash.IsPayout = false; cash.Description = "Auto: Cash Drop from Shift Log " + shift.ShiftNo; cash.CreatedByUserId = request.UserId; cash.CreatedByName = request.UserName; }
        request.Status = "Applied"; request.NotifiedUtc = null; request.ShiftId = shift.Id; request.Variance = shift.Variance;
        request.Message = $"Batch {shift.ShiftNo} — {shift.Date:M/d/yyyy}";
    }
}
