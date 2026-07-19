using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.WinForms;

internal sealed record CashSalesSummaryImportOutcome(
    bool Imported,
    bool Duplicate,
    int? SummaryId,
    CashSalesSummaryImportResult Parsed,
    string StoredPath);

internal static class CashSalesSummaryImportCoordinator
{
    public static async Task<CashSalesSummaryImportOutcome> ImportAsync(
        AppDbContext db,
        IAppPaths paths,
        int storeId,
        string sourcePath,
        int importedByUserId,
        string importedByName,
        CancellationToken cancellationToken = default)
    {
        var parsed = await CashSalesSummaryPdfImporter.ImportAsync(sourcePath, cancellationToken);
        Validate(parsed);

        var duplicate = await db.PosSalesSummaries.AsNoTracking().AnyAsync(
            item => item.StoreId == storeId && item.SourceFileSha256 == parsed.SourceFileSha256,
            cancellationToken);
        if (duplicate)
            return new CashSalesSummaryImportOutcome(false, true, null, parsed, "");

        var reportFolder = Path.Combine(
            paths.AppDataDirectory,
            "POS Cash Sales Summaries",
            storeId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(reportFolder);
        var storedName =
            $"{parsed.ReportFrom:yyyyMMdd}_{parsed.ReportTo:yyyyMMdd}_{parsed.SourceFileSha256[..12]}.pdf";
        var storedPath = Path.Combine(reportFolder, storedName);
        if (!File.Exists(storedPath))
            File.Copy(sourcePath, storedPath, false);

        var entity = CreateEntity(parsed, storeId, storedPath, importedByUserId, importedByName);
        db.PosSalesSummaries.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return new CashSalesSummaryImportOutcome(true, false, entity.Id, parsed, storedPath);
    }

    public static void Validate(CashSalesSummaryImportResult parsed)
    {
        if (parsed.ReportFrom is null || parsed.ReportTo is null)
            throw new InvalidOperationException("The report date range could not be read.");
        if (parsed.ReportTo < parsed.ReportFrom)
            throw new InvalidOperationException("The report end date is earlier than its start date.");
        if (parsed.NetSales <= 0m || parsed.GrossSales <= 0m)
            throw new InvalidOperationException("The PDF did not contain valid gross and net sales totals.");
        if (parsed.TenderLines.Count == 0)
            throw new InvalidOperationException("The PDF did not contain a valid tender breakdown.");
    }

    private static PosSalesSummary CreateEntity(
        CashSalesSummaryImportResult parsed,
        int storeId,
        string storedPath,
        int importedByUserId,
        string importedByName)
        => new()
        {
            StoreId = storeId,
            ReportFrom = parsed.ReportFrom!.Value,
            ReportTo = parsed.ReportTo!.Value,
            SourceSystem = parsed.SourceSystem,
            ReportedStoreName = parsed.StoreName,
            SourceFileName = parsed.SourceFileName,
            SourceFilePath = storedPath,
            SourceFileSha256 = parsed.SourceFileSha256,
            TenderTransactionCount = parsed.TenderTransactionCount,
            GrossAmountReceived = parsed.GrossAmountReceived,
            GiftCardRedeemed = parsed.GiftCardRedeemed,
            NonRevenueReceived = parsed.NonRevenueReceived,
            NonRevenueReturned = parsed.NonRevenueReturned,
            NonRevenueAmount = parsed.NonRevenueAmount,
            GrossSales = parsed.GrossSales,
            Taxes = parsed.Taxes,
            NetSales = parsed.NetSales,
            TaxableSales = parsed.TaxableSales,
            NonTaxableSales = parsed.NonTaxableSales,
            RoundingOffset = parsed.RoundingOffset,
            CashSales = parsed.CashSales,
            CardSales = parsed.CardSales,
            CustomerTransactionCount = parsed.CustomerTransactionCount,
            CustomerAverageSale = parsed.CustomerAverageSale,
            UserLoginCount = parsed.UserLoginCount,
            DeleteVoidCount = parsed.DeleteVoidCount,
            NoSaleCount = parsed.NoSaleCount,
            VoidDeleteAmount = parsed.VoidDeleteAmount,
            TotalDiscount = parsed.TotalDiscount,
            DepartmentQuantity = parsed.DepartmentQuantity,
            DepartmentSales = parsed.DepartmentSales,
            DepartmentCost = parsed.DepartmentCost,
            DepartmentProfit = parsed.DepartmentProfit,
            DepartmentProfitPercent = parsed.DepartmentProfitPercent,
            ImportedByUserId = importedByUserId,
            ImportedByName = importedByName,
            TenderLines = parsed.TenderLines.Select(line => new PosSalesTenderLine
            {
                TenderType = line.TenderType,
                TransactionCount = line.TransactionCount,
                Amount = line.Amount
            }).ToList(),
            HourlyLines = parsed.HourlyLines.Select(line => new PosSalesHourlyLine
            {
                TimePeriod = line.TimePeriod,
                TransactionCount = line.TransactionCount,
                Amount = line.Amount
            }).ToList(),
            DepartmentLines = parsed.DepartmentLines.Select(line => new PosSalesDepartmentLine
            {
                Department = line.Department,
                Quantity = line.Quantity,
                Sales = line.Sales,
                Cost = line.Cost,
                Profit = line.Profit,
                ProfitPercent = line.ProfitPercent,
                SalesPercent = line.SalesPercent
            }).ToList()
        };
}
