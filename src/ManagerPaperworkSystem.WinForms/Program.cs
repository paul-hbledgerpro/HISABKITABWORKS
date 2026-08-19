using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ManagerPaperworkSystem.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(value => value.Equals("--demo-presentation", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                DemoHiddenDesktop.Run(() => RunApplication(args));
            }
            catch (Exception exception)
            {
                var presentationIndex = Array.FindIndex(
                    args,
                    value => value.Equals("--demo-presentation", StringComparison.OrdinalIgnoreCase));
                if (presentationIndex >= 0 && presentationIndex + 1 < args.Length)
                {
                    var directory = Path.GetFullPath(args[presentationIndex + 1]);
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "presentation-error.txt"), exception.ToString());
                }
                Environment.ExitCode = 1;
            }
            return;
        }

        RunApplication(args);
    }

    private static void RunApplication(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Releases before 1.0.158 always launched Upgrade.exe with an
        // administrator credential. When a standard user supplied a different
        // administrator account, the legacy updater then relaunched HISAB KITAB
        // inside that administrator's profile. The normal user's device license
        // is intentionally not stored in that unrelated profile, so startup
        // incorrectly opened activation. Hand the post-update launch back to
        // the signed-in desktop user before any license UI can be displayed.
        if (TryReturnLegacyUpdaterLaunchToDesktopUser(args))
            return;

        if (args.Any(value => value.Equals("--demo-prepare", StringComparison.OrdinalIgnoreCase)))
        {
            RunDemo(launchApplication: false, resetData: false);
            return;
        }

        if (args.Any(value => value.Equals("--demo-reset", StringComparison.OrdinalIgnoreCase)))
        {
            RunDemo(launchApplication: false, resetData: true);
            return;
        }

        var demoCaptureIndex = Array.FindIndex(
            args,
            value => value.Equals("--demo-capture", StringComparison.OrdinalIgnoreCase));
        if (demoCaptureIndex >= 0)
        {
            if (demoCaptureIndex + 1 >= args.Length)
            {
                Environment.ExitCode = 2;
                return;
            }
            DemoRuntime.ConfigureCapture(args[demoCaptureIndex + 1]);
            RunDemo(launchApplication: true, resetData: true);
            return;
        }

        var demoPresentationIndex = Array.FindIndex(
            args,
            value => value.Equals("--demo-presentation", StringComparison.OrdinalIgnoreCase));
        if (demoPresentationIndex >= 0)
        {
            if (demoPresentationIndex + 1 >= args.Length)
            {
                Environment.ExitCode = 2;
                return;
            }
            DemoRuntime.ConfigurePresentation(args[demoPresentationIndex + 1]);
            ConfigureDemoPresentationExceptionLogging();
            RunDemo(launchApplication: true, resetData: true);
            return;
        }

        if (args.Any(value => value.Equals("--demo", StringComparison.OrdinalIgnoreCase)))
        {
            RunDemo(launchApplication: true, resetData: false);
            return;
        }

        if (args.Length >= 5 && args[0].Equals("--export-device-request", StringComparison.OrdinalIgnoreCase))
        {
            DeviceLicenseService.ExportRequest(args[4], args[1], args[2], args[3]);
            return;
        }

        if (args.Length >= 5 && args[0].Equals("--export-device-request-text", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(args[4], DeviceLicenseService.CreateRequestText(args[1], args[2], args[3]));
            return;
        }

        if (args.Any(x => x.Equals("--device-activation", StringComparison.OrdinalIgnoreCase)))
        {
            using var activation = new DeviceActivationForm();
            if (activation.ShowDialog() != DialogResult.OK)
                return;
        }

        if (args.Any(x => x.Equals("--invoice-email-sync", StringComparison.OrdinalIgnoreCase)))
        {
            RunInvoiceEmailSync();
            return;
        }

        if (args.Any(x => x.Equals("--database-cloud-backup", StringComparison.OrdinalIgnoreCase)))
        {
            RunDatabaseCloudBackup();
            return;
        }

        var portalStoreArgument = Array.FindIndex(
            args,
            x => x.Equals("--portal-sync-store", StringComparison.OrdinalIgnoreCase));
        if (portalStoreArgument >= 0)
        {
            Guid? storeConfigurationId = null;
            if (portalStoreArgument + 1 < args.Length &&
                Guid.TryParse(args[portalStoreArgument + 1], out var parsedId))
                storeConfigurationId = parsedId;
            RunPortalSync(
                storeConfigurationId,
                ParsePortalSyncReportKind(args),
                ParseDateOnlyArgument(args, "--portal-sync-backfill-from"),
                ParseDateOnlyArgument(args, "--portal-sync-backfill-through"));
            return;
        }

        if (args.Any(x => x.Equals("--portal-sync", StringComparison.OrdinalIgnoreCase)))
        {
            RunPortalSync(null, ParsePortalSyncReportKind(args));
            return;
        }

        try
        {
            // Check before licensing, database initialization, setup, and login.
            // This lets an older installation repair itself even when a later
            // startup stage is temporarily unable to complete.
            if (AppUpdateStartupService
                .CheckBeforeApplicationStartupAsync()
                .GetAwaiter()
                .GetResult())
            {
                return;
            }

            var retryStartup = true;
            while (retryStartup)
            {
                retryStartup = false;

                if (!StartupFlow.EnsureLicenseReady())
                    return;

                using var services = AppBootstrap.BuildServices();
                ProgramServices.Set(services);
                try
                {
                    AppBootstrap.InitializeDatabaseAsync(services).GetAwaiter().GetResult();
                    LicensedBusinessService.SynchronizeAsync(services).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    if (StartupFlow.HandleDatabaseStartupFailure(ex))
                    {
                        retryStartup = true;
                        continue;
                    }

                    return;
                }

                if (!StartupFlow.EnsureSetupReady(services))
                    return;

                using var login = services.GetRequiredService<LoginForm>();
                if (login.ShowDialog() != DialogResult.OK)
                    return;

                Application.Run(services.GetRequiredService<MainForm>());
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"HISAB KITAB failed to start.\n\n{AppBootstrap.RedactSensitiveText(ex.Message)}",
                "HISAB KITAB",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static bool TryReturnLegacyUpdaterLaunchToDesktopUser(string[] args)
    {
        if (args.Length != 0 || File.Exists(DeviceLicenseService.InstalledLicensePath))
            return false;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
                return false;

            var legacyUpdaterIsRunning = Process.GetProcesses()
                .Any(process =>
                {
                    try
                    {
                        return process.ProcessName.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) ||
                               process.ProcessName.Equals("Update", StringComparison.OrdinalIgnoreCase);
                    }
                    finally
                    {
                        process.Dispose();
                    }
                });
            if (!legacyUpdaterIsRunning)
                return false;

            var shellType = Type.GetTypeFromProgID("Shell.Application")
                ?? throw new InvalidOperationException("The Windows desktop shell is unavailable.");
            dynamic shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("The Windows desktop shell could not be started.");
            try
            {
                shell.ShellExecute(
                    Application.ExecutablePath,
                    "",
                    Path.GetDirectoryName(Application.ExecutablePath) ?? AppContext.BaseDirectory,
                    "open",
                    1);
            }
            finally
            {
                if (Marshal.IsComObject(shell))
                    Marshal.FinalReleaseComObject(shell);
            }

            return true;
        }
        catch
        {
            MessageBox.Show(
                "The update was installed successfully, but the older updater opened HISAB KITAB " +
                "with a different Windows administrator account.\n\n" +
                "Close this message and open HISAB KITAB again from your normal Windows account. " +
                "Your existing license remains there and does not need to be activated again.",
                "Update Installed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return true;
        }
    }

    private static void RunDemo(bool launchApplication, bool resetData)
    {
        try
        {
            DemoRuntime.Enable();
            using var services = AppBootstrap.BuildServices();
            ProgramServices.Set(services);
            AppBootstrap.InitializeDatabaseAsync(services).GetAwaiter().GetResult();
            if (resetData)
                DemoDataService.ResetAsync(services).GetAwaiter().GetResult();
            else
                DemoDataService.EnsureSeededAsync(services).GetAwaiter().GetResult();
            DemoRuntime.ConfigureLicense();
            DemoDataService.ConfigureDemoSession(services);
            var staleErrorPath = Path.Combine(DemoRuntime.AppDataDirectory, "demo-prepare-error.txt");
            if (File.Exists(staleErrorPath))
                File.Delete(staleErrorPath);
            if (launchApplication)
                Application.Run(services.GetRequiredService<MainForm>());
        }
        catch (Exception exception)
        {
            if (!launchApplication)
            {
                Directory.CreateDirectory(DemoRuntime.AppDataDirectory);
                File.WriteAllText(
                    Path.Combine(DemoRuntime.AppDataDirectory, "demo-prepare-error.txt"),
                    exception.ToString());
                Environment.ExitCode = 1;
                return;
            }
            MessageBox.Show(
                $"HISAB KITAB Demo could not start.\n\n{AppBootstrap.RedactSensitiveText(exception.Message)}",
                "HISAB KITAB Demo",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void ConfigureDemoPresentationExceptionLogging()
    {
        Directory.CreateDirectory(DemoRuntime.PresentationDirectory);
        var errorPath = Path.Combine(
            DemoRuntime.PresentationDirectory,
            "presentation-thread-errors.txt");
        if (File.Exists(errorPath))
            File.Delete(errorPath);

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) =>
        {
            try
            {
                File.AppendAllText(
                    errorPath,
                    $"[{DateTimeOffset.Now:O}]{Environment.NewLine}" +
                    $"{eventArgs.Exception}{Environment.NewLine}{Environment.NewLine}");
            }
            catch
            {
                // A presentation must never stop behind a hidden exception dialog.
            }
        };
    }

    private static PortalSyncReportKind? ParsePortalSyncReportKind(string[] args)
    {
        var reportArgument = Array.FindIndex(
            args,
            value => value.Equals("--portal-sync-report", StringComparison.OrdinalIgnoreCase));
        if (reportArgument < 0 || reportArgument + 1 >= args.Length)
            return null;

        return args[reportArgument + 1].Trim().ToLowerInvariant() switch
        {
            "cash-sales" => PortalSyncReportKind.CashSalesSummary,
            "z-reports" => PortalSyncReportKind.ZReports,
            _ => null
        };
    }

    private static DateOnly? ParseDateOnlyArgument(string[] args, string argumentName)
    {
        var argument = Array.FindIndex(
            args,
            value => value.Equals(argumentName, StringComparison.OrdinalIgnoreCase));
        if (argument < 0 || argument + 1 >= args.Length)
            return null;

        return DateOnly.TryParseExact(
            args[argument + 1],
            "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var date)
            ? date
            : null;
    }

    private static void RunPortalSync(
        Guid? storeConfigurationId,
        PortalSyncReportKind? reportKind,
        DateOnly? historicalStartDate = null,
        DateOnly? historicalEndDate = null)
    {
        try
        {
            var historicalBackfill =
                historicalStartDate.HasValue || historicalEndDate.HasValue;
            // Scheduled execution must never open an activation or error dialog.
            var licenseValidation = DeviceLicenseService.ValidateInstalledLicense();
            if (licenseValidation.Status != DeviceLicenseStatus.Valid)
            {
                PortalSyncService.WriteDiagnostic(
                    "",
                    false,
                    $"Scheduled POS sync stopped because the device license status is " +
                    $"{licenseValidation.Status}: {licenseValidation.Message}");
                Environment.ExitCode = 1;
                return;
            }
            using var services = AppBootstrap.BuildServices();
            AppBootstrap.InitializeDatabaseAsync(services).GetAwaiter().GetResult();
            LicensedBusinessService.SynchronizeAsync(services).GetAwaiter().GetResult();
            var paths = services.GetRequiredService<ManagerPaperworkSystem.Core.Services.IAppPaths>();
            var results = PortalSyncService.RunDueAsync(
                    paths,
                    force: historicalBackfill,
                    visibleChrome: false,
                    onlyStoreConfigurationId: storeConfigurationId,
                    onlyReportKind: reportKind,
                    waitForExistingRun: true,
                    existingRunWaitTimeout: TimeSpan.FromHours(2),
                    historicalStartDate: historicalStartDate,
                    historicalEndDate: historicalEndDate)
                .GetAwaiter()
                .GetResult();
            if (results.Any(result => !result.Success))
                Environment.ExitCode = 1;
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            try
            {
                var directory = Path.Combine(AppBootstrap.AppDataPath, "Logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "pos-portal-sync.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\tFAILED\t" +
                    $"{AppBootstrap.RedactSensitiveText(exception.Message)}{Environment.NewLine}");
            }
            catch
            {
                // Scheduled background execution has no interactive error path.
            }
        }
    }

    private static void RunInvoiceEmailSync()
    {
        try
        {
            var licenseStatus = DeviceLicenseService.ValidateInstalledLicense().Status;
            if (licenseStatus != DeviceLicenseStatus.Valid)
            {
                InvoiceEmailBackgroundSyncService.WriteLog(
                    "Scheduler",
                    "",
                    false,
                    $"Scheduled invoice sync stopped because the device license status is {licenseStatus}.");
                Environment.ExitCode = 1;
                return;
            }

            using var services = AppBootstrap.BuildServices();
            ProgramServices.Set(services);
            AppBootstrap.InitializeDatabaseAsync(services).GetAwaiter().GetResult();
            LicensedBusinessService.SynchronizeAsync(services).GetAwaiter().GetResult();
            var results = InvoiceEmailBackgroundSyncService.RunDueAsync(
                    services,
                    force: false)
                .GetAwaiter()
                .GetResult();
            if (results.Any(result => !result.Success))
                Environment.ExitCode = 1;
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            InvoiceEmailBackgroundSyncService.WriteLog(
                "Scheduler",
                "",
                false,
                exception.Message);
        }
    }

    private static void RunDatabaseCloudBackup()
    {
        try
        {
            var validation = DeviceLicenseService.ValidateInstalledLicense();
            LicenseRuntime.CurrentLicense = validation.Payload;
            if (validation.Status != DeviceLicenseStatus.Valid)
            {
                DatabaseCloudBackupService.WriteLog(
                    "",
                    false,
                    $"Scheduled database backup stopped because the device license status is {validation.Status}.");
                Environment.ExitCode = 1;
                return;
            }

            var results = DatabaseCloudBackupService.RunDueAsync(force: false)
                .GetAwaiter()
                .GetResult();
            if (results.Any(result => !result.Success))
                Environment.ExitCode = 1;
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            DatabaseCloudBackupService.WriteLog(
                "",
                false,
                exception.Message);
        }
    }
}
