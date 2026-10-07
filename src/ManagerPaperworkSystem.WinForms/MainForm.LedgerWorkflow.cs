using System.Diagnostics;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.Core.Models;
using Microsoft.EntityFrameworkCore;
namespace ManagerPaperworkSystem.WinForms;

internal sealed partial class MainForm
{
    private DateOnly _ledgerFrom = LedgerMonthService.Month(DateOnly.FromDateTime(DateTime.Today));
    private DateOnly _ledgerTo = DateOnly.FromDateTime(DateTime.Today);
    private int _ledgerPeriodMode;
    private bool _showingShiftResult;
    private readonly HashSet<string> _openingPrompts = [];

    private static void ShowShiftResult(IWin32Window owner, string caption, decimal? variance, string message)
    {
        using var form = new ShiftDropResultForm(caption, variance, message);
        form.ShowDialog(owner);
    }
    private Task RecordShiftDropAsync()
    {
        if (LicenseRuntime.IsReadOnly) return Task.CompletedTask;
        var storeId = _currentStoreId; var connection = CurrentStoreConnectionString();
        var business = CurrentLicensedBusiness();
        var settings = PortalSyncSettingsStore.Load().Stores.SingleOrDefault(x => x.BusinessId == business?.BusinessId && x.DatabaseName == business?.DatabaseName);
        if (settings is null) { MessageBox.Show(this, "Save automatic Z-report setup for this store first."); return Task.CompletedTask; }
        using var form = new ShiftDropEntryForm(settings.BusinessName);
        var batch = form.Batch;
        var drop = form.Drop;
        var payout = form.Payout;
        var reason = form.Reason;
        var save = form.SaveButton;
        form.FormClosing += (_, e) => { if (!save.Enabled) e.Cancel = true; };
        save.Click += async (_, _) =>
        {
            save.Enabled = false; batch.Enabled = drop.Enabled = payout.Enabled = reason.Enabled = false;
            try
            {
                await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
                var batchNumber = batch.Text.Trim(); var replace = false;
                var existing = await db.ShiftLogs.Where(x => x.StoreId == storeId && x.ShiftNo == batchNumber && !x.IsCorrection && (x.CashDropReceived != 0 || x.RegisterPayout != 0 || x.PayoutReason != "" || db.PendingShiftDrops.Any(p => p.StoreId == storeId && p.ShiftId == x.Id && p.Status == "Applied"))).ToListAsync();
                if (existing.Count > 0) { if (!_session.IsAdmin) throw new InvalidOperationException("This batch already has entered amounts. Use Add Correction."); if (MessageBox.Show(form, "Replace the existing cash drop and payout for this batch?", "Existing entry", MessageBoxButtons.YesNo) != DialogResult.Yes) return; replace = true; }
                var request = await ShiftDropWorkflow.RecordAsync(db, storeId, settings.Id, settings.PortalStoreName, batchNumber, drop.Value, payout.Value, reason.Text.Trim(), _session.UserId, _session.DisplayName, replace);
                if (request.Status == "Applied") { request.NotifiedUtc = DateTime.UtcNow; await db.SaveChangesAsync(); }
                form.Hide();
                ShowShiftResult(this, "Batch " + batchNumber, request.Variance, "Entry saved — waiting for batch to sync.");
                if (request.Status == "Pending")
                {
                    try { using var process = Process.Start(new ProcessStartInfo { FileName = Environment.ProcessPath!, UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--portal-sync-store", settings.Id.ToString("D"), "--portal-sync-report", "z-reports" } }); }
                    catch (Exception ex) { MessageBox.Show(this, "Your entry is saved. Automatic sync will retry. " + AppBootstrap.RedactSensitiveText(ex.Message)); }
                }
                save.Enabled = true; form.DialogResult = DialogResult.OK; form.Close();
            }
            catch (Exception ex) { MessageBox.Show(form, AppBootstrap.RedactSensitiveText(ex.GetBaseException().Message), "Record Shift Drop"); }
            finally { save.Enabled = true; batch.Enabled = drop.Enabled = payout.Enabled = reason.Enabled = true; }
        };
        ThemePreferences.ApplyTree(form); if (form.ShowDialog(this) == DialogResult.OK) ShowModule("Shift Cash Drop");
        return Task.CompletedTask;
    }

    private async Task ShowPendingResultAsync()
    {
        if (_showingShiftResult || OwnedForms.Any(x => x.Visible) || Modal || !Visible || LicenseRuntime.IsReadOnly) return;
        _showingShiftResult = true;
        try
        {
            var storeId = _currentStoreId; await using var db = CreateDb(); var result = await db.PendingShiftDrops.FirstOrDefaultAsync(x => x.StoreId == _currentStoreId && x.UserId == _session.UserId && x.NotifiedUtc == null && (x.Status == "Applied" || x.Status == "Attention" || (x.Status == "Pending" && x.LastAttemptUtc != null && x.Message != "Waiting for batch"))); if (result is null || storeId != _currentStoreId) return;
            ShowShiftResult(this, "Batch " + result.Batch, result.Variance, result.Message);
            result.NotifiedUtc = DateTime.UtcNow; await db.SaveChangesAsync();
            if (result.Status == "Applied" && storeId == _currentStoreId && _currentModule is "Shift Cash Drop" or "Cash On Hand" or "Cash & Sales Summary") ShowModule(_currentModule);
        }
        catch (Exception ex) { Debug.WriteLine(ex.Message); }
        finally { _showingShiftResult = false; }
    }

    private async Task ShowPendingDropsAsync()
    {
        var storeId = _currentStoreId; await using var db = CreateDb();
        using var form = new Form { Text = "Pending Shift Drops", ClientSize = new Size(850, 370) }; WinTheme.Apply(form);
        var grid = WinTheme.Grid(); grid.DataSource = await db.PendingShiftDrops.AsNoTracking().Where(x => x.StoreId == storeId && (x.Status == "Pending" || x.Status == "Attention")).Select(x => new { x.Id, x.Batch, x.Drop, x.Payout, x.Reason, x.Status, x.Message }).ToListAsync(); HideId(grid);
        var cancel = WinTheme.Button("Cancel selected entry"); cancel.Dock = DockStyle.Bottom; form.Controls.Add(grid); form.Controls.Add(cancel);
        cancel.Click += async (_, _) =>
        {
            try
            {
                var id = SelectedId(grid); if (id is null) return; var row = await db.PendingShiftDrops.SingleAsync(x => x.Id == id && x.StoreId == storeId); if (row.UserId != _session.UserId && !_session.IsAdmin) { MessageBox.Show(form, "Only the entry's user or an owner can cancel it."); return; }
                if (MessageBox.Show(form, "Cancel this waiting entry? You can then record corrected amounts.", "Cancel entry", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                var changed = await db.PendingShiftDrops.Where(x => x.Id == id && (x.Status == "Pending" || x.Status == "Attention")).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Cancelled")); if (changed == 0) MessageBox.Show(form, "This entry has already been applied. Review the shift instead."); form.Close();
            }
            catch (Exception ex) { MessageBox.Show(form, AppBootstrap.RedactSensitiveText(ex.GetBaseException().Message), "Pending Shift Drops"); }
        }; ThemePreferences.ApplyTree(form); form.ShowDialog(this);
    }
}
