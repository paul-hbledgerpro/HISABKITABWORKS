using ManagerPaperworkSystem.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ManagerPaperworkSystem.WinForms;

internal static class StartupFlow
{
    public static bool EnsureLicenseReady()
    {
        var deviceLicense = DeviceLicenseService.ValidateInstalledLicense();
        if (deviceLicense.Status == DeviceLicenseStatus.Valid)
            return true;

        if (deviceLicense.Status == DeviceLicenseStatus.Expired)
        {
            LicenseRuntime.IsReadOnly = true;
            MessageBox.Show(
                deviceLicense.Message + "\n\nImport a renewed device license to restore editing.",
                "Subscription Expired", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return true;
        }

        if (deviceLicense.Status == DeviceLicenseStatus.Invalid)
            MessageBox.Show(deviceLicense.Message, "Device License Invalid", MessageBoxButtons.OK, MessageBoxIcon.Error);
        using var form = new DeviceActivationForm();
        return form.ShowDialog() == DialogResult.OK;
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
        using var form = new DeviceActivationForm();
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
