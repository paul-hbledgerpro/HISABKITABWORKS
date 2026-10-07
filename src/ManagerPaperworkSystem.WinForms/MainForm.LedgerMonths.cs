using ManagerPaperworkSystem.Core.Models;
using Microsoft.EntityFrameworkCore;
namespace ManagerPaperworkSystem.WinForms;

internal sealed partial class MainForm
{
    private Control AddLedgerPeriodBar(Control body, string module)
    {
        var host = new Panel { Dock = DockStyle.Fill }; body.Dock = DockStyle.Fill;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 45, WrapContents = false, AutoScroll = true, Padding = new Padding(4) };
        var period = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 }; period.Items.AddRange(["Current Month", "Previous Month", "Custom"]);
        var from = WinTheme.DatePicker(); from.Width = 130; from.Value = _ledgerFrom.ToDateTime(TimeOnly.MinValue);
        var to = WinTheme.DatePicker(); to.Width = 130; to.Value = _ledgerTo.ToDateTime(TimeOnly.MinValue);
        var current = LedgerMonthService.Month(DateOnly.FromDateTime(DateTime.Today)); period.SelectedIndex = _ledgerPeriodMode;
        period.SelectedIndexChanged += (_, _) => { if (period.SelectedIndex == 2) return; var start = period.SelectedIndex == 0 ? current : current.AddMonths(-1); from.Value = start.ToDateTime(TimeOnly.MinValue); to.Value = (period.SelectedIndex == 0 ? DateOnly.FromDateTime(DateTime.Today) : current.AddDays(-1)).ToDateTime(TimeOnly.MinValue); };
        from.Enabled = to.Enabled = period.SelectedIndex == 2;
        period.SelectedIndexChanged += (_, _) => from.Enabled = to.Enabled = period.SelectedIndex == 2;
        var view = WinTheme.Button("View"); view.Width = 80; view.Height = 30; view.Click += (_, _) => { if (from.Value.Date > to.Value.Date) { MessageBox.Show(this, "Choose a valid date range."); return; } _ledgerPeriodMode = period.SelectedIndex; _ledgerFrom = DateOnly.FromDateTime(from.Value); _ledgerTo = DateOnly.FromDateTime(to.Value); ShowModule(module); };
        bar.Controls.Add(new Label { Text = "Period", Width = 45, TextAlign = ContentAlignment.MiddleLeft }); bar.Controls.Add(period); bar.Controls.Add(from); bar.Controls.Add(to); bar.Controls.Add(view);
        using var db = CreateDb(); var closed = db.LedgerMonths.AsNoTracking().Any(x => x.StoreId == _currentStoreId && x.IsClosed && x.Month <= _ledgerTo && x.Month.AddMonths(1) > _ledgerFrom);
        if (closed) { ApplyReadOnlyMode(body); bar.Controls.Add(new Label { Text = "Closed period — review only", AutoSize = true, ForeColor = WinTheme.Copper }); }
        host.Controls.Add(body); host.Controls.Add(bar); return host;
    }

    private bool _promptingOpeningCash;
    private async Task PromptOpeningCashAsync()
    {
        if (_promptingOpeningCash || OwnedForms.Any(x => x.Visible) || !Visible || _currentModule != "Cash On Hand") return;
        _promptingOpeningCash = true;
        try { await PromptOpeningCashCoreAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        finally { _promptingOpeningCash = false; }
    }

    private async Task PromptOpeningCashCoreAsync()
    {
        if (_currentModule != "Cash On Hand" || LicenseRuntime.IsReadOnly || IsDisposed) return;
        var month = LedgerMonthService.Month(DateOnly.FromDateTime(DateTime.Today)); var storeId = _currentStoreId;
        var key = CurrentStoreConnectionString() + "|" + storeId + "|" + month;
        if (_openingPrompts.Contains(key)) return;
        await using var db = CreateDb();
        if (await db.CashOnHand.AnyAsync(x => x.StoreId == storeId && x.Date == month && x.Reference == "CARRY_FORWARD")) return;
        if (storeId != _currentStoreId || _currentModule != "Cash On Hand") return;
        _openingPrompts.Add(key);
        using var form = new Form { Text = $"Opening Cash — {month:MMMM yyyy}", ClientSize = new Size(430, 175), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false }; WinTheme.Apply(form);
        var label = new Label { Text = $"Enter this store's opening cash on hand for {month:MMMM yyyy}.", Dock = DockStyle.Top, Height = 60, Padding = new Padding(12) };
        var amount = new NumericUpDown { DecimalPlaces = 2, Maximum = 99999999, Dock = DockStyle.Top, Font = WinTheme.BodyFont(14) };
        var save = WinTheme.Button("Save Opening Cash", true); save.Dock = DockStyle.Bottom; form.Controls.Add(amount); form.Controls.Add(label); form.Controls.Add(save); form.AcceptButton = save;
        form.FormClosing += (_, e) => { if (!save.Enabled) e.Cancel = true; };
        save.Click += async (_, _) => { save.Enabled = false; try { await LedgerMonthService.SetOpeningAsync(db, storeId, month, amount.Value, _session.UserId, _session.DisplayName); save.Enabled = true; form.DialogResult = DialogResult.OK; form.Close(); } catch (Exception ex) { MessageBox.Show(form, AppBootstrap.RedactSensitiveText(ex.GetBaseException().Message)); } finally { save.Enabled = true; } };
        ThemePreferences.ApplyTree(form); if (form.ShowDialog(this) == DialogResult.OK && _currentStoreId == storeId) ShowModule("Cash On Hand");
    }

    private async Task ManageLedgerMonthAsync()
    {
        if (!_session.IsAdmin || LicenseRuntime.IsReadOnly) { MessageBox.Show(this, "An owner must close or reopen a month. Previous months are available through Period on the transaction screens."); return; }
        var storeId = _currentStoreId; await using var db = CreateDb();
        using var form = new Form { Text = "Month Close / History", ClientSize = new Size(520, 300) }; WinTheme.Apply(form);
        var month = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "MMMM yyyy", ShowUpDown = true, Value = DateTime.Today.AddMonths(-1), Dock = DockStyle.Top };
        var explanation = new Label { Text = "Closed months stay in the database and remain available through Period / Custom. Reopening allows corrections and late imports.\n\nConfirm that all shifts and daily reports for the month are complete before closing.", Dock = DockStyle.Top, Height = 100, Padding = new Padding(10) };
        var history = WinTheme.Grid(); history.DataSource = await db.LedgerMonths.AsNoTracking().Where(x => x.StoreId == storeId).OrderByDescending(x => x.Month).Select(x => new { x.Month, x.IsClosed, x.ClosedBy, x.ClosedUtc, x.OpeningCash }).ToListAsync();
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48 };
        form.FormClosing += (_, e) => { if (!actions.Enabled) e.Cancel = true; };
        foreach (var closed in new[] { true, false })
        {
            var button = WinTheme.Button(closed ? "Close Month" : "Reopen Month", closed); button.Width = 180; actions.Controls.Add(button); button.Click += async (_, _) =>
            {
                var target = LedgerMonthService.Month(DateOnly.FromDateTime(month.Value));
                if (MessageBox.Show(form, $"{(closed ? "Close" : "Reopen")} {target:MMMM yyyy} for the selected store?", "Month", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                actions.Enabled = false; try { await LedgerMonthService.SetClosedAsync(db, storeId, target, closed, _session.DisplayName); actions.Enabled = true; form.DialogResult = DialogResult.OK; form.Close(); } catch (Exception ex) { MessageBox.Show(form, AppBootstrap.RedactSensitiveText(ex.GetBaseException().Message)); } finally { actions.Enabled = true; }
            };
        }
        form.Controls.Add(history); form.Controls.Add(explanation); form.Controls.Add(month); form.Controls.Add(actions); ThemePreferences.ApplyTree(form);
        if (form.ShowDialog(this) == DialogResult.OK) { _ledgerPeriodMode = 0; _ledgerFrom = LedgerMonthService.Month(DateOnly.FromDateTime(DateTime.Today)); _ledgerTo = DateOnly.FromDateTime(DateTime.Today); ShowModule(_currentModule); }
    }
}
