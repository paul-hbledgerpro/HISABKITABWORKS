using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ManagerPaperworkSystem.WinForms;

internal enum PortalSyncReportKind
{
    CashSalesSummary,
    ZReports
}

internal sealed class PortalSyncSettingsDocument
{
    public int Version { get; set; } = 2;
    public List<PortalStoreSyncSettings> Stores { get; set; } = [];
}

internal sealed class PortalStoreSyncSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public string BusinessName { get; set; } = "";
    public string StoreGuid { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public string PortalStoreName { get; set; } = "";
    public string PortalUrl { get; set; } = "https://posweboffice.com/";
    public string PortalEmail { get; set; } = "";
    public string PortalPassword { get; set; } = "";
    public string StoreUserName { get; set; } = "";
    public string StorePassword { get; set; } = "";
    public int DailyHour { get; set; } = 1;
    public int DailyMinute { get; set; } = 15;
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public DateOnly? LastImportedReportDate { get; set; }
    public bool CashSalesSummaryEnabled { get; set; } = true;
    public int CashSalesDailyHour { get; set; } = 1;
    public int CashSalesDailyMinute { get; set; } = 15;
    public DateTime? LastCashSummaryAttemptUtc { get; set; }
    public DateTime? LastCashSummarySuccessUtc { get; set; }
    public DateOnly? LastCashSummaryReportDate { get; set; }
    public string LastCashSummaryStatus { get; set; } = "Not run yet";
    public bool ZReportsEnabled { get; set; } = true;
    public int ZReportsDailyHour { get; set; } = 1;
    public int ZReportsDailyMinute { get; set; } = 30;
    public DateTime? LastZReportAttemptUtc { get; set; }
    public DateTime? LastZReportSuccessUtc { get; set; }
    public DateOnly? LastZReportDate { get; set; }
    public string LastZReportStatus { get; set; } = "Not run yet";
    public string LastStatus { get; set; } = "Not run yet";

    public bool IsEnabled(PortalSyncReportKind reportKind) =>
        reportKind == PortalSyncReportKind.CashSalesSummary
            ? CashSalesSummaryEnabled
            : ZReportsEnabled;

    public TimeOnly GetRunTime(PortalSyncReportKind reportKind) =>
        reportKind == PortalSyncReportKind.CashSalesSummary
            ? new TimeOnly(CashSalesDailyHour, CashSalesDailyMinute)
            : new TimeOnly(ZReportsDailyHour, ZReportsDailyMinute);

    public string GetLastStatus(PortalSyncReportKind reportKind) =>
        reportKind == PortalSyncReportKind.CashSalesSummary
            ? LastCashSummaryStatus
            : LastZReportStatus;
}

internal static class PortalSyncSettingsStore
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("HISAB-KITAB-WORKS-POS-PORTAL-SYNC-V1");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string ProtectedPath =>
        Path.Combine(AppBootstrap.AppDataPath, "pos-portal-sync.protected");

    public static string ProfileDirectory(Guid id) =>
        ResolveAutomationDirectory(id, "ChromeProfiles", "Chrome Profiles");

    public static string DownloadDirectory(Guid id) =>
        ResolveAutomationDirectory(id, "Downloads", "Downloads");

    public static string ReportFeedDirectory(Guid id) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HisabKitabPOS",
            "ReportFeeds",
            id.ToString("N"));

    public static string CashSalesSummaryFeedDirectory(Guid id) =>
        Path.Combine(ReportFeedDirectory(id), "Cash Sales Summary");

    public static string ZReportFeedDirectory(Guid id) =>
        Path.Combine(ReportFeedDirectory(id), "Z Reports");

    public static string StoreReportArchiveDirectory(PortalStoreSyncSettings settings)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
        {
            documents = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents");
        }

        var invalid = Path.GetInvalidFileNameChars();
        var storeName = new string((settings.BusinessName ?? "")
            .Trim()
            .Where(character => !invalid.Contains(character))
            .ToArray());
        if (string.IsNullOrWhiteSpace(storeName))
            storeName = "HISAB KITAB Store";

        return Path.Combine(documents, $"{storeName} Reports");
    }

    public static string CashSalesSummaryArchiveDirectory(PortalStoreSyncSettings settings) =>
        Path.Combine(StoreReportArchiveDirectory(settings), "Cash and Sales Summary Reports");

    public static string ZReportArchiveDirectory(PortalStoreSyncSettings settings) =>
        Path.Combine(StoreReportArchiveDirectory(settings), "Z Reports");

    public static PortalSyncSettingsDocument Load()
    {
        try
        {
            if (!File.Exists(ProtectedPath))
                return new PortalSyncSettingsDocument();
            var protectedBytes = File.ReadAllBytes(ProtectedPath);
            var clear = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                var document = JsonSerializer.Deserialize<PortalSyncSettingsDocument>(clear, JsonOptions)
                               ?? new PortalSyncSettingsDocument();
                return Normalize(document);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
        catch
        {
            return new PortalSyncSettingsDocument();
        }
    }

    public static void Save(PortalSyncSettingsDocument document)
    {
        Directory.CreateDirectory(AppBootstrap.AppDataPath);
        Normalize(document);
        var clear = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        try
        {
            var protectedBytes = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            var temporaryPath = ProtectedPath + ".new";
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, ProtectedPath, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private static PortalSyncSettingsDocument Normalize(PortalSyncSettingsDocument document)
    {
        document.Stores ??= [];
        if (document.Version < 2)
        {
            foreach (var settings in document.Stores)
            {
                settings.CashSalesSummaryEnabled = settings.Enabled;
                settings.ZReportsEnabled = settings.Enabled;
                settings.CashSalesDailyHour = settings.DailyHour;
                settings.CashSalesDailyMinute = settings.DailyMinute;
                settings.ZReportsDailyHour = settings.DailyHour;
                settings.ZReportsDailyMinute = settings.DailyMinute;
                settings.LastCashSummaryAttemptUtc = settings.LastAttemptUtc;
                settings.LastCashSummarySuccessUtc = settings.LastSuccessUtc;
                settings.LastZReportAttemptUtc = settings.LastAttemptUtc;
                settings.LastZReportSuccessUtc = settings.LastSuccessUtc;
                settings.LastCashSummaryStatus = settings.LastStatus;
                settings.LastZReportStatus = settings.LastStatus;
            }
        }

        foreach (var settings in document.Stores)
        {
            settings.CashSalesDailyHour = Math.Clamp(settings.CashSalesDailyHour, 0, 23);
            settings.CashSalesDailyMinute = Math.Clamp(settings.CashSalesDailyMinute, 0, 59);
            settings.ZReportsDailyHour = Math.Clamp(settings.ZReportsDailyHour, 0, 23);
            settings.ZReportsDailyMinute = Math.Clamp(settings.ZReportsDailyMinute, 0, 59);

            // Keep the original fields populated so older installed versions can
            // still read the protected document while clients roll forward.
            settings.Enabled = settings.CashSalesSummaryEnabled || settings.ZReportsEnabled;
            settings.DailyHour = settings.CashSalesDailyHour;
            settings.DailyMinute = settings.CashSalesDailyMinute;
            settings.LastAttemptUtc = Latest(
                settings.LastCashSummaryAttemptUtc,
                settings.LastZReportAttemptUtc);
            settings.LastSuccessUtc = Latest(
                settings.LastCashSummarySuccessUtc,
                settings.LastZReportSuccessUtc);
            settings.LastStatus = string.Join(" | ", new[]
            {
                $"Cash & Sales: {settings.LastCashSummaryStatus}",
                $"Z Reports: {settings.LastZReportStatus}"
            });
        }

        document.Version = 2;
        return document;
    }

    private static DateTime? Latest(DateTime? left, DateTime? right)
    {
        if (!left.HasValue)
            return right;
        if (!right.HasValue)
            return left;
        return left.Value >= right.Value ? left : right;
    }

    private static string ResolveAutomationDirectory(
        Guid id,
        string compactDirectoryName,
        string legacyDirectoryName)
    {
        // Chrome's new headless mode treats unquoted path fragments as
        // additional page targets. Puppeteer/Chrome combinations on Windows
        // can expose that behavior when a persistent profile or download path
        // contains spaces. Keep the automation-only files in a compact path.
        var compactPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HisabKitabPOS",
            compactDirectoryName,
            id.ToString("N"));
        if (Directory.Exists(compactPath))
            return compactPath;

        var legacyPath = Path.Combine(
            AppBootstrap.AppDataPath,
            "POS Portal Sync",
            legacyDirectoryName,
            id.ToString("N"));
        if (!Directory.Exists(legacyPath))
            return compactPath;

        Directory.CreateDirectory(Path.GetDirectoryName(compactPath)!);
        try
        {
            Directory.Move(legacyPath, compactPath);
        }
        catch (IOException)
        {
            CopyDirectory(legacyPath, compactPath);
        }
        catch (UnauthorizedAccessException)
        {
            CopyDirectory(legacyPath, compactPath);
        }
        return compactPath;
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)), true);
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
            CopyDirectory(
                directory,
                Path.Combine(destinationDirectory, Path.GetFileName(directory)));
    }
}
