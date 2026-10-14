using ManagerPaperworkSystem.WinForms;
using Xunit;

namespace ManagerPaperworkSystem.Sync.Tests;

public class ZBatchConflictRecoveryTests
{
    [Fact]
    public async Task SeveralExistingConflictsDoNotPreventLaterBatchesWithoutPendingDrops()
    {
        var saved = new List<int>();
        var conflicts = new List<int>();
        await PortalZBatchSequence.RunAsync(Enumerable.Range(3002, 12), batch =>
        {
            if (batch <= 3004) throw new PortalZReportConflictException(batch.ToString(), new(2026, 10, 7), batch);
            saved.Add(batch);
            return Task.CompletedTask;
        }, (batch, _) => conflicts.Add(batch));
        Assert.Equal(new[] { 3002, 3003, 3004 }, conflicts);
        Assert.Equal(Enumerable.Range(3005, 9), saved);
    }

    [Fact]
    public async Task WrongStoreReceiptStillStopsBeforeAnyFollowingBatch()
    {
        var attempted = new List<int>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => PortalZBatchSequence.RunAsync(new[] { 3005, 3006 }, batch =>
        {
            attempted.Add(batch);
            PortalStoreIsolationPolicy.ValidateZReportStore(
                "GALAXY SMOKE SHOP\n20 S STATE ST\nELGIN, IL\nZ-Report\nRegister Number: 1\nBatch: 3005\nStart Date: 10/08/2026",
                "ELGIN SMOKE SHOP (ELGIN, IL - 60120)");
            return Task.CompletedTask;
        }, (_, _) => throw new Exception("A store mismatch must not be treated as a saved row conflict.")));
        Assert.Equal(new[] { 3005 }, attempted);
    }

    [Fact]
    public async Task CancellationStillStopsBetweenBatches()
    {
        var attempted = new List<int>();
        await Assert.ThrowsAsync<OperationCanceledException>(() => PortalZBatchSequence.RunAsync(new[] { 3002, 3005, 3006 }, batch =>
        {
            attempted.Add(batch);
            if (batch == 3002) throw new PortalZReportConflictException("3002", new(2026, 10, 7), 1);
            throw new OperationCanceledException();
        }, (_, _) => { }));
        Assert.Equal(new[] { 3002, 3005 }, attempted);
    }

    [Fact]
    public async Task UnrelatedErrorsAreNotSkippedEvenWithSimilarErrorText()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => PortalZBatchSequence.RunAsync(new[] { 3002, 3005 }, batch =>
        {
            Assert.Equal(3002, batch);
            throw new InvalidOperationException("Batch conflicts with an existing unverified report");
        }, (_, _) => throw new Exception("Message text cannot grant permission to continue.")));
    }
}
