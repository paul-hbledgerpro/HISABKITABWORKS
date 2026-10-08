using ManagerPaperworkSystem.WinForms;
using Xunit;
namespace ManagerPaperworkSystem.Sync.Tests;

public class ZBatchRecoveryTests
{
    [Fact]
    public void OctoberCloseOutsPrecedeOldUnimportedHoles()
    {
        var pending = PortalSyncRecoveryPolicy.PendingZBatches([1, 315, 1548, 1549, 1550, 1551], new HashSet<long> { 1548, 1549 }, false);
        Assert.Equal(new long[] { 1550, 1551, 315, 1 }, pending);
        // If processing stops at old batch 315, committed October batches stay
        // excluded on retry and tomorrow's close-out is still processed first.
        Assert.Equal(new long[] { 1552, 315, 1 }, PortalSyncRecoveryPolicy.PendingZBatches(
            [1, 315, 1548, 1549, 1550, 1551, 1552], new HashSet<long> { 1548, 1549, 1550, 1551 }, false));
    }

    [Fact]
    public void LateLowerNumberedCloseOutIsStillEligible() => Assert.Equal(
        new long[] { 1550 }, PortalSyncRecoveryPolicy.PendingZBatches([1549, 1550, 1551], new HashSet<long> { 1549, 1551 }, false));

    [Fact]
    public void BackfillWithoutVerifiedAnchorChecksNewestMissingFirst() => Assert.Equal(
        new long[] { 1551, 1550, 315, 1 }, PortalSyncRecoveryPolicy.PendingZBatches([1, 315, 1549, 1550, 1551], new HashSet<long> { 1549 }, true));

    [Fact]
    public void FirstSyncChecksNewestFirstWithoutDuplicates() => Assert.Equal(
        new long[] { 1551, 1550, 1 }, PortalSyncRecoveryPolicy.PendingZBatches([1, 1550, 1551, 1551], new HashSet<long>(), false));

    [Fact]
    public void FullyImportedStoreDoesNotRepeatBatches() => Assert.Empty(
        PortalSyncRecoveryPolicy.PendingZBatches([1550, 1551], new HashSet<long> { 1550, 1551 }, false));
    [Fact]
    public void BackfillResumesAscendingAfterVerifiedBoundary()
    {
        Assert.Equal(new long[] { 1551, 1552, 1553 }, PortalSyncRecoveryPolicy.PendingZBatches(
            [1, 315, 1549, 1550, 1551, 1552, 1553], new HashSet<long> { 1549, 1550 }, true, 1549));
        Assert.Empty(PortalSyncRecoveryPolicy.PendingZBatches(
            [1, 315, 1549, 1550, 1551], new HashSet<long> { 1549, 1550, 1551 }, true, 1549));
    }

    [Fact]
    public void AnOlderDateRangeUsesItsEarlierVerifiedBoundary()
    {
        Assert.Equal(new long[] { 1501, 1503 }, PortalSyncRecoveryPolicy.PendingZBatches(
            [1500, 1501, 1502, 1503, 1549], new HashSet<long> { 1500, 1502, 1549 }, true, 1500));
    }
    [Fact]
    public void PendingDropImportsCannotPushOctoberGapsBehindAncientBatches()
    {
        var portal = Enumerable.Range(1, 3451).Select(number => (long)number);
        var imported = new HashSet<long> { 1, 1000, 3428, 3440, 3441, 3442, 3451 };
        var expected = Enumerable.Range(3429, 23).Select(number => (long)number)
            .Where(number => !imported.Contains(number)).OrderByDescending(number => number).ToArray();
        var pending = PortalSyncRecoveryPolicy.PendingZBatches(portal, imported, false);
        Assert.Equal(expected, pending.Take(expected.Length));
        Assert.Equal(3427L, pending[expected.Length]);
        Assert.Equal(2L, pending[^1]);
        Assert.Empty(pending.Intersect(imported));
    }

    [Fact]
    public void IfOldSourceFilesAreUnavailableRecentGapsStillComeBeforeAncientBatches()
    {
        var portal = Enumerable.Range(1, 3451).Select(number => (long)number);
        var imported = new HashSet<long> { 3440, 3441, 3442, 3451 };
        var pending = PortalSyncRecoveryPolicy.PendingZBatches(portal, imported, false);
        Assert.Equal(Enumerable.Range(3443, 8).Reverse().Select(number => (long)number), pending.Take(8));
        Assert.Equal(Enumerable.Range(3429, 11).Reverse().Select(number => (long)number), pending.Skip(8).Take(11));
    }

    [Fact]
    public void HistoricalRangeIncludesEveryMissingBatchAfterBoundaryRegardlessOfEnteredDrops()
    {
        var portal = Enumerable.Range(1, 3451).Select(number => (long)number);
        var imported = new HashSet<long> { 3428, 3440, 3441, 3442, 3451 };
        var expected = Enumerable.Range(3429, 23).Select(number => (long)number)
            .Where(number => !imported.Contains(number)).ToArray();
        var pending = PortalSyncRecoveryPolicy.PendingZBatches(portal, imported, true, 3428);
        Assert.Equal(expected, pending);
        imported.UnionWith(pending.Take(5)); // A stopped backfill resumes the remaining gaps.
        Assert.Equal(expected.Skip(5), PortalSyncRecoveryPolicy.PendingZBatches(portal, imported, true, 3428));
    }
}
