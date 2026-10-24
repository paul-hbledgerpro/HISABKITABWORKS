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

    public PurchaseService Capture()
    {
        using var db = _dbFactory.CreateDbContext();
        var options = new DbContextOptionsBuilder<AppDbContext>();
        if (db.Database.IsSqlServer()) options.UseSqlServer(db.Database.GetConnectionString());
        else options.UseSqlite(db.Database.GetConnectionString());
        return new PurchaseService(new FixedFactory(options.Options), _paths);
    }
    private sealed class FixedFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private static string CleanDatabaseText(string? value, int maximumLength)
    {
        var clean = Regex.Replace((value ?? "").Trim(), @"\s+", " ");
        return clean.Length <= maximumLength ? clean : clean[..maximumLength].TrimEnd();
    }

    private static string NormalizeProductKey(string? itemCode, string? name) => InvoiceIdentity.ProductKey(itemCode, name);

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
        var safeName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}_{fileName}";
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
        // Capture the database before the first await; invoice + costs commit together.
        using var db = _dbFactory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        if (vendorId.HasValue && !await db.Vendors.AnyAsync(x => x.Id == vendorId.Value && x.StoreId == storeId, ct))
            throw new InvalidOperationException("The selected vendor belongs to another store or is unavailable.");
        var key = InvoiceIdentity.Key(vendorName, invoiceNumber);
        if (!string.IsNullOrWhiteSpace(invoiceNumber))
        {
            var identities = await db.PurchaseInvoices.Where(x => x.StoreId == storeId)
                .Select(x => new { x.VendorName, x.InvoiceNumber, x.InvoiceDate, x.Total }).ToListAsync(ct);
            var duplicate = identities.FirstOrDefault(x => InvoiceIdentity.Key(x.VendorName, x.InvoiceNumber) == key);
            if (duplicate is not null) throw new DuplicateInvoiceException(duplicate.InvoiceDate != invoiceDate || duplicate.Total != total);
        }
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

        db.PurchaseInvoices.Add(inv);
        await db.SaveChangesAsync(ct);

        await ApplyCostTrackingAsync(db, inv, ct);
        await transaction.CommitAsync(ct);
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
            var existing = await FindProductCostAsync(db, storeId, key, sku, ct);

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
            else if (invoiceDate >= existing.LastInvoiceDate)
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

    private static async Task<ProductCost?> FindProductCostAsync(AppDbContext db, int storeId, string key, string sku, CancellationToken ct)
    {
        var existing = db.ProductCosts.Local.FirstOrDefault(x => x.StoreId == storeId && x.ProductKey == key)
            ?? await db.ProductCosts.FirstOrDefaultAsync(x => x.StoreId == storeId && x.ProductKey == key, ct);
        if (existing is null && key.StartsWith("PACK:", StringComparison.Ordinal))
        {
            var priorPackaging = await db.ProductCosts.Where(x => x.StoreId == storeId && x.Sku == sku).ToListAsync(ct);
            existing = priorPackaging.Where(x => NormalizeProductKey(x.Sku, x.ProductName) == key)
                .OrderByDescending(x => x.LastInvoiceDate).ThenByDescending(x => x.Id).FirstOrDefault();
            if (existing is not null) existing.ProductKey = key;
        }
        return existing;
    }

    private static async Task ApplyCostTrackingAsync(AppDbContext db, PurchaseInvoice inv, CancellationToken ct)
    {
        foreach (var line in inv.Lines)
        {
            var key = NormalizeProductKey(line.ItemCode, line.ProductName);
            if (string.IsNullOrWhiteSpace(key)) continue;

            var sku = CleanDatabaseText(line.ItemCode, 80);
            var existing = await FindProductCostAsync(db, inv.StoreId, key, sku, ct);
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
            else if (inv.InvoiceDate >= existing.LastInvoiceDate)
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
