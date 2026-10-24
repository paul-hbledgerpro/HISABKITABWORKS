using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.Data.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ManagerPaperworkSystem.Data.Tests;

public sealed class AccountAdministrationTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
    public async Task InitializeAsync()
    {
        await _connection.OpenAsync(); using var db = Db(); await db.Database.EnsureCreatedAsync();
        db.Stores.AddRange(new Store { Id = 1, Name = "First" }, new Store { Id = 2, Name = "Second" });
        db.Users.AddRange(new UserAccount { Id = 1, Username = "owner", FirstName = "Owner", Role = UserRole.OwnerAdmin, IsActive = true },
            new UserAccount { Id = 2, Username = "manager", FirstName = "Manager", Role = UserRole.Manager, IsActive = true });
        await db.SaveChangesAsync();
    }
    public Task DisposeAsync() => _connection.DisposeAsync().AsTask();
    [Fact] public async Task NonAdminCannotBypassButtons()
    { using var db = Db(); await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AccountAdministration.ChangeAsync(db, 2, 1, "Reset Password", password: "attacker123")); }
    [Fact] public async Task ExistingAccountsKeepAccessAfterRepeatableSchemaUpgrade()
    {
        using var db = Db(); await db.Database.ExecuteSqlRawAsync("ALTER TABLE UserAccounts DROP COLUMN AllowedStoreIds;");
        await LedgerWorkflowSchema.EnsureAsync(db); await LedgerWorkflowSchema.EnsureAsync(db);
        db.ChangeTracker.Clear(); var user = await db.Users.FirstAsync(); Assert.True(user.AllowsStore(1)); Assert.True(user.AllowsStore(2));
    }
    [Theory] [InlineData("Delete")] [InlineData("Deactivate")] [InlineData("Edit")]
    public async Task LastAdministratorIsProtected(string action)
    { using var db = Db(); await Assert.ThrowsAsync<InvalidOperationException>(() => AccountAdministration.ChangeAsync(db, 1, 1, action, first: "Owner")); }
    [Fact] public async Task StorePermissionsAndAuditAreSavedTogether()
    {
        using var db = Db(); await AccountAdministration.ChangeAsync(db, 1, 2, "Edit", "New", "Name", "test@example.com", UserRole.Manager, [2]);
        db.ChangeTracker.Clear(); var user = await db.Users.SingleAsync(x => x.Id == 2);
        Assert.False(user.AllowsStore(1)); Assert.True(user.AllowsStore(2)); Assert.Equal("New", user.FirstName);
        Assert.True(await db.ActivityLogs.AnyAsync(x => x.UserId == 1 && x.Action == "Edit" && x.EntityId == 2));
    }
    [Fact] public async Task InvalidStoresRollBackEdit()
    {
        using (var db = Db()) await Assert.ThrowsAsync<InvalidOperationException>(() => AccountAdministration.ChangeAsync(db, 1, 2, "Edit", "Bad", allowedStores: [99]));
        using var read = Db(); Assert.Equal("Manager", (await read.Users.SingleAsync(x => x.Id == 2)).FirstName);
    }
    [Fact] public async Task PreviouslyUsedAccountMustBeDeactivated()
    {
        using var db = Db(); (await db.Users.SingleAsync(x => x.Id == 2)).LastLoginUtc = DateTime.UtcNow; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => AccountAdministration.ChangeAsync(db, 1, 2, "Delete"));
        await AccountAdministration.ChangeAsync(db, 1, 2, "Deactivate"); Assert.False((await db.Users.SingleAsync(x => x.Id == 2)).IsActive);
        await AccountAdministration.ChangeAsync(db, 1, 2, "Reactivate"); Assert.True((await db.Users.SingleAsync(x => x.Id == 2)).IsActive);
    }
    [Fact] public async Task UnusedAccountCanBeDeletedButTransactionIdentityCannot()
    {
        using var db = Db(); await AccountAdministration.ChangeAsync(db, 1, 2, "Delete"); Assert.False(await db.Users.AnyAsync(x => x.Id == 2));
        db.Users.Add(new UserAccount { Id = 3, Username = "used", FirstName = "Used", IsActive = true });
        db.ShiftLogs.Add(new ShiftLogEntry { StoreId = 1, ShiftNo = "7", Date = new DateOnly(2026, 10, 1), CreatedByUserId = 3 }); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => AccountAdministration.ChangeAsync(db, 1, 3, "Delete"));
    }
}
