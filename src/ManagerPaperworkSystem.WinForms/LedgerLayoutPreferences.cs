using System.Security.Cryptography;
using System.Text;

namespace ManagerPaperworkSystem.WinForms;

internal enum LedgerLayout { ClassicGrid, SideEntryGrid, GuidedGrid }

internal static class LedgerLayoutPreferences
{
    private static string FilePath(string scope, int userId, string section, string? directory)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{scope}\n{userId}\n{section}")));
        return Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hisab Kitab", "ledger-layouts"), key + ".txt");
    }
    public static LedgerLayout Load(string scope, int userId, string section, string? directory = null)
    {
        try { return Enum.TryParse<LedgerLayout>(File.ReadAllText(FilePath(scope, userId, section, directory)), out var value) && Enum.IsDefined(value) ? value : LedgerLayout.SideEntryGrid; }
        catch (IOException) { return LedgerLayout.SideEntryGrid; }
        catch (UnauthorizedAccessException) { return LedgerLayout.SideEntryGrid; }
    }
    public static void Save(string scope, int userId, string section, LedgerLayout value, string? directory = null)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
        var path = FilePath(scope, userId, section, directory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try { File.WriteAllText(temp, value.ToString()); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
