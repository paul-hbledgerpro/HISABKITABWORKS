namespace ManagerPaperworkSystem.WinForms;

// Redirect the production settings implementation to an isolated directory.
internal static class AppBootstrap
{
    public static string AppDataPath { get; } = Path.Combine(Path.GetTempPath(), "HK-Settings-Test-" + Guid.NewGuid().ToString("N"));
}
internal sealed class LicensedBusinessConnection
{
    public int BusinessId { get; set; }
    public string BusinessName { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public string StoreGuid { get; set; } = "";
}
internal static class LicensedBusinessService
{
    public static IReadOnlyList<LicensedBusinessConnection> Load() => [];
}
internal static class StoreDirectoryPreferencesStore
{
    public static bool IsConnected(LicensedBusinessConnection business, IReadOnlyList<LicensedBusinessConnection> all) => true;
}
