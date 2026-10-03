using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.UI.Services;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.WinForms;

internal static class PortalZReportHistory
{
    public static string IdentityPrefix(string portalStore, DateOnly date) =>
        $"Z1|{PortalStoreIsolationPolicy.Normalize(portalStore)}|{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}|";

    public static string Identity(string portalStore, DateOnly date, string batch, string sourcePath) =>
        IdentityPrefix(portalStore, date) + batch.Trim() + "|" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(sourcePath.Trim().Replace('/', '\\').ToUpperInvariant())));

    public static async Task<Dictionary<long, DateOnly>> LoadAsync(
        AppDbContext db, int storeId, string portalStore, CancellationToken cancellationToken,
        Func<string, IReadOnlyList<PosReportData>>? readReports = null)
    {
        readReports ??= new PosReportImportService().ImportZReports;
        var rows = await db.ShiftLogs.AsNoTracking()
            .Where(row => row.StoreId == storeId && row.PosSalesSummaryId == null && !row.IsCorrection)
            .Select(row => new { row.Id, row.Date, row.ShiftNo, row.PosReportKey, row.PosReportPath, row.PosReportStoreIdentity })
            .ToListAsync(cancellationToken);
        var verified = new Dictionary<long, DateOnly>();
        var sources = new Dictionary<string, IReadOnlyList<PosReportData>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!long.TryParse(row.ShiftNo?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var batch))
                continue;
            var identity = Identity(portalStore, row.Date, row.ShiftNo!, row.PosReportPath);
            if (row.PosReportStoreIdentity == identity)
            {
                verified[batch] = row.Date;
                continue;
            }
            // An identity belonging to another store/date/batch cannot establish
            // this store's cursor. Never stamp it with the active store name.
            var identityBase = IdentityPrefix(portalStore, row.Date) + row.ShiftNo!.Trim() + "|";
            if ((!string.IsNullOrWhiteSpace(row.PosReportStoreIdentity) &&
                 !row.PosReportStoreIdentity.StartsWith(identityBase, StringComparison.Ordinal)) ||
                (!string.IsNullOrWhiteSpace(row.PosReportKey) && !row.PosReportKey.StartsWith("ADVENTPOS-Z|", StringComparison.OrdinalIgnoreCase)) ||
                string.IsNullOrWhiteSpace(row.PosReportPath) ||
                row.PosReportPath.StartsWith(@"\\") ||
                !Path.GetExtension(row.PosReportPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                if (!sources.TryGetValue(row.PosReportPath, out var reports))
                {
                    reports = readReports(row.PosReportPath);
                    // A mixed-store source file is not proof for any of its rows.
                    foreach (var report in reports)
                        PortalStoreIsolationPolicy.ValidateZReportStore(report.SourceReportText, portalStore);
                    sources[row.PosReportPath] = reports;
                }
                if (!reports.Any(report => report.ReportDate == row.Date &&
                    long.TryParse(report.ShiftOrBatch?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number == batch))
                    continue;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Unavailable or wrong-store evidence is not an imported batch.
                // The live portal still has to verify its own downloaded report.
                sources[row.PosReportPath] = Array.Empty<PosReportData>();
                continue;
            }
            // Persist only provenance metadata, shared by both Windows accounts.
            // A concurrent edit invalidates this update instead of stamping stale data.
            var saved = await db.ShiftLogs.Where(item => item.Id == row.Id && item.StoreId == storeId &&
                    item.Date == row.Date && item.ShiftNo == row.ShiftNo && item.PosReportPath == row.PosReportPath &&
                    item.PosReportKey == row.PosReportKey && item.PosReportStoreIdentity == row.PosReportStoreIdentity &&
                    item.PosSalesSummaryId == null && !item.IsCorrection)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.PosReportStoreIdentity, identity), cancellationToken);
            if (saved == 1) verified[batch] = row.Date;
        }
        return verified;
    }
}
