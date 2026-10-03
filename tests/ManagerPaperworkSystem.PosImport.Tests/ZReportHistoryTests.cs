using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.UI.Services;
using ManagerPaperworkSystem.WinForms;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ManagerPaperworkSystem.PosImport.Tests;

public sealed class ZReportHistoryTests : IAsyncLifetime
{
    private const string Galaxy = "GALAXY SMOKE SHOP (ELGIN, IL - 60123)";
    private const string Other = "ELGIN SMOKE SHOP (ELGIN, IL - 60120)";
    private static readonly DateOnly September = new(2026, 9, 30);
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        await using var db = Db();
        await db.Database.EnsureCreatedAsync();
        db.Stores.AddRange(new Store { Id = 1, Name = "Galaxy" }, new Store { Id = 2, Name = "Other" });
        await db.SaveChangesAsync();
    }
    public Task DisposeAsync() => connection.DisposeAsync().AsTask();
    private static ShiftLogEntry Row(string batch, DateOnly date, string file, int storeId = 1) => new()
    {
        StoreId = storeId, Date = date, ShiftNo = batch,
        PosReportKey = $"ADVENTPOS-Z|{date:yyyy-MM-dd}|{batch}", PosReportPath = file,
        CashDropReceived = 12m, RegisterPayout = 3m, PayoutReason = "Manager entry"
    };
    private static PosReportData Report(string name, string batch, DateOnly date) => new(date, "admin", batch, 10m, 20m, 30m, 1m, "Z Report")
    { SourceReportText = $"{name}\n20 S STATE ST\nELGIN, IL\nZ-Report\nRegister Number: 2\nBatch: {batch}\nStart Date: {date:MM/dd/yyyy}" };

    [Fact]
    public async Task Contaminated1551And1820CannotHideGalaxyNextBatches()
    {
        await using var db = Db();
        var good = Row("1549", September, "galaxy.pdf");
        var bad = Row("1551", new(2025, 6, 28), "wrong.pdf");
        db.ShiftLogs.AddRange(good, bad, Row("1820", new(2025, 11, 8), "wrong-high.pdf"), Row("9999", September, "other-db.pdf", 2));
        await db.SaveChangesAsync();
        var history = await PortalZReportHistory.LoadAsync(db, 1, Galaxy, default, path => path switch
        {
            "galaxy.pdf" => [Report("GALAXY SMOKE SHOP", "1549", September)],
            "wrong.pdf" => [Report("ELGIN SMOKE SHOP", "1551", new(2025, 6, 28))],
            "wrong-high.pdf" => [Report("ELGIN SMOKE SHOP", "1820", new(2025, 11, 8))],
            _ => throw new Exception("Another database/store was read")
        });
        Assert.Equal(new long[] { 1549 }, history.Keys);
        Assert.Equal(new long[] { 1550, 1551, 1552, 1553 }, PortalSyncRecoveryPolicy.PendingZBatches(
            [1, 1549, 1550, 1551, 1552, 1553], history.Keys.ToHashSet(), true, history.Keys.Max()));
        await using var verify = Db();
        var saved = await verify.ShiftLogs.OrderBy(row => row.Id).ToListAsync();
        Assert.Equal(PortalZReportHistory.Identity(Galaxy, September, "1549", "galaxy.pdf"), saved[0].PosReportStoreIdentity);
        Assert.Equal("", saved[1].PosReportStoreIdentity);
        Assert.All(saved, row => { Assert.Equal(12m, row.CashDropReceived); Assert.Equal(3m, row.RegisterPayout); Assert.Equal("Manager entry", row.PayoutReason); });
        Assert.Equal(4, saved.Count); // No cleanup/deletion or changes to financial data.
    }

    [Fact]
    public async Task VerifiedProvenanceSurvivesAnotherWindowsAccountWithoutOriginalPdf()
    {
        await using var db = Db();
        db.ShiftLogs.Add(Row("1549", September, "original-profile.pdf")); await db.SaveChangesAsync();
        await PortalZReportHistory.LoadAsync(db, 1, Galaxy, default, _ => [Report("GALAXY SMOKE SHOP", "1549", September)]);
        await using var second = Db();
        var history = await PortalZReportHistory.LoadAsync(second, 1, Galaxy, default, _ => throw new UnauthorizedAccessException());
        Assert.Contains(1549L, history.Keys);
    }

    [Theory]
    [InlineData("ELGIN SMOKE SHOP", "1549", 30)]
    [InlineData("GALAXY SMOKE SHOP", "1551", 30)]
    [InlineData("GALAXY SMOKE SHOP", "1549", 29)]
    public async Task WrongStoreDateOrBatchIsNotHistory(string name, string batch, int day)
    {
        await using var db = Db();
        db.ShiftLogs.Add(Row("1549", September, "source.pdf")); await db.SaveChangesAsync();
        Assert.Empty(await PortalZReportHistory.LoadAsync(db, 1, Galaxy, default, _ => [Report(name, batch, new(2026, 9, day))]));
    }

    [Fact]
    public async Task AnotherStoresSavedIdentityCannotBeRelabeled()
    {
        await using var db = Db(); var row = Row("1551", September, "source.pdf");
        row.PosReportStoreIdentity = PortalZReportHistory.Identity(Other, September, "1551", "source.pdf");
        db.ShiftLogs.Add(row); await db.SaveChangesAsync();
        Assert.Empty(await PortalZReportHistory.LoadAsync(db, 1, Galaxy, default, _ => throw new Exception("Must not relabel identity")));
    }

    [Fact]
    public async Task MissingFilesAndUnprovenManualNumbersCannotAdvanceCursor()
    {
        await using var db = Db(); var row = Row("9999", September, ""); row.PosReportKey = "";
        db.ShiftLogs.AddRange(row, Row("1820", September, "missing.pdf")); await db.SaveChangesAsync();
        Assert.Empty(await PortalZReportHistory.LoadAsync(db, 1, Galaxy, default, _ => throw new FileNotFoundException()));
    }

    [Fact]
    public async Task ConcurrentEditCannotReceiveStaleVerificationStamp()
    {
        await using var db = Db(); var row = Row("1549", September, "source.pdf");
        db.ShiftLogs.Add(row); await db.SaveChangesAsync();
        var history = await PortalZReportHistory.LoadAsync(db, 1, Galaxy, default, _ =>
        {
            using var other = Db();
            other.ShiftLogs.Where(item => item.Id == row.Id).ExecuteUpdate(setters => setters.SetProperty(item => item.ShiftNo, "1550"));
            return [Report("GALAXY SMOKE SHOP", "1549", September)];
        });
        Assert.Empty(history);
        await using var verify = Db(); Assert.Equal("", (await verify.ShiftLogs.SingleAsync()).PosReportStoreIdentity);
    }
    [Fact]
    public async Task OlderAppReplacingSourceCannotRetainVerifiedHistory()
    {
        await using var db = Db();
        var row = Row("1551", September, "galaxy.pdf");
        row.PosReportStoreIdentity = PortalZReportHistory.Identity(Galaxy, September, "1551", row.PosReportPath);
        db.ShiftLogs.Add(row); await db.SaveChangesAsync();
        await db.ShiftLogs.Where(item => item.Id == row.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.PosReportPath, "wrong-store.pdf"));
        Assert.Empty(await PortalZReportHistory.LoadAsync(db, 1, Galaxy, default, _ => throw new Exception("Stale identity must not be accepted")));
    }
}
