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

        if (args.Length >= 4 && args[0].Equals("--export-device-request", StringComparison.OrdinalIgnoreCase))
        {
            DeviceLicenseService.ExportRequest(args[3], args[1], args[2]);
            return;
        }

        if (args.Any(x => x.Equals("--device-activation", StringComparison.OrdinalIgnoreCase)))
        {
            Application.Run(new DeviceActivationForm());
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
}
