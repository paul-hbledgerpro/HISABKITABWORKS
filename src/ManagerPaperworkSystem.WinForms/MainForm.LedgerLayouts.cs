using System.Diagnostics;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.Data.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.WinForms;

internal sealed partial class MainForm
{
    private LedgerWorkspaceHost? _ledgerWorkspace;
    private string LedgerPreferenceScope()
    {
        var connection = CurrentStoreConnectionString();
        if (LooksLikeSqlServer(connection))
        {
            var parsed = new SqlConnectionStringBuilder(connection);
            return parsed.DataSource.ToUpperInvariant() + "/" + parsed.InitialCatalog.ToUpperInvariant();
        }
        return connection;
    }
    private LedgerLayout LedgerChoice(string section) => LedgerLayoutPreferences.Load(LedgerPreferenceScope(), _session.UserId, section);
    private ToolStripMenuItem BuildLedgerViewMenu()
    {
        var menu = new ToolStripMenuItem("View") { ForeColor = Color.White };
        foreach (var section in new[] { "Shift Cash Drop", "Cash On Hand" })
        {
            var submenu = new ToolStripMenuItem(section + " Layout");
            foreach (var (label, value) in new[] { ("Classic Grid", LedgerLayout.ClassicGrid), ("Side Entry Grid (default)", LedgerLayout.SideEntryGrid), ("Guided Grid", LedgerLayout.GuidedGrid) })
            {
                var item = MenuItem(label, (_, _) =>
                {
                    try
                    {
                        if (_ledgerWorkspace?.Entry.IsBusy == true) { MessageBox.Show(this, "Finish saving the current entry before changing layouts."); return; }
                        LedgerLayoutPreferences.Save(LedgerPreferenceScope(), _session.UserId, section, value);
                        if (_ledgerWorkspace is { IsDisposed: false } host && host.Section == section && _currentModule == section) host.ApplyLayout(value);
                    }
                    catch (Exception ex) { MessageBox.Show(this, AppBootstrap.RedactSensitiveText(ex.Message), "Layout preference"); }
                });
                item.Tag = value; submenu.DropDownItems.Add(item);
            }
            submenu.DropDownOpening += (_, _) => { foreach (ToolStripMenuItem item in submenu.DropDownItems) item.Checked = item.Tag is LedgerLayout value && value == LedgerChoice(section); };
            menu.DropDownItems.Add(submenu);
        }
        return menu;
    }
    private static AppDbContext CapturedLedgerDb(string connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        if (LooksLikeSqlServer(connection)) options.UseSqlServer(connection); else options.UseSqlite(connection);
        return new AppDbContext(options.Options);
    }
    private static async Task<(string[] Payees, string[] Reasons)> LoadPayoutChoicesAsync(int storeId, string connection)
    {
        await using var db = CapturedLedgerDb(connection);
        return (await db.Vendors.AsNoTracking().Where(x => x.StoreId == storeId).Select(x => x.Name).ToArrayAsync(),
            await db.Purposes.AsNoTracking().Where(x => x.StoreId == storeId).Select(x => x.Name).ToArrayAsync());
    }
    private void EnsureLedgerEntryStore(int storeId, string connection)
    {
        if (LicenseRuntime.IsReadOnly) throw new InvalidOperationException("This license is currently read-only.");
        if (_currentStoreId != storeId || CurrentStoreConnectionString() != connection) throw new InvalidOperationException("The selected store changed. Reopen this section before entering cash.");
    }
    private async Task SaveLedgerShiftAsync(LedgerEntryDraft draft, int storeId, string connection, IWin32Window owner)
    {
        EnsureLedgerEntryStore(storeId, connection);
        var business = CurrentLicensedBusiness();
        var settings = PortalSyncSettingsStore.Load().Stores.SingleOrDefault(x => x.BusinessId == business?.BusinessId && x.DatabaseName == business?.DatabaseName);
        if (settings is null) throw new InvalidOperationException("Save automatic Z-report setup for this store first.");
        var batch = long.TryParse(draft.Batch, out var number) ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : draft.Batch;
        await using var db = CapturedLedgerDb(connection);
        var replace = false;
        var entered = await db.ShiftLogs.AnyAsync(x => x.StoreId == storeId && x.ShiftNo == batch && !x.IsCorrection &&
            (x.CashDropReceived != 0 || x.RegisterPayout != 0 || x.PayoutReason != "" || db.PendingShiftDrops.Any(p => p.StoreId == storeId && p.ShiftId == x.Id && p.Status == "Applied")));
        if (entered)
        {
            if (!_session.IsAdmin) throw new InvalidOperationException("This batch already has entered amounts. Use Add Correction.");
            if (MessageBox.Show(owner, "Replace the existing cash drop and payout for this batch?", "Existing entry", MessageBoxButtons.YesNo) != DialogResult.Yes) throw new OperationCanceledException();
            replace = true;
        }
        EnsureLedgerEntryStore(storeId, connection);
        var result = await ShiftDropWorkflow.RecordAsync(db, storeId, settings.Id, settings.PortalStoreName, batch,
            draft.Amount, draft.Payout, draft.Reason, _session.UserId, _session.DisplayName, replace, draft.Payee);
        // Financial posting has committed. Notification failure must not invite a duplicate save.
        if (result.Status == "Applied")
        {
            try { result.NotifiedUtc = DateTime.UtcNow; await db.SaveChangesAsync(); }
            catch (Exception ex) { Debug.WriteLine(AppBootstrap.RedactSensitiveText(ex.Message)); }
        }
        ShowShiftResult(owner, settings.BusinessName + " — Batch " + batch, result.Variance,
            result.Status == "Applied" ? result.Message : "Entry saved — waiting for this store's batch to sync.");
        if (result.Status == "Pending" && !DemoRuntime.IsEnabled)
        {
            try { using var process = Process.Start(new ProcessStartInfo { FileName = Environment.ProcessPath!, UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--portal-sync-store", settings.Id.ToString("D"), "--portal-sync-report", "z-reports" } }); }
            catch (Exception ex) { MessageBox.Show(owner, "Your entry is saved. Automatic sync will retry. " + AppBootstrap.RedactSensitiveText(ex.Message)); }
        }
    }
    private LedgerEntryPanel NewLedgerEntry(bool shift, int storeId, string connection, Func<Task> refresh)
    {
        return new LedgerEntryPanel(shift, async draft =>
        {
            EnsureLedgerEntryStore(storeId, connection);
            if (shift) await SaveLedgerShiftAsync(draft, storeId, connection, this);
            else
            {
                await using var db = CapturedLedgerDb(connection);
                await CashEntryService.SaveAsync(db, new CashOnHandEntry {
                    StoreId = storeId, Date = draft.Date, IsPayout = true, PayoutAmount = draft.Amount,
                    Description = draft.Reason, CreatedByUserId = _session.UserId, CreatedByName = _session.DisplayName
                }, draft.Payee, draft.Reason);
            }
            if (_currentStoreId == storeId && CurrentStoreConnectionString() == connection)
            {
                try { await refresh(); }
                catch (Exception ex) { MessageBox.Show(this, "The entry was saved, but the grid could not refresh. Click Refresh.\n" + AppBootstrap.RedactSensitiveText(ex.Message)); }
            }
        }, () => LoadPayoutChoicesAsync(storeId, connection));
    }
    private static void StackLegacyShiftDetails(TableLayoutPanel form)
    {
        var cells = Enumerable.Range(0, 4).Select(i => (Title: form.GetControlFromPosition(i, 0)!, Fields: form.GetControlFromPosition(i, 1)!)).ToArray();
        form.Controls.Clear(); form.ColumnStyles.Clear(); form.RowStyles.Clear(); form.ColumnCount = 1; form.RowCount = 8;
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); form.Dock = DockStyle.Top; form.Height = 850;
        for (var i = 0; i < cells.Length; i++)
        {
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); form.RowStyles.Add(new RowStyle(SizeType.Absolute, 175));
            form.Controls.Add(cells[i].Title, 0, i * 2); form.Controls.Add(cells[i].Fields, 0, i * 2 + 1);
        }
        if (form.Parent is Panel parent) parent.AutoScroll = true;
    }
}
