using ManagerPaperworkSystem.WinForms;
using Xunit;

namespace ManagerPaperworkSystem.PortalSettings.Tests;

public sealed class SettingsConcurrencyTests : IDisposable
{
    public SettingsConcurrencyTests() => Directory.CreateDirectory(AppBootstrap.AppDataPath);
    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(AppBootstrap.AppDataPath)) File.Delete(file);
    }
    private static PortalStoreSyncSettings Seed()
    {
        PortalSyncSettingsStore.Update(d => d.Stores.Add(new PortalStoreSyncSettings {
            BusinessId=7, DatabaseName="GalaxyDb", PortalStoreName="GALAXY SMOKE SHOP (ELGIN, IL)",
            PortalPassword="test-secret", LastCashSummaryStatus="cash unchanged" }));
        return PortalSyncSettingsStore.Load().Stores.Single();
    }

    [Fact]
    public void FinishingOldRunCannotUndoNewStoreNameOrPublishOldFailure()
    {
        var oldRun = Seed();
        PortalSyncSettingsStore.Update(d => d.Stores.Single().PortalStoreName="GALAXY SMOKE SHOP (ELGIN, IL - 60123)");
        oldRun.LastZReportAttemptUtc=DateTime.UtcNow;
        oldRun.LastZReportStatus="Old store verification failure";
        PortalSyncSettingsStore.SaveRunStatus(oldRun, PortalSyncReportKind.ZReports);
        var saved=PortalSyncSettingsStore.Load().Stores.Single();
        Assert.Equal("GALAXY SMOKE SHOP (ELGIN, IL - 60123)", saved.PortalStoreName);
        Assert.Equal("Not run yet", saved.LastZReportStatus);
        Assert.Equal("test-secret", saved.PortalPassword);
    }

    [Fact]
    public void RunStatusPreservesOtherStoreAndOtherReportScheduleAndStatus()
    {
        var run=Seed();
        PortalSyncSettingsStore.Update(d => {
            d.Stores.Add(new PortalStoreSyncSettings { BusinessId=8, DatabaseName="OtherDb" });
            d.Stores[0].CashSalesDailyHour=9;
            d.Stores[0].LastCashSummaryStatus="new cash result";
        });
        run.LastZReportAttemptUtc=DateTime.UtcNow;
        run.LastZReportStatus="new Z result";
        PortalSyncSettingsStore.SaveRunStatus(run, PortalSyncReportKind.ZReports);
        var saved=PortalSyncSettingsStore.Load();
        Assert.Equal(2,saved.Stores.Count);
        Assert.Equal(9,saved.Stores[0].CashSalesDailyHour);
        Assert.Equal("new cash result",saved.Stores[0].LastCashSummaryStatus);
        Assert.Equal("new Z result",saved.Stores[0].LastZReportStatus);
    }

    [Fact]
    public void OldResultCannotReplaceANewerResult()
    {
        var old=Seed();
        old.LastZReportAttemptUtc=DateTime.UtcNow.AddMinutes(-5);
        old.LastZReportStatus="old";
        PortalSyncSettingsStore.Update(d => { d.Stores[0].LastZReportAttemptUtc=DateTime.UtcNow; d.Stores[0].LastZReportStatus="new"; });
        PortalSyncSettingsStore.SaveRunStatus(old,PortalSyncReportKind.ZReports);
        Assert.Equal("new",PortalSyncSettingsStore.Load().Stores[0].LastZReportStatus);
    }

    [Fact]
    public async Task SimultaneousReadMergeWritesDoNotLoseStores()
    {
        await Task.WhenAll(Enumerable.Range(1,24).Select(i=>Task.Run(()=>
            PortalSyncSettingsStore.Update(d=>d.Stores.Add(new PortalStoreSyncSettings {BusinessId=i,DatabaseName=$"Db{i}"})))));
        Assert.Equal(24,PortalSyncSettingsStore.Load().Stores.Select(s=>s.BusinessId).Distinct().Count());
    }

    [Fact]
    public void UnreadableSettingsAreNotSilentlyReplacedWithEmptySettings()
    {
        byte[] corrupt=[1,2,3,4];
        File.WriteAllBytes(PortalSyncSettingsStore.ProtectedPath,corrupt);
        Assert.Throws<InvalidOperationException>(()=>PortalSyncSettingsStore.Update(d=>d.Stores.Clear()));
        Assert.Equal(corrupt,File.ReadAllBytes(PortalSyncSettingsStore.ProtectedPath));
    }

    [Fact]
    public void FailedEditLeavesPreviousDocumentIntact()
    {
        Seed();
        var before=File.ReadAllBytes(PortalSyncSettingsStore.ProtectedPath);
        Assert.Throws<InvalidOperationException>(()=>PortalSyncSettingsStore.Update(d=> {d.Stores.Clear();throw new InvalidOperationException("test");}));
        Assert.Equal(before,File.ReadAllBytes(PortalSyncSettingsStore.ProtectedPath));
    }

    [Fact]
    public void CompletingRunCannotRestoreDisconnectedStore()
    {
        var run=Seed();
        PortalSyncSettingsStore.Update(d=> {d.Stores[0].ZReportsEnabled=false;d.Stores[0].LastZReportStatus="Disconnected";});
        PortalSyncSettingsStore.SaveRunStatus(run,PortalSyncReportKind.ZReports);
        Assert.False(PortalSyncSettingsStore.Load().Stores[0].ZReportsEnabled);
        Assert.Equal("Disconnected",PortalSyncSettingsStore.Load().Stores[0].LastZReportStatus);
    }
}
