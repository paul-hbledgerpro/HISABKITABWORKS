using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ManagerPaperworkSystem.WinForms;

internal sealed class PortalSyncSettingsDocument
{
    public int Version { get; set; } = 1;
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
    public int ExpectedDailyZReports { get; set; } = 2;
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public DateOnly? LastImportedReportDate { get; set; }
    public DateOnly? LastCashSummaryReportDate { get; set; }
    public DateOnly? LastZReportDate { get; set; }
    public string LastStatus { get; set; } = "Not run yet";
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
                return JsonSerializer.Deserialize<PortalSyncSettingsDocument>(clear, JsonOptions)
                       ?? new PortalSyncSettingsDocument();
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
