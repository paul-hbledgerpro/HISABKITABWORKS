using System.Text.Json;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.Reports.Pdf;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.WinForms;

internal static class ShiftShortageDelivery
{
    public static async Task SendDueAsync(AppDbContext db, int storeId, string storeGuid, CancellationToken ct = default)
    {
        if (DemoRuntime.IsEnabled || LicenseRuntime.IsReadOnly || string.IsNullOrWhiteSpace(storeGuid)) return;
        var settings = await db.ShiftShortageSettings.AsNoTracking().SingleOrDefaultAsync(x => x.StoreId == storeId, ct);
        if (settings is null || !settings.Enabled || !string.Equals(settings.StoreGuid, storeGuid, StringComparison.OrdinalIgnoreCase)) return;
        using var client = new LiveBankSyncClient();
        if (!client.IsConfigured) return;
        var now = DateTime.UtcNow;
        var rows = await db.ShiftShortageEmails.AsNoTracking()
            .Where(x => x.StoreId == storeId && x.StoreGuid == storeGuid && x.Status == "Queued" && x.NextAttemptUtc <= now)
            .OrderBy(x => x.CreatedUtc).Take(5).ToListAsync(ct);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            // Shared database lease prevents two Windows sessions sending the same row simultaneously.
            var token = Guid.NewGuid().ToString("N");
            var claimed = await db.ShiftShortageEmails.Where(x => x.Id == row.Id && x.StoreId == storeId && x.Status == "Queued" && x.NextAttemptUtc <= now)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptUtc, now.AddMinutes(5)).SetProperty(x => x.LeaseToken, token), ct);
            if (claimed != 1) continue;
            var result = new ShortageEmailResult(false, false, "Email delivery is queued for retry.");
            try
            {
                var current = await db.ShiftShortageSettings.AsNoTracking().SingleAsync(x => x.StoreId == storeId, ct);
                if (!current.Enabled || current.RecipientsJson != row.RecipientsJson || current.StoreGuid != row.StoreGuid)
                {
                    await db.ShiftShortageEmails.Where(x => x.Id == row.Id && x.LeaseToken == token && x.Status == "Queued")
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Cancelled").SetProperty(x => x.Message, "Alert settings changed"), ct);
                    continue;
                }
                var report = JsonSerializer.Deserialize<ShiftShortageReport>(row.ReportJson) ?? throw new InvalidOperationException("Missing alert report.");
                ManagerPaperworkSystem.UI.Services.PdfRuntime.EnsureAvailableOrThrow();
                var pdf = ShiftShortagePdf.Generate(report);
                result = await client.SendShortageAsync(storeGuid, new {
                    eventId = row.Id, recipients = JsonSerializer.Deserialize<string[]>(row.RecipientsJson),
                    report, pdfBase64 = Convert.ToBase64String(pdf)
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var detail = AppBootstrap.RedactSensitiveText(ex.GetBaseException().Message);
                result = new(false, false, "Queued for retry: " + detail[..Math.Min(detail.Length, 450)]);
            }
            var status = result.Sent ? "Sent" : result.Attention ? "Attention" : "Queued";
            await db.ShiftShortageEmails.Where(x => x.Id == row.Id && x.LeaseToken == token && x.Status == "Queued")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status).SetProperty(x => x.Message, result.Message)
                    .SetProperty(x => x.SentUtc, result.Sent ? DateTime.UtcNow : (DateTime?)null)
                    .SetProperty(x => x.NextAttemptUtc, DateTime.UtcNow.AddMinutes(15)).SetProperty(x => x.LeaseToken, ""), ct);
        }
    }
}
