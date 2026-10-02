using System.Diagnostics;
using ManagerPaperworkSystem.WinForms;
using Xunit;

namespace ManagerPaperworkSystem.PortalSettings.Tests;

public sealed class BackfillNotificationTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 10, 1);
    private static string ResultDirectory => Path.Combine(AppBootstrap.AppDataPath, "Backfill Results");
    public void Dispose()
    {
        if (Directory.Exists(ResultDirectory)) Directory.Delete(ResultDirectory, true);
    }
    private static PortalBackfillRequest Start(string business = "GALAXY ELGIN") =>
        PortalBackfillResults.Create(business, "Z Report", Day, Day);

    [Theory]
    [InlineData(true, "2 new shifts imported")]
    [InlineData(false, "Portal login failed")]
    public void CompletedBackgroundResultIsPersistedForLaterDisplay(bool success, string message)
    {
        var job = Start();
        PortalBackfillResults.Complete(job.Id, success, message);
        // No UI listener was present when the child completed.
        using var next = PortalBackfillResults.TakeNext();
        Assert.NotNull(next);
        Assert.Equal("GALAXY ELGIN", next.Request.BusinessName);
        Assert.Equal(Day, next.Request.From);
        Assert.Equal(Day, next.Request.Through);
        Assert.Equal(success, next.Result.Success);
        Assert.Equal(message, next.Result.Message);
    }

    [Fact]
    public void PendingRunDoesNotDisplayAnEarlySuccessOrFailure()
    {
        Start();
        Assert.Null(PortalBackfillResults.TakeNext());
    }

    [Fact]
    public void AcknowledgedResultDoesNotAppearAgain()
    {
        var job = Start();
        PortalBackfillResults.Complete(job.Id, true, "Imported");
        using (var next = PortalBackfillResults.TakeNext()) next!.Acknowledge();
        Assert.Null(PortalBackfillResults.TakeNext());
    }

    [Fact]
    public void ClosingBeforeAcknowledgementLeavesResultAvailableOnReopen()
    {
        var job = Start();
        PortalBackfillResults.Complete(job.Id, false, "Needs attention");
        PortalBackfillResults.TakeNext()!.Dispose();
        using var reopened = PortalBackfillResults.TakeNext();
        Assert.NotNull(reopened);
        Assert.Equal("Needs attention", reopened.Result.Message);
    }

    [Fact]
    public void TwoAppWindowsCannotDisplayTheSameResultTogether()
    {
        var job = Start();
        PortalBackfillResults.Complete(job.Id, true, "Imported");
        using var first = PortalBackfillResults.TakeNext();
        Assert.NotNull(first);
        Assert.Null(PortalBackfillResults.TakeNext());
    }

    [Fact]
    public void FastChildCompletionIsNotOverwrittenByLauncherRegisteringPid()
    {
        var job = Start();
        PortalBackfillResults.Complete(job.Id, true, "Fast import");
        PortalBackfillResults.RegisterProcess(job, 123, DateTime.UtcNow);
        using var next = PortalBackfillResults.TakeNext();
        Assert.Equal("Fast import", next!.Result.Message);
    }

    [Fact]
    public void UnexpectedProcessExitProducesFailureInsteadOfSilence()
    {
        var job = Start() with { StartedUtc = DateTime.UtcNow.AddMinutes(-5) };
        PortalBackfillResults.RegisterProcess(job, int.MaxValue, DateTime.UtcNow.AddMinutes(-5));
        using var next = PortalBackfillResults.TakeNext();
        Assert.NotNull(next);
        Assert.False(next.Result.Success);
        Assert.Contains("stopped before reporting a result", next.Result.Message);
    }

    [Fact]
    public void AnActiveBackgroundProcessIsNotReportedAsInterrupted()
    {
        var job = Start() with { StartedUtc = DateTime.UtcNow.AddMinutes(-5) };
        using var current = Process.GetCurrentProcess();
        PortalBackfillResults.RegisterProcess(job, current.Id, current.StartTime.ToUniversalTime());
        Assert.Null(PortalBackfillResults.TakeNext());
    }

    [Fact]
    public void ReusedProcessIdDoesNotHideInterruptedJob()
    {
        var job = Start() with { StartedUtc = DateTime.UtcNow.AddDays(-1) };
        using var current = Process.GetCurrentProcess();
        PortalBackfillResults.RegisterProcess(job, current.Id, current.StartTime.ToUniversalTime().AddDays(-1));
        using var next = PortalBackfillResults.TakeNext();
        Assert.NotNull(next);
        Assert.False(next.Result.Success);
    }

    [Fact]
    public void DifferentStoresKeepSeparateResults()
    {
        var galaxy = Start();
        var other = Start("OTHER STORE");
        PortalBackfillResults.Complete(galaxy.Id, true, "Galaxy imported");
        PortalBackfillResults.Complete(other.Id, false, "Other store failed");
        var found = new Dictionary<string, string>();
        for (var i = 0; i < 2; i++)
        {
            using var next = PortalBackfillResults.TakeNext();
            Assert.NotNull(next);
            found.Add(next.Request.BusinessName, next.Result.Message);
            next.Acknowledge();
        }
        Assert.Equal("Galaxy imported", found["GALAXY ELGIN"]);
        Assert.Equal("Other store failed", found["OTHER STORE"]);
    }

    [Fact]
    public void ExitDetectionCannotOverwriteTheChildsFinalResult()
    {
        var job = Start();
        PortalBackfillResults.Complete(job.Id, true, "Import finished just before process exit");
        PortalBackfillResults.Complete(job.Id, false, "Process exited", overwrite: false);
        using var next = PortalBackfillResults.TakeNext();
        Assert.True(next!.Result.Success);
        Assert.Equal("Import finished just before process exit", next.Result.Message);
    }

    [Fact]
    public void FailedLaunchCanRemoveUnstartedRequest()
    {
        var job = Start();
        PortalBackfillResults.CancelStartup(job.Id);
        Assert.Empty(Directory.EnumerateFiles(ResultDirectory, "*.request.json"));
    }
}
