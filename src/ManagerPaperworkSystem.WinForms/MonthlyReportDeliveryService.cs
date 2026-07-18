using System.Text.Json;
using ManagerPaperworkSystem.Core.Services;

namespace ManagerPaperworkSystem.WinForms;

internal static class MonthlyReportDeliveryService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<string?> TrySendDueAsync(
        IReportService reportService,
        IAppPaths paths,
        CancellationToken cancellationToken = default)
    {
        var license = LicenseRuntime.CurrentLicense;
        if (license is null ||
            !LicenseRuntime.HasService("MonthlyReports") ||
            string.IsNullOrWhiteSpace(license.MonthlyReportEmail))
            return null;

        var today = DateTime.Today;
        var deliveryDay = Math.Clamp(license.MonthlyReportDay, 1, 28);
        if (today.Day < deliveryDay)
            return null;

        var previousMonth = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
        var periodKey = previousMonth.ToString("yyyy-MM");
        var storeGuid = LicenseRuntime.ActiveStoreGuid.Trim().ToUpperInvariant();
        if (storeGuid.Length == 0)
            return null;

        await Gate.WaitAsync(cancellationToken);
        try
        {
            var statePath = Path.Combine(paths.AppDataDirectory, "monthly-report-delivery-state.json");
            var state = LoadState(statePath);
            var stateKey = $"{license.LicenseId}:{storeGuid}";
            if (state.LastSentPeriodByStore.GetValueOrDefault(stateKey) == periodKey)
                return null;
            if (state.LastAttemptDateByStore.GetValueOrDefault(stateKey) == today.ToString("yyyy-MM-dd"))
                return null;

            state.LastAttemptDateByStore[stateKey] = today.ToString("yyyy-MM-dd");
            SaveState(statePath, state);

            var from = new DateOnly(previousMonth.Year, previousMonth.Month, 1);
            var toDate = previousMonth.AddMonths(1).AddDays(-1);
            var to = new DateOnly(toDate.Year, toDate.Month, toDate.Day);
            var reportFolder = Path.Combine(paths.AppDataDirectory, "Monthly Reports");
            Directory.CreateDirectory(reportFolder);
            var fileName = $"HisabKitab_MonthlyReport_{periodKey}_{SafeFilePart(storeGuid)}.pdf";
            var pdfPath = Path.Combine(reportFolder, fileName);

            await reportService.GenerateAllReportsBundlePdfAsync(from, to, pdfPath, cancellationToken);
            var pdfBytes = await File.ReadAllBytesAsync(pdfPath, cancellationToken);
            using var client = new LiveBankSyncClient();
            await client.EmailReportAsync(
                license.MonthlyReportEmail,
                "Monthly Business Report",
                $"{from:M/d/yyyy} to {to:M/d/yyyy}",
                fileName,
                pdfBytes,
                cancellationToken);

            state.LastSentPeriodByStore[stateKey] = periodKey;
            SaveState(statePath, state);
            return $"Monthly report for {previousMonth:MMMM yyyy} was emailed to {license.MonthlyReportEmail}.";
        }
        catch
        {
            // A failed delivery remains unsent and is retried the next day the app opens.
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static MonthlyReportDeliveryState LoadState(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<MonthlyReportDeliveryState>(File.ReadAllText(path), JsonOptions)
                  ?? new MonthlyReportDeliveryState()
                : new MonthlyReportDeliveryState();
        }
        catch
        {
            return new MonthlyReportDeliveryState();
        }
    }

    private static void SaveState(string path, MonthlyReportDeliveryState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporary, path, true);
    }

    private static string SafeFilePart(string value)
        => string.Concat(value.Select(character => char.IsLetterOrDigit(character) ? character : '_'));

    private sealed class MonthlyReportDeliveryState
    {
        public Dictionary<string, string> LastSentPeriodByStore { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> LastAttemptDateByStore { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
