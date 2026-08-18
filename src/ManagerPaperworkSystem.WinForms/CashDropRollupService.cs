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
        CancellationToken cancellationToken = default)
    {
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
        var zRows = effective
            .Where(item => !string.IsNullOrWhiteSpace(item.PosReportKey))
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

        summary.CashDropReceived = combinedCashDrop;
        var hasReconciliation =
            combinedCashDrop != 0m ||
            summary.RegisterPayout != 0m ||
            !string.IsNullOrWhiteSpace(summary.PayoutReason);
        if (hasReconciliation)
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
                group => group.OrderByDescending(item => item.CreatedUtc).First());

        return originals
            .Select(original =>
                latestCorrections.TryGetValue(original.Id, out var correction)
                    ? correction
                    : original)
            .ToList();
    }
}
