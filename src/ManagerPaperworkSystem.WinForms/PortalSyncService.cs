using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using ManagerPaperworkSystem.Core.Services;
using ManagerPaperworkSystem.Data.Db;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PuppeteerSharp;

namespace ManagerPaperworkSystem.WinForms;

internal sealed record PortalSyncRunResult(
    string BusinessName,
    bool Success,
    bool Imported,
    string Message);

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
        await page.GoToAsync(settings.PortalUrl, WaitUntilNavigation.Networkidle2);
        cancellationToken.ThrowIfCancellationRequested();

        await EnsureSignedInAsync(page, settings);
        await OpenCashAndSalesReportAsync(page);
        var downloadedPath = await GenerateReportAsync(
            browser, page, downloadDirectory, targetDate, cancellationToken);

        await using var db = CreateStoreDatabase(settings);
        await EnsureTargetDatabaseReadyAsync(db, settings.BusinessName);
        var dataStoreId = await ResolveDataStoreIdAsync(db, settings.BusinessName, cancellationToken);
        var outcome = await CashSalesSummaryImportCoordinator.ImportAsync(
            db, paths, dataStoreId, downloadedPath, 0, "Automatic POS Portal Sync", cancellationToken);

        return outcome.Duplicate
            ? new PortalSyncRunResult(settings.BusinessName, true, false,
                $"The {targetDate:M/d/yyyy} POS report was already imported.")
            : new PortalSyncRunResult(settings.BusinessName, true, true,
                $"Imported the {targetDate:M/d/yyyy} POS Cash and Sales Summary.");
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
            if (!string.IsNullOrWhiteSpace(settings.PortalStoreName))
            {
                await page.EvaluateFunctionAsync(
                    @"name => {
                        const select = document.querySelector('#cbxSelectStore');
                        const wanted = Array.from(select.options).find(o =>
                            o.textContent.trim().toLowerCase().includes(name.trim().toLowerCase()));
                        if (wanted) {
                            select.value = wanted.value;
                            select.dispatchEvent(new Event('change', { bubbles: true }));
                        }
                    }",
                    settings.PortalStoreName);
            }

            await WaitUntilAsync(page,
                () => IsVisibleAsync(page, "#txtFinalLoginUserName"),
                TimeSpan.FromSeconds(30),
                "The AdventPOS store login controls did not load.");
            if (!string.IsNullOrWhiteSpace(settings.StoreUserName))
                await ReplaceValueAsync(page, "#txtFinalLoginUserName", settings.StoreUserName);
            if (!string.IsNullOrWhiteSpace(settings.StorePassword))
                await ReplaceValueAsync(page, "#txtFinalLoginPassword", settings.StorePassword);
            await page.EvaluateExpressionAsync(
                "document.querySelector('#chkRememberPwd').checked=true; " +
                "document.querySelector('#btnFinalStepToLogin').click();");
        }

        await WaitUntilAsync(page,
            () => IsPortalHomeReadyAsync(page),
            TimeSpan.FromSeconds(60),
            "AdventPOS did not reach the store home page. A verification code, CAPTCHA, or password update may require attention.");
    }

    private static async Task OpenCashAndSalesReportAsync(IPage page)
    {
        if (!await HasCashReportFunctionsAsync(page))
        {
            await page.EvaluateExpressionAsync(
                @"(() => {
                    const candidates = Array.from(document.querySelectorAll('a,button,li,span'))
                        .filter(e => (e.textContent || '').trim().toLowerCase() === 'reports' && e.offsetParent);
                    if (candidates.length) candidates[candidates.length - 1].click();
                })()");
        }

        await WaitUntilAsync(page,
            () => HasCashReportFunctionsAsync(page),
            TimeSpan.FromSeconds(60),
            "The AdventPOS Reports page did not load.");

        var selected = await page.EvaluateFunctionAsync<bool>(
            @"() => {
                if (!Array.isArray(window.SalesReports_Enum)) return false;
                const index = window.SalesReports_Enum.findIndex(r => r && r.Name === 'CashAndSales');
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

    private static async Task<string> GenerateReportAsync(
        IBrowser browser,
        IPage page,
        string downloadDirectory,
        DateOnly targetDate,
        CancellationToken cancellationToken)
    {
        var dateText = targetDate.ToString("M/d/yyyy", CultureInfo.InvariantCulture);
        await page.EvaluateFunctionAsync(
            @"dateText => {
                const period = document.querySelector('#cbxReportPeriod');
                if (period) {
                    period.value = '13';
                    period.dispatchEvent(new Event('change', { bubbles: true }));
                }
                const start = document.querySelector('#dtRTPStartDate');
                const end = document.querySelector('#dtRTPEndDate');
                if (start) { start.disabled = false; start.value = dateText; }
                if (end) { end.disabled = false; end.value = dateText; }
            }",
            dateText);

        var knownPages = (await browser.PagesAsync()).ToHashSet();
        await page.EvaluateExpressionAsync("ViewReport(true, true, false);");

        var deadline = DateTime.UtcNow.AddSeconds(75);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var downloaded = FindCompletedPdf(downloadDirectory);
            if (downloaded is not null)
            {
                CashSalesSummaryImportCoordinator.Validate(
                    await CashSalesSummaryPdfImporter.ImportAsync(downloaded, cancellationToken));
                return downloaded;
            }

            var reportPage = (await browser.PagesAsync())
                .FirstOrDefault(candidate => !knownPages.Contains(candidate));
            if (reportPage is not null)
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

                var outputPath = Path.Combine(
                    downloadDirectory,
                    $"AdventPOS_CashSales_{targetDate:yyyyMMdd}_{Guid.NewGuid():N}.pdf");
                await reportPage.PdfAsync(outputPath, new PdfOptions
                {
                    PrintBackground = true,
                    Format = PuppeteerSharp.Media.PaperFormat.Letter,
                    Landscape = false,
                    PreferCSSPageSize = true
                });
                try
                {
                    CashSalesSummaryImportCoordinator.Validate(
                        await CashSalesSummaryPdfImporter.ImportAsync(outputPath, cancellationToken));
                    return outputPath;
                }
                catch
                {
                    try { File.Delete(outputPath); } catch { }
                }
            }
            await Task.Delay(1000, cancellationToken);
        }

        throw new InvalidOperationException(
            "AdventPOS did not produce a valid Cash and Sales Summary PDF. The portal may have changed its report screen.");
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
                return !!element && !!element.offsetParent;
            }",
            selector);

    private static Task<bool> IsPortalHomeReadyAsync(IPage page) =>
        page.EvaluateExpressionAsync<bool>(
            "Boolean(localStorage.getItem('StoreConfig')) && " +
            "!document.querySelector('#StoreSelectionModal.show')");

    private static Task<bool> HasCashReportFunctionsAsync(IPage page) =>
        page.EvaluateExpressionAsync<bool>(
            "typeof window.lstSales_SelectedIndexChanged === 'function' && " +
            "Array.isArray(window.SalesReports_Enum) && window.SalesReports_Enum.length > 0");

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
