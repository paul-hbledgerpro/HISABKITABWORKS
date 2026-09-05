using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.Data.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ManagerPaperworkSystem.Data.Tests;

public sealed class CashEntryServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        await using var db = CreateDb();
        await db.Database.EnsureCreatedAsync();
        db.Stores.AddRange(new Store { Id = 1, Name = "First" }, new Store { Id = 2, Name = "Second" });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _connection.DisposeAsync().AsTask();

    private static CashOnHandEntry Payout(int storeId = 1) => new()
    {
        StoreId = storeId, Date = new DateOnly(2026, 9, 5), IsPayout = true,
        PayoutAmount = 12.50m, CreatedByUserId = 7, CreatedByName = "Test manager"
    };

    [Fact]
    public async Task NewVendorAndDefaultPurposeBelongOnlyToSelectedStore()
    {
        await using var db = CreateDb();
        db.Vendors.Add(new Vendor { StoreId = 1, Name = "Supplier" });
        db.Purposes.Add(new Purpose { StoreId = 1, Name = "Payout" });
        await db.SaveChangesAsync();
        var entry = Payout(2);
        await CashEntryService.SaveAsync(db, entry, "  Supplier  ");
        Assert.Equal(2, entry.Vendor!.StoreId);
        Assert.Equal("Supplier", entry.Vendor.Name);
        Assert.Equal(2, entry.Purpose!.StoreId);
        Assert.Equal("Payout", entry.Purpose.Name);
        Assert.Equal(2, await db.Vendors.CountAsync());
        Assert.Equal(2, await db.Purposes.CountAsync());
        var log = await db.ActivityLogs.SingleAsync(x => x.EntityType == nameof(CashOnHandEntry));
        Assert.Equal(entry.Id, log.EntityId);
        Assert.Equal(7, log.UserId);
        Assert.Equal("Test manager", log.UserName);
    }

    [Fact]
    public async Task ReusesCaseAndWhitespaceMatchesInCurrentStore()
    {
        await using var db = CreateDb();
        var vendor = new Vendor { StoreId = 1, Name = "  ACME  " };
        var purpose = new Purpose { StoreId = 1, Name = " payout " };
        db.AddRange(vendor, purpose);
        await db.SaveChangesAsync();
        var entry = Payout();
        await CashEntryService.SaveAsync(db, entry, "acme");
        Assert.Equal(vendor.Id, entry.VendorId);
        Assert.Equal(purpose.Id, entry.PurposeId);
        Assert.Equal(1, await db.Vendors.CountAsync());
        Assert.Equal(1, await db.Purposes.CountAsync());
    }

    [Fact]
    public async Task FinalTypedVendorOverridesStaleSelection()
    {
        await using var db = CreateDb();
        var oldVendor = new Vendor { StoreId = 1, Name = "Old supplier" };
        db.Add(oldVendor);
        await db.SaveChangesAsync();
        var entry = Payout();
        entry.VendorId = oldVendor.Id;
        await CashEntryService.SaveAsync(db, entry, "New supplier");
        Assert.NotEqual(oldVendor.Id, entry.VendorId);
        Assert.Equal("New supplier", entry.Vendor!.Name);
    }

    [Fact]
    public async Task OptionalVendorAndExplicitPurposeArePreserved()
    {
        await using var db = CreateDb();
        var purpose = new Purpose { StoreId = 1, Name = "Repairs" };
        db.Add(purpose);
        await db.SaveChangesAsync();
        var entry = Payout();
        entry.PurposeId = purpose.Id;
        await CashEntryService.SaveAsync(db, entry, "  ");
        Assert.Null(entry.VendorId);
        Assert.Equal(purpose.Id, entry.PurposeId);
        Assert.False(await db.Purposes.AnyAsync(x => x.Name == "Payout"));
    }

    [Fact]
    public async Task RejectsPurposeFromAnotherStoreWithoutSavingAnything()
    {
        await using var db = CreateDb();
        var purpose = new Purpose { StoreId = 2, Name = "Repairs" };
        db.Add(purpose);
        await db.SaveChangesAsync();
        var entry = Payout();
        entry.PurposeId = purpose.Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => CashEntryService.SaveAsync(db, entry, "New supplier"));
        Assert.False(await db.CashOnHand.AnyAsync());
        Assert.False(await db.Vendors.AnyAsync());
    }

    [Fact]
    public async Task AuditFailureRollsBackCashAndLookupsAndAllowsFreshRetry()
    {
        await using (var db = CreateDb())
        {
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectCashAudit BEFORE INSERT ON ActivityLogs WHEN NEW.EntityType = 'CashOnHandEntry' BEGIN SELECT RAISE(ABORT, 'test audit failure'); END;");
            await Assert.ThrowsAsync<DbUpdateException>(() => CashEntryService.SaveAsync(db, Payout(), "New supplier"));
        }
        await using var verify = CreateDb();
        Assert.False(await verify.CashOnHand.AnyAsync());
        Assert.False(await verify.Vendors.AnyAsync());
        Assert.False(await verify.Purposes.AnyAsync());
        Assert.False(await verify.ActivityLogs.AnyAsync(x => x.EntityType != nameof(Store)));
        await verify.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectCashAudit;");
        await CashEntryService.SaveAsync(verify, Payout(), "New supplier");
        Assert.Equal(1, await verify.CashOnHand.CountAsync());
        Assert.Equal(1, await verify.Vendors.CountAsync());
    }

    [Fact]
    public async Task AddCashCreatesNoPayoutLookups()
    {
        await using var db = CreateDb();
        var entry = new CashOnHandEntry { StoreId = 1, Date = new DateOnly(2026, 9, 5), CashAdded = 25m };
        await CashEntryService.SaveAsync(db, entry, "");
        Assert.Equal(25m, entry.CashAdded);
        Assert.False(entry.IsPayout);
        Assert.False(await db.Vendors.AnyAsync());
        Assert.False(await db.Purposes.AnyAsync());
    }

    [Fact]
    public async Task RejectsOverlengthVendorWithoutWrites()
    {
        await using var db = CreateDb();
        await Assert.ThrowsAsync<ArgumentException>(() => CashEntryService.SaveAsync(db, Payout(), new string('x', 201)));
        Assert.False(await db.CashOnHand.AnyAsync());
        Assert.False(await db.Vendors.AnyAsync());
    }
}
