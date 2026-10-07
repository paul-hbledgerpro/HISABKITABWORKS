using System.Diagnostics;

namespace ManagerPaperworkSystem.WinForms;

internal static class WhatsNewStartupService
{
    public static void ShowOnce()
    {
        try
        {
            var version = AppUpdateStartupService.CurrentVersion;
            var notes = ReleaseHighlights.Load();
            if (notes.Version != version) return;
            var store = new WhatsNewStateStore(Path.Combine(AppBootstrap.AppDataPath, "ReleaseNotices"));
            using var acknowledgement = store.TryBegin(version);
            if (acknowledgement is null) return;
            using var form = new WhatsNewForm(notes);
            if (form.ShowDialog() == DialogResult.OK) acknowledgement.Complete();
        }
        catch (Exception ex)
        {
            // A damaged preference folder must never prevent accounting startup.
            Debug.WriteLine("Release notice: " + ex.Message);
        }
    }
}
