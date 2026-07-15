using System;
using System.Collections.Generic;
using System.Linq;

using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Services;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.Reports.Pdf;
using ManagerPaperworkSystem.UI.Services;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.UI.Services;

public sealed class ReportService : IReportService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ISettingsService _settingsService;
    private StoreConnectionService? _storeConnService;

    public ReportService(IDbContextFactory<AppDbContext> dbFactory, ISettingsService settingsService)
    {
        _dbFactory = dbFactory;
        _settingsService = settingsService;
    }

    /// <summary>
    /// Set the StoreConnectionService for multi-database support.
    /// Called by MainWindow after initialization.
    /// </summary>
    public void SetStoreConnectionService(StoreConnectionService service)
    {
        _storeConnService = service;
    }

    /// <summary>
    /// Creates a DbContext that respects store-specific database connections.
    /// </summary>
    private AppDbContext CreateDb()
    {
        return _storeConnService?.CreateDbContext() ?? _dbFactory.CreateDbContext();
    }

    private async Task<(int storeId, string storeName, string storeAddress)> GetCurrentStoreAsync(CancellationToken ct)
    {
        var s = await _settingsService.GetSettingsAsync(ct);
        var storeId = s.LastStoreId > 0 ? s.LastStoreId : s.DefaultStoreId;

        using var db = CreateDb();

        // If using a store-specific database, the data uses StoreId=1 in its own DB
        if (_storeConnService != null && _storeConnService.HasCustomConnection(storeId))
        {
            var remoteStore = await db.Stores.AsNoTracking().FirstOrDefaultAsync(ct);
            if (remoteStore != null)
                return (remoteStore.Id, remoteStore.Name, remoteStore.Address);
        }

        var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(x => x.Id == storeId, ct);
        if (store is null)
            return (storeId, s.StoreName, s.StoreAddress);

        return (storeId, store.Name, store.Address);
    }

    #region Shift Log Reports

    public async Task GenerateShiftLogPdfAsync(DateOnly from, DateOnly to, string outputPdfPath, CancellationToken ct = default)
    {
        await GenerateShiftLogDetailPdfAsync(from, to, outputPdfPath, ct);
    }

    public async Task GenerateShiftLogDetailPdfAsync(DateOnly from, DateOnly to, string outputPdfPath, CancellationToken ct = default)
    {
        PdfRuntime.EnsureAvailableOrThrow();
        var (storeId, storeName, storeAddress) = await GetCurrentStoreAsync(ct);

        using var db = CreateDb();
        var entries = await db.ShiftLogs.AsNoTracking()
            .Where(x => x.StoreId == storeId && x.Date >= from && x.Date <= to)
            .OrderBy(x => x.Date)
            .ToListAsync(ct);

        var eff = EffectiveRows(entries, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);
        SelectedOptionReportPdf.GenerateShiftLog(storeName, storeAddress, from, to, eff, outputPdfPath);
    }

    public async Task GenerateShiftLogSummaryPdfAsync(DateOnly from, DateOnly to, string outputPdfPath, CancellationToken ct = default)
    {
        await GenerateSalesSummaryByDatePdfAsync(from, to, outputPdfPath, ct);
    }

    #endregion

    #region Cash On Hand Reports

    public async Task GenerateCashOnHandPdfAsync(DateOnly from, DateOnly to, string outputPdfPath, CancellationToken ct = default)
    {
        await GenerateCashOnHandDetailPdfAsync(from, to, outputPdfPath, ct);
    }

    public async Task GenerateCashOnHandDetailPdfAsync(DateOnly from, DateOnly to, string outputPdfPath, CancellationToken ct = default)
    {
        PdfRuntime.EnsureAvailableOrThrow();
        var (storeId, storeName, storeAddress) = await GetCurrentStoreAsync(ct);

        using var db = CreateDb();
        var entries = await db.CashOnHand.AsNoTracking()
            .Include(x => x.Vendor)
            .Include(x => x.Purpose)
            .Where(x => x.StoreId == storeId && x.Date >= from && x.Date <= to)
            .OrderBy(x => x.Date)
            .ToListAsync(ct);

        var eff = EffectiveRows(entries, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);
        SelectedOptionReportPdf.GenerateCashOnHand(storeName, storeAddress, from, to, eff, outputPdfPath);
    }

    public async Task GenerateCashOnHandSummaryPdfAsync(DateOnly from, DateOnly to, string outputPdfPath, CancellationToken ct = default)
    {
        PdfRuntime.EnsureAvailableOrThrow();
        var (storeId, storeName, storeAddress) = await GetCurrentStoreAsync(ct);

        using var db = CreateDb();
        var entries = await db.CashOnHand.AsNoTracking()
            .Include(x => x.Vendor)
            .Include(x => x.Purpose)
            .Where(x => x.StoreId == storeId && x.Date >= from && x.Date <= to)
            .OrderBy(x => x.Date)
            .ToListAsync(ct);

        var eff = EffectiveRows(entries, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);
        CashOnHandPdf.GenerateSummary(storeName, storeAddress, from, to, eff, outputPdfPath);
    }

    #endregion

    #region Check Payouts Reports

    public async Task GenerateCheckPayoutsPdfAsync(DateOnly from, DateOnly to, string outputPdfPath, CancellationToken ct = default)
    {
        await GenerateCheckPayoutsDetailPdfAsync(from, to, outputPdfPath, ct);
    }

    public async Task GenerateCheckPayoutsDetailPdfAsync(DateOnly from, DateOnly to, string outputPdfPath, CancellationToken ct = default)
    {
        PdfRuntime.EnsureAvailableOrThrow();
        var (storeId, storeName, storeAddress) = await GetCurrentStoreAsync(ct);

        using var db = CreateDb();
        var entries = await db.CheckPayouts.AsNoTracking()
            .Where(x => x.StoreId == storeId && x.Date >= from && x.Date <= to)
            .OrderBy(x => x.Date)
            .ToListAsync(ct);

        var eff = EffectiveRows(entries, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);
        SelectedOptionReportPdf.GenerateCheckPayouts(storeName, storeAddress, from, to, eff, outputPdfPath);
    }

    public async Task GenerateCheckPayoutsSummaryPdfAsync(DateOnly from, DateOnly to, string outputPdfPath, CancellationToken ct = default)
    {
        PdfRuntime.EnsureAvailableOrThrow();
        var (storeId, storeName, storeAddress) = await GetCurrentStoreAsync(ct);

        using var db = CreateDb();
        var entries = await db.CheckPayouts.AsNoTracking()
            .Where(x => x.StoreId == storeId && x.Date >= from && x.Date <= to)
            .OrderBy(x => x.Date)
            .ToListAsync(ct);

        var eff = EffectiveRows(entries, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);
        CheckPayoutsPdf.GenerateSummary(storeName, storeAddress, from, to, eff, outputPdfPath);
    }

    #endregion

    #region Sales Summary

    public async Task GenerateSalesSummaryByDatePdfAsync(DateOnly from, DateOnly to, string outputPdfPath, CancellationToken ct = default)
    {
        PdfRuntime.EnsureAvailableOrThrow();
        var (storeId, storeName, storeAddress) = await GetCurrentStoreAsync(ct);

        using var db = CreateDb();
        var entries = await db.ShiftLogs.AsNoTracking()
            .Where(x => x.StoreId == storeId && x.Date >= from && x.Date <= to)
            .OrderBy(x => x.Date)
            .ToListAsync(ct);

        var eff = EffectiveRows(entries, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);
        SelectedOptionReportPdf.GenerateSalesSummary(storeName, storeAddress, from, to, eff, outputPdfPath);
    }

    #endregion

    #region Profit & Loss

    public async Task<ProfitLossData> GetProfitLossDataAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var (storeId, _, _) = await GetCurrentStoreAsync(ct);

        using var db = CreateDb();

        var shiftLogs = await db.ShiftLogs.AsNoTracking()
            .Where(x => x.StoreId == storeId && x.Date >= from && x.Date <= to)
            .ToListAsync(ct);
        var effShifts = EffectiveRows(shiftLogs, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);

        var cashEntries = await db.CashOnHand.AsNoTracking()
            .Where(x => x.StoreId == storeId && x.Date >= from && x.Date <= to)
            .ToListAsync(ct);
        var effCash = EffectiveRows(cashEntries, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);

        var checkEntries = await db.CheckPayouts.AsNoTracking()
            .Where(x => x.StoreId == storeId && x.Date >= from && x.Date <= to)
            .ToListAsync(ct);
        var effChecks = EffectiveRows(checkEntries, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);

        var purchases = await db.PurchaseInvoices.AsNoTracking()
            .Where(x => x.StoreId == storeId && x.InvoiceDate >= from && x.InvoiceDate <= to)
            .ToListAsync(ct);

        var data = new ProfitLossData
        {
            GrossSales = effShifts.Sum(x => x.NetSales),
            SalesTax = effShifts.Sum(x => x.Tax),
            CashPayouts = effCash.Where(x => x.IsPayout).Sum(x => x.PayoutAmount),
            CheckPayouts = effChecks.Sum(x => x.CheckAmount),
            Purchases = purchases.Sum(x => x.Total)
        };

        // Bank Statement Transactions
        try
        {
            var fromDate = from.ToDateTime(TimeOnly.MinValue);
            var toDate = to.ToDateTime(TimeOnly.MaxValue);

            var bankTxns = await db.Database.SqlQueryRaw<BankTxnRow>(
                @"SELECT ISNULL(Category, 'Other') AS Category, 
                         ISNULL(SUM(Credit), 0) AS TotalCredit, 
                         ISNULL(SUM(Debit), 0) AS TotalDebit
                  FROM BankStatementTransactions 
                  WHERE StoreId = {0} AND Date >= {1} AND Date <= {2}
                  GROUP BY Category",
                storeId, fromDate, toDate).ToListAsync(ct);

            foreach (var txn in bankTxns)
            {
                var cat = (txn.Category ?? "Other").Trim().ToLower();

                if (txn.TotalCredit > 0)
                {
                    if (cat.Contains("deposit") || cat.Contains("income") || cat.Contains("transfer in"))
                        data.BankDeposits += txn.TotalCredit;
                }

                if (txn.TotalDebit > 0)
                {
                    if (cat.Contains("utilit")) data.Utilities += txn.TotalDebit;
                    else if (cat.Contains("rent") || cat.Contains("lease")) data.Rent += txn.TotalDebit;
                    else if (cat.Contains("payroll") || cat.Contains("salary") || cat.Contains("wage")) data.Payroll += txn.TotalDebit;
                    else if (cat.Contains("insurance")) data.Insurance += txn.TotalDebit;
                    else if (cat.Contains("bank") || cat.Contains("fee") || cat.Contains("service charge")) data.BankFees += txn.TotalDebit;
                    else if (cat.Contains("tax")) data.Taxes += txn.TotalDebit;
                    else if (cat.Contains("loan") || cat.Contains("debt") || cat.Contains("mortgage")) data.LoanDebt += txn.TotalDebit;
                    else data.OtherBankExpenses += txn.TotalDebit;
                }
            }
        }
        catch
        {
            // Bank statement table may not exist — that's fine
        }

        return data;
    }

    private class BankTxnRow
    {
        public string Category { get; set; } = "";
        public decimal TotalCredit { get; set; }
        public decimal TotalDebit { get; set; }
    }

    public async Task GenerateProfitLossPdfAsync(DateOnly from, DateOnly to, string outputPdfPath, CancellationToken ct = default)
    {
        PdfRuntime.EnsureAvailableOrThrow();
        var (storeId, storeName, storeAddress) = await GetCurrentStoreAsync(ct);
        var data = await GetProfitLossDataAsync(from, to, ct);

        SelectedOptionReportPdf.GenerateProfitLoss(storeName, storeAddress, from, to,
            data.GrossSales, data.SalesTax, data.Purchases,
            data.CashPayouts, data.CheckPayouts, outputPdfPath);
    }

    #endregion

    public async Task GenerateAllReportsBundlePdfAsync(DateOnly from, DateOnly to, string outputPdfPath, CancellationToken ct = default)
    {
        PdfRuntime.EnsureAvailableOrThrow();
        var (storeId, storeName, storeAddress) = await GetCurrentStoreAsync(ct);

        using var db = CreateDb();
        var shifts = await db.ShiftLogs.AsNoTracking()
            .Where(x => x.StoreId == storeId && x.Date >= from && x.Date <= to)
            .OrderBy(x => x.Date)
            .ThenBy(x => x.ShiftNo)
            .ToListAsync(ct);
        var effShifts = EffectiveRows(shifts, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);

        var cash = await db.CashOnHand.AsNoTracking()
            .Include(x => x.Vendor)
            .Include(x => x.Purpose)
            .Where(x => x.StoreId == storeId && x.Date >= from && x.Date <= to)
            .OrderBy(x => x.Date)
            .ToListAsync(ct);
        var effCash = EffectiveRows(cash, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);

        var checks = await db.CheckPayouts.AsNoTracking()
            .Where(x => x.StoreId == storeId && x.Date >= from && x.Date <= to)
            .OrderBy(x => x.Date)
            .ToListAsync(ct);
        var effChecks = EffectiveRows(checks, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);

        var purchases = await db.PurchaseInvoices.AsNoTracking()
            .Where(x => x.StoreId == storeId && x.InvoiceDate >= from && x.InvoiceDate <= to)
            .OrderBy(x => x.InvoiceDate)
            .ToListAsync(ct);

        SelectedOptionReportPdf.GenerateAllReportsBundle(storeName, storeAddress, from, to, effShifts, effCash, effChecks, purchases, outputPdfPath);
    }

    private static List<T> EffectiveRows<T>(IEnumerable<T> all,
        Func<T, bool> isCorrection,
        Func<T, int?> correctsId,
        Func<T, int> id,
        Func<T, DateTime> createdUtc)
    {
        var originals = all.Where(x => !isCorrection(x)).ToList();
        var latestCorr = all.Where(isCorrection)
            .Select(x => (row: x, target: correctsId(x)))
            .Where(x => x.target.HasValue)
            .GroupBy(x => x.target!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => createdUtc(x.row)).First().row);

        var result = new List<T>(originals.Count);
        foreach (var o in originals)
        {
            if (latestCorr.TryGetValue(id(o), out var corr)) result.Add(corr);
            else result.Add(o);
        }
        return result;
    }
}
