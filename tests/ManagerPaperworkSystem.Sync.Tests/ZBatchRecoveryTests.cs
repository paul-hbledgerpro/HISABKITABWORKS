using ManagerPaperworkSystem.WinForms;
using Xunit;
namespace ManagerPaperworkSystem.Sync.Tests;

public class ZBatchRecoveryTests
{
    [Fact]
    public void OctoberCloseOutsPrecedeOldUnimportedHoles()
    {
        var pending = PortalSyncRecoveryPolicy.PendingZBatches([1, 315, 1548, 1549, 1550, 1551], new HashSet<long> { 1548, 1549 }, false);
        Assert.Equal(new long[] { 1550, 1551, 1, 315 }, pending);
        // If processing stops at old batch 1, committed October batches stay
        // excluded on retry and tomorrow's close-out is still processed first.
        Assert.Equal(new long[] { 1552, 1, 315 }, PortalSyncRecoveryPolicy.PendingZBatches(
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
}
