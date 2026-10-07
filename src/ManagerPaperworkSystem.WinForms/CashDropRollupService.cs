using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.WinForms;

internal static class CashDropRollupService
{
    public static async Task SyncDateAsync(
        AppDbContext db,
        int storeId,
        DateOnly date,
        int? reconciledByUserId = null,
        string? reconciledByName = null,
        CancellationToken cancellationToken = default,
        bool requirePayoutConsistency = false)
    {
        if(await db.LedgerMonths.AnyAsync(x=>x.StoreId==storeId && x.Month==new DateOnly(date.Year,date.Month,1) && x.IsClosed,cancellationToken)) return;
        var rows = await db.ShiftLogs
            .AsNoTracking()
            .Where(item => item.StoreId == storeId)
            .OrderBy(item => item.CreatedUtc)
            .ToListAsync(cancellationToken);
        var effective = EffectiveRows(rows)
            .Where(item => item.Date == date && item.PosSalesSummaryId == null)
            .ToList();

        // Once Z reports exist for a date, they are the register-level source
        // of truth. Manual legacy rows are used only for dates that predate the
        // Z-report integration.
        var importedOriginalIds = rows.Where(item => !item.IsCorrection && !string.IsNullOrWhiteSpace(item.PosReportKey)).Select(item => item.Id).ToHashSet();
        var zRows = effective
            .Where(item => !string.IsNullOrWhiteSpace(item.PosReportKey) ||
                (item.IsCorrection && item.CorrectsId.HasValue && importedOriginalIds.Contains(item.CorrectsId.Value)))
            .ToList();
        var cashDropRows = zRows.Count > 0 ? zRows : effective;
        var combinedCashDrop = cashDropRows.Sum(item => item.CashDropReceived);

        var summary = await db.PosSalesSummaries
            .Where(item =>
                item.StoreId == storeId &&
                item.ReportFrom <= date &&
                item.ReportTo >= date)
            .OrderByDescending(item => item.ImportedUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (summary is null)
            return;

        // Only daily summaries can receive one day's shift reconciliation.
        if(summary.ReportFrom!=date || summary.ReportTo!=date) return;
        var combinedPayout=cashDropRows.Sum(x=>x.RegisterPayout);
        var combinedReason=string.Join("; ",cashDropRows.Where(x=>x.RegisterPayout!=0).OrderBy(x=>x.ShiftNo).Select(x=>$"Batch {x.ShiftNo}: {x.PayoutReason}"));
        var previous=await db.ShiftPayoutRollups.SingleOrDefaultAsync(x=>x.SummaryId==summary.Id,cancellationToken);
        var payoutConflict = previous is null
            ? combinedPayout != 0 && (summary.RegisterPayout != 0 || summary.PayoutReason.Length > 0)
            : summary.RegisterPayout != previous.AppliedAmount || summary.PayoutReason != previous.AppliedReason;
        if(payoutConflict && requirePayoutConsistency)
            throw new InvalidOperationException("This daily summary has a separately entered payout. An owner must review it with Use Shift Payouts before recording this shift.");
        if(!payoutConflict && (previous is not null || combinedPayout!=0)) {
            if(combinedReason.Length>4000) throw new InvalidOperationException("The combined payout reasons exceed 4,000 characters. Shorten the shift reasons before saving.");
            if(previous is null){previous=new ManagerPaperworkSystem.Core.Models.ShiftPayoutRollup{SummaryId=summary.Id};db.ShiftPayoutRollups.Add(previous);}
            summary.RegisterPayout=combinedPayout;summary.PayoutReason=combinedReason;
            previous.AppliedAmount=combinedPayout;previous.AppliedReason=combinedReason;
        }

        summary.CashDropReceived = combinedCashDrop;
        var hasReconciliation =
            combinedCashDrop != 0m ||
            summary.RegisterPayout != 0m ||
            !string.IsNullOrWhiteSpace(summary.PayoutReason);
        if (hasReconciliation && !payoutConflict)
        {
            var latestEnteredRow = cashDropRows
                .Where(item => item.CashDropReceived != 0m)
                .OrderByDescending(item => item.CreatedUtc)
                .FirstOrDefault();
            summary.IsReconciled = true;
            summary.ReconciledByUserId = reconciledByUserId ?? latestEnteredRow?.CreatedByUserId;
            summary.ReconciledByName = !string.IsNullOrWhiteSpace(reconciledByName)
                ? reconciledByName.Trim()
                : !string.IsNullOrWhiteSpace(latestEnteredRow?.CreatedByName)
                    ? latestEnteredRow.CreatedByName
                    : "Shift Cash Drop";
            summary.ReconciledUtc = DateTime.UtcNow;
        }
        else
        {
            summary.IsReconciled = false;
            summary.ReconciledByUserId = null;
            summary.ReconciledByName = "";
            summary.ReconciledUtc = null;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static List<ShiftLogEntry> EffectiveRows(IReadOnlyList<ShiftLogEntry> rows)
    {
        var originals = rows.Where(item => !item.IsCorrection).ToList();
        var latestCorrections = rows
            .Where(item => item.IsCorrection && item.CorrectsId.HasValue)
            .GroupBy(item => item.CorrectsId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.Id).First());

        return originals
            .Select(original =>
                latestCorrections.TryGetValue(original.Id, out var correction)
                    ? correction
                    : original)
            .ToList();
    }
}
