using System.Text.RegularExpressions;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Services;
using ManagerPaperworkSystem.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.UI.Services;

/// <summary>
/// Handles Purchase Invoices, Product Costs and Price Alerts.
/// This is a UI-layer service because it also manages invoice file storage under AppData.
/// </summary>
public sealed class PurchaseService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IAppPaths _paths;

    public PurchaseService(IDbContextFactory<AppDbContext> dbFactory, IAppPaths paths)
    {
        _dbFactory = dbFactory;
        _paths = paths;
    }

    private static string CleanDatabaseText(string? value, int maximumLength)
    {
        var clean = Regex.Replace((value ?? "").Trim(), @"\s+", " ");
        return clean.Length <= maximumLength ? clean : clean[..maximumLength].TrimEnd();
    }

    private static string NormalizeProductKey(string? itemCode, string? name)
    {
        var code = (itemCode ?? "").Trim();
        code = Regex.Replace(code, @"\s+", "");
        code = code.ToUpperInvariant();

        var s = (name ?? "").Trim();
        s = Regex.Replace(s, @"\s+", " ");
        s = s.ToUpperInvariant();

        return CleanDatabaseText(string.IsNullOrWhiteSpace(code) ? s : $"{code}|{s}", 260);
    }

    public string? CopyInvoiceFileToAppData(int storeId, DateOnly invoiceDate, string? sourceFilePath)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath))
            return null;

        var actualPath = sourceFilePath;
        var hash = actualPath.IndexOf('#');
        if (hash >= 0)
            actualPath = actualPath.Substring(0, hash);

        if (!File.Exists(actualPath))
            return null;

        var invoicesRoot = Path.Combine(_paths.AppDataDirectory, "Invoices", $"Store_{storeId}", invoiceDate.ToString("yyyy-MM"));
        Directory.CreateDirectory(invoicesRoot);

        var fileName = Path.GetFileName(actualPath);
        var safeName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{fileName}";
        foreach (var c in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(c, '_');

        var destPath = Path.Combine(invoicesRoot, safeName);
        File.Copy(actualPath, destPath, overwrite: true);
        return destPath;
    }

    public async Task<List<PurchaseInvoice>> GetInvoicesAsync(int storeId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.PurchaseInvoices.AsNoTracking()
            .Where(x => x.StoreId == storeId)
            .OrderByDescending(x => x.InvoiceDate)
            .ThenByDescending(x => x.Id)
            .Take(500)
            .ToListAsync(ct);
    }

    public async Task<PurchaseInvoice?> GetInvoiceWithLinesAsync(int invoiceId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.PurchaseInvoices.AsNoTracking()
            .Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Id == invoiceId, ct);
    }

    public async Task<PurchaseInvoice> AddInvoiceAsync(
        int storeId,
        DateOnly invoiceDate,
        int? vendorId,
        string vendorName,
        string invoiceNumber,
        decimal total,
        string notes,
        string? sourceFilePath,
        IEnumerable<PurchaseInvoiceLine> lines,
        int userId,
        string userName,
        CancellationToken ct = default)
    {
        var copiedPath = CopyInvoiceFileToAppData(storeId, invoiceDate, sourceFilePath) ?? "";

        var inv = new PurchaseInvoice
        {
            StoreId = storeId,
            VendorId = vendorId,
            VendorName = CleanDatabaseText(vendorName, 200),
            InvoiceNumber = CleanDatabaseText(invoiceNumber, 100),
            InvoiceDate = invoiceDate,
            Total = total,
            Notes = CleanDatabaseText(notes, 500),
            FilePath = copiedPath,
            CreatedByUserId = userId,
            CreatedByName = CleanDatabaseText(userName, 120),
            CreatedUtc = DateTime.UtcNow,
            Lines = lines
                .Where(l => !string.IsNullOrWhiteSpace(l.ProductName))
                .Select(l => new PurchaseInvoiceLine
                {
                    ItemCode = CleanDatabaseText(l.ItemCode, 80),
                    ProductName = CleanDatabaseText(l.ProductName, 260),
                    OrdQuantity = l.OrdQuantity,
                    ShipQuantity = l.ShipQuantity,
                    VolumeMl = l.VolumeMl,
                    Tax = l.Tax,
                    Price = l.Price,
                    Amount = l.Amount,
                    Quantity = l.Quantity,
                    UnitCost = l.UnitCost,
                })
                .ToList()
        };

        using var db = _dbFactory.CreateDbContext();
        db.PurchaseInvoices.Add(inv);
        await db.SaveChangesAsync(ct);

        await ApplyCostTrackingAsync(inv, ct);
        return inv;
    }

    public async Task UpdateInvoiceAsync(
        int invoiceId,
        DateOnly invoiceDate,
        int? vendorId,
        string vendorName,
        string invoiceNumber,
        decimal total,
        string notes,
        string? sourceFilePath,
        IEnumerable<PurchaseInvoiceLine> lines,
        CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var inv = await db.PurchaseInvoices.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == invoiceId, ct);
        if (inv is null) throw new Exception("Invoice not found.");

        var affectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in inv.Lines) affectedKeys.Add(NormalizeProductKey(l.ItemCode, l.ProductName));
        foreach (var l in lines) affectedKeys.Add(NormalizeProductKey(l.ItemCode, l.ProductName));

        inv.InvoiceDate = invoiceDate;
        inv.VendorId = vendorId;
        inv.VendorName = CleanDatabaseText(vendorName, 200);
        inv.InvoiceNumber = CleanDatabaseText(invoiceNumber, 100);
        inv.Total = total;
        inv.Notes = CleanDatabaseText(notes, 500);

        if (!string.IsNullOrWhiteSpace(sourceFilePath) && File.Exists(sourceFilePath))
            inv.FilePath = CopyInvoiceFileToAppData(inv.StoreId, invoiceDate, sourceFilePath) ?? inv.FilePath;

        inv.Lines.Clear();
        foreach (var l in lines.Where(l => !string.IsNullOrWhiteSpace(l.ProductName)))
        {
            inv.Lines.Add(new PurchaseInvoiceLine
            {
                ItemCode = CleanDatabaseText(l.ItemCode, 80),
                ProductName = CleanDatabaseText(l.ProductName, 260),
                OrdQuantity = l.OrdQuantity,
                ShipQuantity = l.ShipQuantity,
                VolumeMl = l.VolumeMl,
                Tax = l.Tax,
                Price = l.Price,
                Amount = l.Amount,
                Quantity = l.Quantity,
                UnitCost = l.UnitCost,
            });
        }

        await db.SaveChangesAsync(ct);
        await RecomputeCostsForStoreAsync(inv.StoreId, affectedKeys, ct);
    }

    public async Task DeleteInvoiceAsync(int invoiceId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var inv = await db.PurchaseInvoices.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == invoiceId, ct);
        if (inv is null) return;

        var storeId = inv.StoreId;
        var affectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in inv.Lines) affectedKeys.Add(NormalizeProductKey(l.ItemCode, l.ProductName));

        db.PurchaseInvoices.Remove(inv);
        await db.SaveChangesAsync(ct);

        await RecomputeCostsForStoreAsync(storeId, affectedKeys, ct);
    }

    public async Task<List<ProductCost>> GetProductCostsAsync(int storeId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.ProductCosts.AsNoTracking()
            .Where(x => x.StoreId == storeId)
            .OrderBy(x => x.ProductName)
            .ToListAsync(ct);
    }

    public async Task<(int upsertCount, int alertCount)> ImportProductCostsAsync(
        int storeId,
        string vendorName,
        string invoiceNumber,
        DateOnly invoiceDate,
        IEnumerable<PurchaseInvoiceLine> lines,
        CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        int upserts = 0, alerts = 0;

        foreach (var line in lines)
        {
            var key = NormalizeProductKey(line.ItemCode, line.ProductName);
            if (string.IsNullOrWhiteSpace(key)) continue;

            var sku = CleanDatabaseText(line.ItemCode, 80);
            var existing = await db.ProductCosts.FirstOrDefaultAsync(x => x.StoreId == storeId && x.ProductKey == key, ct);

            if (existing is null)
            {
                existing = new ProductCost
                {
                    StoreId = storeId,
                    ProductKey = key,
                    ProductName = CleanDatabaseText(line.ProductName, 260),
                    Sku = sku,
                    LastUnitCost = line.UnitCost,
                    LastInvoiceDate = invoiceDate,
                    LastVendorName = CleanDatabaseText(vendorName, 200),
                    LastInvoiceNumber = CleanDatabaseText(invoiceNumber, 100),
                    UpdatedUtc = DateTime.UtcNow
                };
                db.ProductCosts.Add(existing);
                upserts++;
            }
            else
            {
                if (existing.LastUnitCost != line.UnitCost)
                {
                    var dir = line.UnitCost > existing.LastUnitCost ? PriceChangeDirection.Up : PriceChangeDirection.Down;
                    db.PriceAlerts.Add(new PriceAlert
                    {
                        StoreId = storeId,
                        ProductKey = key,
                        ProductName = existing.ProductName,
                        Sku = sku,
                        OldUnitCost = existing.LastUnitCost,
                        NewUnitCost = line.UnitCost,
                        Direction = dir,
                        OldVendorName = CleanDatabaseText(existing.LastVendorName, 200),
                        OldInvoiceNumber = CleanDatabaseText(existing.LastInvoiceNumber, 100),
                        VendorName = CleanDatabaseText(vendorName, 200),
                        InvoiceNumber = CleanDatabaseText(invoiceNumber, 100),
                        InvoiceDate = invoiceDate,
                        PurchaseInvoiceId = null,
                        IsRead = false,
                        CreatedUtc = DateTime.UtcNow
                    });
                    alerts++;
                }

                existing.ProductName = CleanDatabaseText(line.ProductName, 260);
                existing.Sku = sku;
                existing.LastUnitCost = line.UnitCost;
                existing.LastInvoiceDate = invoiceDate;
                existing.LastVendorName = CleanDatabaseText(vendorName, 200);
                existing.LastInvoiceNumber = CleanDatabaseText(invoiceNumber, 100);
                existing.UpdatedUtc = DateTime.UtcNow;
                upserts++;
            }
        }

        await db.SaveChangesAsync(ct);
        return (upserts, alerts);
    }

    public async Task<int> DeleteProductCostsAsync(int storeId, IEnumerable<int> productCostIds, CancellationToken ct = default)
    {
        var ids = (productCostIds ?? Array.Empty<int>()).Distinct().ToList();
        if (ids.Count == 0) return 0;

        using var db = _dbFactory.CreateDbContext();
        var rows = await db.ProductCosts
            .Where(x => x.StoreId == storeId && ids.Contains(x.Id))
            .ToListAsync(ct);

        if (rows.Count == 0) return 0;

        var keys = rows.Select(r => r.ProductKey).Where(k => !string.IsNullOrWhiteSpace(k)).Distinct().ToList();
        if (keys.Count > 0)
        {
            var alerts = await db.PriceAlerts
                .Where(a => a.StoreId == storeId && keys.Contains(a.ProductKey))
                .ToListAsync(ct);
            if (alerts.Count > 0)
                db.PriceAlerts.RemoveRange(alerts);
        }

        db.ProductCosts.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    public async Task<List<PriceAlert>> GetAlertsAsync(int storeId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.PriceAlerts.AsNoTracking()
            .Where(x => x.StoreId == storeId)
            .OrderByDescending(x => x.CreatedUtc)
            .Take(500)
            .ToListAsync(ct);
    }

    public async Task<int> GetUnreadAlertCountAsync(int storeId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.PriceAlerts.AsNoTracking()
            .Where(x => x.StoreId == storeId && !x.IsRead)
            .CountAsync(ct);
    }

    public async Task MarkAlertsReadAsync(int storeId, IEnumerable<int> alertIds, CancellationToken ct = default)
    {
        var ids = alertIds.Distinct().ToList();
        if (ids.Count == 0) return;

        using var db = _dbFactory.CreateDbContext();
        var alerts = await db.PriceAlerts.Where(x => x.StoreId == storeId && ids.Contains(x.Id)).ToListAsync(ct);
        foreach (var a in alerts)
        {
            a.IsRead = true;
            a.ReadUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkAllAlertsReadAsync(int storeId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var alerts = await db.PriceAlerts.Where(x => x.StoreId == storeId && !x.IsRead).ToListAsync(ct);
        foreach (var a in alerts)
        {
            a.IsRead = true;
            a.ReadUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAlertsAsync(int storeId, IEnumerable<int> alertIds, CancellationToken ct = default)
    {
        var ids = alertIds.Distinct().ToList();
        if (ids.Count == 0) return;

        using var db = _dbFactory.CreateDbContext();
        var alerts = await db.PriceAlerts.Where(x => x.StoreId == storeId && ids.Contains(x.Id)).ToListAsync(ct);
        db.PriceAlerts.RemoveRange(alerts);
        await db.SaveChangesAsync(ct);
    }

    private async Task ApplyCostTrackingAsync(PurchaseInvoice inv, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        foreach (var line in inv.Lines)
        {
            var key = NormalizeProductKey(line.ItemCode, line.ProductName);
            if (string.IsNullOrWhiteSpace(key)) continue;

            var sku = CleanDatabaseText(line.ItemCode, 80);
            var existing = await db.ProductCosts.FirstOrDefaultAsync(x => x.StoreId == inv.StoreId && x.ProductKey == key, ct);
            if (existing is null)
            {
                existing = new ProductCost
                {
                    StoreId = inv.StoreId,
                    ProductKey = key,
                    ProductName = CleanDatabaseText(line.ProductName, 260),
                    Sku = sku,
                    LastUnitCost = line.UnitCost,
                    LastInvoiceDate = inv.InvoiceDate,
                    LastVendorName = inv.VendorName,
                    LastInvoiceNumber = inv.InvoiceNumber,
                    UpdatedUtc = DateTime.UtcNow
                };
                db.ProductCosts.Add(existing);
            }
            else
            {
                if (existing.LastUnitCost != line.UnitCost)
                {
                    var dir = line.UnitCost > existing.LastUnitCost ? PriceChangeDirection.Up : PriceChangeDirection.Down;
                    db.PriceAlerts.Add(new PriceAlert
                    {
                        StoreId = inv.StoreId,
                        ProductKey = key,
                        ProductName = existing.ProductName,
                        Sku = sku,
                        OldUnitCost = existing.LastUnitCost,
                        NewUnitCost = line.UnitCost,
                        Direction = dir,
                        OldVendorName = CleanDatabaseText(existing.LastVendorName, 200),
                        OldInvoiceNumber = CleanDatabaseText(existing.LastInvoiceNumber, 100),
                        VendorName = inv.VendorName,
                        InvoiceNumber = inv.InvoiceNumber,
                        InvoiceDate = inv.InvoiceDate,
                        PurchaseInvoiceId = inv.Id,
                        IsRead = false,
                        CreatedUtc = DateTime.UtcNow
                    });
                }

                existing.ProductName = CleanDatabaseText(line.ProductName, 260);
                existing.Sku = sku;
                existing.LastUnitCost = line.UnitCost;
                existing.LastInvoiceDate = inv.InvoiceDate;
                existing.LastVendorName = inv.VendorName;
                existing.LastInvoiceNumber = inv.InvoiceNumber;
                existing.UpdatedUtc = DateTime.UtcNow;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task RecomputeCostsForStoreAsync(int storeId, HashSet<string> affectedKeys, CancellationToken ct)
    {
        if (affectedKeys.Count == 0) return;

        using var db = _dbFactory.CreateDbContext();
        var invoices = await db.PurchaseInvoices.AsNoTracking()
            .Include(x => x.Lines)
            .Where(x => x.StoreId == storeId)
            .OrderByDescending(x => x.InvoiceDate)
            .ThenByDescending(x => x.Id)
            .Take(2000)
            .ToListAsync(ct);

        foreach (var key in affectedKeys.Where(k => !string.IsNullOrWhiteSpace(k)))
        {
            PurchaseInvoice? bestInv = null;
            PurchaseInvoiceLine? bestLine = null;

            foreach (var inv in invoices)
            {
                foreach (var line in inv.Lines)
                {
                    if (NormalizeProductKey(line.ItemCode, line.ProductName) != key) continue;
                    bestInv = inv;
                    bestLine = line;
                    break;
                }
                if (bestInv is not null) break;
            }

            var cost = await db.ProductCosts.FirstOrDefaultAsync(x => x.StoreId == storeId && x.ProductKey == key, ct);

            if (bestInv is null || bestLine is null)
            {
                if (cost is not null)
                    db.ProductCosts.Remove(cost);
                continue;
            }

            if (cost is null)
            {
                cost = new ProductCost
                {
                    StoreId = storeId,
                    ProductKey = key,
                };
                db.ProductCosts.Add(cost);
            }

            cost.ProductName = CleanDatabaseText(bestLine.ProductName, 260);
            cost.Sku = CleanDatabaseText(bestLine.ItemCode, 80);
            cost.LastUnitCost = bestLine.UnitCost;
            cost.LastInvoiceDate = bestInv.InvoiceDate;
            cost.LastVendorName = bestInv.VendorName;
            cost.LastInvoiceNumber = bestInv.InvoiceNumber;
            cost.UpdatedUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }
}
