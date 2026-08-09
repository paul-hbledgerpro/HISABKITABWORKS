using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Services;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.UI.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using PuppeteerSharp;
using PuppeteerSharp.Media;

namespace ManagerPaperworkSystem.WinForms;

internal sealed record PortalSyncRunResult(
    string BusinessName,
    bool Success,
    bool Imported,
    string Message);

internal sealed record ZReportImportOutcome(int Total, int Imported, int Updated);
internal sealed record GeneratedPortalReport(
    string? PdfPath,
    string ScreenshotPath,
    string RenderedText,
    string? ExportError);
internal sealed record CapturedZReport(PosReportData Report, string SourcePath);
internal sealed record PortalTargetStatus(bool CashSummaryPresent, int ZReportCount);
internal sealed record PortalSyncScheduleResult(bool WindowsTaskCreated, string Message);

internal static class PortalSyncService
{
    private const string LegacyTaskName = "HISAB KITAB - Daily POS Report Sync";
    private const string StartupRunName = "HISAB KITAB POS Report Sync";
    private const string CurrentUserRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly SemaphoreSlim RunGate = new(1, 1);

    public static string? FindGoogleChrome()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "Application", "chrome.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static void OpenEnrollmentChrome(PortalStoreSyncSettings settings)
    {
        if (!PortalSyncSettingsStore.IsConnected(settings))
        {
            throw new InvalidOperationException(
                "This store is disconnected from the current PC login. Reconnect it in Stores before running One-Time Setup.");
        }

        var chrome = FindGoogleChrome()
                     ?? throw new InvalidOperationException(
                         "Google Chrome is not installed. Install Chrome, then try the one-time setup again.");
        var profile = PortalSyncSettingsStore.ProfileDirectory(settings.Id);
        Directory.CreateDirectory(profile);
        Process.Start(new ProcessStartInfo
        {
            FileName = chrome,
            UseShellExecute = false,
            ArgumentList =
            {
                $"--user-data-dir={profile}",
                "--new-window",
                "--no-first-run",
                "--no-default-browser-check",
                settings.PortalUrl
            }
        });
    }

    public static int StartHistoricalBackfill(
        Guid storeConfigurationId,
        PortalSyncReportKind reportKind,
        DateOnly historicalStartDate,
        DateOnly historicalEndDate)
    {
        if (historicalEndDate < historicalStartDate)
            throw new InvalidOperationException(
                "Historical end date must be on or after the start date.");
        if (historicalEndDate.DayNumber - historicalStartDate.DayNumber > 365)
            throw new InvalidOperationException(
                "Historical backfill is limited to 366 days per run.");

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new InvalidOperationException(
                "The installed HISAB KITAB executable could not be located.");

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? "",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                "--portal-sync-store", storeConfigurationId.ToString("D"),
                "--portal-sync-report", ReportArgument(reportKind),
                "--portal-sync-backfill-from",
                historicalStartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "--portal-sync-backfill-through",
                historicalEndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            }
        }) ?? throw new InvalidOperationException(
            "The background historical sync process could not be started.");

        return process.Id;
    }

    public static PortalSyncScheduleResult EnsureDailyTask(
        Guid storeConfigurationId,
        PortalSyncReportKind reportKind,
        TimeOnly runAt)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new InvalidOperationException("The installed HISAB KITAB executable could not be located.");

        Exception? startupFallbackError = null;
        try
        {
            EnsureStartupFallback(executable);
        }
        catch (Exception exception)
        {
            startupFallbackError = exception;
        }

        // Older releases used a shared task name. That task may have been
        // created by an administrator or a different Windows account, which
        // prevents a standard client account from replacing it.
        RemoveScheduledTask(LegacyScheduledTaskName(storeConfigurationId, reportKind));
        var taskName = ScheduledTaskName(storeConfigurationId, reportKind);
        var temporaryXml = Path.Combine(
            Path.GetTempPath(),
            $"hisab-kitab-pos-sync-{ReportArgument(reportKind)}-{storeConfigurationId:N}-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(
                temporaryXml,
                CreateScheduledTaskXml(executable, storeConfigurationId, reportKind, runAt),
                Encoding.Unicode);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    "/Create",
                    "/TN", taskName,
                    "/XML", temporaryXml,
                    "/F"
                }
            }) ?? throw new InvalidOperationException("Windows Task Scheduler could not be started.");
            process.WaitForExit(20_000);
            if (process.ExitCode != 0)
            {
                var error = process.StandardError.ReadToEnd().Trim();
                if (string.IsNullOrWhiteSpace(error))
                    error = process.StandardOutput.ReadToEnd().Trim();

                if (startupFallbackError is not null)
                {
                    throw new InvalidOperationException(
                        "Windows rejected both automatic sync startup methods. " +
                        $"Task Scheduler: {(string.IsNullOrWhiteSpace(error) ? "access denied" : error)} " +
                        $"Windows sign-in fallback: {startupFallbackError.Message}");
                }

                return new PortalSyncScheduleResult(
                    false,
                    "Settings saved. Windows blocked the timed task, so automatic catch-up will run " +
                    "at Windows sign-in and whenever HISAB KITAB starts.");
            }

            return new PortalSyncScheduleResult(
                true,
                $"Daily Windows task scheduled for {runAt.ToString("h:mm tt", CultureInfo.CurrentCulture)}.");
        }
        finally
        {
            try
            {
                File.Delete(temporaryXml);
            }
            catch
            {
                // A temporary task definition can be removed by Windows later.
            }
        }
    }

    public static void RemoveDailyTask(
        Guid storeConfigurationId,
        PortalSyncReportKind reportKind)
    {
        RemoveScheduledTask(ScheduledTaskName(storeConfigurationId, reportKind));
        RemoveScheduledTask(LegacyScheduledTaskName(storeConfigurationId, reportKind));
    }

    public static void EnsureConfiguredDailyTasks()
    {
        foreach (var settings in PortalSyncSettingsStore.Load().Stores)
        {
            RemoveScheduledTask($"{LegacyTaskName} - {settings.Id:N}");
            var connected = PortalSyncSettingsStore.IsConnected(settings);
            foreach (var reportKind in Enum.GetValues<PortalSyncReportKind>())
            {
                if (connected && settings.IsEnabled(reportKind))
                    EnsureDailyTask(settings.Id, reportKind, settings.GetRunTime(reportKind));
                else
                    RemoveDailyTask(settings.Id, reportKind);
            }
        }
    }

    public static async Task<IReadOnlyList<PortalSyncRunResult>> RunDueAsync(
        IAppPaths paths,
        bool force,
        bool visibleChrome,
        Guid? onlyStoreConfigurationId = null,
        PortalSyncReportKind? onlyReportKind = null,
        bool waitForExistingRun = false,
        TimeSpan? existingRunWaitTimeout = null,
        DateOnly? historicalStartDate = null,
        DateOnly? historicalEndDate = null,
        CancellationToken cancellationToken = default)
    {
        var historicalBackfill = historicalStartDate.HasValue || historicalEndDate.HasValue;
        if (historicalBackfill)
        {
            if (!historicalStartDate.HasValue || !historicalEndDate.HasValue)
                throw new InvalidOperationException("Select both a historical start date and end date.");
            if (historicalEndDate.Value < historicalStartDate.Value)
                throw new InvalidOperationException("Historical end date must be on or after the start date.");
            if (historicalEndDate.Value.DayNumber - historicalStartDate.Value.DayNumber > 365)
                throw new InvalidOperationException("Historical backfill is limited to 366 days per run.");
            if (!onlyStoreConfigurationId.HasValue || !onlyReportKind.HasValue)
                throw new InvalidOperationException("Historical backfill must target one licensed store and one report type.");
        }

        var waitTimeout = existingRunWaitTimeout ?? TimeSpan.FromMinutes(3);
        var gateAcquired = waitForExistingRun
            ? await RunGate.WaitAsync(waitTimeout, cancellationToken)
            : await RunGate.WaitAsync(0, cancellationToken);
        if (!gateAcquired)
            return [new PortalSyncRunResult("", true, false, "A POS portal sync is already running.")];

        FileStream? processLock = null;
        try
        {
            var lockPath = Path.Combine(AppBootstrap.AppDataPath, "pos-portal-sync.lock");
            Directory.CreateDirectory(AppBootstrap.AppDataPath);
            var lockDeadline = waitForExistingRun
                ? DateTime.UtcNow.Add(waitTimeout)
                : DateTime.UtcNow;
            while (processLock is null)
            {
                try
                {
                    processLock = new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None);
                }
                catch (IOException) when (DateTime.UtcNow < lockDeadline)
                {
                    await Task.Delay(500, cancellationToken);
                }
                catch (IOException)
                {
                    return [new PortalSyncRunResult("", true, false, "A POS portal sync is already running.")];
                }
            }

            var document = PortalSyncSettingsStore.Load();
            var results = new List<PortalSyncRunResult>();
            var configuredStores = document.Stores
                .Where(item => PortalSyncSettingsStore.IsConnected(item) &&
                               (onlyStoreConfigurationId is null ||
                                 item.Id == onlyStoreConfigurationId.Value))
                .ToList();
            if (onlyStoreConfigurationId is not null &&
                (configuredStores.Count == 0 ||
                 (onlyReportKind.HasValue &&
                  !configuredStores[0].IsEnabled(onlyReportKind.Value) &&
                  !historicalBackfill)))
            {
                var missing = new PortalSyncRunResult(
                    "",
                    false,
                    false,
                    $"The scheduled {ReportDisplayName(onlyReportKind ?? PortalSyncReportKind.CashSalesSummary)} " +
                    $"configuration {onlyStoreConfigurationId:D} was not found, is disabled, or its store is disconnected.");
                results.Add(missing);
                WriteLog(missing);
                return results;
            }

            foreach (var settings in configuredStores)
            {
                var yesterday = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
                var reportKinds = onlyReportKind.HasValue
                    ? new[] { onlyReportKind.Value }
                    : Enum.GetValues<PortalSyncReportKind>();
                foreach (var reportKind in reportKinds)
                {
                    if (!settings.IsEnabled(reportKind) && !historicalBackfill)
                        continue;
                    if (!force &&
                        DateTime.Now.TimeOfDay < settings.GetRunTime(reportKind).ToTimeSpan())
                        continue;

                    List<DateOnly> pendingDates;
                    if (historicalBackfill)
                    {
                        pendingDates = reportKind == PortalSyncReportKind.CashSalesSummary
                            ? Enumerable.Range(
                                    0,
                                    historicalEndDate!.Value.DayNumber -
                                    historicalStartDate!.Value.DayNumber + 1)
                                .Select(offset => historicalStartDate.Value.AddDays(offset))
                                .ToList()
                            : [historicalEndDate!.Value];
                    }
                    else
                    {
                        pendingDates = reportKind == PortalSyncReportKind.CashSalesSummary
                            ? await GetPendingCashSummaryDatesAsync(
                                settings,
                                yesterday,
                                cancellationToken)
                            : !force && settings.LastZReportDate >= yesterday
                                ? []
                                : [yesterday];
                    }
                    if (!force &&
                        pendingDates.Count == 0)
                    {
                        var reportName = reportKind == PortalSyncReportKind.CashSalesSummary
                            ? "Cash & Sales Summary"
                            : "Z Report";
                        var skipped = new PortalSyncRunResult(
                            settings.BusinessName,
                            true,
                            false,
                            $"{reportName} sync is current through {yesterday:M/d/yyyy}.");
                        UpdateRunStatus(settings, reportKind, skipped, yesterday);
                        results.Add(skipped);
                        PortalSyncSettingsStore.Save(document);
                        WriteLog(skipped);
                        continue;
                    }

                    // A manual Cash & Sales run verifies yesterday even when the
                    // stored date cursor is already current.
                    if (force && pendingDates.Count == 0)
                        pendingDates.Add(yesterday);

                    foreach (var targetDate in pendingDates)
                    {
                        PortalSyncRunResult result;
                        using var targetTimeout =
                            CancellationTokenSource.CreateLinkedTokenSource(
                                cancellationToken);
                        targetTimeout.CancelAfter(
                            historicalBackfill
                                ? TimeSpan.FromHours(2)
                                : TimeSpan.FromMinutes(20));
                        try
                        {
                            result = await RunStoreWithRetriesAsync(
                                settings,
                                paths,
                                targetDate,
                                reportKind,
                                visibleChrome,
                                historicalStartDate,
                                historicalEndDate,
                                targetTimeout.Token);
                        }
                        catch (OperationCanceledException)
                            when (!cancellationToken.IsCancellationRequested)
                        {
                            result = new PortalSyncRunResult(
                                settings.BusinessName,
                                false,
                                false,
                                $"{ReportDisplayName(reportKind)} sync exceeded " +
                                $"{(historicalBackfill ? "2 hours" : "20 minutes")} and was stopped. " +
                                "The next scheduled run will resume from its stored cursor.");
                        }
                        catch (Exception exception)
                        {
                            result = new PortalSyncRunResult(
                                settings.BusinessName,
                                false,
                                false,
                                AppBootstrap.RedactSensitiveText(exception.Message));
                        }

                        UpdateRunStatus(settings, reportKind, result, targetDate);
                        results.Add(result);
                        PortalSyncSettingsStore.Save(document);
                        WriteLog(result);

                        // Do not retry later Cash & Sales dates while the first
                        // pending date is still blocked. Its date cursor remains
                        // unchanged, so the next scheduled recovery attempt starts
                        // with that same date. Z-report catch-up always follows its
                        // numeric batch cursor in one portal session, so it runs once.
                        if (!result.Success || reportKind == PortalSyncReportKind.ZReports)
                            break;
                    }
                }
            }
            return results;
        }
        finally
        {
            processLock?.Dispose();
            RunGate.Release();
        }
    }

    private static void UpdateRunStatus(
        PortalStoreSyncSettings settings,
        PortalSyncReportKind reportKind,
        PortalSyncRunResult result,
        DateOnly targetDate)
    {
        var now = DateTime.UtcNow;
        settings.LastAttemptUtc = now;
        settings.LastStatus = result.Message;
        if (reportKind == PortalSyncReportKind.CashSalesSummary)
        {
            settings.LastCashSummaryAttemptUtc = now;
            settings.LastCashSummaryStatus = result.Message;
            if (result.Success)
            {
                settings.LastCashSummarySuccessUtc = now;
                settings.LastCashSummaryReportDate = LatestDate(
                    settings.LastCashSummaryReportDate,
                    targetDate);
                settings.LastImportedReportDate = LatestDate(
                    settings.LastImportedReportDate,
                    targetDate);
            }
        }
        else
        {
            settings.LastZReportAttemptUtc = now;
            settings.LastZReportStatus = result.Message;
            if (result.Success)
            {
                settings.LastZReportSuccessUtc = now;
                settings.LastZReportDate = LatestDate(
                    settings.LastZReportDate,
                    targetDate);
            }
        }

        if (result.Success)
            settings.LastSuccessUtc = now;
    }

    private static DateOnly LatestDate(DateOnly? existing, DateOnly candidate) =>
        existing.HasValue && existing.Value > candidate
            ? existing.Value
            : candidate;

    internal static void WriteDiagnostic(
        string businessName,
        bool success,
        string message) =>
        WriteLog(new PortalSyncRunResult(businessName, success, false, message));

    private static string CreateScheduledTaskXml(
        string executable,
        Guid storeConfigurationId,
        PortalSyncReportKind reportKind,
        TimeOnly runAt)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var userSid = WindowsIdentity.GetCurrent().User?.Value
                      ?? throw new InvalidOperationException(
                          "The current Windows user could not be identified for POS scheduling.");
        var start = DateTime.Today.Add(runAt.ToTimeSpan())
            .ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var document = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(ns + "Task",
                new XAttribute("version", "1.4"),
                new XElement(ns + "RegistrationInfo",
                    new XElement(ns + "Description",
                        $"Downloads and imports HISAB KITAB {ReportDisplayName(reportKind)} reports.")),
                new XElement(ns + "Triggers",
                    new XElement(ns + "CalendarTrigger",
                        new XElement(ns + "Repetition",
                            new XElement(ns + "Interval", "PT1H"),
                            new XElement(ns + "Duration", "PT12H"),
                            new XElement(ns + "StopAtDurationEnd", "false")),
                        new XElement(ns + "StartBoundary", start),
                        new XElement(ns + "Enabled", "true"),
                        new XElement(ns + "ScheduleByDay",
                            new XElement(ns + "DaysInterval", "1")))),
                new XElement(ns + "Principals",
                    new XElement(ns + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(ns + "UserId", userSid),
                        new XElement(ns + "LogonType", "InteractiveToken"),
                        new XElement(ns + "RunLevel", "LeastPrivilege"))),
                new XElement(ns + "Settings",
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(ns + "AllowHardTerminate", "true"),
                    new XElement(ns + "StartWhenAvailable", "true"),
                    new XElement(ns + "AllowStartOnDemand", "true"),
                    new XElement(ns + "Enabled", "true"),
                    new XElement(ns + "Hidden", "false"),
                    new XElement(ns + "RunOnlyIfIdle", "false"),
                    new XElement(ns + "WakeToRun", "true"),
                    new XElement(ns + "ExecutionTimeLimit", "PT2H"),
                    new XElement(ns + "Priority", "7"),
                    new XElement(ns + "RestartOnFailure",
                        new XElement(ns + "Interval", "PT15M"),
                        new XElement(ns + "Count", "3"))),
                new XElement(ns + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(ns + "Exec",
                        new XElement(ns + "Command", executable),
                        new XElement(ns + "Arguments",
                            $"--portal-sync-store {storeConfigurationId:D} " +
                            $"--portal-sync-report {ReportArgument(reportKind)}"),
                        new XElement(ns + "WorkingDirectory",
                            Path.GetDirectoryName(executable) ?? "")))));
        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static string ScheduledTaskName(
        Guid storeConfigurationId,
        PortalSyncReportKind reportKind) =>
        $"{LegacyScheduledTaskName(storeConfigurationId, reportKind)} - U{CurrentWindowsUserScope()}";

    private static string LegacyScheduledTaskName(
        Guid storeConfigurationId,
        PortalSyncReportKind reportKind) =>
        $"HISAB KITAB - Daily {ReportDisplayName(reportKind)} Sync - {storeConfigurationId:N}";

    private static string CurrentWindowsUserScope()
    {
        var identity = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(identity))
            identity = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(digest.AsSpan(0, 6));
    }

    private static string ReportDisplayName(PortalSyncReportKind reportKind) =>
        reportKind == PortalSyncReportKind.CashSalesSummary
            ? "Cash Sales Summary"
            : "Z Report";

    private static string ReportArgument(PortalSyncReportKind reportKind) =>
        reportKind == PortalSyncReportKind.CashSalesSummary
            ? "cash-sales"
            : "z-reports";

    private static void EnsureStartupFallback(string executable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(CurrentUserRunKey, writable: true)
                        ?? throw new InvalidOperationException(
                            "The current Windows user's startup settings could not be opened.");
        key.SetValue(
            StartupRunName,
            $"\"{executable}\" --portal-sync",
            RegistryValueKind.String);
    }

    private static void RemoveScheduledTask(string taskName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "/Delete", "/TN", taskName, "/F" }
            });
            process?.WaitForExit(10_000);
        }
        catch
        {
            // The in-app catch-up remains active even when Windows rejects
            // removal of an obsolete or disabled scheduled task.
        }
    }

    private static async Task<PortalSyncRunResult> RunStoreWithRetriesAsync(
        PortalStoreSyncSettings settings,
        IAppPaths paths,
        DateOnly targetDate,
        PortalSyncReportKind reportKind,
        bool visibleChrome,
        DateOnly? historicalStartDate,
        DateOnly? historicalEndDate,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        var maximumAttempts = 3;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                return await RunStoreAsync(
                    settings,
                    paths,
                    targetDate,
                    reportKind,
                    visibleChrome,
                    historicalStartDate,
                    historicalEndDate,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastError = exception;
                if (IsTemporaryPortalConnectionFailure(exception))
                    maximumAttempts = 5;
                if (attempt >= maximumAttempts)
                    break;
                await Task.Delay(
                    IsTemporaryPortalConnectionFailure(exception)
                        ? TimeSpan.FromSeconds(attempt * 30)
                        : TimeSpan.FromSeconds(attempt * 10),
                    cancellationToken);
            }
        }
        throw lastError ?? new InvalidOperationException("POS portal sync failed after repeated attempts.");
    }

    private static async Task<PortalSyncRunResult> RunStoreAsync(
        PortalStoreSyncSettings settings,
        IAppPaths paths,
        DateOnly targetDate,
        PortalSyncReportKind reportKind,
        bool visibleChrome,
        DateOnly? historicalStartDate,
        DateOnly? historicalEndDate,
        CancellationToken cancellationToken)
    {
        var chrome = FindGoogleChrome()
                     ?? throw new InvalidOperationException("Google Chrome is not installed.");
        var profile = PortalSyncSettingsStore.ProfileDirectory(settings.Id);
        var downloadDirectory = PortalSyncSettingsStore.DownloadDirectory(settings.Id);
        var targetStatus = reportKind == PortalSyncReportKind.CashSalesSummary
            ? await GetTargetStatusAsync(settings, targetDate, cancellationToken)
            : new PortalTargetStatus(false, 0);
        // The database is the accounting source of truth. A missing internal feed copy
        // must never cause the same store/date to be imported a second time.
        var needsCashSummary =
            reportKind == PortalSyncReportKind.CashSalesSummary &&
            !targetStatus.CashSummaryPresent;

        await using var db = CreateStoreDatabase(settings);
        await EnsureTargetDatabaseReadyAsync(db, settings.BusinessName);
        var dataStoreId = await ResolveDataStoreIdAsync(
            db,
            settings.BusinessName,
            cancellationToken);
        var latestImportedZBatch = reportKind == PortalSyncReportKind.ZReports
            ? await GetLatestImportedZBatchAsync(
                db,
                dataStoreId,
                cancellationToken)
            : null;
        var zResult = new ZReportImportOutcome(0, 0, 0);

        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(downloadDirectory);
        DeleteOldDownloads(downloadDirectory);
        var runDirectory = Path.Combine(downloadDirectory, $"run-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
        var cashDownloadDirectory = Path.Combine(runDirectory, "cash-sales-summary");
        var zDownloadDirectory = Path.Combine(runDirectory, "z-reports");
        Directory.CreateDirectory(cashDownloadDirectory);
        Directory.CreateDirectory(zDownloadDirectory);

        await CloseDedicatedProfileBrowserAsync(profile, cancellationToken);
        await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = !visibleChrome,
            ExecutablePath = chrome,
            UserDataDir = profile,
            DefaultViewport = null,
            Args =
            [
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-background-networking",
                $"--download-default-directory={downloadDirectory}"
            ]
        });

        var pages = await browser.PagesAsync();
        var page = pages.FirstOrDefault() ?? await browser.NewPageAsync();
        page.DefaultTimeout = 45_000;
        await ConfigureDownloadsAsync(page, downloadDirectory);
        string? cashSummaryPath = null;
        var rejectedZReportDetails = new List<string>();
        var lastProcessedZBatch = latestImportedZBatch;
        Exception? cashSummaryError = null;
        Exception? zReportsError = null;
        try
        {
            await page.GoToAsync(settings.PortalUrl, WaitUntilNavigation.Networkidle2);
            cancellationToken.ThrowIfCancellationRequested();

            await EnsureSignedInAsync(page, settings);
            if (needsCashSummary)
            {
                try
                {
                    await OpenCashAndSalesReportAsync(page);
                    var archiveDirectory =
                        PortalSyncSettingsStore.CashSalesSummaryArchiveDirectory(settings);
                    Directory.CreateDirectory(archiveDirectory);
                    var generated = await GenerateReportAsync(
                        browser,
                        page,
                        cashDownloadDirectory,
                        targetDate,
                        "Cash and Sales Summary",
                        Path.Combine(archiveDirectory, $"{targetDate:yyyy-MM-dd}.pdf"),
                        Path.Combine(archiveDirectory, $"{targetDate:yyyy-MM-dd}.png"),
                        cancellationToken);
                    if (string.IsNullOrWhiteSpace(generated.PdfPath))
                    {
                        throw new InvalidOperationException(
                            "The Cash and Sales Summary was displayed and its screenshot was saved, " +
                            "but AdventPOS did not provide a readable PDF." +
                            FormatExportError(generated.ExportError));
                    }
                    CashSalesSummaryImportCoordinator.Validate(
                        CashSalesSummaryPdfImporter.ImportAsync(
                                generated.PdfPath,
                                cancellationToken)
                            .GetAwaiter().GetResult());
                    cashSummaryPath = StoreCashSummaryFeedFile(
                        settings,
                        generated.PdfPath,
                        targetDate);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    cashSummaryError = exception;
                }
            }

            if (reportKind == PortalSyncReportKind.ZReports)
            {
                try
                {
                    await OpenCloseOutZReportAsync(page);
                    var portalBatches = await GetRecentBatchNumbersAsync(
                        page,
                        5_000,
                        cancellationToken);
                    if (portalBatches.Count == 0)
                    {
                        throw new InvalidOperationException(
                            "AdventPOS did not provide any batch numbers for the Close-Out Report (Z-Report).");
                    }

                    var numericPortalBatches = portalBatches
                        .Select(batch => new
                        {
                            Batch = batch,
                            Number = long.TryParse(
                                batch,
                                NumberStyles.None,
                                CultureInfo.InvariantCulture,
                                out var number)
                                ? number
                                : (long?)null
                        })
                        .Where(item => item.Number.HasValue)
                        .Select(item => new
                        {
                            item.Batch,
                            Number = item.Number!.Value
                        })
                        .ToList();

                    // Genuine AdventPOS-keyed rows are the primary cursor. Older
                    // databases may predate PosReportKey, so only accept a legacy
                    // numeric Shift No when that same number exists in this
                    // portal's batch list. Other shift systems can use much larger
                    // numeric identifiers and must not make Z sync appear caught up.
                    if (!latestImportedZBatch.HasValue)
                    {
                        latestImportedZBatch =
                            await GetLatestMatchingLegacyZBatchAsync(
                                db,
                                dataStoreId,
                                numericPortalBatches
                                    .Select(item => item.Number)
                                    .ToHashSet(),
                                cancellationToken);
                        lastProcessedZBatch = latestImportedZBatch;
                    }

                    // Once a trustworthy cursor exists, process every later
                    // portal batch in ascending order. A brand-new store
                    // bootstraps from the requested date instead of importing
                    // the entire portal history.
                    var historicalZBackfill =
                        historicalStartDate.HasValue &&
                        historicalEndDate.HasValue;
                    var candidateBatches = historicalZBackfill
                        ? numericPortalBatches
                            .OrderByDescending(item => item.Number)
                            .ToList()
                        : latestImportedZBatch.HasValue
                            ? numericPortalBatches
                                .Where(item => item.Number > latestImportedZBatch.Value)
                                .OrderBy(item => item.Number)
                                .ToList()
                            : numericPortalBatches
                                .OrderByDescending(item => item.Number)
                                .ToList();

                    foreach (var candidate in candidateBatches)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var batch = candidate.Batch;
                        var batchDownloadDirectory = Path.Combine(
                            zDownloadDirectory,
                            $"batch-{SafeFilePart(batch)}");
                        Directory.CreateDirectory(batchDownloadDirectory);
                        try
                        {
                        var safeBatch = SafeFilePart(batch);
                        var localPdfPath = Path.Combine(
                            batchDownloadDirectory,
                            $"{safeBatch}.pdf");
                        var localScreenshotPath = Path.Combine(
                            batchDownloadDirectory,
                            $"{safeBatch}.png");
                        var generated = await GenerateReportAsync(
                            browser,
                            page,
                            batchDownloadDirectory,
                            targetDate,
                            $"Close-Out Z Report batch {batch}",
                            localPdfPath,
                            localScreenshotPath,
                            cancellationToken,
                            batch,
                            browserPdfFirst: true);
                        ArchiveGeneratedZReport(settings, batch, generated);

                        PosReportData report;
                        var importPdf = false;
                        if (!string.IsNullOrWhiteSpace(generated.PdfPath))
                        {
                            try
                            {
                                report = await WaitForValidZReportBatchAsync(
                                    generated.PdfPath,
                                    batch,
                                    cancellationToken);
                                importPdf = true;
                            }
                            catch
                            {
                                report = ParseRenderedZReport(
                                    generated.RenderedText,
                                    batch);
                            }
                        }
                        else
                        {
                            report = ParseRenderedZReport(
                                generated.RenderedText,
                                batch);
                        }

                        var actualDate = report.ReportDate!.Value;
                        var yesterday = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
                        if (historicalZBackfill)
                        {
                            if (actualDate > historicalEndDate!.Value)
                                continue;
                            if (actualDate < historicalStartDate!.Value)
                                break;
                        }
                        else
                        {
                            if (latestImportedZBatch.HasValue && actualDate > yesterday)
                                break;
                            if (!latestImportedZBatch.HasValue && actualDate > targetDate)
                                continue;
                            if (!latestImportedZBatch.HasValue && actualDate < targetDate)
                                break;
                        }

                        ZReportImportOutcome result;
                        if (importPdf)
                        {
                            var feedPath = StoreZReportFeedFile(
                                settings,
                                generated.PdfPath!,
                                actualDate);
                            result = await ImportZReportsAsync(
                                db,
                                paths,
                                dataStoreId,
                                feedPath,
                                actualDate,
                                cancellationToken);
                        }
                        else
                        {
                            result = await ImportCapturedZReportAsync(
                                db,
                                paths,
                                dataStoreId,
                                new CapturedZReport(
                                    report,
                                    generated.ScreenshotPath),
                                actualDate,
                                cancellationToken);
                        }

                            zResult = new ZReportImportOutcome(
                                zResult.Total + result.Total,
                                zResult.Imported + result.Imported,
                                zResult.Updated + result.Updated);
                            await CashDropRollupService.SyncDateAsync(
                                db,
                                dataStoreId,
                                actualDate,
                                cancellationToken: cancellationToken);
                            lastProcessedZBatch = historicalZBackfill &&
                                                  lastProcessedZBatch.HasValue
                                ? Math.Max(lastProcessedZBatch.Value, candidate.Number)
                                : candidate.Number;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            rejectedZReportDetails.Add(
                                $"batch {batch}: {FirstSentence(exception.Message)}");
                            // Do not skip over an unreadable next batch. Stopping here
                            // keeps the database cursor honest so the next run retries it.
                            throw new InvalidOperationException(
                                $"AdventPOS Z-report catch-up stopped at next batch {batch}: " +
                                FirstSentence(exception.Message),
                                exception);
                        }
                    }

                    if (historicalZBackfill && zResult.Total == 0)
                    {
                        throw new InvalidOperationException(
                            $"No AdventPOS Close-Out batch had a Start Date from " +
                            $"{historicalStartDate:M/d/yyyy} through {historicalEndDate:M/d/yyyy}.");
                    }
                    if (!historicalZBackfill &&
                        !latestImportedZBatch.HasValue &&
                        zResult.Total == 0)
                    {
                        throw new InvalidOperationException(
                            $"No AdventPOS Close-Out batch had Start Date {targetDate:M/d/yyyy}. " +
                            "A first-time sync uses that date to establish the Shift Cash Drop batch cursor.");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    zReportsError = exception;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var diagnostic = await DescribePortalStateAsync(page);
            var screenshotPath = Path.Combine(
                AppBootstrap.AppDataPath,
                "Logs",
                $"pos-portal-sync-{settings.Id:N}.png");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
                await page.ScreenshotAsync(screenshotPath, new ScreenshotOptions { FullPage = true });
            }
            catch
            {
                // Diagnostics must never hide the original portal failure.
            }

            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(diagnostic)
                    ? exception.Message
                    : $"{exception.Message} Portal state: {diagnostic}",
                exception);
        }

        if (cashSummaryError is not null || zReportsError is not null)
        {
            var screenshotPath = Path.Combine(
                AppBootstrap.AppDataPath,
                "Logs",
                $"pos-portal-sync-{settings.Id:N}.png");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
                await page.ScreenshotAsync(screenshotPath, new ScreenshotOptions { FullPage = true });
            }
            catch
            {
                // A screenshot is diagnostic only; successfully downloaded feeds must still import.
            }
        }

        var cashSummaryImported = false;
        if (!string.IsNullOrWhiteSpace(cashSummaryPath))
        {
            var outcome = await CashSalesSummaryImportCoordinator.ImportAsync(
                db,
                paths,
                dataStoreId,
                cashSummaryPath,
                0,
                "Automatic POS Portal Sync",
                cancellationToken);
            cashSummaryImported = !outcome.Duplicate;
        }

        if (reportKind == PortalSyncReportKind.CashSalesSummary)
        {
            await CashDropRollupService.SyncDateAsync(
                db,
                dataStoreId,
                targetDate,
                cancellationToken: cancellationToken);

            if (cashSummaryError is not null)
                throw new InvalidOperationException(
                    $"Cash & Sales Summary sync did not complete for {targetDate:M/d/yyyy}. " +
                    cashSummaryError.Message,
                    cashSummaryError);

            var finalStatus = await GetTargetStatusAsync(
                settings,
                targetDate,
                cancellationToken);
            if (!finalStatus.CashSummaryPresent)
            {
                throw new InvalidOperationException(
                    $"Cash & Sales Summary sync did not complete for {targetDate:M/d/yyyy}. " +
                    "The report is not present in this store database.");
            }

            return new PortalSyncRunResult(
                settings.BusinessName,
                true,
                cashSummaryImported,
                $"Cash & Sales Summary sync for {targetDate:M/d/yyyy}: " +
                $"{(cashSummaryImported ? "report imported" : "report already present")}. " +
                "Cash drop was refreshed from the matching Shift Cash Drop batches.");
        }

        if (zReportsError is not null)
        {
            var cursorDetails = rejectedZReportDetails.Count == 0
                ? ""
                : $" Batch cursor details: {string.Join("; ", rejectedZReportDetails.Take(6))}";
            throw new InvalidOperationException(
                $"Z Report sync did not complete. {zReportsError.Message}{cursorDetails}",
                zReportsError);
        }

        var zReportDescription = zResult.Total > 0
            ? $"{zResult.Imported} new and {zResult.Updated} updated Z-report shift(s) imported " +
              $"sequentially through batch {lastProcessedZBatch}"
            : latestImportedZBatch.HasValue
                ? $"Shift Cash Drop already caught up after batch {latestImportedZBatch.Value}"
                : "Shift Cash Drop cursor established";
        return new PortalSyncRunResult(
            settings.BusinessName,
            true,
            zResult.Imported > 0,
            historicalStartDate.HasValue && historicalEndDate.HasValue
                ? $"Z Report historical sync {historicalStartDate:M/d/yyyy} - " +
                  $"{historicalEndDate:M/d/yyyy}: {zReportDescription}."
                : $"Z Report sync: {zReportDescription}.");
    }

    private static async Task CloseDedicatedProfileBrowserAsync(
        string profileDirectory,
        CancellationToken cancellationToken)
    {
        var portFile = Path.Combine(profileDirectory, "DevToolsActivePort");
        if (!File.Exists(portFile))
            return;

        try
        {
            var lines = await File.ReadAllLinesAsync(portFile, cancellationToken);
            if (lines.Length == 0 ||
                !int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            {
                return;
            }

            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(3)
            };
            var json = await http.GetStringAsync(
                $"http://127.0.0.1:{port}/json/version",
                cancellationToken);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(
                    "webSocketDebuggerUrl",
                    out var endpointElement))
            {
                return;
            }

            var endpoint = endpointElement.GetString();
            if (string.IsNullOrWhiteSpace(endpoint))
                return;

            await using var runningBrowser = await Puppeteer.ConnectAsync(
                new ConnectOptions { BrowserWSEndpoint = endpoint });
            await runningBrowser.CloseAsync();

            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (File.Exists(portFile) && DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(250, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A stale DevTools port file is harmless. Puppeteer will provide
            // the actionable launch error if the dedicated profile is still locked.
        }
    }

    private static string FirstSentence(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "report could not be validated";
        var compact = string.Join(" ", value
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim()));
        var separator = compact.IndexOf(". ", StringComparison.Ordinal);
        return separator < 0 ? compact.TrimEnd('.') : compact[..separator].TrimEnd('.');
    }

    private static string FormatExportError(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : $" Portal export error: {value}";

    private static async Task EnsureSignedInAsync(IPage page, PortalStoreSyncSettings settings)
    {
        var login = await page.QuerySelectorAsync("#txtLoginUserName");
        if (login is not null && await IsVisibleAsync(page, "#txtLoginUserName"))
        {
            if (string.IsNullOrWhiteSpace(settings.PortalEmail) ||
                string.IsNullOrWhiteSpace(settings.PortalPassword))
                throw new InvalidOperationException(
                    "The AdventPOS session expired and protected portal credentials were not saved. Open POS Auto Sync Setup.");
            await ReplaceValueAsync(page, "#txtLoginUserName", settings.PortalEmail);
            await ReplaceValueAsync(page, "#txtLoginPassword", settings.PortalPassword);
            await page.EvaluateExpressionAsync(
                "document.querySelector('#isRememberMe').checked=true; ValidateUser();");
        }

        await WaitForOwnerLoginAsync(page, TimeSpan.FromSeconds(45));

        if (await IsVisibleAsync(page, "#StoreSelectionModal"))
        {
            await SelectPortalStoreAsync(page, settings.PortalStoreName);

            await WaitUntilAsync(page,
                async () =>
                    await IsVisibleAsync(page, "#txtFinalLoginUserName") &&
                    await IsVisibleAsync(page, "#txtFinalLoginPassword") &&
                    await IsVisibleAsync(page, "#btnFinalStepToLogin"),
                TimeSpan.FromSeconds(45),
                "AdventPOS selected the store, but its store-user login controls did not load.");
            if (!string.IsNullOrWhiteSpace(settings.StoreUserName))
                await ReplaceValueAsync(page, "#txtFinalLoginUserName", settings.StoreUserName);
            if (!string.IsNullOrWhiteSpace(settings.StorePassword))
                await ReplaceValueAsync(page, "#txtFinalLoginPassword", settings.StorePassword);

            var storeHomeNavigation = page.WaitForNavigationAsync(new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.Networkidle2],
                Timeout = 60_000
            });
            await page.EvaluateExpressionAsync(
                "document.querySelector('#chkRememberPwd').checked=true; " +
                "document.querySelector('#btnFinalStepToLogin').click();");
            try
            {
                await storeHomeNavigation;
            }
            catch (TimeoutException)
            {
                // The final readiness check below provides the actionable
                // message. Some portal responses retain background requests
                // long enough that Networkidle2 reaches its timeout.
            }
        }

        await WaitUntilAsync(page,
            () => IsPortalHomeReadyAsync(page),
            TimeSpan.FromSeconds(60),
            "AdventPOS did not reach the store home page. A verification code, CAPTCHA, or password update may require attention.");
    }

    private static async Task WaitForOwnerLoginAsync(IPage page, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (await IsVisibleAsync(page, "#StoreSelectionModal") ||
                await IsPortalHomeReadyAsync(page))
            {
                return;
            }

            var portalMessage = await GetVisiblePortalDialogTextAsync(page);
            if (IsTemporaryPortalConnectionFailure(portalMessage))
            {
                await DismissVisiblePortalDialogAsync(page);
                throw new InvalidOperationException(
                    "AdventPOS temporarily reported that its server connection is not open. " +
                    "HISAB KITAB will retry the login automatically.");
            }

            await Task.Delay(500);
        }

        throw new InvalidOperationException(
            "The AdventPOS login did not complete. Complete any verification request in POS Auto Sync Setup.");
    }

    private static Task<string> GetVisiblePortalDialogTextAsync(IPage page) =>
        page.EvaluateExpressionAsync<string>(
            @"(() => {
                const visible = element => {
                    if (!element) return false;
                    const style = window.getComputedStyle(element);
                    const bounds = element.getBoundingClientRect();
                    return style.display !== 'none' &&
                           style.visibility !== 'hidden' &&
                           style.opacity !== '0' &&
                           bounds.width > 0 &&
                           bounds.height > 0;
                };
                const dialogs = Array.from(document.querySelectorAll(
                    '.modal.show, .swal2-container, .bootbox, [role=""dialog""]'));
                const dialog = dialogs.find(visible);
                return dialog ? (dialog.innerText || '').replace(/\s+/g, ' ').trim() : '';
            })()");

    private static Task DismissVisiblePortalDialogAsync(IPage page) =>
        page.EvaluateExpressionAsync(
            @"(() => {
                const visible = element => {
                    if (!element) return false;
                    const style = window.getComputedStyle(element);
                    const bounds = element.getBoundingClientRect();
                    return style.display !== 'none' &&
                           style.visibility !== 'hidden' &&
                           bounds.width > 0 &&
                           bounds.height > 0;
                };
                const buttons = Array.from(document.querySelectorAll(
                    '.modal.show button, .swal2-container button, .bootbox button, [role=""dialog""] button'));
                const button = buttons.find(element => visible(element) &&
                    /^(ok|close|dismiss)$/i.test((element.textContent || '').trim()));
                if (button) button.click();
            })()");

    private static bool IsTemporaryPortalConnectionFailure(Exception exception) =>
        IsTemporaryPortalConnectionFailure(exception.Message) ||
        (exception.InnerException is not null &&
         IsTemporaryPortalConnectionFailure(exception.InnerException));

    private static bool IsTemporaryPortalConnectionFailure(string? message) =>
        !string.IsNullOrWhiteSpace(message) &&
        (message.Contains("connection is not open", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("connection was not open", StringComparison.OrdinalIgnoreCase));

    private static async Task SelectPortalStoreAsync(IPage page, string configuredStoreName)
    {
        if (string.IsNullOrWhiteSpace(configuredStoreName))
            throw new InvalidOperationException(
                "Enter the AdventPOS store name in POS Auto Sync Setup.");

        await WaitUntilAsync(page,
            () => page.EvaluateFunctionAsync<bool>(
                @"configuredName => {
                    const normalize = value => (value || '')
                        .normalize('NFKD')
                        .replace(/[\u0300-\u036f]/g, '')
                        .replace(/[^a-z0-9]/gi, '')
                        .toLowerCase();
                    const select = document.querySelector('#cbxSelectStore');
                    if (!select) return false;
                    const style = window.getComputedStyle(select);
                    const bounds = select.getBoundingClientRect();
                    if (style.display === 'none' || style.visibility === 'hidden' ||
                        bounds.width <= 0 || bounds.height <= 0) return false;
                    const wanted = normalize(configuredName);
                    return Array.from(select.options).some(option => {
                        const actual = normalize(option.textContent);
                        return option.value !== '-1' &&
                               (actual === wanted || actual.includes(wanted) || wanted.includes(actual));
                    });
                }",
                configuredStoreName),
            TimeSpan.FromSeconds(45),
            $"The configured AdventPOS store '{configuredStoreName}' was not found in the Store Selection list.");

        var selectedName = await page.EvaluateFunctionAsync<string>(
            @"configuredName => {
                const normalize = value => (value || '')
                    .normalize('NFKD')
                    .replace(/[\u0300-\u036f]/g, '')
                    .replace(/[^a-z0-9]/gi, '')
                    .toLowerCase();
                const select = document.querySelector('#cbxSelectStore');
                const wanted = normalize(configuredName);
                const options = Array.from(select.options).filter(option => option.value !== '-1');
                const option =
                    options.find(candidate => normalize(candidate.textContent) === wanted) ||
                    options.find(candidate => {
                        const actual = normalize(candidate.textContent);
                        return actual.includes(wanted) || wanted.includes(actual);
                    });
                if (!option) return '';

                select.disabled = false;
                select.value = option.value;
                if (typeof window.LoadStoreUsers === 'function')
                    window.LoadStoreUsers(option.value);
                else {
                    select.dispatchEvent(new Event('input', { bubbles: true }));
                    select.dispatchEvent(new Event('change', { bubbles: true }));
                }

                // The portal can show a remembered-user tile after store
                // selection. The unattended sync always uses the protected
                // credentials saved in HISAB KITAB, so open the credential
                // controls explicitly.
                if (typeof window.UserAnotherAccount_Clicked === 'function')
                    window.UserAnotherAccount_Clicked();

                return (option.textContent || '').trim();
            }",
            configuredStoreName);

        if (string.IsNullOrWhiteSpace(selectedName))
            throw new InvalidOperationException(
                $"AdventPOS did not select the configured store '{configuredStoreName}'.");
    }

    private static async Task OpenCashAndSalesReportAsync(IPage page)
    {
        if (!await IsVisibleAsync(page, "#ReportModal"))
        {
            var opened = await page.EvaluateExpressionAsync<bool>(
                @"(() => {
                    // Admin Reports is a Bootstrap modal in AdventPOS. Opening
                    // the modal directly is more reliable than depending on
                    // changing sidebar markup, and it invokes the portal's own
                    // report date/filter loading handlers.
                    const reportModal = document.querySelector('#ReportModal');
                    if (reportModal && window.jQuery && typeof window.jQuery.fn.modal === 'function') {
                        window.jQuery(reportModal).modal('show');
                        return true;
                    }

                    const normalize = value => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
                    const isVisible = element => {
                        if (!element) return false;
                        const style = window.getComputedStyle(element);
                        const bounds = element.getBoundingClientRect();
                        return style.display !== 'none' &&
                               style.visibility !== 'hidden' &&
                               bounds.width > 0 &&
                               bounds.height > 0;
                    };
                    const elements = Array.from(
                        document.querySelectorAll('a,button,[role=""button""],li,span'));
                    // Admin Reports is a submenu item. It is intentionally
                    // hidden until the Report menu is hovered, but a
                    // programmatic click still executes its portal handler.
                    const adminReports = Array.from(
                        document.querySelectorAll('a,button,[role=""button""]')).find(element =>
                        normalize(element.textContent) === 'admin reports');
                    if (adminReports) {
                        adminReports.click();
                        return true;
                    }
                    const candidate =
                        elements.find(element =>
                            isVisible(element) &&
                            (normalize(element.textContent) === 'reports' ||
                             normalize(element.textContent) === 'report'));
                    if (!candidate) return false;
                    const clickable = candidate.closest('a,button,[role=""button""]') || candidate;
                    clickable.click();
                    return true;
                })()");

            if (!opened)
                throw new InvalidOperationException(
                    "The AdventPOS Admin Reports control was not found after store login.");
        }

        await WaitUntilAsync(page,
            async () =>
                await HasCashReportFunctionsAsync(page) &&
                await IsVisibleAsync(page, "#ReportModal"),
            TimeSpan.FromSeconds(60),
            "The AdventPOS Admin Reports window did not finish loading.");

        var selected = await page.EvaluateFunctionAsync<bool>(
            @"() => {
                if (!Array.isArray(window.SalesReports_Enum)) return false;
                const salesTab = document.querySelector(
                    'ul#ULReportTabs li[data-id=""0""] a, ul#ULReportTabs li[data-Id=""0""] a');
                if (salesTab) {
                    if (window.jQuery && typeof window.jQuery.fn.tab === 'function')
                        window.jQuery(salesTab).tab('show');
                    else
                        salesTab.click();
                }

                const normalize = value => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
                const index = window.SalesReports_Enum.findIndex(report =>
                    report && (report.Name === 'CashAndSales' ||
                               normalize(report.LongName) === 'cash and sales summary'));
                if (index < 0) return false;
                const selector = `#Div_tbRptSales [data-Id='${index}'], #Div_tbRptSales [data-id='${index}']`;
                const item = document.querySelector(selector);
                if (!item || typeof window.lstSales_SelectedIndexChanged !== 'function') return false;
                window.lstSales_SelectedIndexChanged(item);
                return true;
            }");
        if (!selected)
            throw new InvalidOperationException("Cash and Sales Summary is not available for this AdventPOS account.");
    }

    private static async Task OpenCloseOutZReportAsync(IPage page)
    {
        if (!await IsVisibleAsync(page, "#ReportModal"))
        {
            var reopened = await page.EvaluateExpressionAsync<bool>(
                @"(() => {
                    const reportModal = document.querySelector('#ReportModal');
                    if (reportModal && window.jQuery && typeof window.jQuery.fn.modal === 'function') {
                        window.jQuery(reportModal).modal('show');
                        return true;
                    }

                    const normalize = value => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
                    const adminReports = Array.from(
                        document.querySelectorAll('a,button,[role=""button""]')).find(element =>
                        normalize(element.textContent) === 'admin reports');
                    if (!adminReports) return false;
                    adminReports.click();
                    return true;
                })()");

            if (!reopened)
                throw new InvalidOperationException(
                    "The AdventPOS Admin Reports control could not be reopened for the Close-Out Z Report.");
        }

        await WaitUntilAsync(page,
            async () =>
                await HasCashReportFunctionsAsync(page) &&
                await IsVisibleAsync(page, "#ReportModal"),
            TimeSpan.FromSeconds(30),
            "The AdventPOS Admin Reports window was not available for the Close-Out Z Report.");

        var selected = await page.EvaluateFunctionAsync<bool>(
            @"() => {
                if (!Array.isArray(window.SalesReports_Enum)) return false;
                const salesTab = document.querySelector(
                    'ul#ULReportTabs li[data-id=""0""] a, ul#ULReportTabs li[data-Id=""0""] a');
                if (salesTab) {
                    if (window.jQuery && typeof window.jQuery.fn.tab === 'function')
                        window.jQuery(salesTab).tab('show');
                    else
                        salesTab.click();
                }

                const normalize = value => (value || '')
                    .replace(/[^a-z0-9]/gi, '')
                    .toLowerCase();
                const index = window.SalesReports_Enum.findIndex(report => {
                    if (!report) return false;
                    const name = normalize(report.Name);
                    const longName = normalize(report.LongName);
                    return name === 'zreport' ||
                           name === 'closeoutreport' ||
                           longName === 'closeoutreportzreport';
                });
                if (index < 0) return false;
                const selector = `#Div_tbRptSales [data-Id='${index}'], #Div_tbRptSales [data-id='${index}']`;
                const item = document.querySelector(selector);
                if (!item || typeof window.lstSales_SelectedIndexChanged !== 'function') return false;
                window.lstSales_SelectedIndexChanged(item);
                return true;
            }");
        if (!selected)
            throw new InvalidOperationException(
                "Close-Out Report (Z-Report) is not available in AdventPOS Admin Reports for this store.");

        await WaitUntilAsync(
            page,
            () => page.EvaluateExpressionAsync<bool>(
                BatchSelectorAvailableScript),
            TimeSpan.FromSeconds(30),
            "The AdventPOS Close-Out Report opened, but its Batch selector did not become ready.");
    }

    private const string BatchSelectorAvailableScript =
        @"(() => {
            const visible = element => {
                if (!element) return false;
                const style = window.getComputedStyle(element);
                const rect = element.getBoundingClientRect();
                return style.display !== 'none' && style.visibility !== 'hidden' &&
                       rect.width > 0 && rect.height > 0;
            };
            const numeric = option => /^\d+$/.test((option.textContent || '').trim());
            const selects = Array.from(document.querySelectorAll('select')).filter(visible);
            const describe = select => {
                const labels = select.labels
                    ? Array.from(select.labels).map(label => label.textContent || '').join(' ')
                    : '';
                const container = select.closest('tr,.row,.form-group,.input-group,[class*=""selection""]');
                return [
                    select.id || '',
                    select.name || '',
                    select.getAttribute('aria-label') || '',
                    labels,
                    container ? container.textContent || '' : ''
                ].join(' ').toLowerCase();
            };
            return selects.some(select =>
                describe(select).includes('batch') &&
                Array.from(select.options || []).some(numeric));
        })()";

    private static async Task<IReadOnlyList<string>> GetRecentBatchNumbersAsync(
        IPage page,
        int maximum,
        CancellationToken cancellationToken)
    {
        await WaitUntilAsync(
            page,
            () => page.EvaluateExpressionAsync<bool>(BatchSelectorAvailableScript),
            TimeSpan.FromSeconds(30),
            "AdventPOS did not load the batch list for the Close-Out Z Report.");
        cancellationToken.ThrowIfCancellationRequested();

        var batches = await page.EvaluateFunctionAsync<string[]>(
            @"maximum => {
                const visible = element => {
                    if (!element) return false;
                    const style = window.getComputedStyle(element);
                    const rect = element.getBoundingClientRect();
                    return style.display !== 'none' && style.visibility !== 'hidden' &&
                           rect.width > 0 && rect.height > 0;
                };
                const describe = select => {
                    const labels = select.labels
                        ? Array.from(select.labels).map(label => label.textContent || '').join(' ')
                        : '';
                    const container = select.closest('tr,.row,.form-group,.input-group,[class*=""selection""]');
                    return [
                        select.id || '',
                        select.name || '',
                        select.getAttribute('aria-label') || '',
                        labels,
                        container ? container.textContent || '' : ''
                    ].join(' ').toLowerCase();
                };
                const select = Array.from(document.querySelectorAll('select'))
                    .filter(visible)
                    .find(candidate =>
                        describe(candidate).includes('batch') &&
                        Array.from(candidate.options || [])
                            .some(option => /^\d+$/.test((option.textContent || '').trim())));
                if (!select) return [];
                return Array.from(new Set(
                        Array.from(select.options || [])
                            .map(option => (option.textContent || '').trim())
                            .filter(value => /^\d+$/.test(value))))
                    .sort((left, right) => Number(right) - Number(left))
                    .slice(0, maximum);
            }",
            maximum);
        return batches;
    }

    private static async Task SelectBatchAsync(
        IPage page,
        string batch,
        CancellationToken cancellationToken)
    {
        var selected = await page.EvaluateFunctionAsync<bool>(
            @"batch => {
                const visible = element => {
                    if (!element) return false;
                    const style = window.getComputedStyle(element);
                    const rect = element.getBoundingClientRect();
                    return style.display !== 'none' && style.visibility !== 'hidden' &&
                           rect.width > 0 && rect.height > 0;
                };
                const describe = select => {
                    const labels = select.labels
                        ? Array.from(select.labels).map(label => label.textContent || '').join(' ')
                        : '';
                    const container = select.closest('tr,.row,.form-group,.input-group,[class*=""selection""]');
                    return [
                        select.id || '',
                        select.name || '',
                        select.getAttribute('aria-label') || '',
                        labels,
                        container ? container.textContent || '' : ''
                    ].join(' ').toLowerCase();
                };
                const select = Array.from(document.querySelectorAll('select'))
                    .filter(visible)
                    .find(candidate =>
                        describe(candidate).includes('batch') &&
                        Array.from(candidate.options || [])
                            .some(option => (option.textContent || '').trim() === batch));
                if (!select) return false;
                const option = Array.from(select.options || [])
                    .find(candidate => (candidate.textContent || '').trim() === batch);
                if (!option) return false;
                select.value = option.value;
                option.selected = true;
                if (window.jQuery)
                    window.jQuery(select).val(option.value).trigger('change');
                else {
                    select.dispatchEvent(new Event('input', { bubbles: true }));
                    select.dispatchEvent(new Event('change', { bubbles: true }));
                }
                return (select.options[select.selectedIndex]?.textContent || '').trim() === batch;
            }",
            batch);
        if (!selected)
        {
            throw new InvalidOperationException(
                $"AdventPOS batch {batch} was no longer available in the Close-Out report list.");
        }

        await Task.Delay(1000, cancellationToken);
        await WaitUntilAsync(
            page,
            () => page.EvaluateFunctionAsync<bool>(
                @"batch => Array.from(document.querySelectorAll('select')).some(select =>
                    select.offsetParent !== null &&
                    (select.options[select.selectedIndex]?.textContent || '').trim() === batch)",
                batch),
            TimeSpan.FromSeconds(10),
            $"AdventPOS did not retain batch {batch} in the Close-Out report selector.");
    }

    private static async Task<GeneratedPortalReport> GenerateReportAsync(
        IBrowser browser,
        IPage page,
        string downloadDirectory,
        DateOnly targetDate,
        string reportName,
        string archivedPdfPath,
        string screenshotPath,
        CancellationToken cancellationToken,
        string? batchNumber = null,
        bool browserPdfFirst = false)
    {
        var dateText = targetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await page.EvaluateFunctionAsync(
            @"dateText => {
                const period = document.querySelector('#cbxReportPeriod');
                // Use the custom period with an explicit app-calculated date.
                // AdventPOS can run in a different server timezone, so its
                // native Yesterday option can otherwise move the report ahead
                // by one day late in the evening.
                if (period) {
                    period.value = '13';
                    period.dispatchEvent(new Event('change', { bubbles: true }));
                }
                const start = document.querySelector('#dtRTPStartDate');
                const end = document.querySelector('#dtRTPEndDate');
                for (const input of [start, end]) {
                    if (!input) continue;
                    input.disabled = false;
                    if (window.jQuery && typeof window.jQuery.fn.datepicker === 'function')
                        window.jQuery(input).datepicker('setDate', dateText);
                    input.value = dateText;
                    input.dispatchEvent(new Event('input', { bubbles: true }));
                    input.dispatchEvent(new Event('change', { bubbles: true }));
                }
            }",
            dateText);

        await WaitUntilAsync(page,
            () => page.EvaluateFunctionAsync<bool>(
                @"dateText => {
                    const reportType = document.querySelector('#ReportTypeValue_0');
                    const start = document.querySelector('#dtRTPStartDate');
                    const end = document.querySelector('#dtRTPEndDate');
                    const period = document.querySelector('#cbxReportPeriod');
                    return !!reportType && !!(reportType.textContent || '').trim() &&
                           !!start && start.value === dateText &&
                           !!end && end.value === dateText &&
                           !!period && period.value === '13';
                }",
                dateText),
            TimeSpan.FromSeconds(30),
            $"AdventPOS did not finish selecting {reportName} for {targetDate:M/d/yyyy}.");

        if (!string.IsNullOrWhiteSpace(batchNumber))
            await SelectBatchAsync(page, batchNumber, cancellationToken);

        // AdventPOS sometimes reuses an earlier report popup. Close stale
        // report viewers first so every batch starts from a known page.
        foreach (var stalePage in (await browser.PagesAsync()).Where(candidate =>
                     !ReferenceEquals(candidate, page) &&
                     candidate.Url.Contains(
                         "/Report/ViewReportResult",
                         StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                await stalePage.CloseAsync();
            }
            catch
            {
                // A stale popup may already be closing.
            }
        }

        await page.EvaluateExpressionAsync("ViewReport(true, true, false);");
        IPage? reportPage = null;
        var pageDeadline = DateTime.UtcNow.AddSeconds(35);
        while (DateTime.UtcNow < pageDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reportPage = page.Url.Contains(
                "/Report/ViewReportResult",
                StringComparison.OrdinalIgnoreCase)
                ? page
                : (await browser.PagesAsync()).LastOrDefault(candidate =>
                    candidate.Url.Contains(
                        "/Report/ViewReportResult",
                        StringComparison.OrdinalIgnoreCase));
            if (reportPage is not null)
                break;
            await Task.Delay(500, cancellationToken);
        }

        if (reportPage is null)
        {
            throw new InvalidOperationException(
                $"AdventPOS did not open the {reportName} report viewer.");
        }
        if (!string.IsNullOrWhiteSpace(batchNumber) &&
            !ReportPageMatchesBatch(reportPage.Url, batchNumber))
        {
            throw new InvalidOperationException(
                $"AdventPOS opened a report viewer for a different batch instead of batch {batchNumber}.");
        }

        try
        {
            try
            {
                await reportPage.WaitForNetworkIdleAsync(new WaitForNetworkIdleOptions
                {
                    IdleTime = 1000,
                    Timeout = 30_000
                });
            }
            catch
            {
                // ActiveReports can keep a background connection open.
            }

            await Task.Delay(1500, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
            await reportPage.ScreenshotAsync(
                screenshotPath,
                new ScreenshotOptions { FullPage = true });

            var renderedText = await reportPage.EvaluateExpressionAsync<string>(
                "(document.body && (document.body.innerText || document.body.textContent)) || ''");

            string? exportError = null;
            if (browserPdfFirst)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(archivedPdfPath)!);
                    await reportPage.PdfAsync(
                        archivedPdfPath,
                        new PdfOptions
                        {
                            PrintBackground = true,
                            Format = PaperFormat.Letter
                        });
                    if (File.Exists(archivedPdfPath) &&
                        new FileInfo(archivedPdfPath).Length > 0)
                    {
                        return new GeneratedPortalReport(
                            archivedPdfPath,
                            screenshotPath,
                            renderedText,
                            null);
                    }
                    exportError = "Chrome produced an empty Z-report PDF.";
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    exportError = $"Chrome PDF capture: {FirstSentence(exception.Message)}";
                }
            }

            try
            {
                await ConfigureDownloadsAsync(reportPage, downloadDirectory);
                await RequestActiveReportsPdfExportAsync(reportPage);
                var downloadDeadline = DateTime.UtcNow.AddSeconds(45);
                while (DateTime.UtcNow < downloadDeadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var downloaded = FindCompletedPdf(downloadDirectory);
                    if (downloaded is not null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(archivedPdfPath)!);
                        File.Copy(downloaded, archivedPdfPath, true);
                        return new GeneratedPortalReport(
                            archivedPdfPath,
                            screenshotPath,
                            renderedText,
                            null);
                    }
                    await Task.Delay(750, cancellationToken);
                }
                exportError = "The portal's PDF export timed out.";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                exportError = FirstSentence(exception.Message);
            }

            // If the ActiveReports download control fails, ask Chrome to print
            // the already-rendered report page to PDF. This preserves a usable
            // report artifact on portal versions whose Export UI changed.
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(archivedPdfPath)!);
                await reportPage.PdfAsync(
                    archivedPdfPath,
                    new PdfOptions
                    {
                        PrintBackground = true,
                        Format = PaperFormat.Letter
                    });
                if (File.Exists(archivedPdfPath) &&
                    new FileInfo(archivedPdfPath).Length > 0)
                {
                    return new GeneratedPortalReport(
                        archivedPdfPath,
                        screenshotPath,
                        renderedText,
                        exportError);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                exportError = string.IsNullOrWhiteSpace(exportError)
                    ? FirstSentence(exception.Message)
                    : $"{exportError}; Chrome PDF fallback: {FirstSentence(exception.Message)}";
            }

            // The rendered page and screenshot remain useful even when the
            // third-party report viewer refuses to download its PDF.
            return new GeneratedPortalReport(
                null,
                screenshotPath,
                renderedText,
                exportError);
        }
        finally
        {
            if (!ReferenceEquals(reportPage, page))
            {
                try
                {
                    await reportPage.CloseAsync();
                }
                catch
                {
                    // The portal may close its report popup after export.
                }
            }
        }
    }

    private static bool ReportPageMatchesBatch(string reportUrl, string batchNumber)
    {
        if (!Uri.TryCreate(reportUrl, UriKind.Absolute, out var uri))
            return false;

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var batchId = query["BatchID"];
        var batchName = query["BatchName"];
        return string.Equals(batchId, batchNumber, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(batchName, batchNumber, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task RequestActiveReportsPdfExportAsync(IPage reportPage)
    {
        await WaitUntilAsync(
            reportPage,
            () => reportPage.EvaluateExpressionAsync<bool>(
                @"Array.from(document.querySelectorAll('button'))
                    .some(button => button.title === 'Export' &&
                                    !button.closest('.arjs-export-panel'))"),
            TimeSpan.FromSeconds(30),
            "The AdventPOS report opened, but its Export control did not become ready.");

        await reportPage.EvaluateExpressionAsync(
            @"Array.from(document.querySelectorAll('button'))
                .find(button => button.title === 'Export' &&
                                !button.closest('.arjs-export-panel'))
                .click()");

        await WaitUntilAsync(
            reportPage,
            () => IsVisibleAsync(reportPage, ".arjs-export-panel"),
            TimeSpan.FromSeconds(15),
            "The AdventPOS report Export panel did not open.");

        await reportPage.EvaluateExpressionAsync(
            "document.querySelector('.arjs-export-panel .gc-dd button').click()");
        await WaitUntilAsync(
            reportPage,
            () => reportPage.EvaluateExpressionAsync<bool>(
                @"Array.from(document.querySelectorAll('button.gc-dd-menu__item'))
                    .some(button => (button.title || button.textContent || '')
                        .toLowerCase().includes('pdf'))"),
            TimeSpan.FromSeconds(15),
            "PDF was not available in the AdventPOS Export format list.");

        await reportPage.EvaluateExpressionAsync(
            @"Array.from(document.querySelectorAll('button.gc-dd-menu__item'))
                .find(button => (button.title || button.textContent || '')
                    .toLowerCase().includes('pdf'))
                .click()");
        await WaitUntilAsync(
            reportPage,
            () => reportPage.EvaluateExpressionAsync<bool>(
                @"Array.from(document.querySelectorAll('.arjs-export-panel button'))
                    .some(button => button.title === 'Export')"),
            TimeSpan.FromSeconds(15),
            "The AdventPOS PDF Export action did not become ready.");

        await reportPage.EvaluateExpressionAsync(
            @"Array.from(document.querySelectorAll('.arjs-export-panel button'))
                .find(button => button.title === 'Export')
                .click()");
    }

    private static string StoreCashSummaryFeedFile(
        PortalStoreSyncSettings settings,
        string sourcePath,
        DateOnly targetDate)
    {
        var directory = PortalSyncSettingsStore.CashSalesSummaryFeedDirectory(settings.Id);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"{targetDate:yyyy-MM-dd}.pdf");
        File.Copy(sourcePath, destination, true);
        return destination;
    }

    private static string StoreZReportFeedFile(
        PortalStoreSyncSettings settings,
        string sourcePath,
        DateOnly targetDate)
    {
        var matchingBatches = new PosReportImportService()
            .ImportZReports(sourcePath)
            .Where(report =>
                report.ReportDate == targetDate &&
                !string.IsNullOrWhiteSpace(report.ShiftOrBatch))
            .Select(report => SafeFilePart(report.ShiftOrBatch!))
            .Where(batch => !string.IsNullOrWhiteSpace(batch))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(batch => batch, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (matchingBatches.Count == 0)
            throw new InvalidOperationException(
                $"No Z-report batch for {targetDate:M/d/yyyy} was available to store.");

        var directory = PortalSyncSettingsStore.ZReportFeedDirectory(settings.Id);
        Directory.CreateDirectory(directory);
        var batchLabel = matchingBatches.Count == 1
            ? $"Batch-{matchingBatches[0]}"
            : $"Batches-{string.Join("-", matchingBatches)}";
        var destination = Path.Combine(
            directory,
            $"{batchLabel}_{targetDate:yyyy-MM-dd}.pdf");
        File.Copy(sourcePath, destination, true);
        return destination;
    }

    private static string SafeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value
                .Trim()
                .Where(character => !invalid.Contains(character))
                .Select(character => char.IsWhiteSpace(character) ? '-' : character)
                .ToArray())
            .Trim('-', '.', ' ');
    }

    private static void ArchiveGeneratedZReport(
        PortalStoreSyncSettings settings,
        string batch,
        GeneratedPortalReport generated)
    {
        var archiveDirectory = PortalSyncSettingsStore.ZReportArchiveDirectory(settings);
        Directory.CreateDirectory(archiveDirectory);
        var safeBatch = SafeFilePart(batch);
        if (!string.IsNullOrWhiteSpace(generated.PdfPath) &&
            File.Exists(generated.PdfPath))
        {
            File.Copy(
                generated.PdfPath,
                Path.Combine(archiveDirectory, $"{safeBatch}.pdf"),
                true);
        }
        if (File.Exists(generated.ScreenshotPath))
        {
            File.Copy(
                generated.ScreenshotPath,
                Path.Combine(archiveDirectory, $"{safeBatch}.png"),
                true);
        }
    }

    private static async Task<PosReportData> WaitForValidZReportBatchAsync(
        string path,
        string expectedBatch,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return ReadZReportBatch(path, expectedBatch);
            }
            catch (Exception exception)
            {
                lastError = exception;
                if (attempt < 9)
                    await Task.Delay(300, cancellationToken);
            }
        }

        throw lastError ??
              new InvalidOperationException(
                  $"The captured Z report for batch {expectedBatch} could not be read.");
    }

    private static void ValidateZReports(string path, DateOnly targetDate)
    {
        var reports = new PosReportImportService().ImportZReports(path);
        var valid = reports
            .Where(report =>
                report.ReportDate.HasValue &&
                !string.IsNullOrWhiteSpace(report.ShiftOrBatch))
            .ToList();
        var uniqueBatches = valid
            .Where(report => report.ReportDate == targetDate)
            .GroupBy(
                report => report.ShiftOrBatch!.Trim(),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (uniqueBatches.Count == 0)
            throw new InvalidOperationException(
                $"The AdventPOS Close-Out Z Report did not contain a valid batch whose Start Date is " +
                $"{targetDate:M/d/yyyy}. The batch was not imported.");
    }

    private static PosReportData ReadZReportBatch(
        string path,
        string expectedBatch)
    {
        var reports = new PosReportImportService()
            .ImportZReports(path)
            .Where(report =>
                !string.IsNullOrWhiteSpace(report.ShiftOrBatch) &&
                string.Equals(
                    report.ShiftOrBatch.Trim(),
                    expectedBatch.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (reports.Count == 0)
        {
            throw new InvalidOperationException(
                $"the downloaded PDF did not contain selected batch {expectedBatch}");
        }

        return reports.FirstOrDefault(report => report.ReportDate.HasValue)
               ?? throw new InvalidOperationException(
                   $"batch {expectedBatch} did not expose a valid Start Date");
    }

    private static PosReportData ParseRenderedZReport(
        string renderedText,
        string expectedBatch)
    {
        var report = new PosReportImportService().ImportRenderedZReport(renderedText);
        if (!string.Equals(
                report.ShiftOrBatch?.Trim(),
                expectedBatch.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"the rendered report did not contain selected batch {expectedBatch}");
        }
        if (!report.ReportDate.HasValue)
        {
            throw new InvalidOperationException(
                $"batch {expectedBatch} did not expose a valid Start Date");
        }
        return report;
    }

    private static async Task<List<DateOnly>> GetPendingCashSummaryDatesAsync(
        PortalStoreSyncSettings settings,
        DateOnly yesterday,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var db = CreateStoreDatabase(settings);
            await EnsureTargetDatabaseReadyAsync(db, settings.BusinessName);
            var dataStoreId = await ResolveDataStoreIdAsync(
                db,
                settings.BusinessName,
                cancellationToken);

            var reportDates = await db.PosSalesSummaries
                .AsNoTracking()
                .Where(item => item.StoreId == dataStoreId)
                .Select(item => item.ReportTo)
                .ToListAsync(cancellationToken);
            var latestCashSummaryDate = reportDates.Count == 0
                ? (DateOnly?)null
                : reportDates.Max();

            // Cash & Sales Summary has its own date cursor. Pull the next
            // calendar day after the latest stored report and continue in
            // chronological order through yesterday.
            if (!latestCashSummaryDate.HasValue)
                return [yesterday];
            if (latestCashSummaryDate.Value >= yesterday)
                return [];

            var pending = new List<DateOnly>();
            for (var date = latestCashSummaryDate.Value.AddDays(1);
                 date <= yesterday;
                 date = date.AddDays(1))
            {
                pending.Add(date);
            }
            return pending;
        }
        catch
        {
            // RunStoreAsync will return the actionable database/schema error.
            return [yesterday];
        }
    }

    private static async Task<long?> GetLatestImportedZBatchAsync(
        AppDbContext db,
        int storeId,
        CancellationToken cancellationToken)
    {
        // Never use every numeric Shift No as a portal cursor. A store can retain
        // rows from another register system whose identifiers are unrelated to
        // AdventPOS and much larger than its current batch numbers.
        var shiftNumbers = await db.ShiftLogs
            .AsNoTracking()
            .Where(item =>
                item.StoreId == storeId &&
                item.ShiftNo != null &&
                item.PosReportKey != null &&
                item.PosReportKey.StartsWith("ADVENTPOS-Z|"))
            .Select(item => item.ShiftNo!)
            .ToListAsync(cancellationToken);

        return LatestNumericBatch(shiftNumbers);
    }

    private static async Task<long?> GetLatestMatchingLegacyZBatchAsync(
        AppDbContext db,
        int storeId,
        IReadOnlySet<long> portalBatches,
        CancellationToken cancellationToken)
    {
        if (portalBatches.Count == 0)
            return null;

        var shiftNumbers = await db.ShiftLogs
            .AsNoTracking()
            .Where(item =>
                item.StoreId == storeId &&
                item.ShiftNo != null &&
                (item.PosReportKey == null || item.PosReportKey == ""))
            .Select(item => item.ShiftNo!)
            .ToListAsync(cancellationToken);

        return LatestNumericBatch(
            shiftNumbers,
            batch => portalBatches.Contains(batch));
    }

    private static long? LatestNumericBatch(
        IEnumerable<string> values,
        Func<long, bool>? include = null)
    {
        long? latest = null;
        foreach (var value in values)
        {
            if (!long.TryParse(
                    value.Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var batch))
            {
                continue;
            }
            if (include is not null && !include(batch))
                continue;

            if (!latest.HasValue || batch > latest.Value)
                latest = batch;
        }
        return latest;
    }

    private static async Task<PortalTargetStatus> GetTargetStatusAsync(
        PortalStoreSyncSettings settings,
        DateOnly targetDate,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var db = CreateStoreDatabase(settings);
            await EnsureTargetDatabaseReadyAsync(db, settings.BusinessName);
            var dataStoreId = await ResolveDataStoreIdAsync(
                db,
                settings.BusinessName,
                cancellationToken);
            var cashSummaryPresent = await db.PosSalesSummaries
                .AsNoTracking()
                .AnyAsync(item =>
                        item.StoreId == dataStoreId &&
                        item.ReportFrom <= targetDate &&
                        item.ReportTo >= targetDate,
                    cancellationToken);
            var keyPrefix = $"ADVENTPOS-Z|{targetDate:yyyy-MM-dd}|";
            var zReportCount = await db.ShiftLogs
                .AsNoTracking()
                .Where(item =>
                    item.StoreId == dataStoreId &&
                    item.PosReportKey != null &&
                    item.PosReportKey.StartsWith(keyPrefix))
                .Select(item => item.PosReportKey)
                .Distinct()
                .CountAsync(cancellationToken);
            return new PortalTargetStatus(cashSummaryPresent, zReportCount);
        }
        catch
        {
            // The actual sync returns the actionable connection/schema error.
            // Treat an unreadable status as incomplete so it is not skipped.
            return new PortalTargetStatus(false, 0);
        }
    }

    private static async Task<ZReportImportOutcome> ImportZReportsAsync(
        AppDbContext db,
        IAppPaths paths,
        int storeId,
        string sourcePath,
        DateOnly targetDate,
        CancellationToken cancellationToken)
    {
        ValidateZReports(sourcePath, targetDate);
        var reports = new PosReportImportService().ImportZReports(sourcePath)
            .Where(report => report.ReportDate == targetDate)
            .OrderBy(report => report.ShiftOrBatch)
            .ToList();

        var sourceBytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
        var sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes));
        var reportFolder = Path.Combine(
            paths.AppDataDirectory,
            "POS Z Reports",
            storeId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(reportFolder);
        var storedPath = Path.Combine(
            reportFolder,
            $"{targetDate:yyyyMMdd}_{sourceHash[..12]}.pdf");
        if (!File.Exists(storedPath))
            File.Copy(sourcePath, storedPath, false);

        return await UpsertZReportsAsync(
            db,
            storeId,
            reports,
            storedPath,
            targetDate,
            cancellationToken);
    }

    private static async Task<ZReportImportOutcome> ImportCapturedZReportAsync(
        AppDbContext db,
        IAppPaths paths,
        int storeId,
        CapturedZReport captured,
        DateOnly targetDate,
        CancellationToken cancellationToken)
    {
        if (captured.Report.ReportDate != targetDate ||
            string.IsNullOrWhiteSpace(captured.Report.ShiftOrBatch))
        {
            throw new InvalidOperationException(
                $"The captured Z report was not valid for {targetDate:M/d/yyyy}.");
        }

        var sourceBytes = await File.ReadAllBytesAsync(captured.SourcePath, cancellationToken);
        var sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes));
        var reportFolder = Path.Combine(
            paths.AppDataDirectory,
            "POS Z Reports",
            storeId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(reportFolder);
        var extension = Path.GetExtension(captured.SourcePath);
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".png";
        var storedPath = Path.Combine(
            reportFolder,
            $"{targetDate:yyyyMMdd}_{sourceHash[..12]}{extension}");
        if (!File.Exists(storedPath))
            File.Copy(captured.SourcePath, storedPath, false);

        return await UpsertZReportsAsync(
            db,
            storeId,
            [captured.Report],
            storedPath,
            targetDate,
            cancellationToken);
    }

    private static async Task<ZReportImportOutcome> UpsertZReportsAsync(
        AppDbContext db,
        int storeId,
        IReadOnlyList<PosReportData> reports,
        string storedPath,
        DateOnly targetDate,
        CancellationToken cancellationToken)
    {
        var imported = 0;
        var updated = 0;
        foreach (var report in reports)
        {
            var batch = report.ShiftOrBatch!.Trim();
            var employee = string.IsNullOrWhiteSpace(report.Employee)
                ? "POS User"
                : report.Employee.Trim();
            var reportKey = BuildZReportKey(targetDate, batch);
            var shift = await db.ShiftLogs.FirstOrDefaultAsync(item =>
                    item.StoreId == storeId &&
                    (item.PosReportKey == reportKey ||
                     (item.Date == targetDate &&
                      item.ShiftNo == batch &&
                      item.PosSalesSummaryId == null)),
                cancellationToken);
            if (shift is null)
            {
                shift = new ShiftLogEntry
                {
                    StoreId = storeId,
                    Date = targetDate,
                    ShiftNo = batch,
                    Employee = employee,
                    PosReportKey = reportKey,
                    PosReportPath = storedPath,
                    CreatedByUserId = 0,
                    CreatedByName = "Automatic POS Portal Sync",
                    CreatedUtc = DateTime.UtcNow
                };
                db.ShiftLogs.Add(shift);
                imported++;
            }
            else
            {
                shift.PosReportKey = reportKey;
                shift.PosReportPath = storedPath;
                shift.CreatedByName ??= "Automatic POS Portal Sync";
                updated++;
            }

            // Keep any manager-entered cash drop, payout, and reason intact
            // when a retry refreshes the source report totals.
            shift.Date = targetDate;
            shift.ShiftNo = batch;
            shift.Employee = employee;
            shift.PayoutReason ??= "";
            shift.CorrectionReason ??= "";
            shift.CashTotal = report.CashTotal;
            shift.CardTotal = report.CardTotal;
            shift.NetSales = report.NetSales;
            shift.Tax = report.TaxTotal;
        }

        // Z reports are the register-level source of truth for Shift Cash Drop.
        // Remove the older consolidated daily ShiftLog projection to avoid
        // counting the same sales a third time. The Cash & Sales Summary and
        // its manager reconciliation remain intact in PosSalesSummaries.
        var consolidated = await db.ShiftLogs
            .Where(item =>
                item.StoreId == storeId &&
                item.Date == targetDate &&
                item.PosSalesSummaryId != null)
            .ToListAsync(cancellationToken);
        if (consolidated.Count > 0)
        {
            var references = consolidated
                .Select(item => $"SHIFTLOG:{item.Id}")
                .ToList();
            var linkedCashRows = await db.CashOnHand
                .Where(item =>
                    item.StoreId == storeId &&
                    item.Reference != null &&
                    references.Contains(item.Reference))
                .ToListAsync(cancellationToken);
            db.CashOnHand.RemoveRange(linkedCashRows);
            db.ShiftLogs.RemoveRange(consolidated);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new ZReportImportOutcome(reports.Count, imported, updated);
    }

    private static string BuildZReportKey(DateOnly date, string batch)
    {
        var safeBatch = new string(batch
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_')
            .Select(char.ToUpperInvariant)
            .ToArray());
        return $"ADVENTPOS-Z|{date:yyyy-MM-dd}|{safeBatch}";
    }

    private static async Task ConfigureDownloadsAsync(IPage page, string downloadDirectory)
    {
        var session = await page.CreateCDPSessionAsync();
        await session.SendAsync("Page.setDownloadBehavior", new
        {
            behavior = "allow",
            downloadPath = downloadDirectory
        });
    }

    private static async Task ReplaceValueAsync(IPage page, string selector, string value)
    {
        await page.EvaluateFunctionAsync(
            @"(selector, value) => {
                const input = document.querySelector(selector);
                input.focus();
                input.value = value;
                input.dispatchEvent(new Event('input', { bubbles: true }));
                input.dispatchEvent(new Event('change', { bubbles: true }));
            }",
            selector,
            value);
    }

    private static Task<bool> IsVisibleAsync(IPage page, string selector) =>
        page.EvaluateFunctionAsync<bool>(
            @"selector => {
                const element = document.querySelector(selector);
                if (!element) return false;
                const style = window.getComputedStyle(element);
                const bounds = element.getBoundingClientRect();
                return style.display !== 'none' &&
                       style.visibility !== 'hidden' &&
                       style.opacity !== '0' &&
                       bounds.width > 0 &&
                       bounds.height > 0;
            }",
            selector);

    private static Task<bool> IsPortalHomeReadyAsync(IPage page) =>
        page.EvaluateExpressionAsync<bool>(
            @"(() => {
                const isVisible = element => {
                    if (!element) return false;
                    const style = window.getComputedStyle(element);
                    const bounds = element.getBoundingClientRect();
                    return style.display !== 'none' &&
                           style.visibility !== 'hidden' &&
                           style.opacity !== '0' &&
                           bounds.width > 0 &&
                           bounds.height > 0;
                };
                return Boolean(localStorage.getItem('StoreConfig')) &&
                       !isVisible(document.querySelector('#StoreSelectionModal')) &&
                       !isVisible(document.querySelector('#txtLoginUserName'));
            })()");

    private static Task<bool> HasCashReportFunctionsAsync(IPage page) =>
        page.EvaluateExpressionAsync<bool>(
            "typeof window.lstSales_SelectedIndexChanged === 'function' && " +
            "Array.isArray(window.SalesReports_Enum) && window.SalesReports_Enum.length > 0");

    private static async Task<string> DescribePortalStateAsync(IPage page)
    {
        try
        {
            return await page.EvaluateExpressionAsync<string>(
                @"(() => {
                    const clean = value => (value || '').replace(/\s+/g, ' ').trim();
                    const visible = element => {
                        if (!element) return false;
                        const style = window.getComputedStyle(element);
                        const bounds = element.getBoundingClientRect();
                        return style.display !== 'none' &&
                               style.visibility !== 'hidden' &&
                               style.opacity !== '0' &&
                               bounds.width > 0 &&
                               bounds.height > 0;
                    };
                    const messages = Array.from(document.querySelectorAll(
                            '.modal.show, .swal2-container, .bootbox, [role=""dialog""]'))
                        .filter(visible)
                        .map(element => clean(element.innerText))
                        .filter(Boolean)
                        .map(value => value.substring(0, 240));
                    const store = document.querySelector('#cbxSelectStore');
                    const selectedStore = store && store.selectedIndex >= 0
                        ? clean(store.options[store.selectedIndex].textContent)
                        : '';
                    const phase =
                        visible(document.querySelector('#txtFinalLoginUserName')) ? 'store-user-login' :
                        visible(document.querySelector('#txtLoginUserName')) ? 'owner-login' :
                        document.querySelector('#ReportModal.show') ? 'admin-reports' :
                        'store-home';
                    return [
                        `phase=${phase}`,
                        selectedStore ? `store=${selectedStore}` : '',
                        messages.length ? `message=${messages.join(' | ')}` : ''
                    ].filter(Boolean).join('; ');
                })()");
        }
        catch
        {
            return "";
        }
    }

    private static async Task WaitUntilAsync(
        IPage page,
        Func<Task<bool>> predicate,
        TimeSpan timeout,
        string error)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate())
                return;
            await Task.Delay(500);
        }
        throw new InvalidOperationException(error);
    }

    private static AppDbContext CreateStoreDatabase(PortalStoreSyncSettings settings)
    {
        var businesses = LicensedBusinessService.Load();
        var licensed = PortalSyncSettingsStore.FindLicensedBusiness(settings, businesses);
        if (licensed is null)
            throw new InvalidOperationException(
                $"'{settings.BusinessName}' is no longer included in this PC license.");
        if (!StoreDirectoryPreferencesStore.IsConnected(licensed, businesses))
            throw new InvalidOperationException(
                $"'{settings.BusinessName}' is disconnected from this PC login.");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(LocalSqlServerPolicy.BuildConnectionString(licensed.DatabaseName))
            .Options;
        return new AppDbContext(options);
    }

    private static async Task EnsureTargetDatabaseReadyAsync(AppDbContext db, string businessName)
    {
        var connectionString = db.Database.GetConnectionString();
        if (!string.IsNullOrWhiteSpace(connectionString))
            await DatabaseSchemaService.EnsureSchemaAsync(connectionString, businessName);
        await ManagerPaperworkSystem.Data.Services.DbInitializer.InitializeAsync(db);
    }

    private static async Task<int> ResolveDataStoreIdAsync(
        AppDbContext db,
        string businessName,
        CancellationToken cancellationToken)
    {
        return await StoreDataIdentityResolver.ResolveAsync(
            db,
            businessName,
            cancellationToken);
    }

    private static string? FindCompletedPdf(string downloadDirectory) =>
        Directory.EnumerateFiles(downloadDirectory, "*.pdf")
            .Where(path => !path.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

    private static void DeleteOldDownloads(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-14))
                    File.Delete(file);
            }
            catch
            {
                // A locked audit/download file can be cleaned on a later run.
            }
        }
    }

    private static void WriteLog(PortalSyncRunResult result)
    {
        try
        {
            var directory = Path.Combine(AppBootstrap.AppDataPath, "Logs");
            Directory.CreateDirectory(directory);
            var line =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{result.BusinessName}\t{(result.Success ? "OK" : "FAILED")}\t{result.Message}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(directory, "pos-portal-sync.log"), line);
        }
        catch
        {
            // Logging must not turn a successful import into a failed sync.
        }
    }
}
