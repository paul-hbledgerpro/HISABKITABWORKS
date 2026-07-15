using ManagerPaperworkSystem.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ManagerPaperworkSystem.WinForms;

internal static class StartupFlow
{
    public static bool EnsureLicenseReady()
    {
        var hasConnection = File.Exists(AppBootstrap.ConnectionSettingsPath);
        var hasLicense = File.Exists(AppBootstrap.LicenseFilePath);

        if (!hasConnection && !hasLicense)
        {
            using var form = new LicenseActivationForm(keyOnly: false);
            return form.ShowDialog() == DialogResult.OK;
        }

        if (hasConnection && !hasLicense)
        {
            using var form = new LicenseActivationForm(keyOnly: true);
            return form.ShowDialog() == DialogResult.OK;
        }

        if (!hasConnection)
        {
            using var form = new LicenseActivationForm(keyOnly: false);
            return form.ShowDialog() == DialogResult.OK;
        }

        return true;
    }

    public static bool HandleDatabaseStartupFailure(Exception ex)
    {
        var message =
            "HISAB KITAB could not connect to the saved database connection.\n\n" +
            $"{AppBootstrap.RedactSensitiveText(ex.Message)}\n\n" +
            "Click Yes to repair the connection by importing/entering the license again.\n" +
            "Click No to close the app.";

        if (MessageBox.Show(message, "Database Connection", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return false;

        AppBootstrap.ClearSavedConnectionSettings();
        using var form = new LicenseActivationForm(keyOnly: false);
        return form.ShowDialog() == DialogResult.OK;
    }

    public static bool EnsureSetupReady(IServiceProvider services)
    {
        var settings = services.GetRequiredService<ISettingsService>();
        var auth = services.GetRequiredService<IAuthService>();
        var configured = settings.IsConfiguredAsync().GetAwaiter().GetResult();
        var hasUsers = auth.HasAnyUsersAsync().GetAwaiter().GetResult();
        if (configured && hasUsers)
            return true;

        using var form = services.GetRequiredService<SetupForm>();
        return form.ShowDialog() == DialogResult.OK;
    }
}
