using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Services;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.UI.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PuppeteerSharp;

namespace ManagerPaperworkSystem.WinForms;

internal sealed record PortalSyncRunResult(
    string BusinessName,
    bool Success,
    bool Imported,
    string Message);

internal sealed record ZReportImportOutcome(int Total, int Imported, int Updated);

internal static class PortalSyncService
{
    private const string TaskName = "HISAB KITAB - Daily POS Report Sync";
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

    public static void EnsureDailyTask(Guid storeConfigurationId, TimeOnly runAt)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new InvalidOperationException("The installed HISAB KITAB executable could not be located.");

        var taskName = $"{TaskName} - {storeConfigurationId:N}";
        var action = $"\"{executable}\" --portal-sync-store {storeConfigurationId:D}";
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
                "/TR", action,
                "/SC", "DAILY",
                "/ST", runAt.ToString("HH:mm", CultureInfo.InvariantCulture),
                "/F"
            }
        }) ?? throw new InvalidOperationException("Windows Task Scheduler could not be started.");
        process.WaitForExit(20_000);
        if (process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd().Trim();
            if (string.IsNullOrWhiteSpace(error))
                error = process.StandardOutput.ReadToEnd().Trim();
            throw new InvalidOperationException(
                "The daily sync task could not be created. " +
                (string.IsNullOrWhiteSpace(error) ? "Run HISAB KITAB once as administrator." : error));
        }
    }

    public static async Task<IReadOnlyList<PortalSyncRunResult>> RunDueAsync(
        IAppPaths paths,
        bool force,
        bool visibleChrome,
        Guid? onlyStoreConfigurationId = null,
        CancellationToken cancellationToken = default)
    {
        if (!await RunGate.WaitAsync(0, cancellationToken))
            return [new PortalSyncRunResult("", true, false, "A POS portal sync is already running.")];

        FileStream? processLock = null;
        try
        {
            var lockPath = Path.Combine(AppBootstrap.AppDataPath, "pos-portal-sync.lock");
            Directory.CreateDirectory(AppBootstrap.AppDataPath);
            try
            {
                processLock = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException)
            {
                return [new PortalSyncRunResult("", true, false, "A POS portal sync is already running.")];
            }

            var document = PortalSyncSettingsStore.Load();
            var results = new List<PortalSyncRunResult>();
            foreach (var settings in document.Stores
                         .Where(item => item.Enabled &&
                                        (onlyStoreConfigurationId is null ||
                                         item.Id == onlyStoreConfigurationId.Value))
                         .ToList())
            {
                var yesterday = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
                if (!force && settings.LastImportedReportDate >= yesterday)
                    continue;
                if (!force && DateTime.Now.TimeOfDay <
                    new TimeSpan(settings.DailyHour, settings.DailyMinute, 0))
                    continue;

                var firstDate = force || settings.LastImportedReportDate is null
                    ? yesterday
                    : settings.LastImportedReportDate.Value.AddDays(1);
                if (firstDate < yesterday.AddDays(-30))
                    firstDate = yesterday.AddDays(-30);

                for (var targetDate = firstDate; targetDate <= yesterday; targetDate = targetDate.AddDays(1))
                {
                    PortalSyncRunResult result;
                    try
                    {
                        result = await RunStoreWithRetriesAsync(
                            settings, paths, targetDate, visibleChrome, cancellationToken);
                    }
                    catch (Exception exception)
                    {
                        result = new PortalSyncRunResult(
                            settings.BusinessName,
                            false,
                            false,
                            AppBootstrap.RedactSensitiveText(exception.Message));
                    }

                    settings.LastAttemptUtc = DateTime.UtcNow;
                    settings.LastStatus = result.Message;
                    if (result.Success)
                    {
                        settings.LastSuccessUtc = DateTime.UtcNow;
                        // A duplicate means the date was already processed successfully.
                        settings.LastImportedReportDate = targetDate;
                    }
                    results.Add(result);
                    PortalSyncSettingsStore.Save(document);
                    WriteLog(result);

                    // Preserve a gap for the next automatic catch-up instead of skipping it.
                    if (!result.Success || force)
                        break;
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

    private static async Task<PortalSyncRunResult> RunStoreWithRetriesAsync(
        PortalStoreSyncSettings settings,
        IAppPaths paths,
        DateOnly targetDate,
        bool visibleChrome,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                return await RunStoreAsync(settings, paths, targetDate, visibleChrome, cancellationToken);
            }
            catch (Exception exception) when (attempt < 3)
            {
                lastError = exception;
                await Task.Delay(TimeSpan.FromSeconds(attempt * 10), cancellationToken);
            }
        }
        throw lastError ?? new InvalidOperationException("POS portal sync failed after three attempts.");
    }

    private static async Task<PortalSyncRunResult> RunStoreAsync(
        PortalStoreSyncSettings settings,
        IAppPaths paths,
        DateOnly targetDate,
        bool visibleChrome,
        CancellationToken cancellationToken)
    {
        var chrome = FindGoogleChrome()
                     ?? throw new InvalidOperationException("Google Chrome is not installed.");
        var profile = PortalSyncSettingsStore.ProfileDirectory(settings.Id);
        var downloadDirectory = PortalSyncSettingsStore.DownloadDirectory(settings.Id);
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(downloadDirectory);
        DeleteOldDownloads(downloadDirectory);
        var runDirectory = Path.Combine(downloadDirectory, $"run-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
        var cashDownloadDirectory = Path.Combine(runDirectory, "cash-sales-summary");
        var zDownloadDirectory = Path.Combine(runDirectory, "z-reports");
        Directory.CreateDirectory(cashDownloadDirectory);
        Directory.CreateDirectory(zDownloadDirectory);

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
        string cashSummaryPath;
        string zReportsPath;
        try
        {
            await page.GoToAsync(settings.PortalUrl, WaitUntilNavigation.Networkidle2);
            cancellationToken.ThrowIfCancellationRequested();

            await EnsureSignedInAsync(page, settings);
            await OpenCashAndSalesReportAsync(page);
            cashSummaryPath = await GenerateReportAsync(
                browser, page, cashDownloadDirectory, targetDate, "Cash and Sales Summary",
                downloaded =>
                    CashSalesSummaryImportCoordinator.Validate(
                        CashSalesSummaryPdfImporter.ImportAsync(downloaded, cancellationToken)
                            .GetAwaiter().GetResult()),
                cancellationToken);

            await OpenZReportAsync(page);
            zReportsPath = await GenerateReportAsync(
                browser, page, zDownloadDirectory, targetDate, "Z Report",
                downloaded => ValidateZReports(downloaded, settings.ExpectedDailyZReports, targetDate),
                cancellationToken);
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

        await using var db = CreateStoreDatabase(settings);
        await EnsureTargetDatabaseReadyAsync(db, settings.BusinessName);
        var dataStoreId = await ResolveDataStoreIdAsync(db, settings.BusinessName, cancellationToken);
        var outcome = await CashSalesSummaryImportCoordinator.ImportAsync(
            db, paths, dataStoreId, cashSummaryPath, 0, "Automatic POS Portal Sync", cancellationToken);
        var zResult = await ImportZReportsAsync(
            db, paths, dataStoreId, zReportsPath, settings.ExpectedDailyZReports, targetDate,
            cancellationToken);

        var imported = !outcome.Duplicate || zResult.Imported > 0;
        return new PortalSyncRunResult(
            settings.BusinessName,
            true,
            imported,
            $"POS sync for {targetDate:M/d/yyyy}: " +
            $"{(outcome.Duplicate ? "daily summary already present" : "daily summary imported")}; " +
            $"{zResult.Imported} new and {zResult.Updated} updated Z-report shift(s), " +
            $"{zResult.Total} register report(s) verified.");
    }

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

        await WaitUntilAsync(page, async () =>
                await IsVisibleAsync(page, "#StoreSelectionModal") ||
                await IsPortalHomeReadyAsync(page),
            TimeSpan.FromSeconds(45),
            "The AdventPOS login did not complete. Complete any verification request in POS Auto Sync Setup.");

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

    private static async Task OpenZReportAsync(IPage page)
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
                    "The AdventPOS Admin Reports control could not be reopened for the Z Report.");
        }

        await WaitUntilAsync(page,
            async () =>
                await HasCashReportFunctionsAsync(page) &&
                await IsVisibleAsync(page, "#ReportModal"),
            TimeSpan.FromSeconds(30),
            "The AdventPOS Admin Reports window was not available for the Z Report.");

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
                           longName === 'zreport' ||
                           name.includes('zreport') ||
                           longName.includes('zreport');
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
                "Z Report is not available in AdventPOS Admin Reports for this store.");
    }

    private static async Task<string> GenerateReportAsync(
        IBrowser browser,
        IPage page,
        string downloadDirectory,
        DateOnly targetDate,
        string reportName,
        Action<string> validateDownload,
        CancellationToken cancellationToken)
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
            "AdventPOS did not finish selecting Cash and Sales Summary for Yesterday.");

        var knownPages = (await browser.PagesAsync()).ToHashSet();
        await page.EvaluateExpressionAsync("ViewReport(true, true, false);");

        var deadline = DateTime.UtcNow.AddSeconds(75);
        var exportRequested = false;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var downloaded = FindCompletedPdf(downloadDirectory);
            if (downloaded is not null)
            {
                validateDownload(downloaded);
                return downloaded;
            }

            var reportPage = page.Url.Contains(
                    "/Report/ViewReportResult",
                    StringComparison.OrdinalIgnoreCase)
                ? page
                : (await browser.PagesAsync())
                    .FirstOrDefault(candidate => !knownPages.Contains(candidate));
            if (reportPage is not null && !exportRequested)
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
                    // Some report viewers keep a background connection open.
                }
                await ConfigureDownloadsAsync(reportPage, downloadDirectory);
                await RequestActiveReportsPdfExportAsync(reportPage);
                exportRequested = true;
            }
            await Task.Delay(1000, cancellationToken);
        }

        throw new InvalidOperationException(
            $"AdventPOS did not produce a valid {reportName} PDF. The portal may have changed its report screen.");
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

    private static void ValidateZReports(
        string path,
        int expectedReportCount,
        DateOnly targetDate)
    {
        var reports = new PosReportImportService().ImportZReports(path);
        var valid = reports
            .Where(report =>
                report.ReportDate.HasValue &&
                !string.IsNullOrWhiteSpace(report.ShiftOrBatch) &&
                (report.CashTotal != 0m ||
                 report.CardTotal != 0m ||
                 report.NetSales != 0m ||
                 report.TaxTotal != 0m))
            .ToList();
        if (valid.Count < Math.Max(1, expectedReportCount))
            throw new InvalidOperationException(
                $"The AdventPOS Z Report contained {valid.Count} valid register report(s); " +
                $"{Math.Max(1, expectedReportCount)} were expected.");
        if (valid.Any(report => report.ReportDate != targetDate))
            throw new InvalidOperationException(
                $"The AdventPOS Z Report did not contain only the requested shift date {targetDate:M/d/yyyy}.");
    }

    private static async Task<ZReportImportOutcome> ImportZReportsAsync(
        AppDbContext db,
        IAppPaths paths,
        int storeId,
        string sourcePath,
        int expectedReportCount,
        DateOnly targetDate,
        CancellationToken cancellationToken)
    {
        ValidateZReports(sourcePath, expectedReportCount, targetDate);
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
                updated++;
            }

            // Keep any manager-entered cash drop, payout, and reason intact
            // when a retry refreshes the source report totals.
            shift.Date = targetDate;
            shift.ShiftNo = batch;
            shift.Employee = employee;
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
        var licensed = LicensedBusinessService.Load().FirstOrDefault(item =>
            string.Equals(item.DatabaseName, settings.DatabaseName, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(settings.StoreGuid) &&
             string.Equals(item.StoreGuid, settings.StoreGuid, StringComparison.OrdinalIgnoreCase)));
        if (licensed is null)
            throw new InvalidOperationException(
                $"'{settings.BusinessName}' is no longer included in this PC license.");

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = licensed.Connection.Server,
            InitialCatalog = licensed.Connection.Database,
            TrustServerCertificate = true,
            Encrypt = true,
            ConnectTimeout = 15,
            ConnectRetryCount = 2,
            ConnectRetryInterval = 2
        };
        if (!string.IsNullOrWhiteSpace(licensed.Connection.ConnectionString))
            builder.ConnectionString = licensed.Connection.ConnectionString;
        else if (string.IsNullOrWhiteSpace(licensed.Connection.Username))
            builder.IntegratedSecurity = true;
        else
        {
            builder.UserID = licensed.Connection.Username;
            builder.Password = licensed.Connection.Password;
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(builder.ConnectionString)
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
        static string Normalize(string value) =>
            new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        var wanted = Normalize(businessName);
        var stores = await db.Stores.AsNoTracking()
            .Where(item => item.IsActive)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return stores.FirstOrDefault(item => Normalize(item.Name) == wanted)?.Id
               ?? stores.FirstOrDefault()?.Id
               ?? throw new InvalidOperationException(
                   $"The database for '{businessName}' does not contain an active store.");
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
