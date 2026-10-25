using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Services;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.UI.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ManagerPaperworkSystem.PosImport.Tests;

public sealed class InvoiceWorkflowTests : IAsyncLifetime, IAppPaths
{
    public string AppDataDirectory { get; } = Path.Combine(Path.GetTempPath(), "HK_Invoice_Test_" + Guid.NewGuid().ToString("N"));
    public string DatabasePath => Path.Combine(AppDataDirectory, "first.db");
    public string BackupsDirectory => Path.Combine(AppDataDirectory, "backups");
    private Factory _factory = null!;
    private sealed class Factory(string path) : IDbContextFactory<AppDbContext>
    {
        public string Path = path;
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=" + Path).Options);
    }
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(AppDataDirectory); _factory = new Factory(DatabasePath);
        using var db = _factory.CreateDbContext(); await db.Database.EnsureCreatedAsync();
        db.Stores.AddRange(new Store { Id = 1, Name = "First" }, new Store { Id = 2, Name = "Second" }); await db.SaveChangesAsync();
    }
    public Task DisposeAsync() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(AppDataDirectory, true); return Task.CompletedTask; }
    private Task<PurchaseInvoice> Add(PurchaseService service, string number = "A-001", int store = 1, decimal cost = 10, int day = 10) =>
        service.AddInvoiceAsync(store, new DateOnly(2026, 10, day), null, "Supplier", number, cost, "test", null,
            [new PurchaseInvoiceLine { ItemCode = "123456789012", ProductName = "Test 6 pack", Quantity = 1, UnitCost = cost }], 1, "Owner");
    [Fact] public async Task DuplicateAcrossSourcesIsStoreScopedAndConflictsAreExplicit()
    {
        var service = new PurchaseService(_factory, this); await Add(service);
        Assert.False((await Assert.ThrowsAsync<DuplicateInvoiceException>(() => Add(service, "a001"))).Conflicting);
        Assert.True((await Assert.ThrowsAsync<DuplicateInvoiceException>(() => Add(service, cost: 20))).Conflicting);
        await Add(service, store: 2);
        using var db = _factory.CreateDbContext(); Assert.Equal(2, await db.PurchaseInvoices.CountAsync()); Assert.Equal(2, await db.ProductCosts.CountAsync());
    }
    [Fact] public async Task CapturedJobDoesNotFollowStoreSwitch()
    {
        var service = new PurchaseService(_factory, this).Capture(); _factory.Path = Path.Combine(AppDataDirectory, "second.db");
        using (var second = _factory.CreateDbContext()) { await second.Database.EnsureCreatedAsync(); }
        await Add(service);
        using (var second = _factory.CreateDbContext()) Assert.Equal(0, await second.PurchaseInvoices.CountAsync());
        _factory.Path = DatabasePath; using var first = _factory.CreateDbContext(); Assert.Equal(1, await first.PurchaseInvoices.CountAsync());
    }
    [Fact] public async Task FailedPriceWriteRollsBackInvoiceAndCanRetry()
    {
        using (var db = _factory.CreateDbContext()) await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_price BEFORE INSERT ON ProductCosts BEGIN SELECT RAISE(ABORT, 'simulated interruption'); END;");
        var service = new PurchaseService(_factory, this); await Assert.ThrowsAsync<DbUpdateException>(() => Add(service));
        using (var db = _factory.CreateDbContext()) { Assert.Equal(0, await db.PurchaseInvoices.CountAsync()); await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_price;"); }
        await Add(service); using var verify = _factory.CreateDbContext(); Assert.Equal(1, await verify.PurchaseInvoices.CountAsync());
    }
    [Fact] public async Task OldBackfillDoesNotReverseLatestPriceAndRepeatedProductsShareOneCost()
    {
        var service = new PurchaseService(_factory, this); await Add(service, cost: 12); await Add(service, "OLD", cost: 5, day: 1);
        using var db = _factory.CreateDbContext(); Assert.Equal(12, (await db.ProductCosts.SingleAsync()).LastUnitCost);
        var alert = await db.PriceAlerts.SingleAsync(); Assert.Equal(5, alert.OldUnitCost); Assert.Equal(12, alert.NewUnitCost);
        Assert.Equal(new DateOnly(2026, 10, 10), alert.InvoiceDate);
    }
    [Fact] public async Task ExistingPackPriceIsReusedWithoutCreatingADuplicateProduct()
    {
        using (var db = _factory.CreateDbContext())
        {
            db.ProductCosts.Add(new ProductCost { StoreId = 1, Sku = "123456789012", ProductKey = "123456789012|TEST 6 PACK", ProductName = "Test 6 pack",
                LastUnitCost = 8, LastInvoiceDate = new(2026, 10, 1) }); await db.SaveChangesAsync();
        }
        await Add(new PurchaseService(_factory, this));
        using var verify = _factory.CreateDbContext(); Assert.Equal(10, (await verify.ProductCosts.SingleAsync()).LastUnitCost);
        var alert = await verify.PriceAlerts.SingleAsync(); Assert.Equal(8, alert.OldUnitCost); Assert.Equal(10, alert.NewUnitCost);
    }
    [Fact] public async Task FailedManualEditRollsBackInvoiceAndPriceTogether()
    {
        var service = new PurchaseService(_factory, this); var invoice = await Add(service);
        using (var db = _factory.CreateDbContext()) await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_cost_update BEFORE UPDATE ON ProductCosts BEGIN SELECT RAISE(ABORT, 'simulated failure'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => service.UpdateInvoiceAsync(invoice.Id, invoice.InvoiceDate, null, "Supplier", "A-001", 20, "", null,
            [new PurchaseInvoiceLine { ItemCode = "123456789012", ProductName = "Test 6 pack", Quantity = 1, UnitCost = 20 }]));
        using var verify = _factory.CreateDbContext(); Assert.Equal(10, (await verify.PurchaseInvoices.SingleAsync()).Total); Assert.Equal(10, (await verify.ProductCosts.SingleAsync()).LastUnitCost);
    }
    [Fact] public async Task FailedManualDeletePreservesInvoiceAndProduct()
    {
        var service = new PurchaseService(_factory, this); var invoice = await Add(service);
        using (var db = _factory.CreateDbContext()) await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_cost_delete BEFORE DELETE ON ProductCosts BEGIN SELECT RAISE(ABORT, 'simulated failure'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => service.DeleteInvoiceAsync(invoice.Id));
        using var verify = _factory.CreateDbContext(); Assert.Equal(1, await verify.PurchaseInvoices.CountAsync()); Assert.Equal(1, await verify.ProductCosts.CountAsync());
    }
    [Fact] public async Task HistoricalChangesAreChronologicalIdempotentAndStoreScoped()
    {
        var service = new PurchaseService(_factory, this);
        await Add(service, "NEW", cost: 12, day: 20);
        await Add(service, "OLD", cost: 8, day: 1);
        await Add(service, "MIDDLE", cost: 10, day: 10);
        await Add(service, "OTHER", store: 2, cost: 99, day: 5);
        using (var db = _factory.CreateDbContext())
        {
            var rows = await db.PriceAlerts.OrderBy(x => x.InvoiceDate).ToListAsync();
            Assert.Equal(2, rows.Count); Assert.All(rows, x => Assert.Equal(1, x.StoreId));
            Assert.Equal((8m, 10m), (rows[0].OldUnitCost, rows[0].NewUnitCost));
            Assert.Equal((10m, 12m), (rows[1].OldUnitCost, rows[1].NewUnitCost));
            rows[0].IsRead = true; await db.SaveChangesAsync();
        }
        await service.RepairInvoicePriceHistoryAsync(1); await service.RepairInvoicePriceHistoryAsync(1);
        using var verify = _factory.CreateDbContext(); Assert.Equal(2, await verify.PriceAlerts.CountAsync());
        Assert.True((await verify.PriceAlerts.OrderBy(x => x.InvoiceDate).FirstAsync()).IsRead);
        Assert.Equal(12, (await verify.ProductCosts.SingleAsync(x => x.StoreId == 1)).LastUnitCost);
    }
    [Fact] public async Task RepairRestoresMissingHistoryAndPeriodSnapshotDoesNotAlterLatestCost()
    {
        var service = new PurchaseService(_factory, this);
        await Add(service, "OCT", cost: 12);
        await service.AddInvoiceAsync(1, new(2026, 9, 30), null, "Supplier", "SEP", 8, "", null,
            [new PurchaseInvoiceLine { ItemCode = "123456789012", ProductName = "Test 6 pack", Quantity = 1, UnitCost = 8 }], 1, "Owner");
        using (var db = _factory.CreateDbContext()) { db.PriceAlerts.RemoveRange(db.PriceAlerts); await db.SaveChangesAsync(); }
        await service.RepairInvoicePriceHistoryAsync(1);
        var september = await service.GetProductCostsForPeriodAsync(1, new(2026, 9, 1), new(2026, 9, 30));
        Assert.Equal(8, Assert.Single(september).LastUnitCost);
        Assert.Empty(await service.GetProductCostsForPeriodAsync(2, new(2026, 9, 1), new(2026, 9, 30)));
        using var verify = _factory.CreateDbContext(); Assert.Equal(12, (await verify.ProductCosts.SingleAsync()).LastUnitCost);
        Assert.Equal(new DateOnly(2026, 10, 10), (await verify.PriceAlerts.SingleAsync()).InvoiceDate);
    }
    [Fact] public async Task UnchangedHistoricalPriceDoesNotProduceAnAlert()
    {
        var service = new PurchaseService(_factory, this); await Add(service, "LATEST", cost: 10);
        await Add(service, "EARLY", cost: 10, day: 1); await service.RepairInvoicePriceHistoryAsync(1);
        using var db = _factory.CreateDbContext(); Assert.Empty(await db.PriceAlerts.ToListAsync());
    }
    [Fact] public async Task CompletedRepairAndUnrelatedImportsDoNotRecreateDeletedHistoricalAlerts()
    {
        var service = new PurchaseService(_factory, this);
        await Add(service, "FIRST", cost: 5, day: 1); await Add(service, "SECOND", cost: 8, day: 2);
        await service.RepairInvoicePriceHistoryAsync(1);
        using (var db = _factory.CreateDbContext()) { db.PriceAlerts.RemoveRange(db.PriceAlerts); await db.SaveChangesAsync(); }
        await service.RepairInvoicePriceHistoryAsync(1);
        await Add(service, "THIRD", cost: 12, day: 3);
        using var verify = _factory.CreateDbContext();
        var alert = await verify.PriceAlerts.SingleAsync(); Assert.Equal("THIRD", alert.InvoiceNumber);
        Assert.Equal(8, alert.OldUnitCost); Assert.Equal(12, alert.NewUnitCost);
    }
    [Fact] public void PackAndSingleUnitCannotSharePriceKey()
    {
        Assert.NotEqual(InvoiceIdentity.ProductKey("123456789012", "Test 6 pack"), InvoiceIdentity.ProductKey("123456789012", "Test single"));
        Assert.NotEqual(InvoiceIdentity.ProductKey("123456789012", "Test 6 pack"), InvoiceIdentity.ProductKey("123456789012", "Test 12 pack"));
        Assert.Equal(InvoiceIdentity.ProductKey("123456789012", "Test 6 pack"), InvoiceIdentity.ProductKey("123456789012", "Renamed Test 6 pack"));
        Assert.NotEqual(InvoiceIdentity.ProductKey("SKU7", "A 6 pack"), InvoiceIdentity.ProductKey("SKU7", "B 6 pack"));
    }
    [Fact] public void MissingTotalsAndUnreconciledLinesRequireReview()
    {
        ImportedInvoice Invoice(decimal? total) => new() { VendorName = "Supplier", InvoiceNumber = "A", InvoiceDate = new DateOnly(2026, 10, 1), Total = total,
            Lines = [new PurchaseInvoiceLine { ProductName = "Test", Quantity = 2, UnitCost = 5 }] };
        Assert.Null(InvoiceValidation.ReviewReason(Invoice(10))); Assert.NotNull(InvoiceValidation.ReviewReason(Invoice(null)));
        Assert.NotNull(InvoiceValidation.ReviewReason(Invoice(12)));
    }
}
