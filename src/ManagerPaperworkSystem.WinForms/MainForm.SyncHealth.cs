using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.WinForms;

internal sealed partial class MainForm
{
    private async Task ShowSyncHealthAsync()
    {
        try
        {
            var storeId = _currentStoreId; var name = _session.StoreName; var business = CurrentLicensedBusiness();
            var settings = PortalSyncSettingsStore.Load().Stores.FirstOrDefault(x => x.BusinessId == business?.BusinessId && x.DatabaseName == business?.DatabaseName);
            await using var db = CapturedLedgerDb(CurrentStoreConnectionString());
            var pending = await db.PendingShiftDrops.CountAsync(x => x.StoreId == storeId && x.Status == "Pending");
            var latest = await db.ShiftLogs.AsNoTracking().Where(x => x.StoreId == storeId && !x.IsCorrection && x.PosReportKey != "")
                .OrderByDescending(x => x.Date).ThenByDescending(x => x.Id).Select(x => new { x.Date, x.ShiftNo }).FirstOrDefaultAsync();
            static string Local(DateTime? value) => value?.ToLocalTime().ToString("g") ?? "No successful run recorded";
            var text = $"{name}\r\n\r\nLatest imported shift: {(latest is null ? "None" : $"{latest.Date:MM/dd/yyyy} — Batch {latest.ShiftNo}")}\r\nPending cash drops: {pending}\r\n\r\n"
                + (settings is null ? "No portal schedule is configured. Contact your developer.\r\n" :
                    $"Z reports: {(settings.Enabled && settings.ZReportsEnabled ? "Enabled" : "Disabled")}\r\nLast attempt: {Local(settings.LastZReportAttemptUtc)}\r\nLast success: {Local(settings.LastZReportSuccessUtc)}\r\nReport date: {settings.LastZReportDate}\r\n{settings.LastZReportStatus}\r\n\r\nCash & Sales Summary last success: {Local(settings.LastCashSummarySuccessUtc)}\r\n{settings.LastCashSummaryStatus}\r\n")
                + $"\r\nLast off-device database backup: {Local(business is null ? null : DatabaseCloudBackupService.LastSuccessfulBackup(business))}";
            using var dialog = new Form { Text = "Sync Health", ClientSize = new Size(720, 540), StartPosition = FormStartPosition.CenterParent };
            WinTheme.Apply(dialog);
            var content = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = AppBootstrap.RedactSensitiveText(text) };
            var ok = WinTheme.Button("OK"); ok.Dock = DockStyle.Bottom; ok.Height = 42; ok.DialogResult = DialogResult.OK;
            dialog.Controls.Add(content); dialog.Controls.Add(ok); dialog.AcceptButton = ok; dialog.ShowDialog(this);
        }
        catch (Exception ex) { MessageBox.Show(this, AppBootstrap.RedactSensitiveText(ex.Message), "Sync Health"); }
    }
}
