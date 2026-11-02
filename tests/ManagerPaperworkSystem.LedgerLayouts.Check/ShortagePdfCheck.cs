using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Reports.Pdf;
using QuestPDF.Infrastructure;

internal static class ShortagePdfCheck
{
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        QuestPDF.Settings.License = LicenseType.Community;
        var report = new ShiftShortageReport("Example Smoke Shop (fictional)", "3451", new(2026, 11, 2),
            "Sample Employee", 200, 350, 510, 40, 160, 15, "Example Supplier", "Supplies for this shift",
            25, 10, "Sample Manager", new DateTime(2026, 11, 2, 18, 0, 0, DateTimeKind.Utc), false);
        File.WriteAllBytes(Path.Combine(directory, "shortage-example.pdf"), ShiftShortagePdf.Generate(report));
        File.WriteAllBytes(Path.Combine(directory, "shortage-long-example.pdf"), ShiftShortagePdf.Generate(report with {
            StoreName = new string('W', 200), Payee = new string('W', 200), Reason = new string('W', 300),
            Employee = new string('W', 100), EnteredBy = new string('W', 120), IsCorrection = true
        }));
        Console.WriteLine("Generated two fictional PDF fixtures; application not launched and no emails sent.");
    }
}
