using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ManagerPaperworkSystem.WinForms;

internal sealed partial class MainForm
{
    private readonly Stopwatch _demoPresentationClock = new();
    private StreamWriter? _demoCueWriter;
    private bool _demoPresentationCanClose;
    private DemoPresentationRecorder? _demoRecorder;

    private async Task PlayDetailedDemoPresentationAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var readyPath = Path.Combine(outputDirectory, "presentation-ready.txt");
        var completePath = Path.Combine(outputDirectory, "presentation-complete.txt");
        var errorPath = Path.Combine(outputDirectory, "presentation-error.txt");
        var cuesPath = Path.Combine(outputDirectory, "presentation-cues.txt");
        var goPath = Path.Combine(outputDirectory, "presentation-go.txt");

        try
        {
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(0, 0, 2560, 1440);
            TopMost = false;
            ShowInTaskbar = false;
            FormClosing += PreventPrematureDemoPresentationClose;
            await Task.Delay(1500);

            File.WriteAllText(readyPath, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            var goDeadline = DateTime.UtcNow.AddMinutes(2);
            while (!File.Exists(goPath) && DateTime.UtcNow < goDeadline)
                await Task.Delay(100);
            if (!File.Exists(goPath))
                throw new TimeoutException("The presentation recorder did not send its start signal.");
            _demoRecorder = new DemoPresentationRecorder(this, outputDirectory, 5);
            _demoRecorder.Start();
            await Task.Delay(1000);

            _demoCueWriter = new StreamWriter(cuesPath, false) { AutoFlush = true };
            _demoPresentationClock.Restart();

            await DemoSceneAsync("introduction", "WELCOME TO HISAB KITAB WORKS", 12, async () =>
            {
                ShowModule("Dashboard");
                await Task.Delay(8500);
            });

            await DemoSceneAsync("dashboard", "LIVE BUSINESS DASHBOARD", 30, async () =>
            {
                ShowModule("Dashboard");
                await DemoPointAtTextAsync("TOTAL SALES");
                await DemoPointAtTextAsync("CASH ON HAND");
                await DemoPointAtTextAsync("NET PROFIT");
                await DemoPointAtTextAsync("RECENT TRANSACTIONS");
            });

            await DemoSceneAsync("multiple-stores", "MULTIPLE STORES — ONE LOGIN", 25, async () =>
            {
                if (_storeCombo.Items.Count > 1)
                {
                    await DemoMoveCursorAsync(_storeCombo);
                    _storeCombo.DroppedDown = true;
                    await Task.Delay(1800);
                    _storeCombo.SelectedIndex = _storeCombo.SelectedIndex == 0 ? 1 : 0;
                    _storeCombo.DroppedDown = false;
                    await Task.Delay(3500);
                }
                ShowModule("Dashboard");
                await Task.Delay(5000);
            });

            await DemoSceneAsync("cash-sales-summary", "CASH & SALES SUMMARY", 55, async () =>
            {
                ShowModule("Cash & Sales Summary");
                await Task.Delay(2200);
                var grids = DemoControls<DataGridView>().Where(grid => grid.Rows.Count > 0).ToList();
                if (grids.Count > 0)
                {
                    await DemoSelectFirstRowAsync(grids[0]);
                    await Task.Delay(2500);
                }
                var tabs = DemoControls<TabControl>().FirstOrDefault();
                if (tabs is not null)
                {
                    for (var index = 1; index < tabs.TabPages.Count; index++)
                    {
                        tabs.SelectedIndex = index;
                        await Task.Delay(1800);
                    }
                    tabs.SelectedIndex = 0;
                }
                if (DemoNamed<TextBox>("DemoCashSalesRegisterPayout") is { } registerPayout)
                    await DemoTypeAsync(registerPayout, "24.50");
                if (DemoNamed<TextBox>("DemoCashSalesPayoutReason") is { } payoutReason)
                    await DemoTypeAsync(payoutReason, "Register supply purchase");
                await DemoClickAsync("SAVE / UPDATE");
            });

            await DemoSceneAsync("shift-cash-drop", "SHIFT CASH DROP & Z-REPORT REVIEW", 60, async () =>
            {
                ShowModule("Shift Cash Drop");
                await Task.Delay(2200);
                var grid = DemoControls<DataGridView>().FirstOrDefault(item => item.Rows.Count > 0);
                if (grid is not null)
                {
                    await DemoSelectFirstRowAsync(grid);
                    await Task.Delay(2500);
                }
                if (DemoNamed<TextBox>("DemoShiftCashDrop") is { } shiftDrop)
                    await DemoTypeAsync(shiftDrop, "1006.55");
                if (DemoNamed<TextBox>("DemoShiftRegisterPayout") is { } shiftPayout)
                    await DemoTypeAsync(shiftPayout, "0.00");
                await DemoClickAsync("SAVE / UPDATE CASH DROP");
                await Task.Delay(2500);
                await DemoPointAtTextAsync("VARIANCE");
                await DemoPointAtTextAsync("EXPORT TO EXCEL");
            });

            await DemoSceneAsync("cash-on-hand", "CASH ON HAND ENTRY", 60, async () =>
            {
                ShowModule("Cash On Hand");
                await Task.Delay(2200);
                // The new entry form runs a modal message loop. Queue the demo
                // typing on that loop so the recording can operate the dialog.
                using var entryTimer = new System.Windows.Forms.Timer { Interval = 200 };
                entryTimer.Tick += async (_, _) =>
                {
                    var entryForm = Application.OpenForms.OfType<CashEntryForm>().FirstOrDefault();
                    if (entryForm is null) return;
                    entryTimer.Stop();
                    try
                    {
                        if (DemoNamed<TextBox>("CashEntryAmount", entryForm) is { } cashAdded)
                            await DemoTypeAsync(cashAdded, "850.00");
                        if (DemoNamed<TextBox>("CashEntryNote", entryForm) is { } cashDescription)
                            await DemoTypeAsync(cashDescription, "Daily safe deposit");
                        await DemoClickAsync("SAVE CASH", entryForm);
                    }
                    catch
                    {
                        entryForm.Close();
                    }
                };
                entryTimer.Start();
                await DemoClickAsync("ADD CASH");
                entryTimer.Stop();
                await Task.Delay(3000);
                var grid = DemoControls<DataGridView>().FirstOrDefault(item => item.Rows.Count > 0);
                if (grid is not null) await DemoSelectFirstRowAsync(grid);
                await DemoPointAtTextAsync("OPENING BALANCE");
                await DemoPointAtTextAsync("CLOSING BALANCE");
            });

            await DemoSceneAsync("check-payout", "CHECK PAYOUT WORKFLOW", 60, async () =>
            {
                ShowModule("Check Payout");
                await Task.Delay(2200);
                await DemoClickAsync("NEW CHECK");
                var checkFields = new (string Name, string Value)[]
                {
                    ("DemoCheckVendor", "Midwest Distribution"),
                    ("DemoCheckAmount", "475.25"),
                    ("DemoCheckPurpose", "Weekly inventory order"),
                    ("DemoCheckNumber", "4512")
                };
                foreach (var field in checkFields)
                    if (DemoNamed<TextBox>(field.Name) is { } box) await DemoTypeAsync(box, field.Value);
                await DemoPointAtTextAsync("CHECK DETAILS");
                await DemoClickAsync("SAVE");
                await Task.Delay(2500);
                await DemoPointAtTextAsync("PRINT CHECK");
                await DemoPointAtTextAsync("CLEAR");
            });

            await DemoSceneAsync("operations-hub", "OPERATIONS HUB", 35, async () =>
            {
                ShowModule("Operations Hub");
                await Task.Delay(2200);
                await DemoPointAtTextAsync("SHIFT CASH DROP");
                await DemoPointAtTextAsync("AVAILABLE BALANCE");
                await DemoPointAtTextAsync("NET MOVEMENT");
                await DemoPointAtTextAsync("PURCHASES");
                await DemoPointAtTextAsync("NET PROFIT");
            });

            await DemoSceneAsync("vendors-purposes", "VENDORS & PURPOSES", 50, async () =>
            {
                ShowModule("Vendors & Purposes");
                await Task.Delay(2200);
                if (DemoNamed<ComboBox>("DemoVendorType") is { } vendorType) vendorType.SelectedIndex = 0;
                var vendorFields = new (string Name, string Value)[]
                {
                    ("DemoVendorName", "Demo Fuel & Supply"),
                    ("DemoVendorContact", "Jordan Lee"),
                    ("DemoVendorPhone", "847-555-0184"),
                    ("DemoVendorEmail", "orders@example.test"),
                    ("DemoVendorNotes", "Preferred weekly supplier")
                };
                foreach (var field in vendorFields)
                    if (DemoNamed<TextBox>(field.Name) is { } box) await DemoTypeAsync(box, field.Value);
                await DemoClickAsync("ADD");
                await Task.Delay(2200);
                var grid = DemoControls<DataGridView>().FirstOrDefault(item => item.Rows.Count > 0);
                if (grid is not null) await DemoSelectFirstRowAsync(grid);
                await DemoPointAtTextAsync("UPDATE SELECTED");
                await DemoPointAtTextAsync("ADD CORRECTION");
            });

            await DemoSceneAsync("purchases", "PURCHASES & INVOICE LINE ITEMS", 65, async () =>
            {
                ShowModule("Purchases");
                await Task.Delay(2200);
                await DemoClickAsync("NEW PURCHASE");
                var purchaseFields = new (string Name, string Value)[]
                {
                    ("DemoPurchaseVendor", "Demo Fuel & Supply"),
                    ("DemoPurchaseInvoiceNumber", "DEMO-10045"),
                    ("DemoPurchaseTax", "0.00"),
                    ("DemoPurchaseTotal", "210.00"),
                    ("DemoPurchaseLineProduct", "Premium Coffee 12oz"),
                    ("DemoPurchaseLineQuantity", "24"),
                    ("DemoPurchaseLineUnitCost", "8.75")
                };
                foreach (var field in purchaseFields)
                    if (DemoNamed<TextBox>(field.Name) is { } box) await DemoTypeAsync(box, field.Value);
                await DemoClickAsync("ADD LINE");
                await Task.Delay(1800);
                await DemoClickAsync("ADD");
                await Task.Delay(2600);
                await DemoPointAtTextAsync("IMPORT PURCHASES");
                await DemoPointAtTextAsync("EMAIL INVOICES");
                await DemoPointAtTextAsync("OPEN INVOICE PDF");
            });

            await DemoSceneAsync("bank-statement", "BANK STATEMENT RECONCILIATION", 60, async () =>
            {
                ShowModule("Bank Statement");
                await Task.Delay(2200);
                var search = DemoControls<TextBox>().FirstOrDefault(box => !box.ReadOnly && box.Enabled);
                if (search is not null) await DemoTypeAsync(search, "utility");
                await Task.Delay(2000);
                if (search is not null) await DemoTypeAsync(search, "");
                var grid = DemoControls<DataGridView>().FirstOrDefault(item => item.Rows.Count > 0);
                if (grid is not null) await DemoSelectFirstRowAsync(grid);
                await DemoPointAtTextAsync("CATEGORIZE SELECTED");
                await DemoPointAtTextAsync("MATCH TRANSACTION");
                await DemoClickAsync("MARK REVIEWED");
                await Task.Delay(2200);
                await DemoPointAtTextAsync("IMPORT STATEMENT");
                await DemoPointAtTextAsync("ENDING BALANCE");
            });

            await DemoSceneAsync("product-costs", "PRODUCT COST TRACKING", 45, async () =>
            {
                ShowModule("Product Costs");
                await Task.Delay(2200);
                await DemoPointAtTextAsync("SEARCH PRODUCT");
                await DemoPointAtTextAsync("ALL SUPPLIERS");
                var grid = DemoControls<DataGridView>().FirstOrDefault(item => item.Rows.Count > 0);
                if (grid is not null) await DemoSelectFirstRowAsync(grid);
                await DemoPointAtTextAsync("LATEST UNIT COST");
                await DemoPointAtTextAsync("IMPORT INVOICE");
                await DemoPointAtTextAsync("UPDATE COSTS");
                await DemoPointAtTextAsync("ADD CORRECTION");
            });

            await DemoSceneAsync("price-alerts", "PRICE ALERTS", 45, async () =>
            {
                ShowModule("Price Alerts");
                await Task.Delay(2200);
                var grid = DemoControls<DataGridView>().FirstOrDefault(item => item.Rows.Count > 0);
                if (grid is not null) await DemoSelectFirstRowAsync(grid);
                await DemoClickAsync("MARK READ");
                await Task.Delay(1600);
                await DemoPointAtTextAsync("CREATE RULE");
                await DemoPointAtTextAsync("UPDATE SELECTED");
                await DemoClickAsync("RESOLVE SELECTED");
                await Task.Delay(2200);
                await DemoPointAtTextAsync("HIGH PRIORITY");
            });

            await DemoSceneAsync("scheduling", "EMPLOYEE SCHEDULING", 95, async () =>
            {
                ShowModule("Scheduling");
                await Task.Delay(2200);
                await DemoShowModalAsync(
                    new WeeklySchedulePlannerForm(CreateDb, _currentStoreId, _session.DisplayName),
                    28,
                    async form =>
                    {
                        var grid = DemoControls<DataGridView>(form).FirstOrDefault(item => item.Rows.Count > 0);
                        if (grid is not null) await DemoSelectFirstRowAsync(grid);
                        await DemoPointAtTextAsync("COPY PREVIOUS WEEK", form);
                        await DemoPointAtTextAsync("SAVE WEEK", form);
                        await DemoPointAtTextAsync("EXPORT PDF", form);
                    });
                await DemoShowModalAsync(
                    new ScheduleBuilderForm(CreateDb, _currentStoreId, _session.DisplayName),
                    22,
                    async form =>
                    {
                        var grid = DemoControls<DataGridView>(form).FirstOrDefault(item => item.Rows.Count > 0);
                        if (grid is not null) await DemoSelectFirstRowAsync(grid);
                        await DemoPointAtTextAsync("SAVE", form);
                    });
                ShowModule("Scheduling");
                await DemoPointAtTextAsync("MANAGE SCHEDULE");
                await DemoPointAtTextAsync("RUN PAYROLL");
            });

            await DemoSceneAsync("payroll", "PAYROLL WORKFLOW", 110, async () =>
            {
                ShowModule("Payroll");
                await Task.Delay(2200);
                await DemoShowModalAsync(
                    new EmployeeManagerForm(CreateDb, _currentStoreId, _session.DisplayName),
                    20,
                    async form =>
                    {
                        var grid = DemoControls<DataGridView>(form).FirstOrDefault(item => item.Rows.Count > 0);
                        if (grid is not null) await DemoSelectFirstRowAsync(grid);
                    });
                await DemoShowModalAsync(
                    new EmployeeHoursForm(
                        CreateDb,
                        _currentStoreId,
                        _session.DisplayName,
                        initialFrequency: PayFrequency.Biweekly),
                    18,
                    async form =>
                    {
                        var grid = DemoControls<DataGridView>(form).FirstOrDefault(item => item.Rows.Count > 0);
                        if (grid is not null) await DemoSelectFirstRowAsync(grid);
                    });
                await DemoShowModalAsync(
                    new PayrollRunForm(CreateDb, _currentStoreId, _session.DisplayName),
                    30,
                    async form =>
                    {
                        await DemoPointAtTextAsync("CALCULATE", form);
                        await DemoPointAtTextAsync("FINALIZE", form);
                        await DemoPointAtTextAsync("PRINT CHECKS", form);
                    });
                await DemoShowModalAsync(
                    new PayrollHistoryForm(CreateDb, _currentStoreId, _session.DisplayName),
                    18,
                    async form =>
                    {
                        var grid = DemoControls<DataGridView>(form).FirstOrDefault(item => item.Rows.Count > 0);
                        if (grid is not null) await DemoSelectFirstRowAsync(grid);
                    });
                ShowModule("Payroll");
            });

            await DemoSceneAsync("profit-loss", "PROFIT & LOSS", 50, async () =>
            {
                ShowModule("Profit & Loss");
                await Task.Delay(2200);
                await DemoPointAtTextAsync("TOTAL INCOME");
                await DemoPointAtTextAsync("COST OF GOODS SOLD");
                await DemoPointAtTextAsync("EXPENSES BY CATEGORY");
                await DemoPointAtTextAsync("NET PROFIT");
                await DemoPointAtTextAsync("MARGIN %");
                await DemoPointAtTextAsync("EXPORT REPORT");
            });

            await DemoSceneAsync("reports", "REPORTS, PDF & EXCEL", 70, async () =>
            {
                ShowModule("Reports");
                await Task.Delay(2200);
                foreach (var label in new[] { "SALES SUMMARY", "SHIFT LOG", "CASH ON HAND", "CHECK PAYOUTS", "PROFIT / LOSS", "PAYROLL" })
                    await DemoPointAtTextAsync(label);
                await DemoPointAtTextAsync("GENERATE REPORT");
                var today = DateOnly.FromDateTime(DateTime.Today);
                var from = new DateOnly(today.Year, today.Month, 1);
                await DemoShowModalAsync(
                    new ReportViewerForm(
                        $"Sales Summary - {from:M/d/yyyy} to {today:M/d/yyyy}",
                        outputPath => SaveReportPdfAsync("Sales Summary", from, today, outputPath)),
                    24,
                    async form =>
                    {
                        await DemoPointAtTextAsync("PRINT REPORT", form);
                        await DemoPointAtTextAsync("SAVE AS PDF", form);
                        await DemoPointAtTextAsync("OPEN EXTERNALLY", form);
                    });
                await DemoPointAtTextAsync("EXPORT ALL REPORTS");
                await DemoPointAtTextAsync("EXPORT TO EXCEL");
            });

            await DemoSceneAsync("administration", "STORES & USER ACCOUNTS", 60, async () =>
            {
                await DemoShowModalAsync(new StoreManagerForm(_dbFactory, _services), 24, async form =>
                {
                    var grid = DemoControls<DataGridView>(form).FirstOrDefault(item => item.Rows.Count > 0);
                    if (grid is not null) await DemoSelectFirstRowAsync(grid);
                    await DemoPointAtTextAsync("DEFAULT", form);
                });
                await DemoShowModalAsync(new UserAccountsForm(_services.GetRequiredService<IAuthService>()), 24, async form =>
                {
                    var grid = DemoControls<DataGridView>(form).FirstOrDefault(item => item.Rows.Count > 0);
                    if (grid is not null) await DemoSelectFirstRowAsync(grid);
                });
                ShowModule("Dashboard");
            });

            await DemoSceneAsync("closing", "RUN YOUR BUSINESS WITH CLARITY", 15, async () =>
            {
                ShowModule("Dashboard");
                await Task.Delay(11000);
            });

            DemoCue("END");
            File.WriteAllText(completePath, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            Environment.ExitCode = 0;
        }
        catch (Exception exception)
        {
            File.WriteAllText(errorPath, exception.ToString());
            Environment.ExitCode = 1;
        }
        finally
        {
            if (_demoRecorder is not null)
            {
                await _demoRecorder.StopAsync();
                _demoRecorder.Dispose();
                _demoRecorder = null;
            }
            _demoCueWriter?.Dispose();
            _demoCueWriter = null;
            TopMost = false;
            _demoPresentationCanClose = true;
            Close();
        }
    }

    private void PreventPrematureDemoPresentationClose(object? sender, FormClosingEventArgs args)
    {
        if (!_demoPresentationCanClose && !string.IsNullOrWhiteSpace(DemoRuntime.PresentationDirectory))
            args.Cancel = true;
    }

    private async Task DemoSceneAsync(string key, string title, int seconds, Func<Task> action)
    {
        DemoCue(key);
        ShowDemoPresentationToast(title);
        var sceneStart = _demoPresentationClock.Elapsed;
        await action();
        var remaining = TimeSpan.FromSeconds(seconds) - (_demoPresentationClock.Elapsed - sceneStart);
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining);
    }

    private void DemoCue(string key)
    {
        _demoCueWriter?.WriteLine($"{_demoPresentationClock.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)}|{key}");
    }

    private void ShowDemoPresentationToast(string title)
    {
        var oldToast = Controls.Find("DemoPresentationToast", true).FirstOrDefault();
        oldToast?.Dispose();
        var toast = new Label
        {
            Name = "DemoPresentationToast",
            Text = title,
            AutoSize = false,
            Size = new Size(620, 62),
            Location = new Point(ClientSize.Width - 650, ClientSize.Height - 92),
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            BackColor = WinTheme.BlueDark,
            ForeColor = Color.White,
            Font = WinTheme.BoldFont(16),
            TextAlign = ContentAlignment.MiddleCenter,
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(toast);
        toast.BringToFront();
        var timer = new System.Windows.Forms.Timer { Interval = 3200 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            if (!toast.IsDisposed) toast.Dispose();
        };
        timer.Start();
    }

    private IEnumerable<T> DemoControls<T>(Control? root = null) where T : Control
    {
        root ??= _content;
        foreach (Control control in root.Controls)
        {
            if (control is T typed && control.Visible)
                yield return typed;
            foreach (var nested in DemoControls<T>(control))
                yield return nested;
        }
    }

    private T? DemoNamed<T>(string name, Control? root = null) where T : Control
        => DemoControls<T>(root ?? _content).FirstOrDefault(control => control.Name.Equals(name, StringComparison.Ordinal));

    private Control? DemoFindText(string text, Control? root = null)
    {
        root ??= _content;
        return DemoControls<Control>(root)
            .Where(control => control.Visible && !string.IsNullOrWhiteSpace(control.Text))
            .FirstOrDefault(control => control.Text.Trim().Equals(text, StringComparison.OrdinalIgnoreCase))
            ?? DemoControls<Control>(root)
                .Where(control => control.Visible && !string.IsNullOrWhiteSpace(control.Text))
                .FirstOrDefault(control => control.Text.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private async Task DemoPointAtTextAsync(string text, Control? root = null)
    {
        var control = DemoFindText(text, root);
        if (control is null) return;
        await DemoMoveCursorAsync(control);
        await Task.Delay(1200);
    }

    private async Task DemoClickAsync(string text, Control? root = null)
    {
        var button = DemoControls<Button>(root ?? _content)
            .FirstOrDefault(item => item.Visible && item.Enabled && item.Text.Trim().Equals(text, StringComparison.OrdinalIgnoreCase))
            ?? DemoControls<Button>(root ?? _content)
                .FirstOrDefault(item => item.Visible && item.Enabled && item.Text.Contains(text, StringComparison.OrdinalIgnoreCase));
        if (button is null) return;
        await DemoMoveCursorAsync(button);
        button.PerformClick();
        await Task.Delay(1300);
    }

    private static async Task DemoTypeAsync(TextBoxBase box, string value)
    {
        if (!box.Enabled || box.ReadOnly) return;
        box.Focus();
        box.Clear();
        foreach (var character in value)
        {
            box.AppendText(character.ToString());
            await Task.Delay(character == ' ' ? 90 : 55);
        }
        await Task.Delay(650);
    }

    private async Task DemoSelectFirstRowAsync(DataGridView grid)
    {
        if (grid.Rows.Count == 0) return;
        var column = grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(item => item.Visible);
        if (column is null) return;
        grid.Focus();
        grid.ClearSelection();
        grid.CurrentCell = grid.Rows[0].Cells[column.Index];
        grid.Rows[0].Selected = true;
        var point = grid.GetCellDisplayRectangle(column.Index, 0, true);
        var screen = grid.PointToScreen(new Point(point.Left + Math.Min(point.Width / 2, 80), point.Top + point.Height / 2));
        _demoRecorder?.SetPointer(screen);
        await Task.Delay(1600);
    }

    private async Task DemoMoveCursorAsync(Control control)
    {
        var destination = control.PointToScreen(new Point(Math.Max(2, control.Width / 2), Math.Max(2, control.Height / 2)));
        var start = _demoRecorder?.Pointer ?? destination;
        const int steps = 18;
        for (var step = 1; step <= steps; step++)
        {
            var progress = step / (double)steps;
            var eased = progress * progress * (3 - (2 * progress));
            var x = start.X + (int)((destination.X - start.X) * eased);
            var y = start.Y + (int)((destination.Y - start.Y) * eased);
            _demoRecorder?.SetPointer(new Point(x, y));
            await Task.Delay(18);
        }
    }

    private async Task DemoShowModalAsync(Form form, int seconds, Func<Form, Task>? interact = null)
    {
        form.StartPosition = FormStartPosition.CenterParent;
        form.Shown += async (_, _) =>
        {
            await Task.Delay(1300);
            if (interact is not null) await interact(form);
            var delay = Math.Max(1000, (seconds * 1000) - 7500);
            await Task.Delay(delay);
            if (!form.IsDisposed) form.Close();
        };
        form.ShowDialog(this);
        form.Dispose();
        await Task.Delay(900);
    }
}
