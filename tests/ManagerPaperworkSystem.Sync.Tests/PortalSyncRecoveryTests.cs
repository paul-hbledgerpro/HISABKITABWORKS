using ManagerPaperworkSystem.WinForms;
using Xunit;

namespace ManagerPaperworkSystem.Sync.Tests;

public sealed class PortalSyncRecoveryTests
{
    private static readonly DateOnly Through = new(2026, 9, 4);
    private static readonly DateTime NowUtc = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
    private static (DateOnly, DateOnly) Day(int offset) => (Through.AddDays(offset), Through.AddDays(offset));

    [Fact]
    public void OlderOutageDaysAreDueEvenBeforeTodaysScheduledTime()
    {
        var due = PortalSyncRecoveryPolicy.DueThrough(new DateTime(2026, 9, 5, 0, 30, 0), new TimeOnly(1, 15), false);
        Assert.Equal(new DateOnly(2026, 9, 3), due);
        var pending = PortalSyncRecoveryPolicy.PendingCashDates([Day(-5)], due);
        Assert.Equal(new[] { Through.AddDays(-4), Through.AddDays(-3), Through.AddDays(-2), Through.AddDays(-1) }, pending);
    }

    [Fact]
    public void ScheduledTimeAndManualRunIncludeYesterday()
    {
        Assert.Equal(Through, PortalSyncRecoveryPolicy.DueThrough(new DateTime(2026, 9, 5, 1, 15, 0), new TimeOnly(1, 15), false));
        Assert.Equal(Through, PortalSyncRecoveryPolicy.DueThrough(new DateTime(2026, 9, 5, 0, 30, 0), new TimeOnly(1, 15), true));
    }

    [Fact]
    public void FindingNewestReportDoesNotHideAnEarlierMissingDay()
    {
        var pending = PortalSyncRecoveryPolicy.PendingCashDates([Day(-3), Day(-1), Day(0)], Through);
        Assert.Equal(new[] { Through.AddDays(-2) }, pending);
    }

    [Fact]
    public void NewDaysComeBeforeOlderGapsSoAnOldFailureCannotBlockTomorrow()
    {
        var pending = PortalSyncRecoveryPolicy.PendingCashDates([Day(-4), Day(-2)], Through);
        Assert.Equal(new[] { Through.AddDays(-1), Through, Through.AddDays(-3) }, pending);
    }

    [Fact]
    public void LongOutageRecoversAllNewDaysBeyondThirtyDayWindow()
    {
        var pending = PortalSyncRecoveryPolicy.PendingCashDates([Day(-60)], Through);
        Assert.Equal(60, pending.Count);
        Assert.Equal(Through.AddDays(-59), pending[0]);
        Assert.Equal(Through, pending[^1]);
    }

    [Fact]
    public void InitialSyncDoesNotImportUnrequestedHistoricalYears()
    {
        Assert.Equal(new[] { Through }, PortalSyncRecoveryPolicy.PendingCashDates([], Through));
        Assert.Empty(PortalSyncRecoveryPolicy.PendingCashDates([Day(0)], Through));
    }

    [Fact]
    public void RecentGapRepairIsBoundedAndHonorsExistingDateRanges()
    {
        var pending = PortalSyncRecoveryPolicy.PendingCashDates(
            [(Through.AddDays(-90), Through.AddDays(-31)), (Through.AddDays(-5), Through)], Through);
        Assert.Equal(24, pending.Count);
        Assert.Equal(Through.AddDays(-29), pending[0]);
        Assert.Equal(Through.AddDays(-6), pending[^1]);
    }

    [Fact]
    public void ExistingOverlappingCoverageIsNotImportedAgain()
    {
        Assert.Empty(PortalSyncRecoveryPolicy.PendingCashDates(
            [(Through.AddDays(-8), Through.AddDays(-3)), (Through.AddDays(-4), Through)], Through));
    }

    [Fact]
    public void FutureAndInvalidRowsDoNotHideMissingReports()
    {
        Assert.Equal(new[] { Through }, PortalSyncRecoveryPolicy.PendingCashDates(
            [(Through.AddDays(1), Through.AddDays(2)), (Through, Through.AddDays(-1))], Through));
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(14, false)]
    [InlineData(15, true)]
    [InlineData(60, true)]
    public void RepeatedTimerTicksRespectRetryDelay(int minutes, bool due) =>
        Assert.Equal(due, PortalSyncRecoveryPolicy.RetryIsDue(NowUtc.AddMinutes(-minutes), NowUtc));

    [Fact]
    public void FirstAttemptAndClockCorrectionDoNotStallRecovery()
    {
        Assert.True(PortalSyncRecoveryPolicy.RetryIsDue(null, NowUtc));
        Assert.True(PortalSyncRecoveryPolicy.RetryIsDue(NowUtc.AddHours(1), NowUtc));
    }

    [Fact]
    public void ZBatchCheckRetriesLateRegistersEvenWhenYesterdayWasAlreadySuccessful()
    {
        Assert.False(PortalSyncRecoveryPolicy.ShouldCheckZBatches(Through, NowUtc.AddHours(-1), Through, NowUtc));
        Assert.True(PortalSyncRecoveryPolicy.ShouldCheckZBatches(Through, NowUtc.AddHours(-4), Through, NowUtc));
        Assert.True(PortalSyncRecoveryPolicy.ShouldCheckZBatches(Through.AddDays(-1), NowUtc.AddHours(-1), Through, NowUtc));
        Assert.True(PortalSyncRecoveryPolicy.ShouldCheckZBatches(null, null, Through, NowUtc));
    }
}
