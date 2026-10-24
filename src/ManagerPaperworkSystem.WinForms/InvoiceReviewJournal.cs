using System.Text.Json;
using ManagerPaperworkSystem.Core.Services;

namespace ManagerPaperworkSystem.WinForms;

internal sealed record InvoiceReviewItem(DateTime Time, string Status, string File, string Detail);
internal static class InvoiceReviewJournal
{
    private static string Folder(IAppPaths paths, string key) => Path.Combine(paths.AppDataDirectory, "Invoice Reviews",
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key.Trim().ToUpperInvariant()))));
    public static void Record(IAppPaths paths, string key, string file, string status, string detail)
    {
        var folder = Folder(paths, key); Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json");
        var temporary = destination + ".tmp";
        System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(new InvoiceReviewItem(DateTime.UtcNow, status, file, detail)));
        System.IO.File.Move(temporary, destination);
    }
    public static List<InvoiceReviewItem> Read(IAppPaths paths, string key)
    {
        var folder = Folder(paths, key); var result = new List<InvoiceReviewItem>();
        if (!Directory.Exists(folder)) return result;
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            try { var row = JsonSerializer.Deserialize<InvoiceReviewItem>(System.IO.File.ReadAllText(file)); if (row is not null) result.Add(row); } catch (IOException) { } catch (JsonException) { }
        return result.OrderByDescending(x => x.Time).ToList();
    }
}
