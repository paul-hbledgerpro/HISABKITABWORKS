using Microsoft.Extensions.DependencyInjection;

namespace ManagerPaperworkSystem.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

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
            RunPortalSync(storeConfigurationId, ParsePortalSyncReportKind(args));
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

    private static void RunPortalSync(
        Guid? storeConfigurationId,
        PortalSyncReportKind? reportKind)
    {
        try
        {
            // Scheduled execution must never open an activation or error dialog.
            var licenseStatus = DeviceLicenseService.ValidateInstalledLicense().Status;
            if (licenseStatus != DeviceLicenseStatus.Valid)
            {
                PortalSyncService.WriteDiagnostic(
                    "",
                    false,
                    $"Scheduled POS sync stopped because the device license status is {licenseStatus}.");
                Environment.ExitCode = 1;
                return;
            }
            using var services = AppBootstrap.BuildServices();
            AppBootstrap.InitializeDatabaseAsync(services).GetAwaiter().GetResult();
            LicensedBusinessService.SynchronizeAsync(services).GetAwaiter().GetResult();
            var paths = services.GetRequiredService<ManagerPaperworkSystem.Core.Services.IAppPaths>();
            var results = PortalSyncService.RunDueAsync(
                    paths,
                    force: false,
                    visibleChrome: false,
                    onlyStoreConfigurationId: storeConfigurationId,
                    onlyReportKind: reportKind)
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
