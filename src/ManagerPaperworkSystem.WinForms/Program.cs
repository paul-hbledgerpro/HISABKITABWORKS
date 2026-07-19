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

        var portalStoreArgument = Array.FindIndex(
            args,
            x => x.Equals("--portal-sync-store", StringComparison.OrdinalIgnoreCase));
        if (portalStoreArgument >= 0)
        {
            Guid? storeConfigurationId = null;
            if (portalStoreArgument + 1 < args.Length &&
                Guid.TryParse(args[portalStoreArgument + 1], out var parsedId))
                storeConfigurationId = parsedId;
            RunPortalSync(storeConfigurationId);
            return;
        }

        if (args.Any(x => x.Equals("--portal-sync", StringComparison.OrdinalIgnoreCase)))
        {
            RunPortalSync(null);
            return;
        }

        try
        {
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

    private static void RunPortalSync(Guid? storeConfigurationId)
    {
        try
        {
            // Scheduled execution must never open an activation or error dialog.
            if (DeviceLicenseService.ValidateInstalledLicense().Status != DeviceLicenseStatus.Valid)
                return;
            using var services = AppBootstrap.BuildServices();
            AppBootstrap.InitializeDatabaseAsync(services).GetAwaiter().GetResult();
            LicensedBusinessService.SynchronizeAsync(services).GetAwaiter().GetResult();
            var paths = services.GetRequiredService<ManagerPaperworkSystem.Core.Services.IAppPaths>();
            PortalSyncService.RunDueAsync(
                    paths,
                    force: false,
                    visibleChrome: false,
                    onlyStoreConfigurationId: storeConfigurationId)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception)
        {
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
}
