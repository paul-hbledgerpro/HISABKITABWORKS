using System.Text.Json;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.WinForms;

internal sealed partial class MainForm
{
    private readonly System.Windows.Forms.Timer _shortageEmailTimer = new() { Interval = 60_000 };
    private bool _sendingShiftShortages;

    private async Task DeliverShiftShortagesAsync()
    {
        if (_sendingShiftShortages || _loadingStores || IsDisposed || DemoRuntime.IsEnabled) return;
        _sendingShiftShortages = true;
        var storeId = _currentStoreId;
        var connection = CurrentStoreConnectionString();
        var storeGuid = LicenseRuntime.ActiveStoreGuid;
        try
        {
            await Task.Run(async () => {
                await using var db = CapturedLedgerDb(connection);
                await ShiftShortageDelivery.SendDueAsync(db, storeId, storeGuid);
            });
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Shortage email retry: " + AppBootstrap.RedactSensitiveText(ex.Message)); }
        finally { _sendingShiftShortages = false; }
    }

    private async Task ManageShiftShortageAsync()
    {
        if (!_session.IsAdmin && !_developerSettingsUnlocked) return;
        var storeId = _currentStoreId; var connection = CurrentStoreConnectionString();
        var storeGuid = LicenseRuntime.ActiveStoreGuid;
        var storeName = _session.StoreName;
        if (string.IsNullOrWhiteSpace(storeGuid)) { MessageBox.Show(this, "Select a licensed store first."); return; }
        try
        {
            await using var db = CapturedLedgerDb(connection);
            var saved = await db.ShiftShortageSettings.AsNoTracking().SingleOrDefaultAsync(x => x.StoreId == storeId);
            using var form = new Form { Text = "Shift Shortage Alerts — " + storeName, Size = new Size(760, 680),
                MinimumSize = new Size(680, 600), StartPosition = FormStartPosition.CenterParent, AutoScaleMode = AutoScaleMode.Dpi,
                BackColor = WinTheme.Bg, ForeColor = WinTheme.Text, Font = WinTheme.BodyFont(10) };
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 9 };
            foreach (var height in new[] { 38, 36, 36, 26, 100, 60, 30 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            var title = new Label { Text = storeName, Dock = DockStyle.Fill, Font = WinTheme.BoldFont(14) };
            var enabled = new CheckBox { Text = "Email the owner when a shift is short", Checked = saved?.Enabled == true, Dock = DockStyle.Fill };
            var limitRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            var limit = new NumericUpDown { DecimalPlaces = 2, Maximum = 99999999, Value = saved?.Threshold ?? 10, Width = 130, AccessibleName = "Shortage limit" };
            limitRow.Controls.Add(new Label { Text = "Send when shortage is more than $", AutoSize = true, Padding = new Padding(0, 5, 6, 0) });
            limitRow.Controls.Add(limit);
            var recipients = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
                AccessibleName = "Recipient email addresses", Text = string.Join(Environment.NewLine, JsonSerializer.Deserialize<string[]>(saved?.RecipientsJson ?? "[]") ?? []) };
            var description = new Label { Dock = DockStyle.Fill, Text = "The detailed shift report is emailed after the drop is saved and its report is available. New entries only. Saving changed settings cancels unsent alerts; an email already being sent may still arrive." };
            var history = WinTheme.Grid(); history.Dock = DockStyle.Fill; history.ReadOnly = true;
            async Task RefreshHistory() {
                await using var readDb = CapturedLedgerDb(connection);
                var rows = await readDb.ShiftShortageEmails.AsNoTracking().Where(x => x.StoreId == storeId).OrderByDescending(x => x.CreatedUtc).Take(50).ToListAsync();
                history.DataSource = rows.Select(x => new {
                    Batch = JsonSerializer.Deserialize<ShiftShortageReport>(x.ReportJson)?.Batch,
                    Status = x.Status == "Sent" ? "Accepted by email service" : x.Status,
                    Recorded = x.CreatedUtc.ToLocalTime(), x.Message
                }).ToList();
            }
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var close = WinTheme.Button("Close"); close.Click += (_, _) => form.Close();
            var save = WinTheme.Button("Save Settings", true); save.Width = 150;
            var refresh = WinTheme.Button("Refresh status"); refresh.Width = 150;
            refresh.Click += async (_, _) => { try { await RefreshHistory(); } catch (Exception ex) { MessageBox.Show(form, AppBootstrap.RedactSensitiveText(ex.Message)); } };
            save.Click += async (_, _) => {
                save.Enabled = false;
                try {
                    EnsureLedgerEntryStore(storeId, connection);
                    var addresses = string.IsNullOrWhiteSpace(recipients.Text) && !enabled.Checked ? [] : ShiftShortageService.ParseRecipients(recipients.Text);
                    await using var writeDb = CapturedLedgerDb(connection);
                    await ShiftShortageService.SaveSettingsAsync(writeDb, new ShiftShortageSettings {
                        StoreId = storeId, StoreGuid = storeGuid, StoreName = storeName, Enabled = enabled.Checked,
                        Threshold = limit.Value, RecipientsJson = JsonSerializer.Serialize(addresses),
                        UpdatedUtc = DateTime.UtcNow, UpdatedBy = _session.DisplayName
                    }, _session.IsAdmin || _developerSettingsUnlocked);
                    MessageBox.Show(form, "Shortage alert settings saved for " + storeName + ".", "Settings saved");
                    await RefreshHistory();
                } catch (Exception ex) { MessageBox.Show(form, AppBootstrap.RedactSensitiveText(ex.Message), "Shortage alerts"); }
                finally { if (!save.IsDisposed) save.Enabled = true; }
            };
            actions.Controls.AddRange([close, save, refresh]);
            root.Controls.Add(title, 0, 0); root.Controls.Add(enabled, 0, 1); root.Controls.Add(limitRow, 0, 2);
            root.Controls.Add(new Label { Text = "Recipient emails (one per line, up to 10)", Dock = DockStyle.Fill }, 0, 3);
            root.Controls.Add(recipients, 0, 4); root.Controls.Add(description, 0, 5);
            root.Controls.Add(new Label { Text = "Recent email delivery status", Dock = DockStyle.Fill }, 0, 6);
            root.Controls.Add(history, 0, 7); root.Controls.Add(actions, 0, 8); form.Controls.Add(root);
            await RefreshHistory(); WinTheme.Apply(form); form.StartPosition = FormStartPosition.CenterParent; form.ShowDialog(this);
        }
        catch (Exception ex) { MessageBox.Show(this, AppBootstrap.RedactSensitiveText(ex.Message), "Shortage alerts"); }
    }
}
