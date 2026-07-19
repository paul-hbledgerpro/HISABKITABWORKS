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
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public DateOnly? LastImportedReportDate { get; set; }
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
        Path.Combine(AppBootstrap.AppDataPath, "POS Portal Sync", "Chrome Profiles", id.ToString("N"));

    public static string DownloadDirectory(Guid id) =>
        Path.Combine(AppBootstrap.AppDataPath, "POS Portal Sync", "Downloads", id.ToString("N"));

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
}
