using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Services;
using Microsoft.EntityFrameworkCore;
namespace ManagerPaperworkSystem.WinForms;

internal static partial class PortalSyncService
{
    private static async Task<List<PortalSyncRunResult>> ProcessPendingDropsAsync(PortalStoreSyncSettings settings, IAppPaths paths, CancellationToken ct)
    {
        await using var db = CreateStoreDatabase(settings);
        await EnsureTargetDatabaseReadyAsync(db, settings.BusinessName);
        var storeId = await ResolveDataStoreIdAsync(db, settings.BusinessName, ct);
        var retryBefore = DateTime.UtcNow.AddMinutes(-5);
        var requests = await db.PendingShiftDrops.AsNoTracking().Where(x => x.StoreId == storeId && x.ConfigurationId == settings.Id && x.Status == "Pending" && (x.LastAttemptUtc == null || x.LastAttemptUtc < retryBefore)).OrderBy(x => x.CreatedUtc).Take(10).ToListAsync(ct);
        var results = new List<PortalSyncRunResult>();
        foreach (var request in requests)
        {
            await db.PendingShiftDrops.Where(x => x.Id == request.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastAttemptUtc, DateTime.UtcNow), ct);
            try
            {
                await ShiftDropWorkflow.ApplyAsync(db, request.Id, settings.PortalStoreName);
                db.ChangeTracker.Clear();
                var current = await db.PendingShiftDrops.AsNoTracking().SingleAsync(x => x.Id == request.Id, ct);
                if (current.Status == "Pending")
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(10));
                    await RunStoreWithRetriesAsync(settings, paths, DateOnly.FromDateTime(DateTime.Today), PortalSyncReportKind.ZReports, false, null, null, timeout.Token, null, request.Batch);
                    await ShiftDropWorkflow.ApplyAsync(db, request.Id, settings.PortalStoreName);
                }
                db.ChangeTracker.Clear();
                current = await db.PendingShiftDrops.AsNoTracking().SingleAsync(x => x.Id == request.Id, ct);
                if (current.Status != "Applied") throw new InvalidOperationException("The batch has not arrived yet. The entered cash is still waiting.");
                results.Add(new PortalSyncRunResult(settings.BusinessName, true, true, $"Batch {request.Batch}: cash drop and payout recorded."));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                db.ChangeTracker.Clear();
                var current = await db.PendingShiftDrops.SingleAsync(x => x.Id == request.Id, ct);
                if (current.Status == "Pending" || current.Status == "Attention")
                {
                    current.Message = AppBootstrap.RedactSensitiveText(ex.Message);
                    if (current.Message.Length > 1000) current.Message = current.Message[..1000];
                    if ((current.Message.Contains("closed month", StringComparison.OrdinalIgnoreCase) || current.Message.Contains("month is closed", StringComparison.OrdinalIgnoreCase)) || current.Message.Contains("already has entered", StringComparison.OrdinalIgnoreCase) || current.Message.Contains("review", StringComparison.OrdinalIgnoreCase)) current.Status = "Attention";
                    await db.SaveChangesAsync(ct);
                }
                results.Add(new PortalSyncRunResult(settings.BusinessName, false, false, $"Batch {request.Batch}: {current.Message}"));
            }
        }
        foreach (var result in results) WriteLog(result);
        return results;
    }
}
