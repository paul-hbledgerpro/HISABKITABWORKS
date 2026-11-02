using System.Text.Json;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.Data.Services;
using ManagerPaperworkSystem.WinForms;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ManagerPaperworkSystem.PosImport.Tests;

public sealed class ShiftShortageTests
{
    private static ShiftShortageSettings Settings(int store = 1, bool enabled = true) => new() {
        StoreId = store, StoreGuid = "store-" + store, StoreName = "Fixture " + store, Enabled = enabled,
        Threshold = 10, RecipientsJson = "[\"owner@example.com\",\"other@example.com\"]",
        UpdatedUtc = DateTime.UtcNow, UpdatedBy = "Owner"
    };
    private static ShiftLogEntry Shift(int store = 1, decimal drop = 89.99m) => new() {
        Id = store, StoreId = store, Date = new(2026, 11, 2), ShiftNo = "3004",
        CashTotal = 100, CashDropReceived = drop, Employee = "Employee"
    };
    private static async Task WithDb(Func<AppDbContext, Task> action)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(); await ShiftShortageSchema.EnsureAsync(db);
        await action(db);
    }
    [Theory]
    [InlineData(90, true, 0)] [InlineData(89.99, true, 1)] [InlineData(110, true, 0)] [InlineData(0, false, 0)]
    public Task Only_enabled_strict_shortages_queue(decimal drop, bool enabled, int count) => WithDb(async db => {
        db.ShiftShortageSettings.Add(Settings(enabled: enabled)); await db.SaveChangesAsync();
        await ShiftShortageService.QueueAsync(db, Shift(drop: drop), "Manager"); await db.SaveChangesAsync();
        Assert.Equal(count, await db.ShiftShortageEmails.CountAsync());
    });
    [Fact]
    public Task Same_batch_in_two_stores_stays_separate_and_repeat_is_not_emailed_twice() => WithDb(async db => {
        db.ShiftShortageSettings.AddRange(Settings(1), Settings(2)); await db.SaveChangesAsync();
        await ShiftShortageService.QueueAsync(db, Shift(1), "Manager"); await db.SaveChangesAsync();
        await ShiftShortageService.QueueAsync(db, Shift(1), "Manager"); await db.SaveChangesAsync();
        await ShiftShortageService.QueueAsync(db, Shift(2), "Manager"); await db.SaveChangesAsync();
        var rows = await db.ShiftShortageEmails.OrderBy(x => x.StoreId).ToListAsync();
        Assert.Equal(2, rows.Count); Assert.Equal("store-1", rows[0].StoreGuid); Assert.Equal("store-2", rows[1].StoreGuid);
        Assert.Equal(10.01m, JsonSerializer.Deserialize<ShiftShortageReport>(rows[0].ReportJson)!.Shortage);
    });
    [Fact]
    public Task Alert_rolls_back_with_the_drop() => WithDb(async db => {
        db.ShiftShortageSettings.Add(Settings()); await db.SaveChangesAsync();
        await using (var tx = await db.Database.BeginTransactionAsync()) {
            await ShiftShortageService.QueueAsync(db, Shift(), "Manager"); await db.SaveChangesAsync(); await tx.RollbackAsync();
        }
        db.ChangeTracker.Clear(); Assert.Empty(await db.ShiftShortageEmails.ToListAsync());
    });
    [Fact]
    public Task Owner_permission_required_and_settings_cancel_only_this_stores_unsent_alerts() => WithDb(async db => {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ShiftShortageService.SaveSettingsAsync(db, Settings(), false));
        await ShiftShortageService.SaveSettingsAsync(db, Settings(1), true);
        await ShiftShortageService.SaveSettingsAsync(db, Settings(2), true);
        await ShiftShortageService.QueueAsync(db, Shift(1), "Manager"); await ShiftShortageService.QueueAsync(db, Shift(2), "Manager"); await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await ShiftShortageService.SaveSettingsAsync(db, Settings(1, false), true);
        Assert.Equal("Cancelled", (await db.ShiftShortageEmails.AsNoTracking().SingleAsync(x => x.StoreId == 1)).Status);
        Assert.Equal("Queued", (await db.ShiftShortageEmails.AsNoTracking().SingleAsync(x => x.StoreId == 2)).Status);
    });
    [Fact]
    public void Recipients_are_validated_deduplicated_and_bounded()
    {
        Assert.Equal(new[] { "owner@example.com", "second@example.com" }, ShiftShortageService.ParseRecipients("owner@example.com; OWNER@example.com\nsecond@example.com"));
        Assert.Throws<InvalidOperationException>(() => ShiftShortageService.ParseRecipients("bad-email"));
        Assert.Throws<InvalidOperationException>(() => ShiftShortageService.ParseRecipients("Owner <owner@example.com>"));
        Assert.Throws<InvalidOperationException>(() => ShiftShortageService.ParseRecipients(string.Join(";", Enumerable.Range(0, 11).Select(i => i + "@example.com"))));
    }
    [SqlServerFact]
    public async Task Pending_drop_does_not_alert_until_matched_and_schema_upgrade_is_repeatable()
    {
        var name = "HK_Shortage_Test_" + Guid.NewGuid().ToString("N");
        var cs = $"Server={Environment.GetEnvironmentVariable("HK_TEST_SQL_SERVER")};Database={name};Integrated Security=True;TrustServerCertificate=True;Pooling=False";
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options);
        const string portal = "GALAXY SMOKE SHOP (HANOVER PARK, IL)";
        try {
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE ShiftShortageSettings; DROP TABLE ShiftShortageEmails;");
            await ShiftShortageSchema.EnsureAsync(db); await ShiftShortageSchema.EnsureAsync(db);
            db.Stores.Add(new Store { Name = "Fixture" }); await db.SaveChangesAsync();
            await ShiftShortageService.SaveSettingsAsync(db, Settings(), true);
            var request = await ShiftDropWorkflow.RecordAsync(db, 1, Guid.NewGuid(), portal, "3451", 80, 5, "Supplies", 1, "Manager", false);
            Assert.Equal("Pending", request.Status); Assert.Empty(await db.ShiftShortageEmails.ToListAsync());
            var shift = new ShiftLogEntry { StoreId = 1, ShiftNo = "3451", Date = new(2026,11,2), CashTotal = 100,
                PosReportKey = "fixture3451", PosReportPath = "fixture.pdf" };
            shift.PosReportStoreIdentity = PortalZReportHistory.Identity(portal, shift.Date, shift.ShiftNo, shift.PosReportPath);
            db.ShiftLogs.Add(shift); await db.SaveChangesAsync();
            await ShiftDropWorkflow.ApplyAsync(db, request.Id, portal);
            await ShiftDropWorkflow.ApplyAsync(db, request.Id, portal);
            var alert = Assert.Single(await db.ShiftShortageEmails.AsNoTracking().ToListAsync());
            Assert.Equal(15, JsonSerializer.Deserialize<ShiftShortageReport>(alert.ReportJson)!.Shortage);
            Assert.Equal(80, (await db.CashOnHand.SingleAsync()).CashAdded);
        } finally { await db.Database.EnsureDeletedAsync(); }
    }
}
