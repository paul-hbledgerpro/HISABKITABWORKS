using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using ManagerPaperworkSystem.Core.Services;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.UI.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ManagerPaperworkSystem.WinForms;

internal sealed record InvoiceBackgroundSyncResult(
    string StoreKey,
    bool Success,
    string Message);

internal static class InvoiceEmailBackgroundSyncService
{
    private const string TaskName = "HISAB KITAB - Invoice Email Sync";
    private static readonly SemaphoreSlim RunGate = new(1, 1);

    public static void EnsureTask(InvoiceEmailSyncService service)
    {
        if (service.GetEnabledStoreKeys().Count == 0)
            return;

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new InvalidOperationException("The installed HISAB KITAB executable could not be located.");

        var temporaryXml = Path.Combine(
            Path.GetTempPath(),
            $"hisab-kitab-invoice-sync-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(
                temporaryXml,
                CreateScheduledTaskXml(executable),
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
                    "/TN", TaskName,
                    "/XML", temporaryXml,
                    "/F"
                }
            }) ?? throw new InvalidOperationException(
                "Windows Task Scheduler could not be started.");
            process.WaitForExit(20_000);
            if (process.ExitCode != 0)
            {
                var error = process.StandardError.ReadToEnd().Trim();
                if (string.IsNullOrWhiteSpace(error))
                    error = process.StandardOutput.ReadToEnd().Trim();
                throw new InvalidOperationException(
                    "The invoice email sync task could not be created. " +
                    (string.IsNullOrWhiteSpace(error)
                        ? "Run HISAB KITAB once as administrator."
                        : error));
            }
        }
        finally
        {
            try
            {
                File.Delete(temporaryXml);
            }
            catch
            {
                // Windows can remove an abandoned temporary task definition later.
            }
        }
    }

    public static async Task<IReadOnlyList<InvoiceBackgroundSyncResult>> RunDueAsync(
        IServiceProvider services,
        bool force,
        CancellationToken cancellationToken = default)
    {
        if (!await RunGate.WaitAsync(0, cancellationToken))
        {
            return
            [
                new InvoiceBackgroundSyncResult(
                    "",
                    true,
                    "An invoice email sync is already running.")
            ];
        }

        FileStream? processLock = null;
        try
        {
            var lockPath = Path.Combine(
                AppBootstrap.AppDataPath,
                "invoice-email-sync.lock");
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
                return
                [
                    new InvoiceBackgroundSyncResult(
                        "",
                        true,
                        "An invoice email sync is already running on this PC.")
                ];
            }

            var paths = services.GetRequiredService<IAppPaths>();
            var defaultFactory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
            var activeConnection = services.GetRequiredService<ActiveConnectionInfo>();
            var invoiceImporter = services.GetRequiredService<InvoiceImportService>();
            var probeConnections = CreateStoreConnections(defaultFactory, activeConnection);
            var probePurchases = new PurchaseService(
                new StoreDbContextFactory(probeConnections),
                paths);
            var probeService = new InvoiceEmailSyncService(
                paths,
                invoiceImporter,
                probePurchases);
            var results = new List<InvoiceBackgroundSyncResult>();

            foreach (var storeKey in probeService.GetEnabledStoreKeys())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!force && !probeService.IsDue(storeKey, TimeSpan.FromHours(4)))
                    continue;

                using var storeTimeout =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                storeTimeout.CancelAfter(TimeSpan.FromMinutes(20));
                WriteLog(
                    "Gmail/IMAP",
                    storeKey,
                    true,
                    "Background invoice email scan started.");
                try
                {
                    var connectionStoreId = ParseConnectionStoreId(storeKey);
                    var storeConnections = CreateStoreConnections(
                        defaultFactory,
                        activeConnection);
                    storeConnections.CurrentStoreId = connectionStoreId;
                    var purchaseService = new PurchaseService(
                        new StoreDbContextFactory(storeConnections),
                        paths);
                    var syncService = new InvoiceEmailSyncService(
                        paths,
                        invoiceImporter,
                        purchaseService);
                    var dataStoreId = await ResolveDataStoreIdAsync(
                        storeConnections,
                        storeTimeout.Token);
                    var outcome = await syncService.SyncAsync(
                        storeKey,
                        dataStoreId,
                        0,
                        "Automatic Invoice Email Sync",
                        ct: storeTimeout.Token);
                    var message =
                        $"Invoice email sync checked {outcome.MessagesChecked} message(s), " +
                        $"imported {outcome.InvoicesImported}, skipped {outcome.DuplicatesSkipped} duplicate(s), " +
                        $"and sent {outcome.NeedsReview} attachment(s) to review.";
                    var result = new InvoiceBackgroundSyncResult(
                        storeKey,
                        true,
                        message);
                    results.Add(result);
                    WriteLog("Gmail/IMAP", storeKey, true, message);
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    const string message =
                        "Background invoice email sync exceeded 20 minutes and was stopped. " +
                        "The next scheduled run will resume from the last successful scan.";
                    var result = new InvoiceBackgroundSyncResult(
                        storeKey,
                        false,
                        message);
                    results.Add(result);
                    WriteLog("Gmail/IMAP", storeKey, false, message);
                }
                catch (Exception exception)
                {
                    var message = AppBootstrap.RedactSensitiveText(exception.Message);
                    var result = new InvoiceBackgroundSyncResult(
                        storeKey,
                        false,
                        message);
                    results.Add(result);
                    WriteLog("Gmail/IMAP", storeKey, false, message);
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

    public static void WriteLog(
        string source,
        string storeKey,
        bool success,
        string message)
    {
        try
        {
            var directory = Path.Combine(AppBootstrap.AppDataPath, "Logs");
            Directory.CreateDirectory(directory);
            var safeMessage = AppBootstrap.RedactSensitiveText(message)
                .Replace('\r', ' ')
                .Replace('\n', ' ');
            File.AppendAllText(
                Path.Combine(directory, "invoice-email-sync.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{source}\t{storeKey}\t" +
                $"{(success ? "OK" : "FAILED")}\t{safeMessage}{Environment.NewLine}");
        }
        catch
        {
            // Logging must not turn a successful invoice import into a failed sync.
        }
    }

    private static StoreConnectionService CreateStoreConnections(
        IDbContextFactory<AppDbContext> defaultFactory,
        ActiveConnectionInfo activeConnection)
    {
        var service = new StoreConnectionService(
            defaultFactory,
            activeConnection.ConnectionString,
            activeConnection.UseSqlServer);
        foreach (var pair in AppBootstrap.LoadStoreConnections())
        {
            if (int.TryParse(
                    pair.Key,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var storeId))
            {
                service.RegisterStore(storeId, pair.Value);
            }
        }
        return service;
    }

    private static int ParseConnectionStoreId(string storeKey)
    {
        var separator = storeKey.LastIndexOf('|');
        if (separator < 0 ||
            !int.TryParse(
                storeKey[(separator + 1)..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var storeId) ||
            storeId <= 0)
        {
            throw new InvalidOperationException(
                "The saved invoice email configuration does not identify its licensed store.");
        }
        return storeId;
    }

    private static async Task<int> ResolveDataStoreIdAsync(
        StoreConnectionService storeConnections,
        CancellationToken cancellationToken)
    {
        var businessName = "";
        try
        {
            var databaseName = new SqlConnectionStringBuilder(
                storeConnections.GetCurrentConnectionString()).InitialCatalog;
            businessName = LicensedBusinessService.Load()
                .FirstOrDefault(item =>
                    string.Equals(
                        item.DatabaseName,
                        databaseName,
                        StringComparison.OrdinalIgnoreCase))
                ?.BusinessName ?? "";
        }
        catch
        {
            // A single-store database can still be resolved by its first active store.
        }

        await using var db = storeConnections.CreateDbContext();
        return await StoreDataIdentityResolver.ResolveAsync(
            db,
            businessName,
            cancellationToken);
    }

    private static string CreateScheduledTaskXml(string executable)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var userSid = WindowsIdentity.GetCurrent().User?.Value
                      ?? throw new InvalidOperationException(
                          "The current Windows user could not be identified for invoice scheduling.");
        var start = DateTime.Today.AddMinutes(15)
            .ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var document = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(ns + "Task",
                new XAttribute("version", "1.4"),
                new XElement(ns + "RegistrationInfo",
                    new XElement(ns + "Description",
                        "Checks configured store mailboxes and imports verified PDF invoices.")),
                new XElement(ns + "Triggers",
                    new XElement(ns + "CalendarTrigger",
                        new XElement(ns + "Repetition",
                            new XElement(ns + "Interval", "PT4H"),
                            new XElement(ns + "Duration", "P1D"),
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
                    new XElement(ns + "ExecutionTimeLimit", "PT30M"),
                    new XElement(ns + "Priority", "7"),
                    new XElement(ns + "RestartOnFailure",
                        new XElement(ns + "Interval", "PT15M"),
                        new XElement(ns + "Count", "3"))),
                new XElement(ns + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(ns + "Exec",
                        new XElement(ns + "Command", executable),
                        new XElement(ns + "Arguments", "--invoice-email-sync"),
                        new XElement(ns + "WorkingDirectory",
                            Path.GetDirectoryName(executable) ?? "")))));
        return document.ToString(SaveOptions.DisableFormatting);
    }
}
