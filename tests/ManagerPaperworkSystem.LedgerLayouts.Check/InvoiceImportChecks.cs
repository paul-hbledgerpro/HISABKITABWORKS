using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Services;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.UI.Services;
using ManagerPaperworkSystem.WinForms;
using Microsoft.EntityFrameworkCore;

internal static class InvoiceImportChecks
{
    private sealed record Paths(string AppDataDirectory) : IAppPaths
    {
        public string DatabasePath => Path.Combine(AppDataDirectory, "invoices.db");
        public string BackupsDirectory => Path.Combine(AppDataDirectory, "backups");
    }
    private sealed class Factory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=" + path).Options);
    }
    private static void Check(bool condition, string detail) { if (!condition) throw new Exception(detail); }
    public static async Task RunAsync(string output, string[] sourcePdfs)
    {
        // Only the new fixture database is written. Source PDFs are read-only and never checked in.
        var paths = new Paths(Path.Combine(output, "invoice-fixture-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(paths.AppDataDirectory);
        var factory = new Factory(paths.DatabasePath);
        using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            db.Stores.AddRange(new Store { Id = 1, Name = "Fixture store" }, new Store { Id = 2, Name = "Other store" });
            await db.SaveChangesAsync();
        }
        var importer = new InvoiceImportService();
        var purchase = new PurchaseService(factory, paths);
        var service = new InvoiceEmailSyncService(paths, importer, purchase);
        var expectedImported = 0; var expectedReview = 0;
        foreach (var source in sourcePdfs)
        {
            var parsed = await importer.ImportAsync(source);
            Check(parsed.Success, "Sample must parse");
            expectedImported += parsed.Invoices.Count(x => InvoiceValidation.ReviewReason(x) is null);
            expectedReview += parsed.Invoices.Count(x => InvoiceValidation.ReviewReason(x) is not null);
        }
        Check(expectedImported > 0, "At least one sample must be verified");
        var first = await service.ImportDownloadedPdfsAsync("fixture", 1, 1, "Fixture Owner", sourcePdfs);
        Check(first.InvoicesImported == expectedImported && first.NeedsReview == expectedReview,
            $"Expected {expectedImported} imports/{expectedReview} reviews, got {first.InvoicesImported}/{first.NeedsReview}");
        var repeated = await service.ImportDownloadedPdfsAsync("fixture", 1, 1, "Fixture Owner", sourcePdfs);
        Check(repeated.InvoicesImported == 0 && repeated.DuplicatesSkipped == expectedImported, "Retry must not duplicate invoices or costs");
        using var verify = factory.CreateDbContext();
        Check(await verify.PurchaseInvoices.CountAsync() == expectedImported, "Stored invoice count");
        Check(!await verify.PurchaseInvoices.AnyAsync(x => x.StoreId != 1) && !await verify.ProductCosts.AnyAsync(x => x.StoreId != 1), "Store isolation");
        Check(await verify.ProductCosts.AnyAsync(x => x.LastUnitCost > 0), "Invoice lines create product costs");
        Check(await verify.PurchaseInvoices.AnyAsync(x => x.Notes.Contains("Printed invoice adjustments")), "Printed adjustments retained in notes");
        Console.WriteLine($"PASS: {expectedImported} verified invoices imported, {expectedReview} left for review; repeat import skipped all duplicates; product costs populated; other store untouched. No app launched.");
    }
}
