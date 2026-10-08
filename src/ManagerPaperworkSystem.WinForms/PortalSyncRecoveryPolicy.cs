namespace ManagerPaperworkSystem.WinForms;

/// <summary>Recovery decisions use stored report coverage, not a Windows profile's success label.</summary>
internal static class PortalSyncRecoveryPolicy
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ZBatchRecheckInterval = TimeSpan.FromHours(4);
    public const int RecentGapDays = 30;

    public static DateOnly DueThrough(DateTime localNow, TimeOnly runTime, bool force) =>
        DateOnly.FromDateTime(localNow).AddDays(
            force || TimeOnly.FromDateTime(localNow) >= runTime ? -1 : -2);

    public static bool RetryIsDue(DateTime? lastAttemptUtc, DateTime utcNow) =>
        !lastAttemptUtc.HasValue || lastAttemptUtc > utcNow ||
        utcNow - lastAttemptUtc.Value >= RetryInterval;

    public static bool ShouldCheckZBatches(DateOnly? lastReportDate, DateTime? lastSuccessUtc,
        DateOnly dueThrough, DateTime utcNow, bool targetDatePresent) =>
        !targetDatePresent || !lastReportDate.HasValue || lastReportDate < dueThrough ||
        !lastSuccessUtc.HasValue || lastSuccessUtc > utcNow ||
        utcNow - lastSuccessUtc.Value >= ZBatchRecheckInterval;

    public static List<long> PendingZBatches(
        IEnumerable<long> portalBatches, IReadOnlySet<long> importedBatches, bool historical, long? historicalAnchor = null)
    {
        var missing = portalBatches.Distinct().Where(batch => !importedBatches.Contains(batch));
        // Backfill resumes after the last verified batch before its date range,
        // rather than treating unrelated or contaminated rows as its cursor.
        if (historical && historicalAnchor.HasValue)
            return missing.Where(batch => batch > historicalAnchor.Value).OrderBy(batch => batch).ToList();
        if (historical || importedBatches.Count == 0)
            return missing.OrderByDescending(batch => batch).ToList();

        var latest = importedBatches.Max();
        // A targeted pending-drop import can jump the highest known batch.
        // Check recent gaps below it before years-old missing receipts; do not
        // treat that single imported batch as proof that earlier shifts exist.
        return missing.Where(batch => batch > latest).OrderBy(batch => batch)
            .Concat(missing.Where(batch => batch <= latest).OrderByDescending(batch => batch))
            .ToList();
    }

    public static List<DateOnly> PendingCashDates(
        IEnumerable<(DateOnly From, DateOnly Through)> reportCoverage, DateOnly dueThrough)
    {
        var coverage = reportCoverage
            .Where(range => range.From <= range.Through && range.From <= dueThrough)
            .ToList();
        if (coverage.Count == 0)
            return [dueThrough];

        var latest = coverage.Max(range => range.Through);
        var earliest = coverage.Min(range => range.From);
        var pending = new List<DateOnly>();

        // Catch up after an outage even when it lasted more than the gap window.
        // New days come first so an unavailable older report cannot block tomorrow.
        if (latest < dueThrough)
        {
            for (var date = latest.AddDays(1); date <= dueThrough; date = date.AddDays(1))
                pending.Add(date);
        }

        var gapStart = dueThrough.AddDays(-(RecentGapDays - 1));
        if (gapStart < earliest)
            gapStart = earliest;
        var gapEnd = latest < dueThrough ? latest : dueThrough;
        for (var date = gapStart; date <= gapEnd; date = date.AddDays(1))
        {
            if (!coverage.Any(range => range.From <= date && range.Through >= date))
                pending.Add(date);
        }
        return pending;
    }
}
