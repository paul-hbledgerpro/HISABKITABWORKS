using System.Diagnostics;
using System.Data.Common;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Services;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.Data.Services;
using ManagerPaperworkSystem.Reports.Pdf;
using ManagerPaperworkSystem.UI.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig;

namespace ManagerPaperworkSystem.WinForms;

internal sealed partial class MainForm : Form
{
    private readonly IServiceProvider _services;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ISettingsService _settingsService;
    private readonly IReportService _reportService;
    private readonly IAppPaths _paths;
    private readonly SessionState _session;
    private readonly StoreConnectionService _storeConnections;
    private readonly PurchaseService _purchaseService;
    private readonly InvoiceEmailSyncService _invoiceEmailSyncService;
    private readonly InvoiceImportService _invoiceImportService;
    private readonly PosReportImportService _posImporter;
    private readonly CheckPrintService _checkPrintService;
    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = WinTheme.Bg, Padding = new Padding(16) };
    private readonly FlowLayoutPanel _nav = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
    private readonly ComboBox _storeCombo = WinTheme.ComboBox();
    private readonly Label _status = WinTheme.Label("");
    private readonly Dictionary<string, Button> _navButtons = new();
    private readonly SemaphoreSlim _bankDeliveryGate = new(1, 1);
    private readonly SemaphoreSlim _invoiceEmailSyncGate = new(1, 1);
    private readonly System.Windows.Forms.Timer _monthlyDeliveryTimer = new() { Interval = 60 * 60 * 1000 };
    private readonly System.Windows.Forms.Timer _invoiceEmailSyncTimer = new() { Interval = 4 * 60 * 60 * 1000 };
    private readonly int _loginStoreConnectionId;
    private int _currentConnectionStoreId;
    private int _currentStoreId;
    private bool _loadingStores;
    private string _currentModule = "Dashboard";
    private bool _syncingShiftDrops;
    private Func<Task>? _pendingModuleActivation;

    public MainForm(IServiceProvider services, IDbContextFactory<AppDbContext> dbFactory, ISettingsService settingsService, IReportService reportService, IAppPaths paths, SessionState session, ActiveConnectionInfo connectionInfo, InvoiceImportService invoiceImportService, PosReportImportService posImporter, CheckPrintService checkPrintService)
    {
        _services = services;
        _dbFactory = dbFactory;
        _settingsService = settingsService;
        _reportService = reportService;
        _paths = paths;
        _session = session;
        _invoiceImportService = invoiceImportService;
        _posImporter = posImporter;
        _checkPrintService = checkPrintService;
        _loginStoreConnectionId = session.LastStoreId <= 0 ? 1 : session.LastStoreId;
        _currentConnectionStoreId = _loginStoreConnectionId;
        _currentStoreId = 1;
        _storeConnections = new StoreConnectionService(dbFactory, connectionInfo.ConnectionString, connectionInfo.UseSqlServer)
        {
            CurrentStoreId = _loginStoreConnectionId
        };
        _purchaseService = new PurchaseService(new StoreDbContextFactory(_storeConnections), _paths);
        _invoiceEmailSyncService = new InvoiceEmailSyncService(_paths, _invoiceImportService, _purchaseService);
        ReloadLicensedStoreConnections();
        if (_reportService is ReportService concreteReportService)
            concreteReportService.SetStoreConnectionService(_storeConnections);

        WinTheme.Apply(this);
        Text = "HISAB KITAB";
        if (LicenseRuntime.IsReadOnly)
            Text += " - READ-ONLY (SUBSCRIPTION EXPIRED)";
        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(1280, 760);

        Controls.Add(BuildRoot());
        _monthlyDeliveryTimer.Tick += async (_, _) => await BeginMonthlyBankStatementDeliveryAsync();
        Load += async (_, _) =>
        {
            await LoadStoresAsync();
            ShowModule("Dashboard");
            _ = BeginMonthlyReportDeliveryAsync();
            _ = BeginMonthlyBankStatementDeliveryAsync();
            _ = BeginDuePosPortalSyncAsync();
            _ = BeginDueInvoiceEmailSyncAsync();
            _monthlyDeliveryTimer.Start();
            _invoiceEmailSyncTimer.Start();
        };
        _invoiceEmailSyncTimer.Tick += async (_, _) => await BeginDueInvoiceEmailSyncAsync();
        FormClosed += (_, _) =>
        {
            _monthlyDeliveryTimer.Stop();
            _monthlyDeliveryTimer.Dispose();
            _invoiceEmailSyncTimer.Stop();
            _invoiceEmailSyncTimer.Dispose();
        };
    }

    private string CurrentInvoiceEmailStoreKey()
        => $"{LicenseRuntime.ActiveStoreGuid}|{_currentConnectionStoreId}";

    private async Task BeginDueInvoiceEmailSyncAsync()
    {
        var storeKey = CurrentInvoiceEmailStoreKey();
        if (!_invoiceEmailSyncService.IsDue(storeKey, TimeSpan.FromHours(4))
            || !await _invoiceEmailSyncGate.WaitAsync(0))
            return;

        try
        {
            var result = await _invoiceEmailSyncService.SyncAsync(
                storeKey,
                _currentStoreId,
                _session.UserId,
                _session.DisplayName);
            if (!IsDisposed && (result.InvoicesImported > 0 || result.NeedsReview > 0))
            {
                BeginInvoke(() =>
                    _status.Text = $"Invoice email sync: {result.InvoicesImported} imported; {result.NeedsReview} need review.");
            }
        }
        catch
        {
            // Automatic invoice sync is non-blocking and retries at the next interval.
            // Manual Sync from Purchases displays the actionable error to the client.
        }
        finally
        {
            _invoiceEmailSyncGate.Release();
        }
    }

    private async Task BeginDuePosPortalSyncAsync()
    {
        try
        {
            // Refresh task settings after an application update so existing
            // clients gain missed-start, wake, battery, and retry behavior.
            PortalSyncService.EnsureConfiguredDailyTasks();
        }
        catch
        {
            // The in-app catch-up below still works if Windows rejects a task update.
        }

        try
        {
            var results = await PortalSyncService.RunDueAsync(_paths, force: false, visibleChrome: false);
            var message = results.LastOrDefault()?.Message;
            if (!string.IsNullOrWhiteSpace(message) && !IsDisposed)
                BeginInvoke(() => _status.Text = message);
        }
        catch
        {
            // The scheduled task and the next app startup retry automatically.
        }
    }

    private async Task BeginMonthlyReportDeliveryAsync()
    {
        var message = await MonthlyReportDeliveryService.TrySendDueAsync(_reportService, _paths);
        if (!string.IsNullOrWhiteSpace(message) && !IsDisposed)
            BeginInvoke(() => _status.Text = message);
    }

    private async Task BeginMonthlyBankStatementDeliveryAsync()
    {
        if (!await _bankDeliveryGate.WaitAsync(0))
            return;

        try
        {
            var message = await TrySendMonthlyBankStatementAsync();
            if (!string.IsNullOrWhiteSpace(message) && !IsDisposed)
                BeginInvoke(() => _status.Text = message);
        }
        catch
        {
            // Automatic delivery is retried the next day the app is opened.
            // It must never prevent the client from using the application.
        }
        finally
        {
            _bankDeliveryGate.Release();
        }
    }

    private async Task<string?> TrySendMonthlyBankStatementAsync()
    {
        var today = DateTime.Today;
        if (today.Day < 5)
            return null;

        await using var db = CreateDb();
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync();
        if (settings is null ||
            !settings.AutoEmailBankStatementOnFifth ||
            !MailAddress.TryCreate(settings.AccountantEmail, out _))
            return null;

        var storeGuid = LicenseRuntime.ActiveStoreGuid.Trim().ToUpperInvariant();
        if (storeGuid.Length == 0)
            return null;

        var previousMonth = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
        var periodKey = previousMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var statePath = Path.Combine(_paths.AppDataDirectory, "monthly-bank-statement-delivery-state.json");
        var state = LoadBankStatementDeliveryState(statePath);
        var stateKey = $"{LicenseRuntime.CurrentLicense?.LicenseId ?? 0}:{storeGuid}";
        if (state.LastSentPeriodByStore.GetValueOrDefault(stateKey) == periodKey ||
            state.LastAttemptDateByStore.GetValueOrDefault(stateKey) == today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            return null;

        state.LastAttemptDateByStore[stateKey] = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        SaveBankStatementDeliveryState(statePath, state);

        using (var bankClient = new LiveBankSyncClient())
        {
            if (bankClient.IsConfigured && (await bankClient.GetConnectionsAsync()).Count > 0)
            {
                var syncResult = await bankClient.SyncAsync();
                await SaveLiveBankSyncAsync(syncResult);
            }
        }

        var rows = await LoadBankStatementRowsAsync(previousMonth.Month, previousMonth.Year);
        if (rows.Count == 0)
            return $"Monthly bank statement for {_session.StoreName} is waiting for {previousMonth:MMMM} transactions.";

        var from = new DateOnly(previousMonth.Year, previousMonth.Month, 1);
        var finalDay = previousMonth.AddMonths(1).AddDays(-1);
        var to = new DateOnly(finalDay.Year, finalDay.Month, finalDay.Day);
        var folder = Path.Combine(_paths.AppDataDirectory, "Monthly Bank Statements");
        Directory.CreateDirectory(folder);
        var fileName = $"BankStatement_{periodKey}_{SafeBankFilePart(storeGuid)}.pdf";
        var pdfPath = Path.Combine(folder, fileName);
        BankStatementReportPdf.Generate(
            string.IsNullOrWhiteSpace(settings.StoreName) ? _session.StoreName : settings.StoreName,
            settings.StoreAddress,
            from,
            to,
            rows.Select(row => new BankStatementReportRow(
                row.Date,
                row.Description,
                row.Debit,
                row.Credit,
                row.Category,
                row.Source,
                row.IsMatched,
                row.CheckNumber)).ToList(),
            pdfPath);

        var pdfBytes = await File.ReadAllBytesAsync(pdfPath);
        using (var bankClient = new LiveBankSyncClient())
        {
            await bankClient.EmailReportAsync(
                settings.AccountantEmail,
                $"{_session.StoreName} Bank Statement",
                $"{from:M/d/yyyy} to {to:M/d/yyyy}",
                fileName,
                pdfBytes);
        }

        state.LastSentPeriodByStore[stateKey] = periodKey;
        SaveBankStatementDeliveryState(statePath, state);
        return $"{previousMonth:MMMM yyyy} bank statement for {_session.StoreName} was emailed to {settings.AccountantEmail}.";
    }

    private async Task ShowBankStatementEmailSettingsAsync()
    {
        if (!_session.IsAdmin)
        {
            MessageBox.Show(this, "Only an Owner/Admin can change accountant delivery settings.",
                "Access Restricted", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        await using var db = CreateDb();
        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings is null)
        {
            settings = new AppSettings { StoreName = _session.StoreName };
            db.Settings.Add(settings);
        }

        using var dialog = new Form
        {
            Text = $"Accountant Bank Statement Delivery - {_session.StoreName}",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            AutoScaleMode = AutoScaleMode.Dpi,
            ClientSize = new Size(690, 370),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            BackColor = WinTheme.Bg,
            Icon = WinTheme.TryLoadIcon()
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(24),
            BackColor = WinTheme.Bg
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.Controls.Add(new Label
        {
            Text = "ACCOUNTANT DELIVERY",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Copper,
            Font = WinTheme.HeaderFont(16),
            TextAlign = ContentAlignment.MiddleLeft
        });
        layout.Controls.Add(new Label
        {
            Text = $"These settings apply only to {_session.StoreName}. Other licensed stores keep their own bank connection and accountant email.",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Muted,
            Font = WinTheme.BodyFont(9.5f),
            TextAlign = ContentAlignment.MiddleLeft
        });
        layout.Controls.Add(new Label
        {
            Text = "ACCOUNTANT EMAIL",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Text,
            Font = WinTheme.BoldFont(9.5f),
            TextAlign = ContentAlignment.BottomLeft
        });
        var email = WinTheme.TextBox();
        email.Dock = DockStyle.Fill;
        email.Text = settings.AccountantEmail;
        layout.Controls.Add(email);
        var enabled = new CheckBox
        {
            Text = "Automatically email the previous month’s bank statement on the 5th",
            Checked = settings.AutoEmailBankStatementOnFifth,
            AutoSize = true,
            ForeColor = WinTheme.Text,
            Font = WinTheme.BodyFont(10),
            Padding = new Padding(0, 14, 0, 0)
        };
        layout.Controls.Add(enabled);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var save = WinTheme.Button("Save Settings", true);
        save.Width = 180;
        save.DialogResult = DialogResult.OK;
        var cancel = WinTheme.Button("Cancel");
        cancel.Width = 130;
        cancel.DialogResult = DialogResult.Cancel;
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        layout.Controls.Add(actions);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;

        while (dialog.ShowDialog(this) == DialogResult.OK)
        {
            var value = email.Text.Trim();
            if (enabled.Checked && !MailAddress.TryCreate(value, out _))
            {
                MessageBox.Show(dialog, "Enter a valid accountant email address before enabling automatic delivery.",
                    "Accountant Email", MessageBoxButtons.OK, MessageBoxIcon.Information);
                continue;
            }

            settings.AccountantEmail = value;
            settings.AutoEmailBankStatementOnFifth = enabled.Checked;
            await db.SaveChangesAsync();
            MessageBox.Show(this,
                enabled.Checked
                    ? $"Automatic bank statement delivery is enabled for {_session.StoreName}."
                    : $"Automatic bank statement delivery is disabled for {_session.StoreName}.",
                "Bank Statement Delivery", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (enabled.Checked)
                _ = BeginMonthlyBankStatementDeliveryAsync();
            return;
        }
    }

    private static BankStatementDeliveryState LoadBankStatementDeliveryState(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<BankStatementDeliveryState>(File.ReadAllText(path))
                  ?? new BankStatementDeliveryState()
                : new BankStatementDeliveryState();
        }
        catch
        {
            return new BankStatementDeliveryState();
        }
    }

    private static void SaveBankStatementDeliveryState(string path, BankStatementDeliveryState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    private static string SafeBankFilePart(string value)
        => string.Concat(value.Select(character => char.IsLetterOrDigit(character) ? character : '_'));

    private sealed class BankStatementDeliveryState
    {
        public Dictionary<string, string> LastSentPeriodByStore { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> LastAttemptDateByStore { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private AppDbContext CreateDb() => _storeConnections.CreateDbContext();

    private Control BuildRoot()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 2,
            BackColor = WinTheme.Bg
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var menu = BuildMenu();
        root.Controls.Add(menu, 0, 0);
        root.SetColumnSpan(menu, 2);

        var sidebarHeader = BuildSidebarHeader();
        root.Controls.Add(sidebarHeader, 0, 1);

        var header = BuildHeader();
        root.Controls.Add(header, 1, 1);

        var sidebar = BuildSidebar();
        root.Controls.Add(sidebar, 0, 2);
        root.Controls.Add(_content, 1, 2);

        _status.Text = $"Store: {_session.StoreName}    |    User: {_session.DisplayName} ({_session.Role})";
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleCenter;
        root.Controls.Add(_status, 0, 3);
        root.SetColumnSpan(_status, 2);
        return root;
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip
        {
            BackColor = WinTheme.BlueDark,
            ForeColor = Color.White,
            RenderMode = ToolStripRenderMode.Professional,
            GripStyle = ToolStripGripStyle.Hidden,
            Padding = new Padding(10, 3, 0, 3)
        };
        var file = new ToolStripMenuItem("File") { ForeColor = Color.White };
        file.DropDownItems.Add(MenuItem("Reports (PDF)...", (_, _) => ShowModule("Reports")));
        file.DropDownItems.Add(MenuItem("Open Database Folder", (_, _) => Process.Start("explorer.exe", _paths.AppDataDirectory)));
        file.DropDownItems.Add(MenuItem("Copy Database Path", (_, _) => Clipboard.SetText(_paths.DatabasePath)));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(MenuItem("Logout", (_, _) => Close()));
        file.DropDownItems.Add(MenuItem("Exit", (_, _) => Close()));

        var settings = new ToolStripMenuItem("Settings") { ForeColor = Color.White };
        settings.DropDownItems.Add(MenuItem("License / Renewal...", (_, _) => OpenDeviceActivation()));
        settings.DropDownItems.Add(new ToolStripSeparator());
        settings.DropDownItems.Add(MenuItem("Change Password...", (_, _) => OpenForm<ChangePasswordForm>()));
        settings.DropDownItems.Add(MenuItem("User Accounts...", (_, _) => OpenAdminForm<UserAccountsForm>()));
        settings.DropDownItems.Add(MenuItem("Database Connection...", (_, _) => OpenAdminForm<DatabaseSettingsForm>()));

        var help = new ToolStripMenuItem("Help") { ForeColor = Color.White };
        help.DropDownItems.Add(MenuItem("Check for Updates...", async (_, _) => await AppUpdateStartupService.CheckManuallyAsync(this)));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(MenuItem("About", (_, _) => MessageBox.Show(this, "HISAB KITAB WinForms\nConverted shell using the existing database logic.", "About", MessageBoxButtons.OK, MessageBoxIcon.Information)));

        menu.Items.Add(file);
        menu.Items.Add(settings);
        menu.Items.Add(help);
        return menu;
    }

    private static ToolStripMenuItem MenuItem(string text, EventHandler click)
    {
        var item = new ToolStripMenuItem(text) { ForeColor = Color.Black, BackColor = Color.White };
        item.Click += click;
        return item;
    }

    private Control BuildSidebarHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = WinTheme.BlueDark, Padding = new Padding(12) };
        var logo = new PictureBox { Dock = DockStyle.Top, Height = 92, SizeMode = PictureBoxSizeMode.Zoom, Image = WinTheme.TryLoadLogo() };
        var role = new Label { Text = _session.IsAdmin ? "ADMIN VIEW" : "MANAGER VIEW", Dock = DockStyle.Top, Height = 22, ForeColor = Color.FromArgb(203, 222, 242), Font = WinTheme.BodyFont(9) };
        panel.Controls.Add(role);
        panel.Controls.Add(logo);
        return panel;
    }

    private Control BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel, Padding = new Padding(18, 10, 18, 10) };
        panel.Paint += (_, e) => WinTheme.PaintGradient(e, panel.ClientRectangle);

        var storePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.LeftToRight,
            Width = 560,
            Height = 60,
            Padding = new Padding(0, 14, 0, 0)
        };
        storePanel.Controls.Add(new Label { Text = "Store:", ForeColor = Color.White, AutoSize = true, Padding = new Padding(0, 8, 8, 0) });
        _storeCombo.Width = 280;
        _storeCombo.SelectedIndexChanged += async (_, _) => await StoreChangedAsync();
        storePanel.Controls.Add(_storeCombo);
        var stores = WinTheme.Button("Stores...", true);
        stores.Width = 120;
        stores.Click += (_, _) => OpenAdminForm<StoreManagerForm>();
        storePanel.Controls.Add(stores);

        panel.Controls.Add(storePanel);
        return panel;
    }

    private Control BuildSidebar()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = WinTheme.BlueDark, Padding = new Padding(10) };
        _nav.BackColor = panel.BackColor;
        panel.Controls.Add(_nav);

        AddSection("MAIN");
        AddNav("Dashboard", "Dashboard", false);
        AddNav("Cash & Sales Summary", "Cash & Sales Summary", false);
        AddNav("Shift Cash Drop", "Shift Cash Drop", false);
        AddNav("Cash On Hand", "Cash On Hand", false);
        AddNav("Check Payout", "Check Payout", false);
        AddSection("OPERATIONS");
        AddNav("Operations Hub", "Operations Hub", false);
        AddNav("Vendors & Purposes", "Vendors & Purposes", false);
        AddNav("Purchases", "Purchases", false);
        AddNav("Bank Statement", "Bank Statement", false);
        AddNav("Product Costs", "Product Costs", false);
        AddNav("Price Alerts", "Price Alerts", false);
        if (LicenseRuntime.HasService("Payroll"))
            AddNav("Payroll", "Payroll", true);
        if (LicenseRuntime.HasService("Scheduling"))
            AddNav("Scheduling", "Scheduling", true);
        AddNav("Profit & Loss", "Profit & Loss", false);
        AddNav("Reports", "Reports", false);
        AddSection("ADMIN");
        AddNav("Stores", "Stores", true);
        AddNav("User Accounts", "User Accounts", true);
        AddNav("Database Settings", "Database Settings", true);

        return panel;
    }

    private void AddSection(string text)
    {
        _nav.Controls.Add(new Label
        {
            Text = text,
            ForeColor = Color.FromArgb(203, 222, 242),
            Font = WinTheme.BoldFont(8),
            Width = 210,
            Height = 28,
            TextAlign = ContentAlignment.BottomLeft
        });
    }

    private void AddNav(string text, string module, bool adminOnly)
    {
        var button = WinTheme.Button(text);
        button.Width = 210;
        button.Height = 42;
        button.Text = $"  {text}";
        button.TextAlign = ContentAlignment.MiddleLeft;
        button.Font = WinTheme.BoldFont(9);
        button.BackColor = WinTheme.BlueDark;
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderColor = Color.FromArgb(53, 91, 130);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(31, 78, 125);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(19, 49, 80);
        button.Enabled = !adminOnly || _session.IsAdmin;
        button.Click += (_, _) =>
        {
            if (adminOnly && !_session.IsAdmin)
            {
                MessageBox.Show(this, "Only Owner/Admin accounts can open this section.", "Access Restricted", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (module == "Stores") OpenAdminForm<StoreManagerForm>();
            else if (module == "User Accounts") OpenAdminForm<UserAccountsForm>();
            else if (module == "Database Settings") OpenAdminForm<DatabaseSettingsForm>();
            else ShowModule(module);
        };
        _navButtons[module] = button;
        _nav.Controls.Add(button);
    }

    private static string NavIcon(string module)
        => module switch
        {
            "Dashboard" => "\uE80F",
            "Cash & Sales Summary" => "\uE9D2",
            "Shift Cash Drop" => "\uE8C7",
            "Cash On Hand" => "\uEAFD",
            "Check Payout" => "\uE8A1",
            "Operations Hub" => "\uECA5",
            "Vendors & Purposes" => "\uE716",
            "Purchases" => "\uE719",
            "Bank Statement" => "\uE825",
            "Product Costs" => "\uE71B",
            "Price Alerts" => "\uE7BA",
            "Payroll" => "\uE8C7",
            "Scheduling" => "\uE787",
            "Profit & Loss" => "\uE9D9",
            "Reports" => "\uE749",
            "Stores" => "\uE719",
            "User Accounts" => "\uE77B",
            "Database Settings" => "\uE950",
            _ => "\uE10F"
        };

    private void ShowModule(string module)
    {
        _currentModule = module;
        _pendingModuleActivation = null;
        foreach (var kvp in _navButtons)
        {
            var active = kvp.Key == module;
            kvp.Value.BackColor = active ? WinTheme.Copper : WinTheme.BlueDark;
            kvp.Value.ForeColor = Color.White;
            kvp.Value.FlatAppearance.BorderColor = active ? WinTheme.CopperDark : Color.FromArgb(53, 91, 130);
        }

        _content.SuspendLayout();
        _content.Controls.Clear();
        try
        {
            var control = module switch
            {
                "Dashboard" => BuildDashboard(),
                "Cash & Sales Summary" => BuildCashSalesSummary(),
                "Shift Cash Drop" => BuildShiftCashDrop(),
                "Cash On Hand" => BuildCashOnHand(),
                "Check Payout" => BuildCheckPayout(),
                "Operations Hub" => BuildOperationsHub(),
                "Vendors & Purposes" => BuildVendorsPurposes(),
                "Purchases" => BuildPurchases(),
                "Bank Statement" => BuildBankStatement(),
                "Product Costs" => BuildProductCosts(),
                "Price Alerts" => BuildPriceAlerts(),
                "Payroll" => BuildPayroll(),
                "Scheduling" => BuildScheduling(),
                "Profit & Loss" => BuildProfitLoss(),
                "Reports" => BuildReports(),
                _ => BuildDashboard()
            };
            ApplyLightModuleTheme(control);
            if (LicenseRuntime.IsReadOnly)
                ApplyReadOnlyMode(control);
            _content.Controls.Add(control);
            var activation = _pendingModuleActivation;
            _pendingModuleActivation = null;
            if (activation is not null)
            {
                _content.BeginInvoke(new Action(async () =>
                {
                    if (_currentModule == module && !control.IsDisposed && control.Parent == _content)
                        await activation();
                }));
            }
        }
        catch (Exception ex)
        {
            _pendingModuleActivation = null;
            _content.Controls.Add(BuildModuleError(module, ex));
        }
        finally
        {
            _content.ResumeLayout();
        }
    }

    private void OpenDeviceActivation()
    {
        using var form = new DeviceActivationForm();
        if (form.ShowDialog(this) != DialogResult.OK)
            return;
        MessageBox.Show(this, "The device license was installed. Restart HISAB KITAB to apply the renewed subscription.",
            "License Installed", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void ApplyReadOnlyMode(Control root)
    {
        foreach (Control child in root.Controls)
            ApplyReadOnlyMode(child);

        if (root is not Button button)
            return;
        var text = button.Text.ToUpperInvariant();
        if (text.Contains("REPORT") || text.Contains("EXPORT") || text.Contains("PRINT") ||
            text.Contains("PREVIEW") || text.Contains("VIEW") || text.Contains("SEARCH") ||
            text.Contains("FILTER") || text.Contains("REFRESH") || text.Contains("OPEN"))
            return;

        var mutationWords = new[]
        {
            "SAVE", "ADD", "NEW", "CREATE", "DELETE", "REMOVE", "UPDATE", "EDIT",
            "CORRECT", "IMPORT", "RECORD", "RECONCILE", "PAY", "CLEAR", "ACTIVATE", "SET "
        };
        if (mutationWords.Any(text.Contains))
            button.Enabled = false;
    }

    private static void ApplyLightModuleTheme(Control root)
    {
        if (controlIsWorkSurface(root) && IsLegacyDarkSurface(root.BackColor))
            root.BackColor = root is TableLayoutPanel or FlowLayoutPanel ? WinTheme.Bg : WinTheme.Panel;

        foreach (Control control in root.Controls)
            ApplyLightModuleTheme(control);

        if (root is Label label && label.ForeColor == Color.White && !HasDarkParent(label))
            label.ForeColor = WinTheme.Text;

        if (root is TextBoxBase textBox)
        {
            if (IsLegacyDarkSurface(textBox.BackColor))
                textBox.BackColor = Color.White;
            if (textBox.ForeColor == Color.White)
                textBox.ForeColor = WinTheme.Text;
        }

        if (root is ComboBox combo)
        {
            if (IsLegacyDarkSurface(combo.BackColor))
                combo.BackColor = Color.White;
            if (combo.ForeColor == Color.White)
                combo.ForeColor = WinTheme.Text;
        }

        static bool controlIsWorkSurface(Control control)
            => control is Panel or TableLayoutPanel or FlowLayoutPanel or GroupBox;

        static bool HasDarkParent(Control control)
            => control.Parent is not null && control.Parent.BackColor.GetBrightness() < 0.42f;

        static bool IsLegacyDarkSurface(Color color)
            => color == Color.FromArgb(6, 30, 44)
               || color == Color.FromArgb(10, 42, 62)
               || color == Color.FromArgb(16, 55, 80)
               || color == Color.FromArgb(9, 36, 54)
               || color == Color.FromArgb(12, 47, 54)
               || color == Color.FromArgb(7, 24, 40);
    }

    private async Task LoadStoresAsync()
    {
        _loadingStores = true;
        try
        {
            using var db = _dbFactory.CreateDbContext();
            var stores = await db.Stores.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
            _storeCombo.DataSource = stores;
            _storeCombo.DisplayMember = nameof(Store.Name);
            _storeCombo.ValueMember = nameof(Store.Id);
            var selected = stores.FirstOrDefault(s => s.Id == _currentConnectionStoreId)
                ?? stores.FirstOrDefault(s => StoreNamesMatch(s.Name, _session.StoreName))
                ?? stores.FirstOrDefault();
            if (selected is not null)
            {
                _storeCombo.SelectedValue = selected.Id;
                _currentConnectionStoreId = selected.Id;
                _storeConnections.CurrentStoreId = selected.Id;
                await EnsureCurrentStoreDatabaseReadyAsync(selected.Name);
                _currentStoreId = await ResolveDataStoreIdAsync(selected.Name);
                LicenseRuntime.ConfigurePayrollStateForConnection(CurrentStoreConnectionString());
                _session.LastStoreId = selected.Id;
                _session.StoreName = selected.Name;
                _status.Text = $"Store: {_session.StoreName}    |    User: {_session.DisplayName} ({_session.Role})";
                await SaveSelectedStorePreferenceAsync(selected.Id, selected.Name, selected.Address);
            }
        }
        finally
        {
            _loadingStores = false;
        }
    }

    private async Task SaveSelectedStorePreferenceAsync(int storeId, string storeName, string? storeAddress = null)
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            settings.LastStoreId = storeId;
            settings.DefaultStoreId = storeId;
            settings.StoreName = storeName;
            if (!string.IsNullOrWhiteSpace(storeAddress))
                settings.StoreAddress = storeAddress;
            await _settingsService.SaveSettingsAsync(settings);
        }
        catch
        {
            // The visible store selection should still win even if settings cannot be saved.
        }
    }

    private static bool StoreNamesMatch(string? left, string? right)
    {
        static string Normalize(string? value) => Regex.Replace(value ?? "", @"[^A-Za-z0-9]+", "").ToUpperInvariant();
        var a = Normalize(left);
        var b = Normalize(right);
        return a.Length > 0 && a == b;
    }

    private async Task StoreChangedAsync()
    {
        if (_loadingStores)
            return;

        if (_storeCombo.SelectedItem is not Store store || store.Id == _currentConnectionStoreId)
            return;

        _currentConnectionStoreId = store.Id;
        _storeConnections.CurrentStoreId = store.Id;
        await EnsureCurrentStoreDatabaseReadyAsync(store.Name);
        _currentStoreId = await ResolveDataStoreIdAsync(store.Name);
        LicenseRuntime.ConfigurePayrollStateForConnection(CurrentStoreConnectionString());
        _session.LastStoreId = store.Id;
        _session.StoreName = store.Name;
        _status.Text = $"Store: {store.Name}    |    User: {_session.DisplayName} ({_session.Role})";

        await SaveSelectedStorePreferenceAsync(store.Id, store.Name, store.Address);

        ShowModule(_currentModule);
        _ = BeginMonthlyBankStatementDeliveryAsync();
        _ = BeginDueInvoiceEmailSyncAsync();
    }

    private async Task EnsureCurrentStoreDatabaseReadyAsync(string businessName)
    {
        await using var db = CreateDb();
        if (db.Database.IsSqlServer())
        {
            var connectionString = db.Database.GetConnectionString();
            if (!string.IsNullOrWhiteSpace(connectionString))
                await DatabaseSchemaService.EnsureSchemaAsync(connectionString, businessName);
        }

        await DbInitializer.InitializeAsync(db);
    }

    private async Task<int> ResolveDataStoreIdAsync(string businessName)
    {
        using var db = CreateDb();
        var stores = await db.Stores.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Id).ToListAsync();
        var match = stores.FirstOrDefault(x => StoreNamesMatch(x.Name, businessName)) ?? stores.FirstOrDefault();
        if (match is null)
            throw new InvalidOperationException($"The database for '{businessName}' does not contain an active store record.");
        return match.Id;
    }

    private Control BuildModuleError(string module, Exception ex)
    {
        var panel = WinTheme.Card();
        panel.Dock = DockStyle.Fill;
        var title = new Label
        {
            Text = $"{module} could not load",
            ForeColor = WinTheme.Red,
            Font = WinTheme.HeaderFont(18),
            Dock = DockStyle.Top,
            Height = 42
        };
        var detail = new TextBox
        {
            Text = AppBootstrap.RedactSensitiveText(ex.ToString()),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            ForeColor = Color.Black,
            Font = WinTheme.BodyFont(10)
        };
        panel.Controls.Add(detail);
        panel.Controls.Add(title);
        return ModuleShell("\uE783", module, "This section hit an error, but the app is still usable.", panel);
    }

    private async void OpenAdminForm<T>() where T : Form
    {
        if (!_session.IsAdmin)
        {
            MessageBox.Show(this, "Only Owner/Admin accounts can open this section.", "Access Restricted", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var form = _services.GetRequiredService<T>();
        form.ShowDialog(this);
        ReloadLicensedStoreConnections();
        await LoadStoresAsync();
        ShowModule(_currentModule);
    }

    private void ReloadLicensedStoreConnections()
    {
        var connections = AppBootstrap.LoadStoreConnections()
            .Where(pair => int.TryParse(pair.Key, out _))
            .ToDictionary(pair => int.Parse(pair.Key), pair => pair.Value);
        _storeConnections.ReplaceStoreConnections(connections);
    }

    private void OpenForm<T>() where T : Form
    {
        using var form = _services.GetRequiredService<T>();
        form.ShowDialog(this);
    }

    private Control ModuleShell(string icon, string title, string subtitle, Control body)
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, BackColor = WinTheme.Bg };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 18, 0, 8), BackColor = WinTheme.Bg };
        var glyph = new Label
        {
            Text = "",
            Left = 0,
            Top = 18,
            Width = 0,
            Height = 42,
            ForeColor = WinTheme.Copper,
            Font = new Font("Segoe MDL2 Assets", 26),
            TextAlign = ContentAlignment.MiddleCenter
        };
        var h = new Label
        {
            Text = title,
            Left = 0,
            Top = 18,
            Width = 640,
            Height = 42,
            ForeColor = WinTheme.Copper,
            Font = WinTheme.HeaderFont(20),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var s = new Label
        {
            Text = subtitle,
            Left = 2,
            Top = 62,
            Width = 900,
            Height = 24,
            ForeColor = WinTheme.Muted,
            Font = WinTheme.BodyFont(10),
            TextAlign = ContentAlignment.MiddleLeft
        };
        header.Controls.Add(glyph);
        header.Controls.Add(h);
        header.Controls.Add(s);
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(body, 0, 1);
        return root;
    }

    private Control BuildDashboard()
    {
        var now = DateTime.Today;
        using var db = CreateDb();
        // Select only dashboard fields and coalesce legacy nullable text values in
        // SQL. This keeps one malformed/older ShiftLogs row from taking down the
        // entire dashboard before the startup repair migration can normalize it.
        var shiftRows = db.ShiftLogs.AsNoTracking()
            .Where(x => x.StoreId == _currentStoreId)
            .Select(x => new ShiftLogEntry
            {
                Id = x.Id,
                StoreId = x.StoreId,
                Date = x.Date,
                Employee = x.Employee ?? "",
                ShiftNo = x.ShiftNo ?? "",
                CashTotal = x.CashTotal,
                CardTotal = x.CardTotal,
                NetSales = x.NetSales,
                Tax = x.Tax,
                CashDropReceived = x.CashDropReceived,
                RegisterPayout = x.RegisterPayout,
                PayoutReason = x.PayoutReason ?? "",
                CreatedByUserId = x.CreatedByUserId,
                CreatedByName = x.CreatedByName ?? "",
                IsCorrection = x.IsCorrection,
                CorrectsId = x.CorrectsId,
                CorrectionReason = x.CorrectionReason ?? "",
                CreatedUtc = x.CreatedUtc
            })
            .ToList();
        var shifts = EffectiveRows(shiftRows, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc)
            .Where(x => x.Date.Month == now.Month && x.Date.Year == now.Year).ToList();
        var cashRows = db.CashOnHand.AsNoTracking().Where(x => x.StoreId == _currentStoreId).ToList();
        var cash = EffectiveRows(cashRows, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);
        var checkRows = db.CheckPayouts.AsNoTracking().Where(x => x.StoreId == _currentStoreId).ToList();
        var checks = EffectiveRows(checkRows, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);
        var purchases = db.PurchaseInvoices.AsNoTracking().Where(x => x.StoreId == _currentStoreId).ToList();
        var purchasesThisMonth = purchases.Where(x => x.InvoiceDate.Month == now.Month && x.InvoiceDate.Year == now.Year).ToList();

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = WinTheme.Bg };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 124));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 330));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var kpis = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = WinTheme.Bg };
        for (var i = 0; i < 4; i++)
            kpis.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        kpis.Controls.Add(KpiCard("Cash On Hand", cash.Sum(x => x.CashAdded - x.PayoutAmount).ToString("C"), "Current store balance", WinTheme.Copper), 0, 0);
        kpis.Controls.Add(KpiCard("Uncleared Checks", checks.Where(x => !x.Cleared).Sum(x => x.CheckAmount).ToString("C"), "Pending payouts", WinTheme.Red), 1, 0);
        kpis.Controls.Add(KpiCard("Monthly Payouts", (cash.Where(x => x.Date.Month == now.Month && x.Date.Year == now.Year).Sum(x => x.PayoutAmount) + checks.Where(x => x.Date.Month == now.Month && x.Date.Year == now.Year).Sum(x => x.CheckAmount)).ToString("C"), "Cash + check payouts", WinTheme.Copper), 2, 0);
        kpis.Controls.Add(KpiCard("Sales Summary", shifts.Sum(x => x.NetSales).ToString("C"), "This month", WinTheme.Green), 3, 0);
        root.Controls.Add(kpis, 0, 0);

        var middle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = WinTheme.Bg };
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        middle.Controls.Add(BuildSalesOverviewPanel(shifts), 0, 0);
        middle.Controls.Add(BuildPayoutBreakdownPanel(cash, checks, purchases), 1, 0);
        root.Controls.Add(middle, 0, 1);

        root.Controls.Add(BuildRecentTransactionsPanel(shifts, cash, checks, purchases), 0, 2);
        return ModuleShell("", "Dashboard", "Store summary and quick access.", root);
    }

    private static Control KpiCard(string title, string value, string sub, Color color)
    {
        return new KpiCardPanel(title, value, sub, color)
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(8)
        };
    }

    private static Control BuildDashboardBarPanel(IReadOnlyList<ShiftLogEntry> shifts)
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = WinTheme.Panel };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(new Label
        {
            Text = "Sales Summary (Daily)",
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Font = WinTheme.BoldFont(11),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        var bars = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 12, RowCount = 1, BackColor = WinTheme.Panel, Padding = new Padding(8, 8, 8, 2) };
        for (var i = 0; i < 12; i++)
            bars.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 12f));

        var byDay = shifts
            .GroupBy(x => x.Date.Day)
            .OrderBy(x => x.Key)
            .Take(12)
            .Select(g => g.Sum(x => x.NetSales))
            .ToList();
        var max = byDay.Count == 0 ? 1m : Math.Max(1m, byDay.Max());

        for (var i = 0; i < 12; i++)
        {
            var value = i < byDay.Count ? byDay[i] : 0m;
            var host = new Panel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel, Padding = new Padding(5, 0, 5, 0) };
            var barHeight = Math.Max(8, (int)(130 * (value / max)));
            var bar = new Panel { Dock = DockStyle.Bottom, Height = barHeight, BackColor = Color.FromArgb(47, 107, 255) };
            host.Controls.Add(bar);
            bars.Controls.Add(host, i, 0);
        }

        root.Controls.Add(bars, 0, 1);
        return root;
    }

    private static Control BuildSalesOverviewPanel(IReadOnlyList<ShiftLogEntry> shifts)
    {
        var panel = WinTheme.BorderedPanel(14);
        panel.Dock = DockStyle.Fill;
        panel.Margin = new Padding(8);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = WinTheme.Panel };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label
        {
            Text = "Sales Overview (This Month)",
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Font = WinTheme.BoldFont(12),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        var dailySales = shifts
            .GroupBy(x => x.Date.Day)
            .OrderBy(x => x.Key)
            .Select(g => new ChartPoint(g.Key.ToString(CultureInfo.InvariantCulture), g.Sum(x => x.NetSales)))
            .ToList();
        layout.Controls.Add(new SalesLineChart(dailySales) { Dock = DockStyle.Fill }, 0, 1);
        panel.Controls.Add(layout);
        return panel;
    }

    private Control BuildPayoutBreakdownPanel(IReadOnlyList<CashOnHandEntry> cash, IReadOnlyList<CheckPayout> checks, IReadOnlyList<PurchaseInvoice> purchases)
    {
        var panel = WinTheme.BorderedPanel(14);
        panel.Dock = DockStyle.Fill;
        panel.Margin = new Padding(8);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = WinTheme.Panel };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = WinTheme.Panel };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        header.Controls.Add(new Label
        {
            Text = "Payouts by Purpose (This Month)",
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Font = WinTheme.BoldFont(12),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        var month = WinTheme.ComboBox();
        foreach (var m in Enumerable.Range(1, 12))
            month.Items.Add(new BankMonthItem(m, new DateTime(2000, m, 1).ToString("MMMM")));
        month.SelectedIndex = DateTime.Today.Month - 1;
        var year = WinTheme.ComboBox();
        foreach (var y in Enumerable.Range(DateTime.Today.Year - 5, 7).Reverse())
            year.Items.Add(y);
        year.SelectedItem = DateTime.Today.Year;
        header.Controls.Add(month, 1, 0);
        header.Controls.Add(year, 2, 0);
        layout.Controls.Add(header, 0, 0);

        var chartHost = new Panel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel };
        layout.Controls.Add(chartHost, 0, 1);

        void refreshBreakdown()
        {
            var selectedMonth = month.SelectedItem is BankMonthItem mi ? mi.Value : DateTime.Today.Month;
            var selectedYear = year.SelectedItem is int yi ? yi : DateTime.Today.Year;
            header.GetControlFromPosition(0, 0)!.Text = $"Payouts by Purpose ({new DateTime(selectedYear, selectedMonth, 1):MMMM yyyy})";
            var items = new List<BreakdownItem>
            {
                new("Register/Cash Payouts", cash.Where(x => x.Date.Month == selectedMonth && x.Date.Year == selectedYear).Sum(x => x.PayoutAmount), Color.FromArgb(38, 166, 154)),
                new("Check Payouts", checks.Where(x => x.Date.Month == selectedMonth && x.Date.Year == selectedYear).Sum(x => x.CheckAmount), Color.FromArgb(219, 128, 54)),
                new("Purchases", purchases.Where(x => x.InvoiceDate.Month == selectedMonth && x.InvoiceDate.Year == selectedYear).Sum(x => x.Total), Color.FromArgb(44, 132, 184)),
                new("Other", 0m, Color.FromArgb(192, 152, 62))
            }.Where(x => x.Value > 0).ToList();
            chartHost.Controls.Clear();
            chartHost.Controls.Add(new DonutBreakdownControl(items) { Dock = DockStyle.Fill });
        }

        month.SelectedIndexChanged += (_, _) => refreshBreakdown();
        year.SelectedIndexChanged += (_, _) => refreshBreakdown();
        refreshBreakdown();
        panel.Controls.Add(layout);
        return panel;
    }

    private static Control BuildRecentTransactionsPanel(IReadOnlyList<ShiftLogEntry> shifts, IReadOnlyList<CashOnHandEntry> cash, IReadOnlyList<CheckPayout> checks, IReadOnlyList<PurchaseInvoice> purchases)
    {
        var panel = WinTheme.BorderedPanel(14);
        panel.Dock = DockStyle.Fill;
        panel.Margin = new Padding(8, 4, 8, 0);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = WinTheme.Panel };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new Panel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel };
        header.Controls.Add(new Label
        {
            Text = "Recent Transactions",
            Dock = DockStyle.Left,
            Width = 260,
            ForeColor = Color.White,
            Font = WinTheme.BoldFont(12),
            TextAlign = ContentAlignment.MiddleLeft
        });
        var viewAll = WinTheme.Button("View All");
        viewAll.Width = 115;
        viewAll.Height = 34;
        viewAll.Dock = DockStyle.Right;
        header.Controls.Add(viewAll);
        layout.Controls.Add(header, 0, 0);

        var rows = new List<DashboardTransaction>();
        rows.AddRange(shifts.Select(x => new DashboardTransaction(x.Date, "Sales", $"Shift {x.ShiftNo}", x.Employee, "Sales", x.NetSales, "Completed")));
        rows.AddRange(cash.Select(x => new DashboardTransaction(x.Date, x.IsPayout ? "Cash Payout" : "Cash Added", x.Reference, x.Description, x.IsPayout ? "Payout" : "Cash", x.IsPayout ? -x.PayoutAmount : x.CashAdded, "Completed")));
        rows.AddRange(checks.Select(x => new DashboardTransaction(x.Date, "Check Payout", x.CheckNumber, x.Description, x.VendorName, -x.CheckAmount, x.Cleared ? "Cleared" : "Pending")));
        rows.AddRange(purchases.Select(x => new DashboardTransaction(x.InvoiceDate, "Purchase", x.InvoiceNumber, x.VendorName, "Inventory", x.Total, "Completed")));

        var grid = WinTheme.Grid();
        grid.DataSource = rows
            .OrderByDescending(x => x.Date)
            .Take(10)
            .Select(x => new
            {
                Date = x.Date.ToString("M/d/yyyy", CultureInfo.InvariantCulture),
                x.Type,
                x.Reference,
                x.Description,
                x.Category,
                Amount = x.Amount.ToString("C"),
                x.Status
            })
            .ToList();
        layout.Controls.Add(grid, 0, 1);
        panel.Controls.Add(layout);
        return panel;
    }

    private static TableLayoutPanel SectionPage(int formHeight, int actionHeight = 70)
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = WinTheme.Bg };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, formHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, actionHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        return root;
    }

    private static TableLayoutPanel SectionCard(string title, int rows)
    {
        var card = WinTheme.BorderedPanel(14);
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(8);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = rows + 1, ColumnCount = 1, BackColor = WinTheme.Panel };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        for (var i = 0; i < rows; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
        layout.Controls.Add(new Label
        {
            Text = title.ToUpperInvariant(),
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Copper,
            Font = WinTheme.BoldFont(13),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0)
        }, 0, 0);
        card.Controls.Add(layout);
        return layout;
    }

    private static void AddSectionField(TableLayoutPanel card, int row, string labelText, Control input, int labelWidth = 150)
    {
        var host = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = WinTheme.Panel, Padding = new Padding(14, 6, 14, 6) };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host.Controls.Add(new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Font = WinTheme.BodyFont(10),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        input.Dock = DockStyle.Fill;
        input.Margin = Padding.Empty;
        host.Controls.Add(input, 1, 0);
        card.Controls.Add(host, 0, row + 1);
    }

    private static TextBox SectionTextBox(string text = "", bool readOnly = false, bool rightAlign = false)
    {
        var box = WinTheme.TextBox();
        box.Text = text;
        box.ReadOnly = readOnly;
        box.TextAlign = rightAlign ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        box.Font = WinTheme.BodyFont(11);
        box.BackColor = WinTheme.Bg;
        return box;
    }

    private static ComboBox SectionCombo(params string[] values)
    {
        var combo = WinTheme.ComboBox();
        combo.BackColor = WinTheme.Bg;
        combo.ForeColor = Color.White;
        combo.FlatStyle = FlatStyle.Flat;
        combo.Items.AddRange(values.Cast<object>().ToArray());
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        return combo;
    }

    private static FlowLayoutPanel SectionActions()
    {
        return new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true,
            BackColor = WinTheme.Bg,
            Padding = new Padding(4, 10, 4, 10)
        };
    }

    private static Button SectionActionButton(string text, bool filled = false, int width = 170)
    {
        var button = WinTheme.Button(text, filled);
        button.Width = width;
        button.Height = 44;
        button.Margin = new Padding(5, 0, 5, 0);
        return button;
    }

    private static Label StatCard(Control host, string title, Color valueColor, int width = 210)
    {
        var card = WinTheme.BorderedPanel(8);
        card.Width = width;
        card.Height = 58;
        card.Margin = new Padding(5, 0, 5, 0);
        var value = new Label
        {
            Text = "$0.00",
            Dock = DockStyle.Fill,
            ForeColor = valueColor,
            Font = WinTheme.BoldFont(13),
            TextAlign = ContentAlignment.MiddleCenter
        };
        card.Controls.Add(value);
        card.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            ForeColor = Color.White,
            Font = WinTheme.BoldFont(8),
            Height = 20,
            TextAlign = ContentAlignment.MiddleCenter
        });
        host.Controls.Add(card);
        return value;
    }

    private static TableLayoutPanel MockSectionPage(int topHeight, int actionHeight = 58)
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = WinTheme.Bg, Padding = new Padding(2) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, topHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, actionHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        return root;
    }

    private static TableLayoutPanel MockBorderCard(string title, string glyph = "", int headerHeight = 42)
    {
        var shell = WinTheme.BorderedPanel(10);
        shell.Dock = DockStyle.Fill;
        shell.Margin = new Padding(6);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = WinTheme.Panel };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, headerHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.Controls.Add(layout);

        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = WinTheme.Panel, Padding = new Padding(8, 0, 0, 0) };
        if (!string.IsNullOrWhiteSpace(glyph))
            header.Controls.Add(new Label { Text = glyph, Width = 42, Height = headerHeight, ForeColor = WinTheme.Copper, Font = WinTheme.IconFont(22), TextAlign = ContentAlignment.MiddleCenter });
        header.Controls.Add(new Label { Text = title.ToUpperInvariant(), AutoSize = false, Width = 330, Height = headerHeight, ForeColor = Color.FromArgb(241, 193, 140), Font = WinTheme.HeaderFont(13), TextAlign = ContentAlignment.MiddleLeft });
        layout.Controls.Add(header, 0, 0);
        return layout;
    }

    private static void AddMockField(TableLayoutPanel root, string labelText, Control input, int col, int row, int labelWidth = 116)
    {
        var host = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = WinTheme.Panel, Padding = new Padding(8, 4, 8, 4), Margin = Padding.Empty };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host.Controls.Add(new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Font = WinTheme.BoldFont(9),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        }, 0, 0);
        input.Dock = DockStyle.Fill;
        input.Margin = Padding.Empty;
        host.Controls.Add(input, 1, 0);
        root.Controls.Add(host, col, row);
    }

    private static Button MockActionButton(string glyph, string text, bool filled = false, int width = 160)
    {
        var button = WinTheme.Button(text, filled);
        button.Width = width;
        button.Height = 46;
        button.Margin = new Padding(6, 7, 6, 7);
        return button;
    }

    private static Label MockSummaryValue(FlowLayoutPanel host, string title, Color valueColor, int width = 185)
    {
        var card = WinTheme.BorderedPanel(8);
        card.Width = width;
        card.Height = 66;
        card.Margin = new Padding(5, 2, 5, 2);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = WinTheme.Panel };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
        card.Controls.Add(layout);
        layout.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, ForeColor = Color.White, Font = WinTheme.BoldFont(8.5f), TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true }, 0, 0);
        var value = new Label { Text = "$0.00", Dock = DockStyle.Fill, ForeColor = valueColor, Font = WinTheme.HeaderFont(12), TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true };
        layout.Controls.Add(value, 0, 1);
        host.Controls.Add(card);
        return value;
    }

    private static Label BuildCheckSummaryBox(Control host, string title, Color valueColor)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = WinTheme.Panel };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        host.Controls.Add(layout);
        layout.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(241, 193, 140),
            Font = WinTheme.BoldFont(10),
            TextAlign = ContentAlignment.MiddleCenter
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "Amount",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Muted,
            Font = WinTheme.BoldFont(8.5f),
            TextAlign = ContentAlignment.MiddleCenter
        }, 0, 1);
        var value = new Label
        {
            Text = "$0.00",
            Dock = DockStyle.Fill,
            ForeColor = valueColor,
            Font = WinTheme.HeaderFont(16),
            TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = true
        };
        layout.Controls.Add(value, 0, 2);
        return value;
    }

    private Control BuildShiftCashDrop()
    {
        var root = MockSectionPage(315, 72);
        var formShell = WinTheme.BorderedPanel(10);
        formShell.Dock = DockStyle.Fill;
        formShell.Margin = new Padding(4, 6, 4, 6);
        root.Controls.Add(formShell, 0, 0);

        var form = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, BackColor = WinTheme.Panel };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        form.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        form.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        formShell.Controls.Add(form);

        var groupTitles = new[] { "SHIFT INFORMATION", "SALES SUMMARY", "PAYMENT SUMMARY", "CASH HANDLING" };
        for (var i = 0; i < groupTitles.Length; i++)
        {
            form.Controls.Add(new Label { Text = groupTitles[i], Dock = DockStyle.Fill, ForeColor = Color.FromArgb(241, 193, 140), Font = WinTheme.BoldFont(10), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0) }, i, 0);
        }

        var date = WinTheme.DatePicker();
        var employee = SectionTextBox(_session.DisplayName);
        var shift = SectionTextBox();
        var cash = SectionTextBox(rightAlign: true);
        var card = SectionTextBox(rightAlign: true);
        var net = SectionTextBox(rightAlign: true);
        var tax = SectionTextBox(rightAlign: true);
        var drop = SectionTextBox(rightAlign: true);
        var payout = SectionTextBox("0", rightAlign: true);
        var reason = SectionTextBox();
        var posReport = SectionTextBox("Upload using buttons below", readOnly: true);
        var managerEntryMode = _session.Role == UserRole.Manager;
        date.Enabled = !managerEntryMode;
        employee.ReadOnly = managerEntryMode;
        shift.ReadOnly = managerEntryMode;
        cash.ReadOnly = managerEntryMode;
        card.ReadOnly = managerEntryMode;
        net.ReadOnly = managerEntryMode;
        tax.ReadOnly = managerEntryMode;

        var shiftInfo = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = WinTheme.Panel };
        var salesSummary = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = WinTheme.Panel };
        var paymentSummary = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = WinTheme.Panel };
        var cashHandling = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = WinTheme.Panel };
        foreach (var group in new[] { shiftInfo, salesSummary, paymentSummary, cashHandling })
        {
            for (var i = 0; i < 4; i++) group.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        }
        form.Controls.Add(shiftInfo, 0, 1);
        form.Controls.Add(salesSummary, 1, 1);
        form.Controls.Add(paymentSummary, 2, 1);
        form.Controls.Add(cashHandling, 3, 1);

        AddMockField(shiftInfo, "Date", date, 0, 0, 110);
        AddMockField(shiftInfo, "Shift", shift, 0, 1, 110);
        AddMockField(shiftInfo, "Employee", employee, 0, 2, 110);
        AddMockField(shiftInfo, "POS Report", posReport, 0, 3, 110);
        AddMockField(salesSummary, "Cash", cash, 0, 0, 96);
        AddMockField(salesSummary, "Cards", card, 0, 1, 96);
        AddMockField(salesSummary, "Net Sales", net, 0, 2, 96);
        AddMockField(salesSummary, "Tax", tax, 0, 3, 96);
        AddMockField(paymentSummary, "Gross Sales", SectionTextBox(readOnly: true, rightAlign: true), 0, 0, 112);
        AddMockField(paymentSummary, "Total Due", SectionTextBox(readOnly: true, rightAlign: true), 0, 1, 112);
        AddMockField(paymentSummary, "Total Received", SectionTextBox(readOnly: true, rightAlign: true), 0, 2, 112);
        AddMockField(paymentSummary, "Variance", SectionTextBox(readOnly: true, rightAlign: true), 0, 3, 112);
        AddMockField(cashHandling, "Cash Drop", drop, 0, 0, 118);
        AddMockField(cashHandling, "Register Payout", payout, 0, 1, 118);
        AddMockField(cashHandling, "Payout Reason", reason, 0, 2, 118);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = false, BackColor = WinTheme.Bg, Padding = new Padding(0, 6, 0, 6) };
        root.Controls.Add(actions, 0, 1);
        var grid = WinTheme.Grid();
        var suppressShiftSelectionLoad = false;
        root.Controls.Add(grid, 0, 2);
        root.Controls.Add(BuildGridFooter("Shift cash drop records for selected store"), 0, 3);
        var grossBox = (TextBox)((TableLayoutPanel)paymentSummary.GetControlFromPosition(0, 0)!).GetControlFromPosition(1, 0)!;
        var dueBox = (TextBox)((TableLayoutPanel)paymentSummary.GetControlFromPosition(0, 1)!).GetControlFromPosition(1, 0)!;
        var receivedBox = (TextBox)((TableLayoutPanel)paymentSummary.GetControlFromPosition(0, 2)!).GetControlFromPosition(1, 0)!;
        var varianceBox = (TextBox)((TableLayoutPanel)paymentSummary.GetControlFromPosition(0, 3)!).GetControlFromPosition(1, 0)!;
        void recalc()
        {
            var cashValue = Money(cash.Text);
            var cardValue = Money(card.Text);
            var taxValue = Money(tax.Text);
            var dropValue = Money(drop.Text);
            var payoutValue = Money(payout.Text);
            var grossValue = cashValue + cardValue + taxValue;
            var dueValue = cashValue;
            var receivedValue = dropValue + payoutValue;
            var varianceValue = receivedValue - cashValue;
            grossBox.Text = grossValue.ToString("C2");
            dueBox.Text = dueValue.ToString("C2");
            receivedBox.Text = receivedValue.ToString("C2");
            varianceBox.Text = varianceValue.ToString("C2");
            varianceBox.ForeColor = varianceValue > 0 ? WinTheme.Green : varianceValue < 0 ? WinTheme.Red : WinTheme.Text;
        }
        foreach (var box in new[] { cash, card, tax, net, drop, payout })
            box.TextChanged += (_, _) => recalc();
        void refresh()
        {
            using var db = CreateDb();
            grid.DataSource = db.ShiftLogs.AsNoTracking().Where(x => x.StoreId == _currentStoreId).OrderByDescending(x => x.Date).ThenByDescending(x => x.Id)
                .Select(x => new
                {
                    x.Id,
                    x.Date,
                    Shift = x.ShiftNo,
                    x.Employee,
                    Source = x.PosReportKey != "" ? "Automatic Z Report" :
                        x.PosSalesSummaryId != null ? "Daily POS Summary" : "Manual",
                    Cash = x.CashTotal,
                    Card = x.CardTotal,
                    Gross = x.GrossSales,
                    Net = x.NetSales,
                    Tax = x.Tax,
                    Drop = x.CashDropReceived,
                    Payout = x.RegisterPayout,
                    x.PayoutReason,
                    x.Variance
                })
                .ToList();
            HideId(grid);
        }
        grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || !string.Equals(grid.Columns[e.ColumnIndex].DataPropertyName, "Variance", StringComparison.OrdinalIgnoreCase))
                return;

            var value = Convert.ToDecimal(e.Value ?? 0m, CultureInfo.CurrentCulture);
            if (e.CellStyle is not null)
                e.CellStyle.ForeColor = value > 0 ? WinTheme.Green : value < 0 ? WinTheme.Red : WinTheme.Text;
        };
        void clearImported()
        {
            date.Value = DateTime.Today;
            employee.Clear();
            shift.Clear();
            cash.Clear();
            card.Clear();
            net.Clear();
            tax.Clear();
            posReport.Text = "Upload using buttons below";
            posReport.ForeColor = WinTheme.Text;
            grid.ClearSelection();
            drop.Focus();
            drop.SelectAll();
        }
        void clearAllShiftFields()
        {
            suppressShiftSelectionLoad = true;
            try
            {
                grid.CurrentCell = null;
                grid.ClearSelection();
                date.Value = DateTime.Today;
                employee.Clear();
                shift.Clear();
                cash.Clear();
                card.Clear();
                net.Clear();
                tax.Clear();
                drop.Clear();
                payout.Clear();
                reason.Clear();
                posReport.Text = "Upload using buttons below";
                posReport.ForeColor = WinTheme.Text;
            }
            finally
            {
                suppressShiftSelectionLoad = false;
            }
            if (managerEntryMode)
                drop.Focus();
            else
                shift.Focus();
        }
        async Task saveNewAsync()
        {
            if (date.Value == DateTime.MinValue)
                throw new InvalidOperationException("Date is required.");
            if (managerEntryMode && !posReport.Text.StartsWith("Imported:", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Import the POS report before adding a Shift Cash Drop entry.", "Shift Cash Drop", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var db = CreateDb();
            var entry = new ShiftLogEntry
            {
                StoreId = _currentStoreId,
                Date = DateOnly.FromDateTime(date.Value),
                Employee = employee.Text.Trim(),
                ShiftNo = shift.Text.Trim(),
                CashTotal = Money(cash.Text),
                CardTotal = Money(card.Text),
                NetSales = Money(net.Text),
                Tax = Money(tax.Text),
                CashDropReceived = Money(drop.Text),
                RegisterPayout = Money(payout.Text),
                PayoutReason = reason.Text.Trim(),
                CreatedByUserId = _session.UserId,
                CreatedByName = _session.DisplayName
            };
            db.ShiftLogs.Add(entry);
            await db.SaveChangesAsync();
            await SyncShiftLogCashDropsToCashOnHandAsync(entry.Date);
            refresh();
            clearAllShiftFields();
        }
        async Task updateSelectedAsync()
        {
            if (!_session.IsAdmin)
            {
                MessageBox.Show(this, "Only Owner/Admin accounts can update shift cash drop entries.", "Access Restricted", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var id = SelectedId(grid);
            if (id is null)
            {
                MessageBox.Show(this, "Please select the log entry you want to correct.", "Shift Cash Drop", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var db = CreateDb();
            var entry = await db.ShiftLogs.FindAsync(id.Value);
            if (entry is null) return;

            using var dialog = new Form
            {
                Text = "Shift Cash Drop Correction",
                Width = 740,
                Height = 710,
                MinimizeBox = false,
                MaximizeBox = false,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterParent
            };
            WinTheme.Apply(dialog);

            var shell = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = WinTheme.Bg, Padding = new Padding(18) };
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            dialog.Controls.Add(shell);

            shell.Controls.Add(new Label
            {
                Text = "CORRECT SHIFT CASH DROP ENTRY",
                Dock = DockStyle.Fill,
                ForeColor = WinTheme.Copper,
                Font = WinTheme.HeaderFont(18),
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);

            var form = WinTheme.BorderedPanel(14);
            form.Dock = DockStyle.Fill;
            var fields = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 10, ColumnCount = 2, BackColor = WinTheme.Panel };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var i = 0; i < 10; i++) fields.RowStyles.Add(new RowStyle(SizeType.Percent, 10f));
            form.Controls.Add(fields);
            shell.Controls.Add(form, 0, 1);

            void AddCorrectionField(int row, string labelText, Control input)
            {
                fields.Controls.Add(new Label
                {
                    Text = labelText,
                    Dock = DockStyle.Fill,
                    ForeColor = Color.White,
                    Font = WinTheme.BoldFont(10),
                    TextAlign = ContentAlignment.MiddleLeft
                }, 0, row);
                input.Dock = DockStyle.Fill;
                input.Margin = new Padding(0, 6, 0, 6);
                fields.Controls.Add(input, 1, row);
            }

            var correctionDate = WinTheme.DatePicker();
            correctionDate.Value = entry.Date.ToDateTime(TimeOnly.MinValue);
            var correctionShift = SectionTextBox(entry.ShiftNo);
            var correctionEmployee = SectionTextBox(entry.Employee);
            var correctionCash = SectionTextBox(entry.CashTotal.ToString("0.00", CultureInfo.CurrentCulture), rightAlign: true);
            var correctionCard = SectionTextBox(entry.CardTotal.ToString("0.00", CultureInfo.CurrentCulture), rightAlign: true);
            var correctionNet = SectionTextBox(entry.NetSales.ToString("0.00", CultureInfo.CurrentCulture), rightAlign: true);
            var correctionTax = SectionTextBox(entry.Tax.ToString("0.00", CultureInfo.CurrentCulture), rightAlign: true);
            var correctionDrop = SectionTextBox(entry.CashDropReceived.ToString("0.00", CultureInfo.CurrentCulture), rightAlign: true);
            var correctionPayout = SectionTextBox(entry.RegisterPayout.ToString("0.00", CultureInfo.CurrentCulture), rightAlign: true);
            var correctionReason = SectionTextBox(entry.PayoutReason);

            AddCorrectionField(0, "Date", correctionDate);
            AddCorrectionField(1, "Shift", correctionShift);
            AddCorrectionField(2, "Employee", correctionEmployee);
            AddCorrectionField(3, "Cash Total", correctionCash);
            AddCorrectionField(4, "Card Total", correctionCard);
            AddCorrectionField(5, "Net Sales", correctionNet);
            AddCorrectionField(6, "Tax Total", correctionTax);
            AddCorrectionField(7, "Cash Drop", correctionDrop);
            AddCorrectionField(8, "Register Payout", correctionPayout);
            AddCorrectionField(9, "Payout Reason", correctionReason);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = WinTheme.Bg, Padding = new Padding(0, 10, 0, 0) };
            var save = WinTheme.Button("Save Correction", true);
            save.Width = 180;
            save.DialogResult = DialogResult.OK;
            var cancel = WinTheme.Button("Cancel");
            cancel.Width = 120;
            cancel.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            shell.Controls.Add(buttons, 0, 2);
            dialog.AcceptButton = save;
            dialog.CancelButton = cancel;

            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            var oldDate = entry.Date;
            entry.Date = DateOnly.FromDateTime(correctionDate.Value);
            entry.Employee = correctionEmployee.Text.Trim();
            entry.ShiftNo = correctionShift.Text.Trim();
            entry.CashTotal = Money(correctionCash.Text);
            entry.CardTotal = Money(correctionCard.Text);
            entry.NetSales = Money(correctionNet.Text);
            entry.Tax = Money(correctionTax.Text);
            entry.CashDropReceived = Money(correctionDrop.Text);
            entry.RegisterPayout = Money(correctionPayout.Text);
            entry.PayoutReason = correctionReason.Text.Trim();
            await db.SaveChangesAsync();
            await SyncShiftLogCashDropsToCashOnHandAsync(oldDate);
            if (entry.Date != oldDate)
                await SyncShiftLogCashDropsToCashOnHandAsync(entry.Date);
            refresh();
            MessageBox.Show(this, "Selected shift cash drop entry updated successfully.", "Shift Cash Drop", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        async Task saveImportedDropAsync()
        {
            var id = SelectedId(grid);
            if (id is null)
            {
                MessageBox.Show(this,
                    "Double-click an automatically imported Z Report row first.",
                    "Shift Cash Drop",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            using var db = CreateDb();
            var entry = await db.ShiftLogs
                .FirstOrDefaultAsync(x => x.Id == id.Value && x.StoreId == _currentStoreId);
            if (entry is null)
                return;
            if (string.IsNullOrWhiteSpace(entry.PosReportKey))
            {
                MessageBox.Show(this,
                    "This is not an automatically imported Z Report. Use Update Selected for a manual correction.",
                    "Shift Cash Drop",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var payoutAmount = Money(payout.Text);
            if (payoutAmount > 0m && string.IsNullOrWhiteSpace(reason.Text))
            {
                MessageBox.Show(this,
                    "Enter the reason for the register payout.",
                    "Shift Cash Drop",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                reason.Focus();
                return;
            }

            entry.CashDropReceived = Money(drop.Text);
            entry.RegisterPayout = payoutAmount;
            entry.PayoutReason = reason.Text.Trim();
            await db.SaveChangesAsync();
            await SyncShiftLogCashDropsToCashOnHandAsync(entry.Date);
            refresh();
            clearAllShiftFields();
            MessageBox.Show(this,
                $"Cash drop saved for batch {entry.ShiftNo} on {entry.Date:M/d/yyyy}.",
                "Shift Cash Drop",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        void loadSelectedIntoForm()
        {
            if (suppressShiftSelectionLoad)
                return;
            var id = SelectedId(grid);
            if (id is null) return;
            using var db = CreateDb();
            var entry = db.ShiftLogs.AsNoTracking().FirstOrDefault(x => x.Id == id.Value);
            if (entry is null) return;
            date.Value = entry.Date.ToDateTime(TimeOnly.MinValue);
            shift.Text = entry.ShiftNo;
            employee.Text = entry.Employee;
            cash.Text = entry.CashTotal.ToString("0.00", CultureInfo.CurrentCulture);
            card.Text = entry.CardTotal.ToString("0.00", CultureInfo.CurrentCulture);
            net.Text = entry.NetSales.ToString("0.00", CultureInfo.CurrentCulture);
            tax.Text = entry.Tax.ToString("0.00", CultureInfo.CurrentCulture);
            drop.Text = entry.CashDropReceived.ToString("0.00", CultureInfo.CurrentCulture);
            payout.Text = entry.RegisterPayout.ToString("0.00", CultureInfo.CurrentCulture);
            reason.Text = entry.PayoutReason;
            posReport.Text = string.IsNullOrWhiteSpace(entry.PosReportKey)
                ? "Selected entry loaded for correction"
                : "Imported: AdventPOS Z Report";
            posReport.ForeColor = string.IsNullOrWhiteSpace(entry.PosReportKey)
                ? WinTheme.Copper
                : WinTheme.Green;
        }
        grid.SelectionChanged += (_, _) => loadSelectedIntoForm();
        grid.CellDoubleClick += (_, eventArgs) =>
        {
            if (eventArgs.RowIndex < 0)
                return;
            grid.Rows[eventArgs.RowIndex].Selected = true;
            grid.CurrentCell = grid.Rows[eventArgs.RowIndex].Cells
                .Cast<DataGridViewCell>()
                .First(cell => cell.Visible);
            loadSelectedIntoForm();
            drop.Focus();
            drop.SelectAll();
        };

        var dashboard = MockActionButton("", "Dashboard", width: 160);
        dashboard.Click += (_, _) => ShowModule("Dashboard");
        actions.Controls.Add(dashboard);
        var upload = MockActionButton("", "Upload POS Report", width: 215);
        upload.Click += (_, _) => UploadPosReport(date, employee, shift, cash, card, net, tax, drop, posReport);
        actions.Controls.Add(upload);
        var clear = MockActionButton("", "Clear Imported", width: 190);
        clear.Click += (_, _) => clearImported();
        actions.Controls.Add(clear);
        var correction = MockActionButton("", "Add Correction", width: 200);
        correction.Click += async (_, _) => await updateSelectedAsync();
        actions.Controls.Add(correction);
        var saveDrop = MockActionButton("", "Save Imported Drop", true, 210);
        saveDrop.Click += async (_, _) => await saveImportedDropAsync();
        actions.Controls.Add(saveDrop);
        var add = MockActionButton("", "Add", true, 135);
        add.Click += async (_, _) =>
        {
            add.Enabled = false;
            try
            {
                await saveNewAsync();
            }
            finally
            {
                add.Enabled = true;
            }
        };
        actions.Controls.Add(add);
        var update = MockActionButton("", "Update Selected", width: 200);
        update.Enabled = _session.IsAdmin;
        update.Click += async (_, _) => await updateSelectedAsync();
        actions.Controls.Add(update);
        var delete = MockActionButton("", "Delete", width: 135);
        delete.Enabled = _session.IsAdmin;
        delete.Click += async (_, _) =>
        {
            var id = SelectedId(grid);
            if (id is null) return;
            if (MessageBox.Show(this, "Delete selected shift entry?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            using var db = CreateDb();
            var entity = await db.ShiftLogs.FirstOrDefaultAsync(x => x.Id == id.Value && x.StoreId == _currentStoreId);
            if (entity is null) return;
            var oldDate = entity.Date;
            db.ShiftLogs.Remove(entity);
            await db.SaveChangesAsync();
            await SyncShiftLogCashDropsToCashOnHandAsync(oldDate);
            refresh();
        };
        actions.Controls.Add(delete);
        recalc();
        refresh();
        clearAllShiftFields();
        return ModuleShell("\uE8C7", "Shift Cash Drop", "Record and track shift cash drops and register payouts.", root);
    }

    private Control BuildCashOnHand()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, BackColor = WinTheme.Bg, Padding = new Padding(2) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));

        var fields = WinTheme.BorderedPanel(10);
        fields.Dock = DockStyle.Fill;
        fields.Margin = new Padding(4, 6, 4, 6);
        var form = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 3, BackColor = WinTheme.Panel };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 23));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 19));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16));
        for (var i = 0; i < 3; i++) form.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));
        fields.Controls.Add(form);
        root.Controls.Add(fields, 0, 0);

        var date = WinTheme.DatePicker();
        var payout = SectionTextBox("$0.00", rightAlign: true);
        var desc = SectionTextBox();
        var cash = SectionTextBox(rightAlign: true);
        var vendor = WinTheme.ComboBox();
        var carryForward = SectionTextBox("$0.00", rightAlign: true);
        var isPayout = SectionCombo("No", "Yes");
        var purpose = WinTheme.ComboBox();
        AddMockField(form, "Date", date, 0, 0, 110);
        AddMockField(form, "Cash Added", cash, 0, 1, 110);
        AddMockField(form, "Purpose", purpose, 0, 2, 110);
        AddMockField(form, "Is Payout", isPayout, 1, 0, 108);
        AddMockField(form, "Payout Amount", payout, 1, 1, 128);
        AddMockField(form, "Vendor", vendor, 2, 0, 80);
        AddMockField(form, "Description", desc, 2, 1, 112);
        form.SetColumnSpan(form.GetControlFromPosition(2, 1)!, 2);
        AddMockField(form, "Carry Forward", carryForward, 4, 1, 128);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = false, BackColor = WinTheme.Bg, Padding = new Padding(0, 6, 0, 6) };
        root.Controls.Add(actions, 0, 1);
        var stats = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = false, BackColor = WinTheme.Bg, Padding = new Padding(2, 9, 2, 9) };
        root.Controls.Add(stats, 0, 2);
        var grid = WinTheme.Grid();
        var suppressCashSelectionLoad = false;
        root.Controls.Add(grid, 0, 3);
        root.Controls.Add(BuildGridFooter("Cash on hand records for selected store"), 0, 4);
        Label currentBalance = null!;
        Label todayAdded = null!;
        Label pendingPayouts = null!;
        Label openingBalance = null!;
        Label closingBalance = null!;
        int? ComboId(ComboBox combo)
            => combo.SelectedValue is int id ? id : null;

        async Task loadLookupsAsync()
        {
            using var db = CreateDb();
            var vendors = await db.Vendors.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId)
                .OrderBy(x => x.Name)
                .ToListAsync();
            var purposes = await db.Purposes.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId)
                .OrderBy(x => x.Name)
                .ToListAsync();
            vendor.DataSource = vendors;
            vendor.DisplayMember = nameof(Vendor.Name);
            vendor.ValueMember = nameof(Vendor.Id);
            vendor.SelectedIndex = vendors.Count > 0 ? 0 : -1;
            purpose.DataSource = purposes;
            purpose.DisplayMember = nameof(Purpose.Name);
            purpose.ValueMember = nameof(Purpose.Id);
            purpose.SelectedIndex = purposes.Count > 0 ? 0 : -1;
        }

        async Task refreshAsync()
        {
            await SyncShiftLogCashDropsToCashOnHandRecentAsync(120);
            using var db = CreateDb();
            var rows = db.CashOnHand.AsNoTracking()
                .Include(x => x.Vendor).Include(x => x.Purpose)
                .Where(x => x.StoreId == _currentStoreId)
                .ToList();
            var effectiveRows = EffectiveRows(rows, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);
            var today = DateOnly.FromDateTime(DateTime.Today);
            var monthStart = new DateOnly(today.Year, today.Month, 1);
            var nextMonth = monthStart.AddMonths(1);
            grid.DataSource = rows.OrderByDescending(x => x.Date).ThenByDescending(x => x.Id)
                .Select(x => new { x.Id, x.Date, x.CashAdded, x.IsPayout, Payout = x.PayoutAmount, Vendor = x.Vendor != null ? x.Vendor.Name : "", Purpose = x.Purpose != null ? x.Purpose.Name : "", x.Description, Check = x.Reference })
                .ToList();
            currentBalance.Text = effectiveRows.Sum(x => x.CashAdded - x.PayoutAmount).ToString("C2");
            todayAdded.Text = effectiveRows.Where(x => !x.IsPayout && x.Date == today).Sum(x => x.CashAdded).ToString("C2");
            pendingPayouts.Text = effectiveRows.Where(x => x.IsPayout && x.Date >= monthStart && x.Date < nextMonth).Sum(x => x.PayoutAmount).ToString("C2");
            openingBalance.Text = effectiveRows.Where(x => x.Date < today).Sum(x => x.CashAdded - x.PayoutAmount).ToString("C2");
            closingBalance.Text = currentBalance.Text;
            HideId(grid);
        }
        void refresh() => _ = refreshAsync();

        void clearCashOnHandFields()
        {
            suppressCashSelectionLoad = true;
            try
            {
                grid.CurrentCell = null;
                grid.ClearSelection();
                date.Value = DateTime.Today;
                cash.Clear();
                isPayout.SelectedIndex = 0;
                payout.Clear();
                vendor.SelectedIndex = -1;
                purpose.SelectedIndex = -1;
                desc.Clear();
                carryForward.Clear();
            }
            finally
            {
                suppressCashSelectionLoad = false;
            }
            cash.Focus();
        }

        async Task setCarryForwardAsync()
        {
            using var db = CreateDb();
            var amount = Money(carryForward.Text);
            var today = DateTime.Today;
            var firstDay = new DateOnly(today.Year, today.Month, 1);
            var existing = await db.CashOnHand
                .Where(x => x.StoreId == _currentStoreId && x.Date == firstDay && x.Reference == "CARRY_FORWARD" && !x.IsCorrection)
                .OrderByDescending(x => x.Id)
                .FirstOrDefaultAsync();
            if (existing is null)
            {
                db.CashOnHand.Add(new CashOnHandEntry
                {
                    StoreId = _currentStoreId,
                    Date = firstDay,
                    CashAdded = amount,
                    IsPayout = false,
                    PayoutAmount = 0m,
                    Description = "Carry Forward (Start of Month)",
                    Reference = "CARRY_FORWARD",
                    CreatedByUserId = _session.UserId,
                    CreatedByName = _session.DisplayName
                });
            }
            else
            {
                existing.CashAdded = amount;
                existing.IsPayout = false;
                existing.PayoutAmount = 0m;
                existing.Description = "Carry Forward (Start of Month)";
            }
            await db.SaveChangesAsync();
            await refreshAsync();
        }

        void loadSelectedIntoForm()
        {
            if (suppressCashSelectionLoad)
                return;
            var id = SelectedId(grid);
            if (id is null) return;
            using var db = CreateDb();
            var entry = db.CashOnHand.AsNoTracking().FirstOrDefault(x => x.Id == id.Value && x.StoreId == _currentStoreId);
            if (entry is null) return;
            date.Value = entry.Date.ToDateTime(TimeOnly.MinValue);
            cash.Text = entry.CashAdded.ToString("0.00", CultureInfo.CurrentCulture);
            isPayout.SelectedIndex = entry.IsPayout ? 1 : 0;
            payout.Text = entry.PayoutAmount.ToString("0.00", CultureInfo.CurrentCulture);
            if (entry.VendorId.HasValue) vendor.SelectedValue = entry.VendorId.Value;
            if (entry.PurposeId.HasValue) purpose.SelectedValue = entry.PurposeId.Value;
            desc.Text = entry.Description;
        }
        grid.SelectionChanged += (_, _) => loadSelectedIntoForm();

        var dashboard = MockActionButton("", "Go to Dashboard", width: 175);
        dashboard.Click += (_, _) => ShowModule("Dashboard");
        actions.Controls.Add(dashboard);
        var add = MockActionButton("", "Add", true, 145);
        add.Click += async (_, _) =>
        {
            add.Enabled = false;
            try
            {
                using var db = CreateDb();
                db.CashOnHand.Add(new CashOnHandEntry
                {
                    StoreId = _currentStoreId,
                    Date = DateOnly.FromDateTime(date.Value),
                    CashAdded = Money(cash.Text),
                    IsPayout = isPayout.SelectedIndex == 1,
                    PayoutAmount = Money(payout.Text),
                    VendorId = ComboId(vendor),
                    PurposeId = ComboId(purpose),
                    Description = desc.Text.Trim(),
                    CreatedByUserId = _session.UserId,
                    CreatedByName = _session.DisplayName
                });
                await db.SaveChangesAsync();
                await refreshAsync();
                clearCashOnHandFields();
            }
            finally
            {
                add.Enabled = true;
            }
        };
        actions.Controls.Add(add);
        var correction = MockActionButton("", "Add Correction", width: 190);
        correction.Click += async (_, _) =>
        {
            var id = SelectedId(grid);
            if (id is null)
            {
                MessageBox.Show(this, "Select a cash row to correct.", "Cash On Hand", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var db = CreateDb();
            var original = await db.CashOnHand.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id.Value && x.StoreId == _currentStoreId);
            if (original is null) return;
            if (original.IsCorrection)
            {
                MessageBox.Show(this, "You selected a correction row. Please select the original row to correct.", "Cash On Hand", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var dialog = new Form
            {
                Text = "Cash On Hand Correction",
                Width = 700,
                Height = 560,
                MinimizeBox = false,
                MaximizeBox = false,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterParent
            };
            WinTheme.Apply(dialog);
            var shell = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = WinTheme.Bg, Padding = new Padding(18) };
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            dialog.Controls.Add(shell);
            shell.Controls.Add(new Label { Text = "CORRECT CASH ON HAND ENTRY", Dock = DockStyle.Fill, ForeColor = WinTheme.Copper, Font = WinTheme.HeaderFont(18), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            var panel = WinTheme.BorderedPanel(14);
            panel.Dock = DockStyle.Fill;
            var fields = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 8, ColumnCount = 2, BackColor = WinTheme.Panel };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var i = 0; i < 8; i++) fields.RowStyles.Add(new RowStyle(SizeType.Percent, 12.5f));
            panel.Controls.Add(fields);
            shell.Controls.Add(panel, 0, 1);

            void AddField(int row, string labelText, Control input)
            {
                fields.Controls.Add(new Label { Text = labelText, Dock = DockStyle.Fill, ForeColor = Color.White, Font = WinTheme.BoldFont(10), TextAlign = ContentAlignment.MiddleLeft }, 0, row);
                input.Dock = DockStyle.Fill;
                input.Margin = new Padding(0, 6, 0, 6);
                fields.Controls.Add(input, 1, row);
            }

            var correctionDate = WinTheme.DatePicker();
            correctionDate.Value = original.Date.ToDateTime(TimeOnly.MinValue);
            var correctionCash = SectionTextBox(original.CashAdded.ToString("0.00", CultureInfo.CurrentCulture), rightAlign: true);
            var correctionIsPayout = SectionCombo("No", "Yes");
            correctionIsPayout.SelectedIndex = original.IsPayout ? 1 : 0;
            var correctionPayout = SectionTextBox(original.PayoutAmount.ToString("0.00", CultureInfo.CurrentCulture), rightAlign: true);
            var correctionVendor = WinTheme.ComboBox();
            var correctionPurpose = WinTheme.ComboBox();
            var vendors = await db.Vendors.AsNoTracking().Where(x => x.StoreId == _currentStoreId).OrderBy(x => x.Name).ToListAsync();
            var purposes = await db.Purposes.AsNoTracking().Where(x => x.StoreId == _currentStoreId).OrderBy(x => x.Name).ToListAsync();
            correctionVendor.DataSource = vendors;
            correctionVendor.DisplayMember = nameof(Vendor.Name);
            correctionVendor.ValueMember = nameof(Vendor.Id);
            correctionVendor.SelectedValue = original.VendorId ?? 0;
            correctionPurpose.DataSource = purposes;
            correctionPurpose.DisplayMember = nameof(Purpose.Name);
            correctionPurpose.ValueMember = nameof(Purpose.Id);
            correctionPurpose.SelectedValue = original.PurposeId ?? 0;
            var correctionDesc = SectionTextBox(original.Description);
            var auditReason = SectionTextBox();
            AddField(0, "Date", correctionDate);
            AddField(1, "Cash Added", correctionCash);
            AddField(2, "Is Payout", correctionIsPayout);
            AddField(3, "Payout Amount", correctionPayout);
            AddField(4, "Vendor", correctionVendor);
            AddField(5, "Purpose", correctionPurpose);
            AddField(6, "Description", correctionDesc);
            AddField(7, "Correction Reason", auditReason);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = WinTheme.Bg, Padding = new Padding(0, 10, 0, 0) };
            var save = WinTheme.Button("Save Correction", true);
            save.Width = 180;
            save.DialogResult = DialogResult.OK;
            var cancel = WinTheme.Button("Cancel");
            cancel.Width = 120;
            cancel.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            shell.Controls.Add(buttons, 0, 2);
            dialog.AcceptButton = save;
            dialog.CancelButton = cancel;

            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            using var saveDb = CreateDb();
            var entity = await saveDb.CashOnHand.FirstOrDefaultAsync(x => x.Id == original.Id && x.StoreId == _currentStoreId);
            if (entity is null) return;
            entity.Date = DateOnly.FromDateTime(correctionDate.Value);
            entity.CashAdded = Money(correctionCash.Text);
            entity.IsPayout = correctionIsPayout.SelectedIndex == 1;
            entity.PayoutAmount = Money(correctionPayout.Text);
            entity.VendorId = ComboId(correctionVendor);
            entity.PurposeId = ComboId(correctionPurpose);
            entity.Description = correctionDesc.Text.Trim();
            entity.CorrectionReason = auditReason.Text.Trim();
            entity.CreatedByUserId = _session.UserId;
            entity.CreatedByName = _session.DisplayName;
            await saveDb.SaveChangesAsync();
            await refreshAsync();
        };
        actions.Controls.Add(correction);
        var update = MockActionButton("", "Update Selected", width: 190);
        update.Enabled = _session.IsAdmin;
        update.Click += async (_, _) =>
        {
            var id = SelectedId(grid);
            if (id is null) return;
            using var db = CreateDb();
            var entity = await db.CashOnHand.FirstOrDefaultAsync(x => x.Id == id.Value && x.StoreId == _currentStoreId);
            if (entity is null) return;
            entity.Date = DateOnly.FromDateTime(date.Value);
            entity.CashAdded = Money(cash.Text);
            entity.IsPayout = isPayout.SelectedIndex == 1;
            entity.PayoutAmount = Money(payout.Text);
            entity.VendorId = ComboId(vendor);
            entity.PurposeId = ComboId(purpose);
            entity.Description = desc.Text.Trim();
            await db.SaveChangesAsync();
            await refreshAsync();
        };
        actions.Controls.Add(update);
        var delete = MockActionButton("", "Delete Selected", width: 200);
        delete.Enabled = _session.IsAdmin;
        delete.Click += async (_, _) => await DeleteSelectedAsync<CashOnHandEntry>(grid, refresh);
        actions.Controls.Add(delete);
        var setCarry = MockActionButton("", "Set Carry Forward", width: 215);
        setCarry.Click += async (_, _) => await setCarryForwardAsync();
        actions.Controls.Add(setCarry);
        openingBalance = MockSummaryValue(stats, "Opening Balance", WinTheme.Copper, 210);
        todayAdded = MockSummaryValue(stats, "Cash Added Today", WinTheme.Green, 210);
        pendingPayouts = MockSummaryValue(stats, "Payouts This Month", WinTheme.Red, 210);
        closingBalance = MockSummaryValue(stats, "Closing Balance", WinTheme.Green, 210);
        currentBalance = MockSummaryValue(stats, "Carry Forward", WinTheme.Copper, 210);
        var cashOnHandInitialized = false;
        async Task initializeCashOnHandAsync()
        {
            if (cashOnHandInitialized)
                return;

            cashOnHandInitialized = true;
            try
            {
                await loadLookupsAsync();
                await refreshAsync();
                clearCashOnHandFields();
            }
            catch (Exception ex)
            {
                cashOnHandInitialized = false;
                MessageBox.Show(
                    this,
                    $"Cash On Hand could not load.\n\n{AppBootstrap.RedactSensitiveText(ex.Message)}",
                    "Cash On Hand",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        // ShowModule runs this only after the completed section is attached to
        // the visible content panel. This avoids the old builder/handle timing
        // race that left Cash On Hand empty until Refresh was clicked.
        _pendingModuleActivation = initializeCashOnHandAsync;
        return ModuleShell("\uEAFD", "Cash On Hand", "Track cash added, payouts, and carry forward balance.", root);
    }

    private Control BuildCheckPayout()
    {
        var root = MockSectionPage(315, 72);
        var formShell = WinTheme.BorderedPanel(10);
        formShell.Dock = DockStyle.Fill;
        formShell.Margin = new Padding(4, 6, 4, 6);
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = WinTheme.Panel };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        formShell.Controls.Add(top);
        root.Controls.Add(formShell, 0, 0);

        var previewCard = MockBorderCard("Check Details", "\uE8A1", 34);
        var previewShell = previewCard.Parent!;
        previewShell.Margin = new Padding(0, 0, 8, 0);
        top.Controls.Add(previewShell, 0, 0);
        var previewHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(230, 236, 240), Margin = new Padding(10, 4, 10, 10), Padding = new Padding(14) };
        previewCard.Controls.Add(previewHost, 0, 1);

        var formCard = MockBorderCard("Payment Details", "\uE8C7", 34);
        var formShellInner = formCard.Parent!;
        formShellInner.Margin = new Padding(0, 0, 8, 0);
        top.Controls.Add(formShellInner, 1, 0);
        var form = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, BackColor = WinTheme.Panel, Padding = new Padding(4, 0, 4, 4) };
        for (var i = 0; i < 6; i++) form.RowStyles.Add(new RowStyle(SizeType.Percent, 16.66f));
        formCard.Controls.Add(form, 0, 1);

        var summaryCard = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = WinTheme.Panel };
        summaryCard.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        summaryCard.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        top.Controls.Add(summaryCard, 2, 0);

        var date = WinTheme.DatePicker();
        var vendor = SectionTextBox();
        var desc = SectionTextBox();
        var amount = SectionTextBox(rightAlign: true);
        var check = SectionTextBox();
        var cleared = SectionCombo("Uncleared", "Cleared");
        AddMockField(form, "Date", date, 0, 0, 118);
        AddMockField(form, "Vendor", vendor, 0, 1, 118);
        AddMockField(form, "Amount", amount, 0, 2, 118);
        AddMockField(form, "Purpose", desc, 0, 3, 118);
        AddMockField(form, "Bank Account", SectionTextBox("Operating Account"), 0, 4, 118);
        AddMockField(form, "Check #", check, 0, 5, 118);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = false, BackColor = WinTheme.Bg, Padding = new Padding(0, 6, 0, 6) };
        root.Controls.Add(actions, 0, 1);
        var grid = WinTheme.Grid();
        grid.AutoGenerateColumns = false;
        grid.ReadOnly = false;
        grid.EditMode = DataGridViewEditMode.EditOnEnter;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Id", DataPropertyName = nameof(CheckPayoutGridRow.Id), Visible = false, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Date", DataPropertyName = nameof(CheckPayoutGridRow.Date), HeaderText = "Date", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Vendor", DataPropertyName = nameof(CheckPayoutGridRow.Vendor), HeaderText = "Vendor", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Description", DataPropertyName = nameof(CheckPayoutGridRow.Description), HeaderText = "Description", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Amount",
            DataPropertyName = nameof(CheckPayoutGridRow.Amount),
            HeaderText = "Amount",
            ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle { Format = "C2" }
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Check", DataPropertyName = nameof(CheckPayoutGridRow.Check), HeaderText = "Check #", ReadOnly = true });
        grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Cleared",
            DataPropertyName = nameof(CheckPayoutGridRow.Cleared),
            HeaderText = "Cleared",
            ReadOnly = false,
            ThreeState = false,
            TrueValue = true,
            FalseValue = false
        });
        root.Controls.Add(grid, 0, 2);
        root.Controls.Add(BuildGridFooter("Check payout records for selected store"), 0, 3);
        Label unclearedTotal = null!;
        Label clearedThisMonth = null!;
        Label nextCheck = null!;
        var checkPreview = new CheckPreviewPanel();
        checkPreview.Dock = DockStyle.Fill;
        previewHost.Controls.Add(checkPreview);
        void updatePreview()
        {
            checkPreview.Payee = string.IsNullOrWhiteSpace(vendor.Text) ? "Vendor" : vendor.Text.Trim();
            checkPreview.Amount = Money(amount.Text);
            checkPreview.CheckNumber = string.IsNullOrWhiteSpace(check.Text) ? "Next" : check.Text.Trim();
            checkPreview.Invalidate();
        }
        vendor.TextChanged += (_, _) => updatePreview();
        amount.TextChanged += (_, _) => updatePreview();
        check.TextChanged += (_, _) => updatePreview();
        var refreshingChecks = false;
        void configureCheckColumns()
        {
            foreach (DataGridViewColumn column in grid.Columns)
                column.ReadOnly = !string.Equals(column.Name, "Cleared", StringComparison.OrdinalIgnoreCase);
        }
        grid.DataBindingComplete += (_, _) => configureCheckColumns();
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty)
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex >= 0 &&
                string.Equals(grid.Columns[e.ColumnIndex].Name, "Cleared", StringComparison.OrdinalIgnoreCase))
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        grid.CellValueChanged += async (_, e) =>
        {
            if (refreshingChecks || e.RowIndex < 0 ||
                !string.Equals(grid.Columns[e.ColumnIndex].Name, "Cleared", StringComparison.OrdinalIgnoreCase))
                return;
            if (!int.TryParse(grid.Rows[e.RowIndex].Cells["Id"].Value?.ToString(), out var id))
                return;
            using var db = CreateDb();
            var row = await db.CheckPayouts.FirstOrDefaultAsync(x => x.Id == id && x.StoreId == _currentStoreId);
            if (row is null)
                return;
            row.Cleared = Convert.ToBoolean(grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value);
            await db.SaveChangesAsync();
            refresh();
        };
        void refresh()
        {
            refreshingChecks = true;
            using var db = CreateDb();
            var rows = db.CheckPayouts.AsNoTracking().Where(x => x.StoreId == _currentStoreId).ToList();
            grid.DataSource = rows.OrderByDescending(x => x.Date).ThenByDescending(x => x.Id)
                .Select(x => new CheckPayoutGridRow
                {
                    Id = x.Id,
                    Date = x.Date,
                    Vendor = x.VendorName,
                    Description = x.Description,
                    Amount = x.CheckAmount,
                    Check = x.CheckNumber,
                    Cleared = x.Cleared
                })
                .ToList();
            unclearedTotal.Text = rows.Where(x => !x.Cleared).Sum(x => x.CheckAmount).ToString("C2");
            clearedThisMonth.Text = rows.Where(x => x.Cleared && x.Date.Month == DateTime.Today.Month && x.Date.Year == DateTime.Today.Year).Sum(x => x.CheckAmount).ToString("C2");
            nextCheck.Text = NextCheckNumber(rows);
            HideId(grid);
            configureCheckColumns();
            refreshingChecks = false;
        }
        var newCheck = MockActionButton("", "New Check", width: 155);
        newCheck.Click += (_, _) =>
        {
            vendor.Clear();
            desc.Clear();
            amount.Clear();
            check.Clear();
            cleared.SelectedIndex = 0;
            date.Value = DateTime.Today;
        };
        actions.Controls.Add(newCheck);
        var add = MockActionButton("", "Save", true, 135);
        add.Click += async (_, _) =>
        {
            using var db = CreateDb();
            db.CheckPayouts.Add(new CheckPayout
            {
                StoreId = _currentStoreId,
                Date = DateOnly.FromDateTime(date.Value),
                VendorName = vendor.Text.Trim(),
                Description = desc.Text.Trim(),
                CheckAmount = Money(amount.Text),
                CheckNumber = check.Text.Trim(),
                Cleared = cleared.SelectedIndex == 1,
                CreatedByUserId = _session.UserId,
                CreatedByName = _session.DisplayName
            });
            await db.SaveChangesAsync();
            refresh();
        };
        actions.Controls.Add(add);
        var print = MockActionButton("", "Print Check", width: 170);
        print.Click += async (_, _) => await PrintSelectedCheckAsync(grid);
        actions.Controls.Add(print);
        var toggle = MockActionButton("", "Void Check", width: 165);
        toggle.Click += async (_, _) =>
        {
            var id = SelectedId(grid);
            if (id is null) return;
            using var db = CreateDb();
            var row = await db.CheckPayouts.FindAsync(id.Value);
            if (row is null) return;
            row.Cleared = !row.Cleared;
            await db.SaveChangesAsync();
            refresh();
        };
        actions.Controls.Add(toggle);
        var clear = MockActionButton("", "Clear", width: 135);
        clear.Click += (_, _) =>
        {
            vendor.Clear();
            desc.Clear();
            amount.Clear();
            check.Clear();
            cleared.SelectedIndex = 0;
        };
        actions.Controls.Add(clear);
        var delete = MockActionButton("", "Delete Selected", width: 200);
        delete.Enabled = _session.IsAdmin;
        delete.Click += async (_, _) => await DeleteSelectedAsync<CheckPayout>(grid, refresh);
        actions.Controls.Add(delete);
        var unclearedBox = WinTheme.BorderedPanel(10);
        unclearedBox.Dock = DockStyle.Fill;
        unclearedBox.Margin = new Padding(0, 0, 0, 6);
        var clearedBox = WinTheme.BorderedPanel(10);
        clearedBox.Dock = DockStyle.Fill;
        clearedBox.Margin = new Padding(0, 6, 0, 0);
        summaryCard.Controls.Add(unclearedBox, 0, 0);
        summaryCard.Controls.Add(clearedBox, 0, 1);
        unclearedTotal = BuildCheckSummaryBox(unclearedBox, "Uncleared Summary", WinTheme.Red);
        clearedThisMonth = BuildCheckSummaryBox(clearedBox, "Cleared Summary", WinTheme.Green);
        nextCheck = new Label { Visible = false };
        updatePreview();
        refresh();
        return ModuleShell("\uE8A1", "Check Payout", "Record, print, and clear vendor check payouts.", root);
    }

    private Control BuildOperationsHub()
    {
        var snapshot = LoadOperationsHubSnapshot();
        var now = DateTime.Today;
        var toolbarHeight = DpiScale(76);
        var cardRowHeight = DpiScale(324);
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = WinTheme.Bg,
            Padding = new Padding(2, 0, 2, 4)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, toolbarHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var toolbar = WinTheme.BorderedPanel(10);
        toolbar.Dock = DockStyle.Fill;
        toolbar.Margin = new Padding(6, 0, 6, 8);
        var toolbarLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.White
        };
        toolbarLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbarLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        toolbarLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        toolbarLayout.Controls.Add(new Label
        {
            Text = "PRIORITY WORKFLOWS\r\nEight essential areas, live totals, trend graphs, and direct actions in one place.",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Text,
            Font = WinTheme.BoldFont(11),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        toolbarLayout.Controls.Add(new Label
        {
            Text = $"\u25CF  LIVE STORE DATA\r\nAs of {now:MMMM d, yyyy}",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Green,
            Font = WinTheme.BoldFont(9),
            TextAlign = ContentAlignment.MiddleLeft
        }, 1, 0);
        var refresh = WinTheme.Button("REFRESH HUB", true);
        refresh.Dock = DockStyle.Fill;
        refresh.Margin = new Padding(4, 6, 0, 6);
        refresh.Click += (_, _) => ShowModule("Operations Hub");
        toolbarLayout.Controls.Add(refresh, 2, 0);
        toolbar.Controls.Add(toolbarLayout);
        root.Controls.Add(toolbar, 0, 0);

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 4,
            RowCount = 2,
            BackColor = WinTheme.Bg,
            Margin = Padding.Empty,
            Height = cardRowHeight * 2,
            MinimumSize = new Size(DpiScale(1320), cardRowHeight * 2)
        };
        for (var i = 0; i < 4; i++)
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, cardRowHeight));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, cardRowHeight));

        var shiftCard = BuildOperationsCommandCard(
            "\uE8C7", "Shift Cash Log", "Daily register and cash-drop activity", "TODAY",
            "CASH DROP RECEIVED", snapshot.TodayCashDrop.ToString("C2"), WinTheme.Blue,
            "SHIFTS ENTERED", snapshot.TodayShiftCount.ToString("N0"),
            "NET SALES", snapshot.TodayNetSales.ToString("C2"),
            "OPEN SHIFT LOG", () => { ShowModule("Shift Cash Drop"); return Task.CompletedTask; },
            trend: snapshot.SalesTrend, trendCaption: "7-DAY NET SALES");
        grid.Controls.Add(shiftCard.Card, 0, 0);

        var cashCard = BuildOperationsCommandCard(
            "\uEAFD", "Cash On Hand", "Cash balance, additions, and payouts", "CURRENT",
            "AVAILABLE BALANCE", snapshot.CashBalance.ToString("C2"), snapshot.CashBalance >= 0 ? WinTheme.Green : WinTheme.Red,
            "ADDED THIS MONTH", snapshot.MonthCashAdded.ToString("C2"),
            "PAYOUTS THIS MONTH", snapshot.MonthCashPayouts.ToString("C2"),
            "OPEN CASH ON HAND", () => { ShowModule("Cash On Hand"); return Task.CompletedTask; },
            trend: snapshot.CashTrend, trendCaption: "7-DAY CASH MOVEMENT");
        grid.Controls.Add(cashCard.Card, 1, 0);

        var bankCard = BuildOperationsCommandCard(
            "\uE825", "Bank Statement", "Live sync, imports, and reconciliation", "THIS MONTH",
            "NET MOVEMENT", "Loading...", WinTheme.Blue,
            "TRANSACTIONS", "\u2014",
            "LATEST ACTIVITY", "\u2014",
            "OPEN BANK STATEMENT", () => { ShowModule("Bank Statement"); return Task.CompletedTask; },
            "IMPORT STATEMENT", async () => await ImportBankStatementAsync(now.Month, now.Year, () =>
            {
                ShowModule("Operations Hub");
                return Task.CompletedTask;
            }),
            trend: Array.Empty<decimal>(), trendCaption: "DAILY BANK MOVEMENT");
        grid.Controls.Add(bankCard.Card, 2, 0);

        var profitCard = BuildOperationsCommandCard(
            "\uE9D9", "Profit & Loss", "Current-month business performance", "THIS MONTH",
            "NET PROFIT", snapshot.MonthNetProfit.ToString("C2"), snapshot.MonthNetProfit >= 0 ? WinTheme.Green : WinTheme.Red,
            "NET SALES", snapshot.MonthNetSales.ToString("C2"),
            "TOTAL OUTFLOW", snapshot.MonthOutflow.ToString("C2"),
            "OPEN PROFIT & LOSS", () => { ShowModule("Profit & Loss"); return Task.CompletedTask; },
            "CREATE PDF", GenerateProfitLossPdfAsync,
            snapshot.ProfitTrend, "7-DAY OPERATING RESULT");
        grid.Controls.Add(profitCard.Card, 3, 0);

        var checksCard = BuildOperationsCommandCard(
            "\uE8A1", "Check Payout", "Vendor checks and clearing status", "ACTION",
            "UNCLEARED AMOUNT", snapshot.UnclearedCheckAmount.ToString("C2"), snapshot.UnclearedCheckCount > 0 ? WinTheme.Copper : WinTheme.Green,
            "UNCLEARED CHECKS", snapshot.UnclearedCheckCount.ToString("N0"),
            "PAID THIS MONTH", snapshot.MonthCheckPayouts.ToString("C2"),
            "OPEN CHECK PAYOUT", () => { ShowModule("Check Payout"); return Task.CompletedTask; },
            trend: snapshot.CheckTrend, trendCaption: "7-DAY CHECK PAYOUTS");
        grid.Controls.Add(checksCard.Card, 0, 1);

        var purchasesCard = BuildOperationsCommandCard(
            "\uE7BF", "Purchases", "Vendor invoices and inventory spending", "THIS MONTH",
            "PURCHASE TOTAL", snapshot.MonthPurchases.ToString("C2"), WinTheme.Blue,
            "INVOICES", snapshot.MonthPurchaseCount.ToString("N0"),
            "LATEST ENTRY", snapshot.LatestPurchaseDate,
            "OPEN PURCHASES", () => { ShowModule("Purchases"); return Task.CompletedTask; },
            "IMPORT INVOICE", async () => await ImportPurchaseInvoiceAsync(() =>
            {
                ShowModule("Operations Hub");
                return Task.CompletedTask;
            }),
            snapshot.PurchaseTrend, "7-DAY PURCHASES");
        grid.Controls.Add(purchasesCard.Card, 1, 1);

        var costsCard = BuildOperationsCommandCard(
            "\uE71B", "Product Costs", "Tracked supplier and item cost movement", "LIVE",
            "PRODUCTS TRACKED", snapshot.ProductCount.ToString("N0"), WinTheme.Blue,
            "CHANGED IN 30 DAYS", snapshot.RecentProductChanges.ToString("N0"),
            "AVERAGE UNIT COST", snapshot.AverageUnitCost.ToString("C2"),
            "OPEN PRODUCT COSTS", () => { ShowModule("Product Costs"); return Task.CompletedTask; },
            trend: snapshot.ProductCostTrend, trendCaption: "RECENT UNIT COST TREND");
        grid.Controls.Add(costsCard.Card, 2, 1);

        var alertsCard = BuildOperationsCommandCard(
            "\uE7BA", "Price Alerts", "Supplier cost changes requiring review", "ALERTS",
            "UNREAD ALERTS", snapshot.UnreadPriceAlerts.ToString("N0"), snapshot.UnreadPriceAlerts > 0 ? WinTheme.Red : WinTheme.Green,
            "HIGH PRIORITY", snapshot.HighPriorityAlerts.ToString("N0"),
            "TOTAL ALERTS", snapshot.TotalPriceAlerts.ToString("N0"),
            "OPEN PRICE ALERTS", () => { ShowModule("Price Alerts"); return Task.CompletedTask; },
            trend: snapshot.PriceAlertTrend, trendCaption: "7-DAY ALERT ACTIVITY");
        grid.Controls.Add(alertsCard.Card, 3, 1);

        var gridViewport = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Bg,
            AutoScroll = true,
            Margin = Padding.Empty
        };
        gridViewport.Controls.Add(grid);
        root.Controls.Add(gridViewport, 0, 1);
        root.HandleCreated += async (_, _) =>
        {
            try
            {
                var rows = await LoadBankStatementRowsAsync(now.Month, now.Year);
                if (root.IsDisposed || bankCard.Card.IsDisposed)
                    return;

                bankCard.MetricValue.Text = rows.Sum(x => x.Credit - x.Debit).ToString("C2");
                bankCard.MetricValue.ForeColor = rows.Sum(x => x.Credit - x.Debit) >= 0 ? WinTheme.Green : WinTheme.Red;
                bankCard.LeftFactValue.Text = rows.Count.ToString("N0");
                bankCard.RightFactValue.Text = rows.Count == 0 ? "No activity" : rows.Max(x => x.Date).ToString("M/d/yyyy");
                bankCard.Trend.SetValues(
                    Enumerable.Range(0, 7)
                        .Select(offset =>
                        {
                            var day = DateOnly.FromDateTime(now.AddDays(offset - 6));
                            return rows.Where(x => DateOnly.FromDateTime(x.Date) == day).Sum(x => x.Credit - x.Debit);
                        })
                        .ToArray());
            }
            catch
            {
                if (!root.IsDisposed && !bankCard.Card.IsDisposed)
                {
                    bankCard.MetricValue.Text = "Open to review";
                    bankCard.MetricValue.ForeColor = WinTheme.Muted;
                    bankCard.LeftFactValue.Text = "\u2014";
                    bankCard.RightFactValue.Text = "\u2014";
                }
            }
        };

        return ModuleShell("\uECA5", "Operation Hub", "Priority operations, live financial summaries, and quick actions.", root);
    }

    private OperationsHubSnapshot LoadOperationsHubSnapshot()
    {
        try
        {
            var now = DateTime.Today;
            using var db = CreateDb();
            var shifts = EffectiveRows(
                db.ShiftLogs.AsNoTracking().Where(x => x.StoreId == _currentStoreId).ToList(),
                x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);
            var cash = EffectiveRows(
                db.CashOnHand.AsNoTracking().Where(x => x.StoreId == _currentStoreId).ToList(),
                x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);
            var checks = EffectiveRows(
                db.CheckPayouts.AsNoTracking().Where(x => x.StoreId == _currentStoreId).ToList(),
                x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);
            var purchases = db.PurchaseInvoices.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId)
                .ToList();

            var todayShifts = shifts.Where(x => x.Date == DateOnly.FromDateTime(now)).ToList();
            var monthShifts = shifts.Where(x => x.Date.Month == now.Month && x.Date.Year == now.Year).ToList();
            var monthCash = cash.Where(x => x.Date.Month == now.Month && x.Date.Year == now.Year).ToList();
            var monthChecks = checks.Where(x => x.Date.Month == now.Month && x.Date.Year == now.Year).ToList();
            var monthPurchases = purchases.Where(x => x.InvoiceDate.Month == now.Month && x.InvoiceDate.Year == now.Year).ToList();
            var productCosts = db.ProductCosts.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId)
                .ToList();
            var priceAlerts = db.PriceAlerts.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId)
                .ToList();

            decimal payroll = 0;
            try
            {
                payroll = db.PayrollEntries.AsNoTracking()
                    .Where(x => x.PayrollRun!.StoreId == _currentStoreId
                                && x.PayrollRun.Status == PayrollRunStatus.Finalized
                                && x.PayrollRun.PayDate.Month == now.Month
                                && x.PayrollRun.PayDate.Year == now.Year)
                    .Sum(x => (decimal?)x.GrossPay)
                    .GetValueOrDefault();
            }
            catch
            {
                // Payroll is an optional licensed service and older databases may not have its tables yet.
            }

            var netSales = monthShifts.Sum(x => x.NetSales);
            var purchaseTotal = monthPurchases.Sum(x => x.Total);
            var operatingExpenses = monthCash.Where(x => x.IsPayout).Sum(x => x.PayoutAmount)
                                    + monthChecks.Sum(x => x.CheckAmount)
                                    + payroll;
            var trendDays = Enumerable.Range(0, 7)
                .Select(offset => DateOnly.FromDateTime(now.AddDays(offset - 6)))
                .ToArray();
            var salesTrend = trendDays
                .Select(day => shifts.Where(x => x.Date == day).Sum(x => x.NetSales))
                .ToArray();
            var cashTrend = trendDays
                .Select(day => cash.Where(x => x.Date == day).Sum(x => x.CashAdded - x.PayoutAmount))
                .ToArray();
            var checkTrend = trendDays
                .Select(day => checks.Where(x => x.Date == day).Sum(x => x.CheckAmount))
                .ToArray();
            var purchaseTrend = trendDays
                .Select(day => purchases.Where(x => x.InvoiceDate == day).Sum(x => x.Total))
                .ToArray();
            var profitTrend = trendDays
                .Select(day =>
                    shifts.Where(x => x.Date == day).Sum(x => x.NetSales)
                    - purchases.Where(x => x.InvoiceDate == day).Sum(x => x.Total)
                    - cash.Where(x => x.Date == day && x.IsPayout).Sum(x => x.PayoutAmount)
                    - checks.Where(x => x.Date == day).Sum(x => x.CheckAmount))
                .ToArray();
            var productCostTrend = productCosts
                .OrderByDescending(x => x.UpdatedUtc)
                .Take(7)
                .OrderBy(x => x.UpdatedUtc)
                .Select(x => x.LastUnitCost)
                .ToArray();
            var alertTrend = trendDays
                .Select(day => (decimal)priceAlerts.Count(x => DateOnly.FromDateTime(x.CreatedUtc.ToLocalTime()) == day))
                .ToArray();
            var highPriorityAlerts = priceAlerts.Count(x =>
                !x.IsRead
                && Math.Abs(x.OldUnitCost == 0
                    ? 0
                    : ((x.NewUnitCost - x.OldUnitCost) / x.OldUnitCost) * 100m) >= 10m);

            return new OperationsHubSnapshot(
                todayShifts.Sum(x => x.CashDropReceived),
                todayShifts.Count,
                todayShifts.Sum(x => x.NetSales),
                cash.Sum(x => x.CashAdded - x.PayoutAmount),
                monthCash.Sum(x => x.CashAdded),
                monthCash.Where(x => x.IsPayout).Sum(x => x.PayoutAmount),
                checks.Count(x => !x.Cleared),
                checks.Where(x => !x.Cleared).Sum(x => x.CheckAmount),
                monthChecks.Sum(x => x.CheckAmount),
                monthPurchases.Count,
                purchaseTotal,
                monthPurchases.Count == 0 ? "No entries" : monthPurchases.Max(x => x.InvoiceDate).ToString("M/d/yyyy"),
                netSales,
                purchaseTotal + operatingExpenses,
                netSales - purchaseTotal - operatingExpenses,
                productCosts.Count,
                productCosts.Count(x => x.UpdatedUtc >= DateTime.UtcNow.AddDays(-30)),
                productCosts.Count == 0 ? 0 : productCosts.Average(x => x.LastUnitCost),
                priceAlerts.Count,
                priceAlerts.Count(x => !x.IsRead),
                highPriorityAlerts,
                salesTrend,
                cashTrend,
                checkTrend,
                purchaseTrend,
                profitTrend,
                productCostTrend,
                alertTrend);
        }
        catch
        {
            return OperationsHubSnapshot.Empty;
        }
    }

    private OperationsCommandCard BuildOperationsCommandCard(
        string glyph,
        string title,
        string subtitle,
        string badge,
        string metricCaption,
        string metric,
        Color metricColor,
        string leftFactCaption,
        string leftFact,
        string rightFactCaption,
        string rightFact,
        string primaryText,
        Func<Task> primaryAction,
        string? secondaryText = null,
        Func<Task>? secondaryAction = null,
        IReadOnlyList<decimal>? trend = null,
        string trendCaption = "RECENT ACTIVITY")
    {
        var card = WinTheme.BorderedPanel(0);
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(DpiScale(6));
        card.MinimumSize = new Size(DpiScale(320), DpiScale(306));

        var stripe = new Panel { Dock = DockStyle.Left, Width = DpiScale(5), BackColor = WinTheme.Copper };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Color.White,
            Padding = new Padding(DpiScale(14), DpiScale(10), DpiScale(12), DpiScale(9))
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiScale(62)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiScale(68)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiScale(52)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiScale(50)));

        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Color.White };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, DpiScale(48)));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, DpiScale(104)));
        var iconHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(255, 245, 234), Margin = new Padding(0, 0, 8, 8) };
        iconHost.Controls.Add(new Label
        {
            Text = glyph,
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Copper,
            Font = WinTheme.IconFont(20),
            TextAlign = ContentAlignment.MiddleCenter
        });
        heading.Controls.Add(iconHost, 0, 0);
        var headingText = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.White,
            Margin = new Padding(0, 0, DpiScale(4), 0)
        };
        headingText.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiScale(26)));
        headingText.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        headingText.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Text,
            Font = WinTheme.BoldFont(11),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        }, 0, 0);
        headingText.Controls.Add(new Label
        {
            Text = subtitle,
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Muted,
            Font = WinTheme.BodyFont(8.5f),
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true
        }, 0, 1);
        heading.Controls.Add(headingText, 1, 0);
        heading.Controls.Add(new Label
        {
            Text = badge,
            Dock = DockStyle.Fill,
            Margin = new Padding(DpiScale(4), DpiScale(7), 0, DpiScale(15)),
            BackColor = Color.FromArgb(232, 247, 239),
            ForeColor = WinTheme.Green,
            Font = WinTheme.BoldFont(8),
            TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = true
        }, 2, 0);
        layout.Controls.Add(heading, 0, 0);

        var metricPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.White };
        metricPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiScale(24)));
        metricPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        metricPanel.Controls.Add(new Label
        {
            Text = metricCaption,
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Muted,
            Font = WinTheme.BoldFont(8.5f),
            TextAlign = ContentAlignment.BottomLeft
        }, 0, 0);
        var metricValue = new Label
        {
            Text = metric,
            Dock = DockStyle.Fill,
            ForeColor = metricColor,
            Font = WinTheme.HeaderFont(20),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        metricPanel.Controls.Add(metricValue, 0, 1);
        layout.Controls.Add(metricPanel, 0, 1);

        var trendControl = new OperationsSparkline(trend ?? Array.Empty<decimal>(), metricColor, trendCaption)
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 1, 0, 3)
        };
        layout.Controls.Add(trendControl, 0, 2);

        var facts = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = WinTheme.Panel2,
            Padding = new Padding(10, 5, 10, 5),
            Margin = new Padding(0, 3, 0, 7)
        };
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var left = BuildOperationsFact(leftFactCaption, leftFact);
        var right = BuildOperationsFact(rightFactCaption, rightFact);
        facts.Controls.Add(left.Panel, 0, 0);
        facts.Controls.Add(right.Panel, 1, 0);
        layout.Controls.Add(facts, 0, 3);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, BackColor = Color.White, Margin = Padding.Empty };
        actions.ColumnCount = secondaryAction is null ? 1 : 2;
        if (secondaryAction is null)
        {
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        }
        else
        {
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        }

        var primary = WinTheme.Button(primaryText, true);
        primary.Dock = DockStyle.Fill;
        primary.Margin = new Padding(0, 3, secondaryAction is null ? 0 : 4, 0);
        ConfigureOperationsAction(primary, title, primaryAction);
        actions.Controls.Add(primary, 0, 0);
        if (secondaryAction is not null)
        {
            var secondary = WinTheme.Button(secondaryText ?? "MORE");
            secondary.Dock = DockStyle.Fill;
            secondary.Margin = new Padding(4, 3, 0, 0);
            ConfigureOperationsAction(secondary, title, secondaryAction);
            actions.Controls.Add(secondary, 1, 0);
        }
        layout.Controls.Add(actions, 0, 4);

        card.Controls.Add(layout);
        card.Controls.Add(stripe);
        return new OperationsCommandCard(card, metricValue, left.Value, right.Value, trendControl);
    }

    private (Control Panel, Label Value) BuildOperationsFact(string caption, string value)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = WinTheme.Panel2 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiScale(22)));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label
        {
            Text = caption,
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Muted,
            Font = WinTheme.BoldFont(7.5f),
            TextAlign = ContentAlignment.BottomLeft,
            AutoEllipsis = true
        }, 0, 0);
        var valueLabel = new Label
        {
            Text = value,
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Text,
            Font = WinTheme.BoldFont(10),
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true
        };
        panel.Controls.Add(valueLabel, 0, 1);
        return (panel, valueLabel);
    }

    private int DpiScale(int logicalPixels)
        => Math.Max(logicalPixels, (int)Math.Ceiling(logicalPixels * DeviceDpi / 96f));

    private void ConfigureOperationsAction(Button button, string section, Func<Task> action)
    {
        button.Click += async (_, _) =>
        {
            button.Enabled = false;
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, AppBootstrap.RedactSensitiveText(ex.Message), section, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (!button.IsDisposed)
                    button.Enabled = true;
            }
        };
    }

    private sealed record OperationsCommandCard(
        Control Card,
        Label MetricValue,
        Label LeftFactValue,
        Label RightFactValue,
        OperationsSparkline Trend);

    private sealed record OperationsHubSnapshot(
        decimal TodayCashDrop,
        int TodayShiftCount,
        decimal TodayNetSales,
        decimal CashBalance,
        decimal MonthCashAdded,
        decimal MonthCashPayouts,
        int UnclearedCheckCount,
        decimal UnclearedCheckAmount,
        decimal MonthCheckPayouts,
        int MonthPurchaseCount,
        decimal MonthPurchases,
        string LatestPurchaseDate,
        decimal MonthNetSales,
        decimal MonthOutflow,
        decimal MonthNetProfit,
        int ProductCount,
        int RecentProductChanges,
        decimal AverageUnitCost,
        int TotalPriceAlerts,
        int UnreadPriceAlerts,
        int HighPriorityAlerts,
        IReadOnlyList<decimal> SalesTrend,
        IReadOnlyList<decimal> CashTrend,
        IReadOnlyList<decimal> CheckTrend,
        IReadOnlyList<decimal> PurchaseTrend,
        IReadOnlyList<decimal> ProfitTrend,
        IReadOnlyList<decimal> ProductCostTrend,
        IReadOnlyList<decimal> PriceAlertTrend)
    {
        public static OperationsHubSnapshot Empty { get; } = new(
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "No entries", 0, 0, 0,
            0, 0, 0, 0, 0, 0,
            Array.Empty<decimal>(), Array.Empty<decimal>(), Array.Empty<decimal>(),
            Array.Empty<decimal>(), Array.Empty<decimal>(), Array.Empty<decimal>(),
            Array.Empty<decimal>());
    }

    private static IReadOnlyList<string[]> SafeHubRows(Func<IReadOnlyList<string[]>> load)
    {
        try { return load(); }
        catch { return Array.Empty<string[]>(); }
    }

    private void AddHubCard(TableLayoutPanel root, int index, string glyph, string title, string[] headers, IReadOnlyList<string[]> rows, IReadOnlyList<(string Text, Func<Task> Action, bool Filled)> buttons)
    {
        var card = WinTheme.BorderedPanel(12);
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(6);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = WinTheme.Panel };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        card.Controls.Add(layout);

        var heading = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = WinTheme.Panel };
        heading.Controls.Add(new Label { Text = glyph, Width = 34, Height = 34, ForeColor = WinTheme.Copper, Font = WinTheme.IconFont(22), TextAlign = ContentAlignment.MiddleCenter });
        heading.Controls.Add(new Label { Text = title, AutoSize = false, Width = 320, Height = 34, ForeColor = Color.FromArgb(241, 193, 140), Font = WinTheme.HeaderFont(13), TextAlign = ContentAlignment.MiddleLeft });
        layout.Controls.Add(heading, 0, 0);

        var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, BackColor = WinTheme.Panel };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 23));
        toolbar.Controls.Add(HubFilterBox("\uE721  Search..."), 0, 0);
        toolbar.Controls.Add(HubFilterBox("This Month"), 1, 0);
        toolbar.Controls.Add(HubFilterBox("All"), 2, 0);
        layout.Controls.Add(toolbar, 0, 1);

        layout.Controls.Add(BuildMiniTable(headers, rows), 0, 2);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = WinTheme.Panel, Padding = new Padding(0, 6, 0, 0) };
        foreach (var action in buttons.Reverse())
        {
            var button = WinTheme.Button(action.Text, action.Filled);
            button.Width = Math.Max(150, action.Text.Length * 10);
            button.Height = 34;
            button.Click += async (_, _) =>
            {
                button.Enabled = false;
                try { await action.Action(); }
                catch (Exception ex) { MessageBox.Show(this, AppBootstrap.RedactSensitiveText(ex.Message), title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
                finally { button.Enabled = true; }
            };
            footer.Controls.Add(button);
        }
        layout.Controls.Add(footer, 0, 3);
        root.Controls.Add(card, index % 3, index / 3);
    }

    private void AddOperationHubCard(TableLayoutPanel root, int index, string glyph, string title, string badgeText, Color badgeColor, string searchText, string filterText, string[] headers, Func<IReadOnlyList<string[]>> loadRows, IReadOnlyList<(string Text, Func<Task> Action, bool Filled)> buttons)
    {
        var card = WinTheme.BorderedPanel(12);
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(7);
        card.Padding = new Padding(8);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = WinTheme.Panel };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        card.Controls.Add(layout);

        var header = new Panel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel };
        header.Paint += (_, e) =>
        {
            e.Graphics.Clear(WinTheme.Panel);
            using var iconFont = WinTheme.IconFont(20);
            using var titleFont = WinTheme.HeaderFont(11.5f);
            using var badgeFont = WinTheme.BoldFont(8);
            var badgeRect = new Rectangle(Math.Max(94, header.Width - 108), 15, 104, 26);
            var iconRect = new Rectangle(0, 6, 42, 42);
            var titleRect = new Rectangle(48, 4, Math.Max(20, badgeRect.Left - 56), 46);

            TextRenderer.DrawText(e.Graphics, glyph, iconFont, iconRect, WinTheme.Copper, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, title, titleFont, titleRect, Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            using var badgeBack = new SolidBrush(Color.FromArgb(12, 47, 54));
            using var badgePen = new Pen(Color.FromArgb(35, 88, 76));
            e.Graphics.FillRectangle(badgeBack, badgeRect);
            e.Graphics.DrawRectangle(badgePen, badgeRect);
            TextRenderer.DrawText(e.Graphics, badgeText, badgeFont, badgeRect, badgeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        header.Resize += (_, _) => header.Invalidate();
        layout.Controls.Add(header, 0, 0);

        var filters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = WinTheme.Panel, Padding = new Padding(0, 3, 0, 3) };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        var searchBox = BuildOperationHubSearchBox(searchText);
        var filterBox = BuildOperationHubFilterDropDown(title, filterText, badgeText);
        filters.Controls.Add(searchBox, 0, 0);
        filters.Controls.Add(filterBox, 1, 0);
        layout.Controls.Add(filters, 0, 1);

        var miniTable = BuildMiniTable(headers, new[] { new[] { "Loading..." } });
        miniTable.Margin = new Padding(0, 4, 0, 4);
        layout.Controls.Add(miniTable, 0, 2);

        if (miniTable is DataGridView miniGrid)
        {
            var loadedRows = new List<string[]>();
            void RefreshPreview()
            {
                var query = searchBox.Text == searchBox.Tag?.ToString() ? "" : searchBox.Text;
                var filter = filterBox.SelectedItem?.ToString() ?? "";
                ApplyOperationHubPreviewFilter(miniGrid, headers, loadedRows, query, filter);
            }

            searchBox.TextChanged += (_, _) =>
            {
                if (loadedRows.Count > 0)
                    RefreshPreview();
            };
            filterBox.SelectedIndexChanged += (_, _) =>
            {
                if (loadedRows.Count > 0)
                    RefreshPreview();
            };

            void StartPreviewLoad()
            {
                _ = Task.Run(loadRows).ContinueWith(task =>
                {
                    if (task.Status != TaskStatus.RanToCompletion || card.IsDisposed || miniGrid.IsDisposed)
                        return;
                    try
                    {
                        miniGrid.BeginInvoke(new Action(() =>
                        {
                            loadedRows = task.Result.ToList();
                            RefreshPreview();
                        }));
                    }
                    catch
                    {
                        // The card may have been replaced while the background preview was loading.
                    }
                }, TaskScheduler.Default);
            }

            if (card.IsHandleCreated)
                StartPreviewLoad();
            else
                card.HandleCreated += (_, _) => StartPreviewLoad();
        }

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = Math.Max(1, buttons.Count), RowCount = 1, BackColor = WinTheme.Panel, Padding = new Padding(0, 3, 0, 0) };
        foreach (var _ in buttons)
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / Math.Max(1, buttons.Count)));
        for (var i = 0; i < buttons.Count; i++)
        {
            var action = buttons[i];
            var button = WinTheme.Button(action.Text, action.Filled);
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(i == 0 ? 0 : 6, 0, 0, 0);
            button.Click += async (_, _) =>
            {
                button.Enabled = false;
                try { await action.Action(); }
                catch (Exception ex) { MessageBox.Show(this, AppBootstrap.RedactSensitiveText(ex.Message), title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
                finally { button.Enabled = true; }
            };
            actions.Controls.Add(button, i, 0);
        }
        layout.Controls.Add(actions, 0, 3);

        root.Controls.Add(card, index % 3, index / 3);
    }

    private static TextBox BuildOperationHubSearchBox(string placeholder)
    {
        var box = new TextBox
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 6, 2),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(9, 36, 54),
            ForeColor = WinTheme.Muted,
            Font = WinTheme.BodyFont(9),
            Text = placeholder,
            Tag = placeholder
        };

        box.GotFocus += (_, _) =>
        {
            if (box.Text == placeholder)
            {
                box.Text = "";
                box.ForeColor = Color.White;
            }
        };
        box.LostFocus += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(box.Text))
            {
                box.Text = placeholder;
                box.ForeColor = WinTheme.Muted;
            }
        };
        return box;
    }

    private static ComboBox BuildOperationHubFilterDropDown(string title, string filterText, string badgeText)
    {
        var combo = new ComboBox
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(4, 2, 0, 2),
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(9, 36, 54),
            ForeColor = Color.White,
            Font = WinTheme.BodyFont(9)
        };

        foreach (var option in GetOperationHubFilterOptions(title, filterText, badgeText))
            combo.Items.Add(option);
        combo.SelectedIndex = 0;
        return combo;
    }

    private static IEnumerable<string> GetOperationHubFilterOptions(string title, string filterText, string badgeText)
    {
        var options = title switch
        {
            "Vendors & Purposes" => new[] { "All", "Vendor", "Purpose", "Active" },
            "Purchases" => new[] { "All", "Entered", "Completed", "Paid" },
            "Bank Statement" => new[] { "All Accounts", "All Categories", "Matched", "Unmatched" },
            "Product Costs" => new[] { "All", "Live", "Updated" },
            "Price Alerts" => new[] { "All", "Alerts", "High", "Medium", "Low", "Read" },
            "Profit & Loss" => new[] { "All", "Positive", "Negative", "Expense" },
            _ => new[] { "All" }
        };
        return options
            .Concat(new[] { filterText, badgeText })
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void ApplyOperationHubPreviewFilter(DataGridView grid, string[] headers, IReadOnlyList<string[]> rows, string search, string filter)
    {
        IEnumerable<string[]> filtered = rows;
        if (!string.IsNullOrWhiteSpace(search))
        {
            filtered = filtered.Where(row => row.Any(cell => (cell ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        if (!IsNeutralOperationHubFilter(filter))
        {
            filtered = filtered.Where(row => row.Any(cell => string.Equals(cell, filter, StringComparison.OrdinalIgnoreCase) || (cell ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase)));
        }

        RefreshMiniTable(grid, headers, filtered.Take(5).ToList());
    }

    private static bool IsNeutralOperationHubFilter(string filter)
    {
        return string.IsNullOrWhiteSpace(filter)
            || filter.Equals("All", StringComparison.OrdinalIgnoreCase)
            || filter.Equals("All Accounts", StringComparison.OrdinalIgnoreCase)
            || filter.Equals("All Categories", StringComparison.OrdinalIgnoreCase)
            || filter.Equals("This Month", StringComparison.OrdinalIgnoreCase)
            || filter.Equals("Live", StringComparison.OrdinalIgnoreCase)
            || filter.Equals("Alerts", StringComparison.OrdinalIgnoreCase);
    }

    private static Control BuildOperationHubBadge(string text, Color color)
    {
        var shell = new Panel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel, Padding = new Padding(4, 10, 0, 10) };
        var badge = new Label
        {
            Dock = DockStyle.Fill,
            Text = text,
            BackColor = Color.FromArgb(12, 47, 54),
            ForeColor = color,
            Font = WinTheme.BoldFont(8),
            BorderStyle = BorderStyle.FixedSingle,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = true,
            UseCompatibleTextRendering = true
        };
        shell.Controls.Add(badge);
        return shell;
    }

    private Control BuildOperationHubReportsStrip()
    {
        var card = WinTheme.BorderedPanel(12);
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(7, 4, 7, 7);
        card.Padding = new Padding(12, 8, 12, 10);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = WinTheme.Panel };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(layout);

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = WinTheme.Panel };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        header.Controls.Add(new Label { Text = "\uE749", Dock = DockStyle.Fill, ForeColor = WinTheme.Copper, Font = WinTheme.IconFont(20), TextAlign = ContentAlignment.MiddleCenter }, 0, 0);
        header.Controls.Add(new Label { Text = "Reports", Dock = DockStyle.Fill, ForeColor = Color.White, Font = WinTheme.HeaderFont(12), TextAlign = ContentAlignment.MiddleLeft }, 1, 0);
        var viewAll = new Label { Text = "View All Reports >", Dock = DockStyle.Fill, ForeColor = Color.White, Font = WinTheme.BodyFont(9), TextAlign = ContentAlignment.MiddleRight, Cursor = Cursors.Hand };
        viewAll.Click += (_, _) => ShowModule("Reports");
        header.Controls.Add(viewAll, 2, 0);
        layout.Controls.Add(header, 0, 0);

        var reports = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 8, RowCount = 1, BackColor = WinTheme.Panel };
        for (var i = 0; i < 8; i++)
            reports.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12.5f));
        var entries = new (string Glyph, string Title, string Subtitle, Func<Task> Action)[]
        {
            ("\uE7BF", "Purchase Register", "Detailed list of all purchases", () => { ShowModule("Purchases"); return Task.CompletedTask; }),
            ("\uE716", "Vendor Ledger", "Ledger summary by vendor", () => { ShowModule("Vendors & Purposes"); return Task.CompletedTask; }),
            ("\uE8B7", "Stock Summary", "Current stock overview", () => { ShowModule("Product Costs"); return Task.CompletedTask; }),
            ("\uE825", "Bank Reconciliation", "Reconciliation reports", () => { ShowModule("Bank Statement"); return Task.CompletedTask; }),
            ("\uE9D9", "P&L Statement", "Profit and loss summary", async () => await QuickViewReportAsync("Profit & Loss")),
            ("\uE7C3", "Balance Sheet", "Financial position overview", () => { ShowModule("Reports"); return Task.CompletedTask; }),
            ("\uE9D2", "Tax Reports", "Tax summaries and filings", () => { ShowModule("Reports"); return Task.CompletedTask; }),
            ("\uE713", "Custom Reports", "Build your own reports", () => { ShowModule("Reports"); return Task.CompletedTask; })
        };
        for (var i = 0; i < entries.Length; i++)
            reports.Controls.Add(BuildOperationHubReportTile(entries[i].Glyph, entries[i].Title, entries[i].Subtitle, entries[i].Action), i, 0);
        layout.Controls.Add(reports, 0, 1);
        return card;
    }

    private Control BuildOperationHubReportTile(string glyph, string title, string subtitle, Func<Task> action)
    {
        var tile = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = WinTheme.Panel, Margin = new Padding(4, 0, 4, 0), Padding = new Padding(6, 2, 6, 2) };
        tile.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        tile.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        tile.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        tile.Controls.Add(new Label { Text = glyph, Dock = DockStyle.Fill, ForeColor = WinTheme.Copper, Font = WinTheme.IconFont(18), TextAlign = ContentAlignment.BottomLeft }, 0, 0);
        tile.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(241, 193, 140), Font = WinTheme.BoldFont(9), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true }, 0, 1);
        tile.Controls.Add(new Label { Text = subtitle, Dock = DockStyle.Fill, ForeColor = WinTheme.Muted, Font = WinTheme.BodyFont(8), TextAlign = ContentAlignment.TopLeft, AutoEllipsis = true }, 0, 2);
        WireOperationHubReportTile(tile, title, action);
        return tile;
    }

    private void WireOperationHubReportTile(Control control, string title, Func<Task> action)
    {
        control.Cursor = Cursors.Hand;
        control.Click += async (_, _) =>
        {
            control.Enabled = false;
            try { await action(); }
            catch (Exception ex) { MessageBox.Show(this, AppBootstrap.RedactSensitiveText(ex.Message), title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { control.Enabled = true; }
        };

        foreach (Control child in control.Controls)
            WireOperationHubReportTile(child, title, action);
    }

    private Control BuildReportsHubCard()
    {
        var card = WinTheme.BorderedPanel(12);
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(6);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = WinTheme.Panel };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        card.Controls.Add(layout);

        var heading = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = WinTheme.Panel };
        heading.Controls.Add(new Label { Text = "\uE749", Width = 34, Height = 34, ForeColor = WinTheme.Copper, Font = WinTheme.IconFont(22), TextAlign = ContentAlignment.MiddleCenter });
        heading.Controls.Add(new Label { Text = "Reports", AutoSize = false, Width = 320, Height = 34, ForeColor = Color.FromArgb(241, 193, 140), Font = WinTheme.HeaderFont(13), TextAlign = ContentAlignment.MiddleLeft });
        layout.Controls.Add(heading, 0, 0);

        var filters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, BackColor = WinTheme.Panel };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 19));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 13));
        filters.Controls.Add(HubFilterBox("Sales Summary by Date"), 0, 0);
        filters.Controls.Add(HubFilterBox(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).ToString("M/d/yyyy")), 1, 0);
        filters.Controls.Add(HubFilterBox(DateTime.Today.ToString("M/d/yyyy")), 2, 0);
        filters.Controls.Add(HubFilterBox("Group By: Day"), 3, 0);
        var generate = WinTheme.Button("Generate Report", true);
        generate.Height = 34;
        generate.Click += async (_, _) =>
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Save Sales Summary Report",
                Filter = "PDF files (*.pdf)|*.pdf",
                FileName = $"HisabKitab_SalesSummary_{DateTime.Now:yyyyMMdd_HHmm}.pdf"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var from = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
            await _reportService.GenerateSalesSummaryByDatePdfAsync(from, DateOnly.FromDateTime(DateTime.Today), dialog.FileName);
        };
        filters.Controls.Add(generate, 5, 0);
        layout.Controls.Add(filters, 0, 1);

        layout.Controls.Add(BuildMiniTable(
            new[] { "Report Name", "Description", "Last Generated", "Generated By", "Format", "Actions" },
            new[]
            {
                new[] { "Sales Summary by Date", "Daily sales summary with totals", "-", _session.DisplayName, "PDF", "Preview" },
                new[] { "Shift Log", "Cash drop and register payout history", "-", _session.DisplayName, "PDF", "Preview" },
                new[] { "Profit & Loss Statement", "Current month operating statement", "-", _session.DisplayName, "PDF", "Preview" }
            }), 0, 2);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = WinTheme.Panel, Padding = new Padding(0, 6, 0, 0) };
        var open = WinTheme.Button("View All Reports");
        open.Width = 160;
        open.Height = 34;
        open.Click += (_, _) => ShowModule("Reports");
        var export = WinTheme.Button("Export All Reports", true);
        export.Width = 170;
        export.Height = 34;
        export.Click += async (_, _) =>
        {
            await GenerateProfitLossPdfAsync();
        };
        footer.Controls.Add(export);
        footer.Controls.Add(open);
        layout.Controls.Add(footer, 0, 3);
        return card;
    }

    private static Control HubFilterBox(string text)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel, Padding = new Padding(3, 4, 3, 4) };
        var label = new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(9, 36, 54),
            ForeColor = Color.FromArgb(190, 205, 218),
            Font = WinTheme.BodyFont(8),
            BorderStyle = BorderStyle.FixedSingle,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(7, 0, 7, 0),
            AutoEllipsis = true
        };
        panel.Controls.Add(label);
        return panel;
    }

    private static Control BuildMiniTable(string[] headers, IReadOnlyList<string[]> rows)
    {
        var grid = WinTheme.Grid();
        grid.ColumnHeadersHeight = 24;
        grid.RowTemplate.Height = 23;
        grid.DefaultCellStyle.Font = WinTheme.BodyFont(8);
        grid.ColumnHeadersDefaultCellStyle.Font = WinTheme.BoldFont(8);
        grid.ScrollBars = ScrollBars.None;
        grid.AutoGenerateColumns = false;
        grid.AllowUserToResizeColumns = false;
        grid.AllowUserToResizeRows = false;
        grid.Margin = Padding.Empty;
        grid.ClearSelection();
        foreach (var header in headers)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                Name = header.Replace(" ", "", StringComparison.Ordinal),
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        }
        var safeRows = rows.Count > 0 ? rows.Take(5).ToList() : new List<string[]> { new[] { "No records yet" } };
        foreach (var row in safeRows)
        {
            var index = grid.Rows.Add(headers.Select((_, i) => i < row.Length ? row[i] : "").ToArray());
            grid.Rows[index].Height = 23;
            foreach (DataGridViewCell cell in grid.Rows[index].Cells)
            {
                var text = cell.Value?.ToString() ?? "";
                if (text.Contains("Unread", StringComparison.OrdinalIgnoreCase) || text.StartsWith("(", StringComparison.Ordinal) || text.StartsWith("-", StringComparison.Ordinal))
                    cell.Style.ForeColor = WinTheme.Red;
                if (text.Contains("Active", StringComparison.OrdinalIgnoreCase) || text.Contains("Read", StringComparison.OrdinalIgnoreCase) || text.Contains("Entered", StringComparison.OrdinalIgnoreCase))
                    cell.Style.ForeColor = WinTheme.Green;
            }
        }
        grid.ClearSelection();
        return grid;
    }

    private static void RefreshMiniTable(DataGridView grid, string[] headers, IReadOnlyList<string[]> rows)
    {
        if (grid.IsDisposed)
            return;

        grid.Rows.Clear();
        var safeRows = rows.Count > 0 ? rows.Take(5).ToList() : new List<string[]> { new[] { "No records yet" } };
        foreach (var row in safeRows)
        {
            var index = grid.Rows.Add(headers.Select((_, i) => i < row.Length ? row[i] : "").ToArray());
            grid.Rows[index].Height = 23;
            foreach (DataGridViewCell cell in grid.Rows[index].Cells)
            {
                var text = cell.Value?.ToString() ?? "";
                if (text.Contains("Unread", StringComparison.OrdinalIgnoreCase) || text.StartsWith("(", StringComparison.Ordinal) || text.StartsWith("-", StringComparison.Ordinal))
                    cell.Style.ForeColor = WinTheme.Red;
                if (text.Contains("Active", StringComparison.OrdinalIgnoreCase) || text.Contains("Read", StringComparison.OrdinalIgnoreCase) || text.Contains("Entered", StringComparison.OrdinalIgnoreCase) || text.Contains("Completed", StringComparison.OrdinalIgnoreCase))
                    cell.Style.ForeColor = WinTheme.Green;
            }
        }
        grid.ClearSelection();
    }

    private static Control MiniCell(string text, bool header)
    {
        var color = header ? Color.Black : Color.White;
        if (!header && (text.Contains("Unread", StringComparison.OrdinalIgnoreCase) || text.StartsWith("(", StringComparison.Ordinal) || text.StartsWith("-", StringComparison.Ordinal)))
            color = WinTheme.Red;
        if (!header && (text.Contains("Active", StringComparison.OrdinalIgnoreCase) || text.Contains("Read", StringComparison.OrdinalIgnoreCase) || text.Contains("Entered", StringComparison.OrdinalIgnoreCase)))
            color = WinTheme.Green;
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            BackColor = header ? WinTheme.Copper : WinTheme.Panel,
            ForeColor = color,
            Font = header ? WinTheme.BoldFont(9) : WinTheme.BodyFont(9),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 6, 0),
            AutoEllipsis = true
        };
    }

    private IReadOnlyList<string[]> GetVendorPurposePreviewRows(AppDbContext db)
    {
        var rows = new List<string[]>();
        rows.AddRange(db.Vendors.AsNoTracking()
            .Where(x => x.StoreId == _currentStoreId)
            .OrderBy(x => x.Name)
            .Take(3)
            .Select(x => new[] { x.Name, "Vendor", "Active" })
            .ToList());
        rows.AddRange(db.Purposes.AsNoTracking()
            .Where(x => x.StoreId == _currentStoreId)
            .OrderBy(x => x.Name)
            .Take(Math.Max(0, 5 - rows.Count))
            .Select(x => new[] { x.Name, "Purpose", "Active" })
            .ToList());
        return rows;
    }

    private IReadOnlyList<string[]> GetPurchasePreviewRows(AppDbContext db)
    {
        var from = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        var to = from.AddMonths(1).AddDays(-1);
        return db.PurchaseInvoices.AsNoTracking()
            .Where(x => x.StoreId == _currentStoreId && x.InvoiceDate >= from && x.InvoiceDate <= to)
            .OrderByDescending(x => x.InvoiceDate)
            .ThenByDescending(x => x.Id)
            .Take(5)
            .ToList()
            .Select(x => new[] { x.InvoiceDate.ToString("M/d/yyyy", CultureInfo.InvariantCulture), x.VendorName, x.Total.ToString("C2"), "Entered" })
            .ToList();
    }

    private IReadOnlyList<string[]> GetBankPreviewRows()
    {
        try
        {
            return LoadBankStatementRowsAsync(DateTime.Today.Month, DateTime.Today.Year)
                .GetAwaiter()
                .GetResult()
                .Take(5)
                .Select(x => new[] { x.Date.ToString("M/d/yyyy", CultureInfo.InvariantCulture), x.Description, x.Debit == 0 ? "-" : x.Debit.ToString("C2"), x.Credit == 0 ? "-" : x.Credit.ToString("C2"), x.Category })
                .ToList();
        }
        catch
        {
            return Array.Empty<string[]>();
        }
    }

    private IReadOnlyList<string[]> GetProductCostPreviewRows(AppDbContext db)
    {
        return db.ProductCosts.AsNoTracking()
            .Where(x => x.StoreId == _currentStoreId)
            .OrderByDescending(x => x.UpdatedUtc)
            .ThenBy(x => x.ProductName)
            .Take(5)
            .ToList()
            .Select(x => new[] { x.ProductName, x.Sku, x.LastUnitCost.ToString("C4"), x.LastVendorName, x.LastInvoiceDate.ToString("M/d/yyyy", CultureInfo.InvariantCulture) })
            .ToList();
    }

    private IReadOnlyList<string[]> GetPriceAlertPreviewRows(AppDbContext db)
    {
        return db.PriceAlerts.AsNoTracking()
            .Where(x => x.StoreId == _currentStoreId)
            .OrderByDescending(x => x.CreatedUtc)
            .Take(5)
            .ToList()
            .Select(x => new[] { x.ProductName, x.VendorName, x.OldUnitCost.ToString("C4"), x.NewUnitCost.ToString("C4"), x.IsRead ? "Read" : "Unread" })
            .ToList();
    }

    private IReadOnlyList<string[]> GetProfitLossPreviewRows(AppDbContext db)
    {
        var month = DateTime.Today.Month;
        var year = DateTime.Today.Year;
        var shifts = db.ShiftLogs.AsNoTracking().Where(x => x.StoreId == _currentStoreId).ToList()
            .Where(x => x.Date.Month == month && x.Date.Year == year).ToList();
        var purchases = db.PurchaseInvoices.AsNoTracking().Where(x => x.StoreId == _currentStoreId).ToList()
            .Where(x => x.InvoiceDate.Month == month && x.InvoiceDate.Year == year).ToList();
        var cash = db.CashOnHand.AsNoTracking().Where(x => x.StoreId == _currentStoreId).ToList()
            .Where(x => x.Date.Month == month && x.Date.Year == year).ToList();
        var checks = db.CheckPayouts.AsNoTracking().Where(x => x.StoreId == _currentStoreId).ToList()
            .Where(x => x.Date.Month == month && x.Date.Year == year).ToList();
        var payroll = db.PayrollEntries.AsNoTracking()
            .Where(x => x.PayrollRun!.StoreId == _currentStoreId && x.PayrollRun.Status == PayrollRunStatus.Finalized && x.PayrollRun.PayDate.Month == month && x.PayrollRun.PayDate.Year == year)
            .Sum(x => (decimal?)x.GrossPay).GetValueOrDefault();
        var netSales = shifts.Sum(x => x.NetSales);
        var costOfGoods = purchases.Sum(x => x.Total);
        var expenses = cash.Where(x => x.IsPayout).Sum(x => x.PayoutAmount) + checks.Sum(x => x.CheckAmount) + payroll;
        var netProfit = netSales - costOfGoods - expenses;
        return new[]
        {
            new[] { "Net Sales", netSales.ToString("C2"), netSales >= 0 ? "Positive" : "Negative" },
            new[] { "Cost of Goods Sold", costOfGoods.ToString("C2"), "Expense" },
            new[] { "Expenses (including payroll)", expenses.ToString("C2"), "Expense" },
            new[] { "Net Profit", netProfit.ToString("C2"), netProfit >= 0 ? "Positive" : "Negative" }
        };
    }

    private Control BuildVendorsPurposes()
    {
        var root = SectionRoot(250, 72);

        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = WinTheme.Bg };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 80));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        root.Controls.Add(top, 0, 0);

        var formShell = WinTheme.BorderedPanel(10);
        formShell.Dock = DockStyle.Fill;
        formShell.Margin = new Padding(4, 6, 10, 6);
        top.Controls.Add(formShell, 0, 0);
        var form = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 3, BackColor = WinTheme.Panel };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        form.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        form.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        form.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        formShell.Controls.Add(form);

        var type = SectionCombo("Vendor", "Purpose");
        var name = SectionTextBox();
        var contact = SectionTextBox();
        var phone = SectionTextBox();
        var email = SectionTextBox();
        var category = SectionCombo("General", "Office Supplies", "Maintenance", "Food & Beverages", "Utilities", "Rent & Lease", "Travel", "Marketing", "Bank Charges");
        var active = SectionCombo("Active", "Inactive");
        var notes = SectionTextBox();
        AddMockField(form, "Type", type, 0, 0, 70);
        AddMockField(form, "Name *", name, 1, 0, 82);
        AddMockField(form, "Contact Person", contact, 2, 0, 122);
        AddMockField(form, "Phone", phone, 3, 0, 74);
        AddMockField(form, "Email", email, 4, 0, 70);
        AddMockField(form, "Category *", category, 0, 1, 92);
        AddMockField(form, "Status", active, 1, 1, 82);
        AddMockField(form, "Notes", notes, 0, 2, 70);
        form.SetColumnSpan(form.GetControlFromPosition(0, 2)!, 5);

        var stats = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = WinTheme.Bg, Padding = new Padding(0, 5, 0, 0) };
        top.Controls.Add(stats, 1, 0);
        var activeVendors = MetricCard(stats, "Active Vendors", "0", WinTheme.Copper, "View All", 250, 68);
        var activePurposes = MetricCard(stats, "Active Purposes", "0", WinTheme.Copper, "View All", 250, 68);
        var recentlyUpdated = MetricCard(stats, "Recently Updated", "0", WinTheme.Copper, "In last 7 days", 250, 68);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = WinTheme.Bg, Padding = new Padding(0, 8, 0, 8) };
        root.Controls.Add(actions, 0, 1);
        var grid = WinTheme.Grid();
        grid.ReadOnly = false;
        grid.EditMode = DataGridViewEditMode.EditOnEnter;
        root.Controls.Add(grid, 0, 2);
        root.Controls.Add(BuildGridFooter("Showing vendors and purposes for selected store"), 0, 3);

        void refresh()
        {
            using var db = CreateDb();
            var vendors = db.Vendors.AsNoTracking().Where(x => x.StoreId == _currentStoreId).OrderBy(x => x.Name).ToList();
            var purposes = db.Purposes.AsNoTracking().Where(x => x.StoreId == _currentStoreId).OrderBy(x => x.Name).ToList();
            var rows = vendors.Select(x => new { x.Id, Name = x.Name, Type = "Vendor", Contact = "", Phone = "", Email = "", Category = "", Status = "Active", LastUpdated = "" })
                .Concat(purposes.Select(x => new { x.Id, Name = x.Name, Type = "Purpose", Contact = "", Phone = "", Email = "", Category = "", Status = "Active", LastUpdated = "" }))
                .OrderBy(x => x.Type).ThenBy(x => x.Name)
                .ToList();
            grid.DataSource = rows;
            HideId(grid);
            activeVendors.Text = vendors.Count.ToString(CultureInfo.InvariantCulture);
            activePurposes.Text = purposes.Count.ToString(CultureInfo.InvariantCulture);
            recentlyUpdated.Text = rows.Count.ToString(CultureInfo.InvariantCulture);
        }

        void loadSelected()
        {
            if (grid.CurrentRow is null) return;
            type.SelectedItem = grid.CurrentRow.Cells["Type"].Value?.ToString() == "Purpose" ? "Purpose" : "Vendor";
            name.Text = grid.CurrentRow.Cells["Name"].Value?.ToString() ?? "";
            active.SelectedIndex = 0;
        }

        async Task addAsync()
        {
            if (string.IsNullOrWhiteSpace(name.Text)) return;
            using var db = CreateDb();
            if ((type.SelectedItem?.ToString() ?? "Vendor") == "Purpose")
            {
                if (await db.Purposes.AnyAsync(x => x.StoreId == _currentStoreId && x.Name == name.Text.Trim()))
                {
                    MessageBox.Show(this, "This purpose already exists for the selected store.", "Vendors & Purposes", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                db.Purposes.Add(new Purpose { StoreId = _currentStoreId, Name = name.Text.Trim() });
            }
            else
            {
                if (await db.Vendors.AnyAsync(x => x.StoreId == _currentStoreId && x.Name == name.Text.Trim()))
                {
                    MessageBox.Show(this, "This vendor already exists for the selected store.", "Vendors & Purposes", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                db.Vendors.Add(new Vendor { StoreId = _currentStoreId, Name = name.Text.Trim() });
            }
            await db.SaveChangesAsync();
            name.Clear();
            contact.Clear();
            phone.Clear();
            email.Clear();
            notes.Clear();
            refresh();
        }

        async Task updateAsync()
        {
            if (!_session.IsAdmin)
            {
                MessageBox.Show(this, "Only Owner/Admin accounts can update vendors and purposes.", "Access Restricted", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var id = SelectedId(grid);
            if (id is null || string.IsNullOrWhiteSpace(name.Text)) return;
            var selectedType = grid.CurrentRow?.Cells["Type"].Value?.ToString() ?? "";
            using var db = CreateDb();
            if (selectedType == "Purpose")
            {
                var row = await db.Purposes.FirstOrDefaultAsync(x => x.Id == id.Value && x.StoreId == _currentStoreId);
                if (row is not null) row.Name = name.Text.Trim();
            }
            else
            {
                var row = await db.Vendors.FirstOrDefaultAsync(x => x.Id == id.Value && x.StoreId == _currentStoreId);
                if (row is not null) row.Name = name.Text.Trim();
            }
            await db.SaveChangesAsync();
            refresh();
        }

        async Task deleteAsync()
        {
            if (!_session.IsAdmin)
            {
                MessageBox.Show(this, "Only Owner/Admin accounts can delete vendors and purposes.", "Access Restricted", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var id = SelectedId(grid);
            if (id is null) return;
            if (MessageBox.Show(this, "Delete selected vendor or purpose?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            var selectedType = grid.CurrentRow?.Cells["Type"].Value?.ToString() ?? "";
            using var db = CreateDb();
            if (selectedType == "Purpose")
            {
                var row = await db.Purposes.FirstOrDefaultAsync(x => x.Id == id.Value && x.StoreId == _currentStoreId);
                if (row is not null) db.Purposes.Remove(row);
            }
            else
            {
                var row = await db.Vendors.FirstOrDefaultAsync(x => x.Id == id.Value && x.StoreId == _currentStoreId);
                if (row is not null) db.Vendors.Remove(row);
            }
            await db.SaveChangesAsync();
            refresh();
        }

        grid.SelectionChanged += (_, _) => loadSelected();
        AddSectionButton(actions, "Home", (_, _) => ShowModule("Dashboard"), width: 210);
        AddSectionButton(actions, "Add", addAsync, true, 210);
        AddSectionButton(actions, "Add Correction", updateAsync, width: 230, enabled: _session.IsAdmin);
        AddSectionButton(actions, "Update Selected", updateAsync, width: 230, enabled: _session.IsAdmin);
        AddSectionButton(actions, "Delete Selected", deleteAsync, width: 230, enabled: _session.IsAdmin);
        AddSectionButton(actions, "Clear Form", (_, _) => { name.Clear(); contact.Clear(); phone.Clear(); email.Clear(); notes.Clear(); grid.ClearSelection(); }, width: 210);
        AddSectionButton(actions, "Refresh", (_, _) => refresh(), width: 160);
        refresh();
        return ModuleShell("\uE716", "Vendors & Purposes", "Maintain reusable vendors and payout purposes.", root);
    }

    private Control BuildPurchases()
    {
        var root = SectionRoot(260, 72);
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = WinTheme.Bg };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 82));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        top.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        top.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        root.Controls.Add(top, 0, 0);

        var headerShell = WinTheme.BorderedPanel(10);
        headerShell.Dock = DockStyle.Fill;
        headerShell.Margin = new Padding(4, 6, 8, 5);
        top.Controls.Add(headerShell, 0, 0);
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 2, BackColor = WinTheme.Panel };
        for (var i = 0; i < 5; i++) fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        headerShell.Controls.Add(fields);

        var date = WinTheme.DatePicker();
        var vendor = SectionTextBox();
        var number = SectionTextBox();
        var category = SectionCombo("General", "Office Supplies", "Maintenance", "Shipping", "Utilities", "Equipment", "Inventory");
        var payment = SectionCombo("Checking Account", "Cash", "Credit Card", "Other");
        var tax = SectionTextBox("$0.00", rightAlign: true);
        var file = SectionTextBox("No file selected", readOnly: true);
        var total = SectionTextBox("$0.00", rightAlign: true);
        AddMockField(fields, "Date *", date, 0, 0, 78);
        AddMockField(fields, "Vendor *", vendor, 1, 0, 86);
        AddMockField(fields, "Invoice # *", number, 2, 0, 94);
        AddMockField(fields, "Category *", category, 3, 0, 96);
        AddMockField(fields, "Payment Method *", payment, 4, 0, 142);
        AddMockField(fields, "Tax", tax, 0, 1, 78);
        AddMockField(fields, "PDF Attachment", file, 1, 1, 130);
        fields.SetColumnSpan(fields.GetControlFromPosition(1, 1)!, 3);
        AddMockField(fields, "Total Amount *", total, 4, 1, 130);

        var lineShell = WinTheme.BorderedPanel(10);
        lineShell.Dock = DockStyle.Fill;
        lineShell.Margin = new Padding(4, 5, 8, 6);
        top.Controls.Add(lineShell, 0, 1);
        var line = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 2, BackColor = WinTheme.Panel };
        line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        line.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        line.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        lineShell.Controls.Add(line);
        line.Controls.Add(new Label { Text = "Line Items", Dock = DockStyle.Fill, ForeColor = WinTheme.Copper, Font = WinTheme.BoldFont(10), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        line.SetColumnSpan(line.GetControlFromPosition(0, 0)!, 5);
        AddMockField(line, "Product / SKU *", SectionTextBox(), 0, 1, 118);
        AddMockField(line, "Quantity *", SectionTextBox(rightAlign: true), 1, 1, 92);
        AddMockField(line, "Unit Cost *", SectionTextBox(rightAlign: true), 2, 1, 92);
        AddMockField(line, "Line Total", SectionTextBox(readOnly: true, rightAlign: true), 3, 1, 88);
        var addLine = WinTheme.Button("Add Line", true);
        addLine.Dock = DockStyle.Fill;
        addLine.Margin = new Padding(8, 8, 8, 8);
        line.Controls.Add(addLine, 4, 1);

        var stats = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = WinTheme.Bg, Padding = new Padding(0, 6, 0, 0) };
        top.Controls.Add(stats, 1, 0);
        top.SetRowSpan(stats, 2);
        var monthPurchases = MetricCard(stats, "Month Purchases", "$0.00", WinTheme.Text, "This Month", 255, 74);
        var openInvoices = MetricCard(stats, "Open Invoices", "$0.00", WinTheme.Text, "0 Invoices", 255, 74);
        var importedPdfs = MetricCard(stats, "Imported PDFs", "0", WinTheme.Text, "This Month", 255, 74);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = WinTheme.Bg, Padding = new Padding(0, 8, 0, 8) };
        root.Controls.Add(actions, 0, 1);
        var grid = WinTheme.Grid();
        grid.AutoGenerateColumns = false;
        grid.ReadOnly = false;
        grid.EditMode = DataGridViewEditMode.EditOnEnter;
        grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Select",
            DataPropertyName = nameof(PurchaseInvoiceGridRow.Select),
            HeaderText = "Select",
            ReadOnly = false,
            ThreeState = false,
            TrueValue = true,
            FalseValue = false,
            FillWeight = 45
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Id", DataPropertyName = nameof(PurchaseInvoiceGridRow.Id), Visible = false, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Date", DataPropertyName = nameof(PurchaseInvoiceGridRow.Date), HeaderText = "Invoice Date", ReadOnly = true, FillWeight = 85 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Vendor", DataPropertyName = nameof(PurchaseInvoiceGridRow.Vendor), HeaderText = "Vendor", ReadOnly = true, FillWeight = 155 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Invoice", DataPropertyName = nameof(PurchaseInvoiceGridRow.Invoice), HeaderText = "Invoice #", ReadOnly = true, FillWeight = 100 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Total",
            DataPropertyName = nameof(PurchaseInvoiceGridRow.Total),
            HeaderText = "Total",
            ReadOnly = true,
            FillWeight = 80,
            DefaultCellStyle = new DataGridViewCellStyle { Format = "C2" }
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Attachment", DataPropertyName = nameof(PurchaseInvoiceGridRow.Attachment), HeaderText = "PDF Attachment", ReadOnly = true, FillWeight = 145 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", DataPropertyName = nameof(PurchaseInvoiceGridRow.Status), HeaderText = "Status", ReadOnly = true, FillWeight = 75 });
        root.Controls.Add(grid, 0, 2);
        root.Controls.Add(BuildGridFooter("Showing purchases for selected store"), 0, 3);
        string? selectedPurchaseFilePath = null;

        void clearPurchaseForm()
        {
            date.Value = DateTime.Today;
            vendor.Clear();
            number.Clear();
            total.Clear();
            tax.Text = "$0.00";
            file.Text = "No file selected";
            selectedPurchaseFilePath = null;
            grid.ClearSelection();
        }

        async Task loadSelectedInvoiceAsync()
        {
            var id = SelectedId(grid);
            if (id is null) return;
            var invoice = await _purchaseService.GetInvoiceWithLinesAsync(id.Value);
            if (invoice is null) return;
            date.Value = invoice.InvoiceDate.ToDateTime(TimeOnly.MinValue);
            vendor.Text = invoice.VendorName;
            number.Text = invoice.InvoiceNumber;
            total.Text = invoice.Total.ToString("0.00", CultureInfo.CurrentCulture);
            tax.Text = invoice.Lines.Sum(x => x.Tax ?? 0m).ToString("0.00", CultureInfo.CurrentCulture);
            selectedPurchaseFilePath = invoice.FilePath;
            file.Text = string.IsNullOrWhiteSpace(invoice.FilePath) ? "No file selected" : Path.GetFileName(invoice.FilePath);
        }

        async Task updateSelectedInvoiceAsync()
        {
            if (!_session.IsAdmin)
            {
                MessageBox.Show(this, "Only Owner/Admin accounts can update invoices.", "Access Restricted", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var id = SelectedId(grid);
            if (id is null)
            {
                MessageBox.Show(this, "Select a purchase invoice first.", "Purchases", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var existing = await _purchaseService.GetInvoiceWithLinesAsync(id.Value);
            var lines = existing?.Lines ?? new List<PurchaseInvoiceLine>();
            await _purchaseService.UpdateInvoiceAsync(
                id.Value,
                DateOnly.FromDateTime(date.Value),
                null,
                vendor.Text.Trim(),
                number.Text.Trim(),
                Money(total.Text),
                existing?.Notes ?? "",
                selectedPurchaseFilePath,
                lines);
            clearPurchaseForm();
            await refreshAsync();
        }

        async Task refreshAsync()
        {
            var invoices = await _purchaseService.GetInvoicesAsync(_currentStoreId);
            var monthRows = invoices.Where(x => x.InvoiceDate.Month == DateTime.Today.Month && x.InvoiceDate.Year == DateTime.Today.Year).ToList();
            grid.DataSource = invoices
                .Select(x => new PurchaseInvoiceGridRow
                {
                    Id = x.Id,
                    Select = false,
                    Date = x.InvoiceDate,
                    Vendor = x.VendorName,
                    Invoice = x.InvoiceNumber,
                    Total = x.Total,
                    Attachment = Path.GetFileName(x.FilePath),
                    Status = string.IsNullOrWhiteSpace(x.FilePath) ? "Entered" : "Imported"
                })
                .ToList();
            HideId(grid);
            monthPurchases.Text = MoneyText(monthRows.Sum(x => x.Total));
            openInvoices.Text = MoneyText(invoices.Sum(x => x.Total));
            if (openInvoices.Parent?.Controls.OfType<Label>().LastOrDefault() is { } openInvoiceSubtitle)
                openInvoiceSubtitle.Text = $"{invoices.Count} Invoices";
            importedPdfs.Text = invoices.Count(x => !string.IsNullOrWhiteSpace(x.FilePath)).ToString(CultureInfo.InvariantCulture);
        }
        void refresh()
        {
            _ = refreshAsync();
        }
        grid.SelectionChanged += async (_, _) => await loadSelectedInvoiceAsync();
        AddSectionButton(actions, "Home", (_, _) => ShowModule("Dashboard"), width: 150);
        AddSectionButton(actions, "New Purchase", (_, _) => clearPurchaseForm(), width: 185);
        AddSectionButton(actions, "Import Purchases", async () =>
        {
            await ImportPurchaseInvoiceAsync(refreshAsync);
            file.Text = "Uploaded";
        }, true, 210);
        AddSectionButton(actions, "Add", async () =>
        {
            await _purchaseService.AddInvoiceAsync(
                _currentStoreId,
                DateOnly.FromDateTime(date.Value),
                null,
                vendor.Text.Trim(),
                number.Text.Trim(),
                Money(total.Text),
                "",
                null,
                Array.Empty<PurchaseInvoiceLine>(),
                _session.UserId,
                _session.DisplayName);
            clearPurchaseForm();
            await refreshAsync();
        }, true, 160);
        AddSectionButton(actions, "Email Invoices", async () =>
        {
            var storeKey = CurrentInvoiceEmailStoreKey();
            using var setup = new InvoiceEmailSetupForm(_paths, _invoiceEmailSyncService, storeKey);
            if (setup.ShowDialog(this) != DialogResult.OK)
                return;

            try
            {
                var syncResult = await _invoiceEmailSyncService.SyncAsync(
                    storeKey,
                    _currentStoreId,
                    _session.UserId,
                    _session.DisplayName,
                    setup.RequestedInvoiceMonth);
                await refreshAsync();
                var scope = setup.RequestedInvoiceMonth.HasValue
                    ? setup.RequestedInvoiceMonth.Value.ToString("MMMM yyyy", CultureInfo.CurrentCulture)
                    : "new messages";
                MessageBox.Show(this,
                    $"Email invoice sync complete for {scope}.\n\n" +
                    $"Mail folders scanned: {syncResult.FoldersScanned}\n" +
                    $"Messages checked: {syncResult.MessagesChecked}\n" +
                    $"PDF attachments found: {syncResult.AttachmentsFound}\n" +
                    $"Invoices imported: {syncResult.InvoicesImported}\n" +
                    $"Duplicates skipped: {syncResult.DuplicatesSkipped}\n" +
                    $"Needs review: {syncResult.NeedsReview}" +
                    (syncResult.NeedsReview > 0 ? $"\n\nReview folder:\n{syncResult.ReviewFolder}" : ""),
                    "Invoice Email Sync",
                    MessageBoxButtons.OK,
                    syncResult.NeedsReview > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    AppBootstrap.RedactSensitiveText(ex.Message),
                    "Invoice Email Sync",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }, width: 205, enabled: _session.IsAdmin);
        AddSectionButton(actions, "Update Selected", updateSelectedInvoiceAsync, width: 210, enabled: _session.IsAdmin);
        AddSectionButton(actions, "Delete Selected", async () =>
        {
            if (!_session.IsAdmin)
            {
                MessageBox.Show(this, "Only Owner/Admin accounts can delete invoices.", "Access Restricted", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var id = SelectedId(grid);
            if (id is null) return;
            if (MessageBox.Show(this, "Delete selected invoice and recompute product costs?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            await _purchaseService.DeleteInvoiceAsync(id.Value);
            clearPurchaseForm();
            await refreshAsync();
        }, width: 210, enabled: _session.IsAdmin);
        AddSectionButton(actions, "Open File", (_, _) => OpenSelectedPurchaseFile(grid), width: 145);
        AddSectionButton(actions, "Refresh", async () => await refreshAsync(), width: 145);
        refresh();
        return ModuleShell("\uE719", "Purchases", "Record vendor invoices, imports, and purchase totals.", root);
    }

    private Control BuildBankStatement()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, BackColor = WinTheme.Bg, Padding = new Padding(2) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 205));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = WinTheme.Bg };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 74));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        root.Controls.Add(top, 0, 0);

        var filterShell = WinTheme.BorderedPanel(10);
        filterShell.Dock = DockStyle.Fill;
        filterShell.Margin = new Padding(4, 6, 8, 6);
        top.Controls.Add(filterShell, 0, 0);
        var filters = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel, ColumnCount = 4, RowCount = 3 };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        filters.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        filters.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        filters.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        filterShell.Controls.Add(filters);

        var account = SectionCombo("Checking Account", "Operating Account", "All Accounts");
        var month = WinTheme.ComboBox();
        foreach (var m in Enumerable.Range(1, 12))
            month.Items.Add(new BankMonthItem(m, new DateTime(2000, m, 1).ToString("MMMM")));
        month.SelectedIndex = DateTime.Today.Month - 1;
        var year = WinTheme.ComboBox();
        foreach (var y in Enumerable.Range(DateTime.Today.Year - 5, 7).Reverse())
            year.Items.Add(y);
        year.SelectedItem = DateTime.Today.Year;
        var importStatus = SectionCombo("Imported Successfully", "Not Imported");
        var search = SectionTextBox("Search description, check #, amount...");
        var category = SectionCombo("All Categories", "Office Supplies", "Maintenance", "Utilities", "Customer Payment", "Bank Charges", "Purchases");
        var matched = SectionCombo("All", "Yes", "No");
        AddMockField(filters, "Account *", account, 0, 0, 118);
        AddMockField(filters, "Statement Month *", month, 1, 0, 150);
        AddMockField(filters, "Statement Year *", year, 2, 0, 140);
        var upload = WinTheme.Button("Upload Statement", true);
        upload.Dock = DockStyle.Fill;
        upload.Margin = new Padding(8, 8, 8, 8);
        filters.Controls.Add(upload, 3, 0);
        AddMockField(filters, "Import Status", importStatus, 0, 1, 120);
        AddMockField(filters, "Search Transactions", search, 1, 1, 160);
        AddMockField(filters, "Category", category, 2, 1, 90);
        AddMockField(filters, "Matched", matched, 3, 1, 85);

        var liveBankPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
            BackColor = Color.FromArgb(242, 248, 255),
            Margin = new Padding(4, 5, 4, 2),
            Padding = new Padding(8, 4, 8, 4)
        };
        liveBankPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        liveBankPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        liveBankPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        liveBankPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        liveBankPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        var liveBankTitle = new Label
        {
            Text = "LIVE BANK",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = WinTheme.Blue,
            Font = WinTheme.BoldFont(9.5f)
        };
        var liveBankStatus = new Label
        {
            Text = "Not connected — statement upload remains available",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = WinTheme.Muted,
            Font = WinTheme.BodyFont(9f),
            AutoEllipsis = true
        };
        var connectBank = WinTheme.Button("Connect Bank", true);
        connectBank.Dock = DockStyle.Fill;
        connectBank.Margin = new Padding(4, 0, 4, 0);
        var bankEmailSettings = WinTheme.Button("Email Setup");
        bankEmailSettings.Dock = DockStyle.Fill;
        bankEmailSettings.Margin = new Padding(4, 0, 4, 0);
        var syncBank = WinTheme.Button("Sync Now");
        syncBank.Dock = DockStyle.Fill;
        syncBank.Margin = new Padding(4, 0, 0, 0);
        liveBankPanel.Controls.Add(liveBankTitle, 0, 0);
        liveBankPanel.Controls.Add(liveBankStatus, 1, 0);
        liveBankPanel.Controls.Add(bankEmailSettings, 2, 0);
        liveBankPanel.Controls.Add(connectBank, 3, 0);
        liveBankPanel.Controls.Add(syncBank, 4, 0);
        filters.Controls.Add(liveBankPanel, 0, 2);
        filters.SetColumnSpan(liveBankPanel, 4);

        var statGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = WinTheme.Bg };
        statGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        statGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        statGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        statGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        top.Controls.Add(statGrid, 1, 0);
        var importedTotal = MetricCard(statGrid, 0, 0, "Imported Total", "$0.00", WinTheme.Text, "0");
        var matchedTotal = MetricCard(statGrid, 1, 0, "Matched", "$0.00", WinTheme.Green, "0");
        var unmatchedTotal = MetricCard(statGrid, 0, 1, "Unmatched", "$0.00", WinTheme.Red, "0");
        var endingBalance = MetricCard(statGrid, 1, 1, "Ending Balance", "$0.00", WinTheme.Copper, "");

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Bg, Padding = new Padding(0, 8, 0, 8), FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        root.Controls.Add(actions, 0, 1);

        var grid = WinTheme.Grid();
        root.Controls.Add(grid, 0, 2);

        var totals = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = WinTheme.Bg };
        totals.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        totals.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        totals.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
        root.Controls.Add(totals, 0, 3);
        var debits = MetricCard(totals, 0, 0, "Total Debits", "$0.00", WinTheme.Red);
        var credits = MetricCard(totals, 1, 0, "Total Credits", "$0.00", WinTheme.Green);
        var difference = MetricCard(totals, 2, 0, "Difference (Credits - Debits)", "$0.00", WinTheme.Copper);
        root.Controls.Add(BuildGridFooter("Showing bank statement transactions for selected store"), 0, 4);

        var refreshingBankRows = false;
        void configureBankColumns()
        {
            foreach (DataGridViewColumn column in grid.Columns)
                column.ReadOnly = column.Name is not ("Select" or "IncludeInProfitLoss");
            if (grid.Columns.Contains("IncludeInProfitLoss"))
                grid.Columns["IncludeInProfitLoss"]!.HeaderText = "P&L";
        }
        grid.DataBindingComplete += (_, _) => configureBankColumns();
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty)
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex >= 0 &&
                grid.Columns[e.ColumnIndex] is DataGridViewCheckBoxColumn)
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        grid.CellValueChanged += async (_, e) =>
        {
            if (refreshingBankRows || e.RowIndex < 0 ||
                !string.Equals(grid.Columns[e.ColumnIndex].Name, "IncludeInProfitLoss", StringComparison.OrdinalIgnoreCase))
                return;
            if (!int.TryParse(grid.Rows[e.RowIndex].Cells["Id"].Value?.ToString(), out var id))
                return;
            var include = Convert.ToBoolean(grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value);
            await UpdateBankProfitLossInclusionAsync(id, include);
        };

        async Task refreshAsync()
        {
            refreshingBankRows = true;
            var m = month.SelectedItem is BankMonthItem mi ? mi.Value : DateTime.Today.Month;
            var y = year.SelectedItem is int yi ? yi : DateTime.Today.Year;
            await EnsureBankStatementTablesAsync();
            var rows = await LoadBankStatementRowsAsync(m, y);
            grid.DataSource = rows
                .Select(x => new BankStatementGridRow
                {
                    Id = x.Id,
                    IncludeInProfitLoss = x.IncludeInProfitLoss,
                    Date = x.Date,
                    Source = x.Source,
                    Description = x.Description,
                    Debit = x.Debit,
                    Credit = x.Credit,
                    Category = x.Category,
                    Matched = x.IsMatched,
                    MatchReference = x.MatchReference,
                    Check = x.CheckNumber
                })
                .ToList();
            HideId(grid);
            configureBankColumns();
            var debitTotal = rows.Sum(x => x.Debit);
            var creditTotal = rows.Sum(x => x.Credit);
            var matchedRows = rows.Where(x => x.IsMatched).ToList();
            var unmatchedRows = rows.Except(matchedRows).ToList();
            importedTotal.Text = MoneyText(rows.Sum(x => x.Credit + x.Debit));
            matchedTotal.Text = MoneyText(matchedRows.Sum(x => x.Credit + x.Debit));
            unmatchedTotal.Text = MoneyText(unmatchedRows.Sum(x => x.Credit + x.Debit));
            endingBalance.Text = MoneyText(creditTotal - debitTotal);
            debits.Text = MoneyText(debitTotal);
            credits.Text = MoneyText(creditTotal);
            difference.Text = MoneyText(creditTotal - debitTotal);
            refreshingBankRows = false;
        }

        async Task safeRefreshAsync()
        {
            try { await refreshAsync(); }
            catch (Exception ex)
            {
                MessageBox.Show(this, AppBootstrap.RedactSensitiveText(ex.Message), "Bank Statement", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        async Task refreshLiveBankStatusAsync()
        {
            try
            {
                LocalBankConnectionStatus? local;
                using (var client = new LiveBankSyncClient())
                {
                    if (client.IsConfigured)
                    {
                        var remoteConnections = await client.GetConnectionsAsync();
                        var remote = remoteConnections
                            .OrderByDescending(connection => connection.LastSyncedUtc)
                            .FirstOrDefault();
                        if (remote is null)
                        {
                            await ClearLocalBankConnectionStatusAsync();
                            liveBankStatus.Text = "Not connected — click Connect to link a bank";
                            liveBankStatus.ForeColor = WinTheme.Muted;
                            return;
                        }

                        local = new LocalBankConnectionStatus(
                            remote.InstitutionName,
                            remote.AccountName,
                            remote.AccountMask,
                            remote.Status,
                            remote.LastSyncedUtc,
                            remote.LastError);
                    }
                    else
                    {
                        local = await LoadLiveBankStatusAsync();
                    }
                }

                if (local is null)
                {
                    liveBankStatus.Text = "Not connected — statement upload remains available";
                    liveBankStatus.ForeColor = WinTheme.Muted;
                    return;
                }

                var accountLabel = string.Join(" • ", new[]
                {
                    local.InstitutionName,
                    local.AccountName,
                    string.IsNullOrWhiteSpace(local.AccountMask) ? "" : $"••{local.AccountMask}"
                }.Where(x => !string.IsNullOrWhiteSpace(x)));
                var syncedText = local.LastSyncedUtc is null
                    ? "Awaiting first sync"
                    : $"Last sync {local.LastSyncedUtc.Value.ToLocalTime():MMM d, h:mm tt}";
                liveBankStatus.Text = $"{accountLabel}  |  {local.Status}  |  {syncedText}";
                liveBankStatus.ForeColor = string.Equals(local.Status, "Active", StringComparison.OrdinalIgnoreCase)
                    ? WinTheme.Green
                    : WinTheme.Copper;
            }
            catch (Exception ex)
            {
                liveBankStatus.Text = $"Live Bank status unavailable: {AppBootstrap.RedactSensitiveText(ex.Message)}";
                liveBankStatus.ForeColor = WinTheme.Red;
            }
        }

        async Task syncLiveBankAsync(bool showSuccess)
        {
            using var client = new LiveBankSyncClient();
            if (!client.IsConfigured)
            {
                MessageBox.Show(this,
                    "Live Bank Sync is built into this screen, but your developer bank-sync service has not been configured yet.\n\n"
                    + "Statement upload remains fully available. After a supported bank provider is connected to the secure service, this button will synchronize new transactions without storing bank credentials in HISAB KITAB.",
                    "Live Bank Setup Required", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            syncBank.Enabled = false;
            liveBankStatus.Text = "Synchronizing connected bank transactions…";
            liveBankStatus.ForeColor = WinTheme.Blue;
            try
            {
                var connections = await client.GetConnectionsAsync();
                if (connections.Count == 0)
                {
                    liveBankStatus.Text = "Not connected — click Connect to link a bank";
                    liveBankStatus.ForeColor = WinTheme.Muted;
                    if (showSuccess)
                    {
                        MessageBox.Show(this,
                            "No bank account is connected yet.\n\n"
                            + "Click Connect, finish every step in the Plaid browser window, select the account to share, "
                            + "and continue until the HISAB KITAB completion page appears.",
                            "Connect a Bank First", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    return;
                }

                var result = await client.SyncAsync();
                await SaveLiveBankSyncAsync(result);
                await refreshAsync();
                await refreshLiveBankStatusAsync();
                if (showSuccess)
                {
                    MessageBox.Show(this,
                        $"Bank sync complete.\n\nNew: {result.Added.Count}\nUpdated: {result.Modified.Count}\nRemoved: {result.RemovedTransactionIds.Count}",
                        "Live Bank Sync", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                liveBankStatus.Text = "Live Bank sync needs attention";
                liveBankStatus.ForeColor = WinTheme.Red;
                MessageBox.Show(this, AppBootstrap.RedactSensitiveText(ex.Message), "Live Bank Sync",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                syncBank.Enabled = true;
            }
        }

        async Task importAndSelectPeriodAsync()
        {
            var m = month.SelectedItem is BankMonthItem mi ? mi.Value : DateTime.Today.Month;
            var y = year.SelectedItem is int yi ? yi : DateTime.Today.Year;
            var importedPeriod = await ImportBankStatementAsync(m, y, refreshAsync);
            if (importedPeriod is not { } period)
                return;

            SelectBankStatementPeriod(month, year, period.Month, period.Year);
            await safeRefreshAsync();
        }

        AddSectionButton(actions, "Home", (_, _) => ShowModule("Dashboard"), width: 150);
        AddSectionButton(actions, "Import Statement", importAndSelectPeriodAsync, true, 205);
        AddSectionButton(actions, "Categorize Selected", async () =>
        {
            var id = SelectedBankTransactionId(grid);
            if (id is null)
            {
                MessageBox.Show(this, "Select a transaction first.", "Bank Statement", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var selectedCategory = category.Text is "All Categories" or "" ? "Other" : category.Text;
            await UpdateBankTransactionAsync(id.Value, selectedCategory, null);
            await refreshAsync();
        }, width: 220);
        AddSectionButton(actions, "Match Transaction", async () =>
        {
            var id = SelectedBankTransactionId(grid);
            if (id is null)
            {
                MessageBox.Show(this, "Select a transaction first.", "Bank Statement", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var matchReference = PromptText("Match Transaction", "Enter the matching HISAB KITAB check, purchase, payout, or reference:", grid.CurrentRow?.Cells["MatchReference"].Value?.ToString() ?? "");
            if (matchReference is null) return;
            await MatchBankTransactionAsync(id.Value, matchReference);
            await refreshAsync();
        }, width: 210);
        AddSectionButton(actions, "Mark Reviewed", async () =>
        {
            var id = SelectedBankTransactionId(grid);
            if (id is null)
            {
                MessageBox.Show(this, "Select a transaction first.", "Bank Statement", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            await UpdateBankTransactionAsync(id.Value, "Reviewed", null);
            await refreshAsync();
        }, width: 190);
        AddSectionButton(actions, "Delete Selected", async () =>
        {
            if (!_session.IsAdmin) return;
            var id = SelectedBankTransactionId(grid);
            if (id is null) return;
            await using var conn = CreateBankConnection();
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM BankStatementTransactions WHERE Id=@id AND StoreId=@sid";
            AddParam(cmd, "@id", id.Value);
            AddParam(cmd, "@sid", _currentStoreId);
            await cmd.ExecuteNonQueryAsync();
            await refreshAsync();
        }, width: 190, enabled: _session.IsAdmin);
        AddSectionButton(actions, "Recategorize", async () =>
        {
            await RecategorizeBankStatementAsync(
                month.SelectedItem is BankMonthItem mi ? mi.Value : DateTime.Today.Month,
                year.SelectedItem is int yi ? yi : DateTime.Today.Year);
            await refreshAsync();
        }, width: 170);
        AddSectionButton(actions, "Delete Month", async () =>
        {
            if (!_session.IsAdmin) return;
            var m = month.SelectedItem is BankMonthItem mi ? mi.Value : DateTime.Today.Month;
            var y = year.SelectedItem is int yi ? yi : DateTime.Today.Year;
            if (MessageBox.Show(this, $"Delete all bank transactions for {new DateTime(y, m, 1):MMMM yyyy}?", "Bank Statement", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            await ClearBankStatementMonthAsync(m, y);
            await refreshAsync();
        }, width: 165, enabled: _session.IsAdmin);
        AddSectionButton(actions, "Refresh", async () => await safeRefreshAsync(), width: 135);
        upload.Click += async (_, _) =>
        {
            await importAndSelectPeriodAsync();
        };
        bankEmailSettings.Click += async (_, _) => await ShowBankStatementEmailSettingsAsync();
        connectBank.Click += async (_, _) =>
        {
            using var client = new LiveBankSyncClient();
            if (!client.IsConfigured)
            {
                MessageBox.Show(this,
                    "The secure developer bank-sync service must be configured before a real bank can be connected.\n\n"
                    + "This design keeps bank-provider secrets and customer access tokens out of the installed desktop application. The existing Upload Statement option remains available.",
                    "Live Bank Setup Required", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            connectBank.Enabled = false;
            try
            {
                var existingConnectionIds = (await client.GetConnectionsAsync())
                    .Select(connection => connection.ConnectionId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var hostedLink = await client.CreateHostedLinkAsync();
                Process.Start(new ProcessStartInfo(hostedLink.AbsoluteUri) { UseShellExecute = true });
                liveBankStatus.Text = "Complete the secure Plaid connection in your browser…";
                liveBankStatus.ForeColor = WinTheme.Blue;

                var deadline = DateTime.UtcNow.AddMinutes(10);
                while (DateTime.UtcNow < deadline && !IsDisposed)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    if (IsDisposed)
                        return;
                    var connections = await client.GetConnectionsAsync();
                    var connectedAccount = connections.FirstOrDefault(connection =>
                        !existingConnectionIds.Contains(connection.ConnectionId));
                    if (connectedAccount is null)
                        continue;

                    liveBankStatus.Text = "Bank connected — importing the first transactions…";
                    var result = await client.SyncAsync();
                    await SaveLiveBankSyncAsync(result);
                    await refreshAsync();
                    await refreshLiveBankStatusAsync();
                    MessageBox.Show(this,
                        $"Bank account connected successfully.\n\n"
                        + $"Institution: {connectedAccount.InstitutionName}\n"
                        + $"Account: {connectedAccount.AccountName}\n\n"
                        + $"Imported transactions: {result.Added.Count}",
                        "Bank Connected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                liveBankStatus.Text = "Plaid connection is still awaiting completion";
                liveBankStatus.ForeColor = WinTheme.Copper;
                MessageBox.Show(this,
                    "The bank connection has not completed yet.\n\n"
                    + "If the Plaid browser window is still open, finish every step and select the bank account to share. "
                    + "Then return here and click Sync Now.",
                    "Bank Connection Pending", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                liveBankStatus.Text = "Bank connection needs attention";
                liveBankStatus.ForeColor = WinTheme.Red;
                MessageBox.Show(this, AppBootstrap.RedactSensitiveText(ex.Message), "Connect Bank",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                connectBank.Enabled = true;
            }
        };
        syncBank.Click += async (_, _) => await syncLiveBankAsync(showSuccess: true);
        month.SelectedIndexChanged += async (_, _) => await safeRefreshAsync();
        year.SelectedIndexChanged += async (_, _) => await safeRefreshAsync();
        _pendingModuleActivation = async () =>
        {
            await safeRefreshAsync();
            await refreshLiveBankStatusAsync();
            using var client = new LiveBankSyncClient();
            if (client.IsConfigured)
                await syncLiveBankAsync(showSuccess: false);
        };
        return ModuleShell("\uE825", "Bank Statement", "Live bank sync, statement imports, categorization, and reconciliation.", root);
    }

    private static void SelectBankStatementPeriod(ComboBox monthCombo, ComboBox yearCombo, int month, int year)
    {
        for (var i = 0; i < monthCombo.Items.Count; i++)
        {
            if (monthCombo.Items[i] is BankMonthItem item && item.Value == month)
            {
                monthCombo.SelectedIndex = i;
                break;
            }
        }

        if (!yearCombo.Items.Contains(year))
            yearCombo.Items.Add(year);
        yearCombo.SelectedItem = year;
    }

    private Control BuildProductCosts()
    {
        var root = SectionRoot(245, 72);
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = WinTheme.Bg };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        root.Controls.Add(top, 0, 0);

        var formShell = WinTheme.BorderedPanel(10);
        formShell.Dock = DockStyle.Fill;
        formShell.Margin = new Padding(4, 6, 8, 6);
        top.Controls.Add(formShell, 0, 0);
        var form = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 5, BackColor = WinTheme.Panel };
        for (var i = 0; i < 4; i++) form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        for (var i = 0; i < 5; i++) form.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        formShell.Controls.Add(form);
        var productName = SectionTextBox();
        var sku = SectionTextBox();
        var category = SectionCombo("Office Supplies", "Shipping Supplies", "Inventory", "Utilities", "General");
        var vendor = SectionCombo("All Vendors");
        var lastInvoiceDate = WinTheme.DatePicker();
        var unitCost = SectionTextBox(rightAlign: true);
        var averageCost = SectionTextBox(rightAlign: true);
        var stock = SectionTextBox(rightAlign: true);
        var notes = SectionTextBox();
        AddMockField(form, "Product Name *", productName, 0, 0, 130);
        form.SetColumnSpan(form.GetControlFromPosition(0, 0)!, 2);
        AddMockField(form, "Unit Cost *", unitCost, 2, 0, 105);
        form.SetColumnSpan(form.GetControlFromPosition(2, 0)!, 2);
        AddMockField(form, "SKU / UPC *", sku, 0, 1, 130);
        form.SetColumnSpan(form.GetControlFromPosition(0, 1)!, 2);
        AddMockField(form, "Average Cost", averageCost, 2, 1, 120);
        form.SetColumnSpan(form.GetControlFromPosition(2, 1)!, 2);
        AddMockField(form, "Category", category, 0, 2, 100);
        form.SetColumnSpan(form.GetControlFromPosition(0, 2)!, 2);
        AddMockField(form, "Stock On Hand", stock, 2, 2, 125);
        form.SetColumnSpan(form.GetControlFromPosition(2, 2)!, 2);
        AddMockField(form, "Vendor", vendor, 0, 3, 100);
        form.SetColumnSpan(form.GetControlFromPosition(0, 3)!, 2);
        AddMockField(form, "Notes", notes, 2, 3, 80);
        form.SetColumnSpan(form.GetControlFromPosition(2, 3)!, 2);
        AddMockField(form, "Last Invoice Date", lastInvoiceDate, 0, 4, 145);
        form.SetColumnSpan(form.GetControlFromPosition(0, 4)!, 2);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = WinTheme.Bg };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 98));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        top.Controls.Add(right, 1, 0);
        var stats = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = WinTheme.Bg };
        right.Controls.Add(stats, 0, 0);
        var tracked = MetricCard(stats, "Products Tracked", "0", WinTheme.Copper, "", 205, 86);
        var changes = MetricCard(stats, "Cost Changes (30 Days)", "0", WinTheme.Green, "", 220, 86);
        var increase = MetricCard(stats, "Highest Increase (30 Days)", "+0.00%", WinTheme.Red, "", 245, 86);
        var historyShell = MockBorderCard("Recent Cost History (Last 3)", "\uE823", 34);
        right.Controls.Add((Control)historyShell.Parent!, 0, 1);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = WinTheme.Bg, Padding = new Padding(0, 8, 0, 8) };
        root.Controls.Add(actions, 0, 1);
        var grid = WinTheme.Grid();
        root.Controls.Add(grid, 0, 2);
        root.Controls.Add(BuildGridFooter("Showing product costs for selected store"), 0, 3);

        async Task refreshAsync()
        {
            var costs = await _purchaseService.GetProductCostsAsync(_currentStoreId);
            grid.DataSource = costs
                .Select(x => new { x.Id, Select = false, Product = x.ProductName, SKU = x.Sku, Category = "", LastVendor = x.LastVendorName, UnitCost = x.LastUnitCost, AvgCost = x.LastUnitCost, Stock = "", LastDate = x.LastInvoiceDate, Change30Days = "", Actions = "" })
                .ToList();
            HideId(grid);
            tracked.Text = costs.Count.ToString(CultureInfo.InvariantCulture);
            changes.Text = costs.Count(x => x.UpdatedUtc >= DateTime.UtcNow.AddDays(-30)).ToString(CultureInfo.InvariantCulture);
            increase.Text = costs.Count > 0 ? "+0.00%" : "+0.00%";
            var recent = costs.OrderByDescending(x => x.UpdatedUtc).Take(3)
                .Select(x => new[] { x.LastVendorName, x.LastInvoiceDate.ToString("M/d/yyyy", CultureInfo.InvariantCulture), x.LastUnitCost.ToString("C4"), "" })
                .ToList();
            var existingHistory = historyShell.GetControlFromPosition(0, 1);
            if (existingHistory is not null)
            {
                historyShell.Controls.Remove(existingHistory);
                existingHistory.Dispose();
            }
            historyShell.Controls.Add(BuildMiniTable(new[] { "Vendor", "Date", "Unit Cost", "Change" }, recent), 0, 1);
        }
        void refresh() => _ = refreshAsync();
        AddSectionButton(actions, "Home", (_, _) => ShowModule("Dashboard"), width: 150);
        AddSectionButton(actions, "Add Product", (_, _) => MessageBox.Show(this, "Product costs are created from imported invoice line items.", "Product Costs", MessageBoxButtons.OK, MessageBoxIcon.Information), true, 190);
        AddSectionButton(actions, "Import Invoice", async () => await ImportProductCostsAsync(refreshAsync), width: 190);
        AddSectionButton(actions, "Update Costs", async () => await ImportProductCostsAsync(refreshAsync), width: 190);
        AddSectionButton(actions, "Add Correction", async () =>
        {
            await ImportProductCostsAsync(refreshAsync);
        }, width: 205, enabled: _session.IsAdmin);
        AddSectionButton(actions, "Delete Selected", async () =>
        {
            if (!_session.IsAdmin)
            {
                MessageBox.Show(this, "Only Owner/Admin accounts can delete product costs.", "Access Restricted", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var id = SelectedId(grid);
            if (id is null) return;
            if (MessageBox.Show(this, "Delete selected product cost and related alerts?", "Product Costs", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            await _purchaseService.DeleteProductCostsAsync(_currentStoreId, new[] { id.Value });
            await refreshAsync();
        }, width: 205, enabled: _session.IsAdmin);
        AddSectionButton(actions, "Refresh", async () => await refreshAsync(), width: 150);
        refresh();
        return ModuleShell("\uE71B", "Product Costs", "Track item costs, vendors, and price history.", root);
    }

    private Control BuildPriceAlerts()
    {
        var root = SectionRoot(200, 72);
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = WinTheme.Bg };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        root.Controls.Add(top, 0, 0);

        var filterShell = WinTheme.BorderedPanel(10);
        filterShell.Dock = DockStyle.Fill;
        filterShell.Margin = new Padding(4, 6, 8, 6);
        top.Controls.Add(filterShell, 0, 0);
        var filters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, BackColor = WinTheme.Panel };
        for (var i = 0; i < 3; i++) filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        for (var i = 0; i < 3; i++) filters.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));
        filterShell.Controls.Add(filters);
        var category = SectionCombo("All Categories", "Office Supplies", "Inventory", "Utilities", "General");
        var supplier = SectionCombo("All Suppliers");
        var status = SectionCombo("All Statuses", "New", "Read", "Resolved");
        var minChange = SectionCombo("All", "5%+", "10%+", "20%+");
        var product = SectionTextBox("Search by product name or SKU...");
        var threshold = SectionTextBox("10.00", rightAlign: true);
        AddMockField(filters, "Category", category, 0, 0, 105);
        AddMockField(filters, "Supplier", supplier, 1, 0, 105);
        AddMockField(filters, "Status", status, 2, 0, 90);
        AddMockField(filters, "Minimum Change %", minChange, 0, 1, 150);
        AddMockField(filters, "Search Product", product, 1, 1, 135);
        AddMockField(filters, "Alert Threshold (%)", threshold, 2, 1, 155);
        AddMockField(filters, "Auto Alert", SectionCombo("On", "Off"), 0, 2, 105);
        filters.SetColumnSpan(filters.GetControlFromPosition(0, 2)!, 3);

        var statGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = WinTheme.Bg };
        statGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        statGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        statGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
        top.Controls.Add(statGrid, 1, 0);
        var newAlerts = MetricCard(statGrid, 0, 0, "New Alerts", "0", WinTheme.Red, "Unread alerts");
        var highPriority = MetricCard(statGrid, 1, 0, "High Priority", "0", WinTheme.Red, "Require attention");
        var resolved = MetricCard(statGrid, 2, 0, "Resolved This Month", "0", WinTheme.Green, "Alerts resolved");

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = WinTheme.Bg, Padding = new Padding(0, 8, 0, 8) };
        root.Controls.Add(actions, 0, 1);
        var grid = WinTheme.Grid();
        root.Controls.Add(grid, 0, 2);
        root.Controls.Add(BuildGridFooter("Showing price alerts for selected store"), 0, 3);

        void refresh()
        {
            using var db = CreateDb();
            var rows = db.PriceAlerts.AsNoTracking().Where(x => x.StoreId == _currentStoreId).OrderByDescending(x => x.CreatedUtc).ToList();
            grid.DataSource = rows
                .Select(x =>
                {
                    var pct = x.OldUnitCost == 0 ? 0 : ((x.NewUnitCost - x.OldUnitCost) / x.OldUnitCost) * 100m;
                    var priority = Math.Abs(pct) >= 10m ? "High" : Math.Abs(pct) >= 5m ? "Medium" : "Low";
                    return new { x.Id, Select = false, Priority = x.IsRead ? "Read" : priority, Product = x.ProductName, SKU = x.Sku, Supplier = x.VendorName, OldCost = x.OldUnitCost, NewCost = x.NewUnitCost, Change = PercentText(pct), AlertPrice = x.NewUnitCost, Status = x.IsRead ? "Read" : "New", Created = x.CreatedUtc };
                })
                .ToList();
            HideId(grid);
            newAlerts.Text = rows.Count(x => !x.IsRead).ToString(CultureInfo.InvariantCulture);
            highPriority.Text = rows.Count(x => !x.IsRead && Math.Abs(x.OldUnitCost == 0 ? 0 : ((x.NewUnitCost - x.OldUnitCost) / x.OldUnitCost) * 100m) >= 10m).ToString(CultureInfo.InvariantCulture);
            resolved.Text = rows.Count(x => x.IsRead && x.ReadUtc?.Month == DateTime.UtcNow.Month && x.ReadUtc?.Year == DateTime.UtcNow.Year).ToString(CultureInfo.InvariantCulture);
        }
        AddSectionButton(actions, "Home", (_, _) => ShowModule("Dashboard"), width: 160);
        AddSectionButton(actions, "Manage Alerts", async () =>
        {
            await _purchaseService.MarkAllAlertsReadAsync(_currentStoreId);
            refresh();
        }, width: 190);
        AddSectionButton(actions, "Mark Read", async () =>
        {
            var id = SelectedId(grid);
            if (id is null) return;
            await _purchaseService.MarkAlertsReadAsync(_currentStoreId, new[] { id.Value });
            refresh();
        }, width: 170);
        AddSectionButton(actions, "Create Rule", (_, _) => MessageBox.Show(this, "Alert rules use the threshold and auto alert controls above.", "Price Alerts", MessageBoxButtons.OK, MessageBoxIcon.Information), width: 170, enabled: _session.IsAdmin);
        AddSectionButton(actions, "Update Selected", async () =>
        {
            var id = SelectedId(grid);
            if (id is null) return;
            await _purchaseService.MarkAlertsReadAsync(_currentStoreId, new[] { id.Value });
            refresh();
        }, width: 200, enabled: _session.IsAdmin);
        AddSectionButton(actions, "Resolve Selected", async () =>
        {
            var id = SelectedId(grid);
            if (id is null) return;
            await _purchaseService.MarkAlertsReadAsync(_currentStoreId, new[] { id.Value });
            refresh();
        }, width: 200);
        AddSectionButton(actions, "Delete Selected", async () =>
        {
            var id = SelectedId(grid);
            if (id is null) return;
            await _purchaseService.DeleteAlertsAsync(_currentStoreId, new[] { id.Value });
            refresh();
        }, width: 200, enabled: _session.IsAdmin);
        AddSectionButton(actions, "Mark All Read", async () =>
        {
            await _purchaseService.MarkAllAlertsReadAsync(_currentStoreId);
            refresh();
        }, width: 180);
        AddSectionButton(actions, "Refresh", (_, _) => refresh(), width: 145);
        refresh();
        return ModuleShell("\uE7BA", "Price Alerts", "Review supplier cost changes and alert thresholds.", root);
    }

    private Control BuildProfitLoss()
    {
        using var db = CreateDb();
        var month = DateTime.Today.Month;
        var year = DateTime.Today.Year;
        var shifts = db.ShiftLogs.AsNoTracking().Where(x => x.StoreId == _currentStoreId).ToList()
            .Where(x => x.Date.Month == month && x.Date.Year == year).ToList();
        var purchases = db.PurchaseInvoices.AsNoTracking().Where(x => x.StoreId == _currentStoreId).ToList()
            .Where(x => x.InvoiceDate.Month == month && x.InvoiceDate.Year == year).ToList();
        var cash = db.CashOnHand.AsNoTracking().Where(x => x.StoreId == _currentStoreId).ToList()
            .Where(x => x.Date.Month == month && x.Date.Year == year).ToList();
        var checks = db.CheckPayouts.AsNoTracking().Where(x => x.StoreId == _currentStoreId).ToList()
            .Where(x => x.Date.Month == month && x.Date.Year == year).ToList();
        var payroll = db.PayrollEntries.AsNoTracking()
            .Where(x => x.PayrollRun!.StoreId == _currentStoreId && x.PayrollRun.Status == PayrollRunStatus.Finalized && x.PayrollRun.PayDate.Month == month && x.PayrollRun.PayDate.Year == year)
            .Sum(x => (decimal?)x.GrossPay).GetValueOrDefault();
        var periodFrom = new DateOnly(year, month, 1);
        var periodTo = DateOnly.FromDateTime(DateTime.Today);
        var profitLoss = Task.Run(() => _reportService.GetProfitLossDataAsync(periodFrom, periodTo))
            .GetAwaiter().GetResult();
        var revenue = profitLoss.TotalRevenue;
        var bankIncome = profitLoss.BankIncome;
        var cogs = profitLoss.Purchases;
        var cashCheckExpenses = cash.Sum(x => x.PayoutAmount) + checks.Sum(x => x.CheckAmount);
        var bankExpenses = profitLoss.TotalBankExpenses - profitLoss.Payroll;
        var payoutExpenses = profitLoss.TotalExpenses - cogs;
        var expenses = profitLoss.TotalExpenses;
        var net = profitLoss.NetProfitLoss;
        var margin = revenue == 0 ? 0 : (net / revenue) * 100m;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, BackColor = WinTheme.Bg, Padding = new Padding(2) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 250));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        var filters = WinTheme.BorderedPanel(10);
        filters.Dock = DockStyle.Fill;
        filters.Margin = new Padding(4, 6, 4, 6);
        var filterGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1, BackColor = WinTheme.Panel };
        filterGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16));
        filterGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16));
        filterGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14));
        filterGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        filterGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17));
        filterGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17));
        filters.Controls.Add(filterGrid);
        var from = WinTheme.DatePicker();
        from.Value = new DateTime(year, month, 1);
        var to = WinTheme.DatePicker();
        to.Value = DateTime.Today;
        AddMockField(filterGrid, "From", from, 0, 0, 70);
        AddMockField(filterGrid, "To", to, 1, 0, 55);
        AddMockField(filterGrid, "Group By", SectionCombo("Day", "Week", "Month"), 2, 0, 85);
        AddMockField(filterGrid, "Store Comparison", SectionCombo("This Store Only", "All Stores"), 3, 0, 145);
        var refresh = WinTheme.Button("Refresh");
        refresh.Dock = DockStyle.Fill;
        refresh.Margin = new Padding(8, 8, 8, 8);
        refresh.Click += (_, _) => ShowModule("Profit & Loss");
        filterGrid.Controls.Add(refresh, 4, 0);
        var export = WinTheme.Button("Export Report", true);
        export.Dock = DockStyle.Fill;
        export.Margin = new Padding(8, 8, 8, 8);
        export.Click += async (_, _) => await GenerateProfitLossPdfAsync();
        filterGrid.Controls.Add(export, 5, 0);
        root.Controls.Add(filters, 0, 0);

        var metrics = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1, BackColor = WinTheme.Bg };
        for (var i = 0; i < 6; i++) metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16.66f));
        root.Controls.Add(metrics, 0, 1);
        MetricCard(metrics, 0, 0, "Total Income", MoneyText(revenue), WinTheme.Green, bankIncome == 0 ? "Sales" : $"Includes {MoneyText(bankIncome)} bank");
        MetricCard(metrics, 1, 0, "Cost of Goods Sold", MoneyText(cogs), WinTheme.Red, "Purchases");
        MetricCard(metrics, 2, 0, "Gross Profit", MoneyText(revenue - cogs), WinTheme.Green, "");
        MetricCard(metrics, 3, 0, "Expenses", MoneyText(payoutExpenses), WinTheme.Red, "");
        MetricCard(metrics, 4, 0, "Net Profit", MoneyText(net), net >= 0 ? WinTheme.Green : WinTheme.Red, "");
        MetricCard(metrics, 5, 0, "Margin %", PercentText(margin), margin >= 0 ? WinTheme.Green : WinTheme.Red, "");

        var visuals = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = WinTheme.Bg };
        visuals.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        visuals.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        root.Controls.Add(visuals, 0, 2);
        var trend = MockBorderCard("Net Profit Trend", "\uE9D9", 38);
        var trendHost = (Panel)trend.Parent!;
        trendHost.Margin = new Padding(4, 6, 6, 6);
        trend.Controls.Add(new Label { Text = "Net profit uses sales plus bank transactions checked for P&L, less purchases, payouts, payroll, and included bank expenses.", Dock = DockStyle.Fill, ForeColor = WinTheme.Muted, Font = WinTheme.BodyFont(11), TextAlign = ContentAlignment.MiddleCenter }, 0, 1);
        visuals.Controls.Add(trendHost, 0, 0);
        var expense = MockBorderCard("Expenses by Category", "\uE8A7", 38);
        var expenseHost = (Panel)expense.Parent!;
        expenseHost.Margin = new Padding(6, 6, 4, 6);
        expense.Controls.Add(BuildMiniTable(
            new[] { "Category", "Amount", "% of Total" },
            new[]
            {
                new[] { "Purchases", MoneyText(cogs), expenses == 0 ? "0.00%" : PercentText(cogs / expenses * 100m) },
                new[] { "Cash Payouts", MoneyText(cash.Sum(x => x.PayoutAmount)), expenses == 0 ? "0.00%" : PercentText(cash.Sum(x => x.PayoutAmount) / expenses * 100m) },
                new[] { "Check Payouts", MoneyText(checks.Sum(x => x.CheckAmount)), expenses == 0 ? "0.00%" : PercentText(checks.Sum(x => x.CheckAmount) / expenses * 100m) },
                new[] { "Payroll", MoneyText(payroll), expenses == 0 ? "0.00%" : PercentText(payroll / expenses * 100m) },
                new[] { "Bank Expenses", MoneyText(bankExpenses), expenses == 0 ? "0.00%" : PercentText(bankExpenses / expenses * 100m) }
            }), 0, 1);
        visuals.Controls.Add(expenseHost, 1, 0);

        var grid = WinTheme.Grid();
        grid.DataSource = new[]
        {
            new { Category = "Sales + Bank Income", Sales = revenue, COGS = 0m, GrossProfit = revenue, Expense = 0m, NetProfit = revenue, Margin = revenue == 0 ? "0.00%" : "100.00%", Change = "" },
            new { Category = "Purchases", Sales = 0m, COGS = cogs, GrossProfit = -cogs, Expense = cogs, NetProfit = -cogs, Margin = "", Change = "" },
            new { Category = "Cash + Check Payouts", Sales = 0m, COGS = 0m, GrossProfit = 0m, Expense = cashCheckExpenses, NetProfit = -cashCheckExpenses, Margin = "", Change = "" },
            new { Category = "Payroll", Sales = 0m, COGS = 0m, GrossProfit = 0m, Expense = payroll, NetProfit = -payroll, Margin = "", Change = "" },
            new { Category = "Bank Expenses", Sales = 0m, COGS = 0m, GrossProfit = 0m, Expense = bankExpenses, NetProfit = -bankExpenses, Margin = "", Change = "" },
            new { Category = "Total", Sales = revenue, COGS = cogs, GrossProfit = revenue - cogs, Expense = payoutExpenses, NetProfit = net, Margin = PercentText(margin), Change = "" }
        }.ToList();
        root.Controls.Add(grid, 0, 3);
        root.Controls.Add(new Label { Text = "Bank rows checked in the P&L column are included. Matched rows are excluded to prevent double-counting.", Dock = DockStyle.Fill, ForeColor = WinTheme.Muted, Font = WinTheme.BodyFont(9), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0) }, 0, 4);
        return ModuleShell("\uE9D9", "Profit & Loss", "Analyze sales, expenses, and net profit by period.", root);
    }

    private static void AddPlRow(TableLayoutPanel root, string label, decimal value, bool total = false)
    {
        var row = root.RowCount++;
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, total ? 58 : 44));
        root.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, ForeColor = total ? WinTheme.Copper : Color.White, Font = total ? WinTheme.HeaderFont(13) : WinTheme.BodyFont(11), TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        root.Controls.Add(new Label { Text = value.ToString("C"), Dock = DockStyle.Fill, ForeColor = value < 0 ? WinTheme.Red : WinTheme.Green, Font = total ? WinTheme.HeaderFont(15) : WinTheme.BoldFont(11), TextAlign = ContentAlignment.MiddleRight }, 1, row);
    }

    private Control BuildReports()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = WinTheme.Bg, Padding = new Padding(2) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 205));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 180));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));

        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = WinTheme.Bg };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        root.Controls.Add(top, 0, 0);

        var genShell = WinTheme.BorderedPanel(10);
        genShell.Dock = DockStyle.Fill;
        genShell.Margin = new Padding(4, 6, 8, 6);
        top.Controls.Add(genShell, 0, 0);
        var gen = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 4, BackColor = WinTheme.Panel };
        for (var i = 0; i < 4; i++) gen.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        gen.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        gen.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        gen.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        gen.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        genShell.Controls.Add(gen);
        gen.Controls.Add(new Label { Text = "Generate Report", Dock = DockStyle.Fill, ForeColor = WinTheme.Copper, Font = WinTheme.BoldFont(12), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        gen.SetColumnSpan(gen.GetControlFromPosition(0, 0)!, 4);
        var reportTypes = LicenseRuntime.HasService("Payroll")
            ? new[] { "All", "Shift Log", "Cash On Hand", "Check Payouts", "Sales Summary by Date", "Profit & Loss", "Payroll" }
            : new[] { "All", "Shift Log", "Cash On Hand", "Check Payouts", "Sales Summary by Date", "Profit & Loss" };
        var type = SectionCombo(reportTypes);
        var reportFrom = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var reportTo = DateTime.Today;
        var period = SectionCombo(
            "Today",
            "Yesterday",
            "Current Week",
            "Previous Week",
            "Current Month",
            "Previous Month",
            "Current Year",
            "Previous Year",
            "Custom");
        period.SelectedItem = "Current Month";
        var format = SectionCombo("PDF", "Excel");
        var includeDetails = SectionCombo("Include Details", "Summary Only");
        AddMockField(gen, "Report Type *", type, 0, 1, 120);
        gen.SetColumnSpan(gen.GetControlFromPosition(0, 1)!, 2);
        AddMockField(gen, "Period *", period, 2, 1, 70);
        gen.SetColumnSpan(gen.GetControlFromPosition(2, 1)!, 2);
        AddMockField(gen, "Format", format, 0, 2, 80);
        AddMockField(gen, "Details", includeDetails, 1, 2, 80);
        var generate = WinTheme.Button("Generate Report", true);
        generate.Dock = DockStyle.Fill;
        generate.Margin = new Padding(8, 8, 8, 8);
        gen.Controls.Add(generate, 2, 2);
        gen.SetColumnSpan(generate, 2);
        var print = WinTheme.Button("Print");
        print.Dock = DockStyle.Fill;
        print.Margin = new Padding(8, 6, 8, 6);
        gen.Controls.Add(print, 2, 3);
        var emailReport = WinTheme.Button("Email Report");
        emailReport.Dock = DockStyle.Fill;
        emailReport.Margin = new Padding(8, 6, 8, 6);
        gen.Controls.Add(emailReport, 0, 3);
        gen.SetColumnSpan(emailReport, 2);
        var exportAll = WinTheme.Button("Export All Reports");
        exportAll.Dock = DockStyle.Fill;
        exportAll.Margin = new Padding(8, 6, 8, 6);
        gen.Controls.Add(exportAll, 3, 3);

        var quickShell = WinTheme.BorderedPanel(10);
        quickShell.Dock = DockStyle.Fill;
        quickShell.Margin = new Padding(8, 6, 4, 6);
        top.Controls.Add(quickShell, 1, 0);
        var quick = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, BackColor = WinTheme.Panel };
        quick.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        quick.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        quick.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        quick.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        quick.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        quick.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        quickShell.Controls.Add(quick);
        quick.Controls.Add(new Label { Text = "Quick View", Dock = DockStyle.Fill, ForeColor = WinTheme.Copper, Font = WinTheme.BoldFont(12), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        quick.SetColumnSpan(quick.GetControlFromPosition(0, 0)!, 2);
        var quickButtons = LicenseRuntime.HasService("Payroll")
            ? new[] { "Sales Summary", "Shift Log", "Cash On Hand", "Check Payouts", "Profit & Loss", "Payroll" }
            : new[] { "Sales Summary", "Shift Log", "Cash On Hand", "Check Payouts", "Profit & Loss" };
        for (var i = 0; i < quickButtons.Length; i++)
        {
            var reportName = quickButtons[i];
            var b = WinTheme.Button(reportName);
            b.Dock = DockStyle.Fill;
            b.Margin = new Padding(6);
            b.Click += async (_, _) => await OpenReportViewerAsync(reportName, DateOnly.FromDateTime(reportFrom), DateOnly.FromDateTime(reportTo));
            quick.Controls.Add(b, i % 2, 1 + i / 2);
            if (quickButtons.Length == 5 && i == 4) quick.SetColumnSpan(b, 2);
        }

        var preview = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = WinTheme.Bg };
        preview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));
        preview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        root.Controls.Add(preview, 0, 1);
        var metricShell = MockBorderCard("Report Preview: Sales Summary by Date", "\uE9D9", 38);
        var metricHost = (Panel)metricShell.Parent!;
        metricHost.Margin = new Padding(4, 6, 8, 6);
        var metricGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1, BackColor = WinTheme.Panel };
        for (var i = 0; i < 6; i++) metricGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16.66f));
        metricShell.Controls.Add(metricGrid, 0, 1);
        preview.Controls.Add(metricHost, 0, 0);
        var netSalesLabel = MetricCard(metricGrid, 0, 0, "Net Sales", "$0.00", WinTheme.Green);
        var cogsLabel = MetricCard(metricGrid, 1, 0, "Cost of Goods Sold", "$0.00", WinTheme.Red);
        var grossProfitLabel = MetricCard(metricGrid, 2, 0, "Gross Profit", "$0.00", WinTheme.Green);
        var expensesLabel = MetricCard(metricGrid, 3, 0, "Expenses", "$0.00", WinTheme.Red);
        var netProfitLabel = MetricCard(metricGrid, 4, 0, "Net Profit", "$0.00", WinTheme.Green);
        var txLabel = MetricCard(metricGrid, 5, 0, "Transactions", "0", WinTheme.Text);

        var chartShell = MockBorderCard("Net Sales Trend", "\uE9D9", 38);
        var chartHost = (Panel)chartShell.Parent!;
        chartHost.Margin = new Padding(8, 6, 4, 6);
        chartShell.Controls.Add(new Label { Text = "Trend preview uses report date range.", Dock = DockStyle.Fill, ForeColor = WinTheme.Muted, Font = WinTheme.BodyFont(11), TextAlign = ContentAlignment.MiddleCenter }, 0, 1);
        preview.Controls.Add(chartHost, 1, 0);

        var grid = WinTheme.Grid();
        root.Controls.Add(grid, 0, 2);
        root.Controls.Add(BuildGridFooter("Showing generated report history"), 0, 3);

        void previewReport()
        {
            using var db = CreateDb();
            var shifts = db.ShiftLogs.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId && x.Date >= DateOnly.FromDateTime(reportFrom) && x.Date <= DateOnly.FromDateTime(reportTo))
                .OrderBy(x => x.Date)
                .ToList();
            var purchases = db.PurchaseInvoices.AsNoTracking().Where(x => x.StoreId == _currentStoreId).ToList()
                .Where(x => x.InvoiceDate >= DateOnly.FromDateTime(reportFrom) && x.InvoiceDate <= DateOnly.FromDateTime(reportTo)).ToList();
            var cash = db.CashOnHand.AsNoTracking().Where(x => x.StoreId == _currentStoreId).ToList()
                .Where(x => x.Date >= DateOnly.FromDateTime(reportFrom) && x.Date <= DateOnly.FromDateTime(reportTo)).ToList();
            var checks = db.CheckPayouts.AsNoTracking().Where(x => x.StoreId == _currentStoreId).ToList()
                .Where(x => x.Date >= DateOnly.FromDateTime(reportFrom) && x.Date <= DateOnly.FromDateTime(reportTo)).ToList();
            var payroll = db.PayrollEntries.AsNoTracking()
                .Where(x => x.PayrollRun!.StoreId == _currentStoreId && x.PayrollRun.Status == PayrollRunStatus.Finalized && x.PayrollRun.PayDate >= DateOnly.FromDateTime(reportFrom) && x.PayrollRun.PayDate <= DateOnly.FromDateTime(reportTo))
                .Sum(x => (decimal?)x.GrossPay).GetValueOrDefault();
            var netSales = shifts.Sum(x => x.NetSales);
            var cogs = purchases.Sum(x => x.Total);
            var expenses = cash.Sum(x => x.PayoutAmount) + checks.Sum(x => x.CheckAmount) + payroll;
            var profit = netSales - cogs - expenses;
            netSalesLabel.Text = MoneyText(netSales);
            cogsLabel.Text = MoneyText(cogs);
            grossProfitLabel.Text = MoneyText(netSales - cogs);
            expensesLabel.Text = MoneyText(expenses);
            netProfitLabel.Text = MoneyText(profit);
            netProfitLabel.ForeColor = profit >= 0 ? WinTheme.Green : WinTheme.Red;
            txLabel.Text = shifts.Count.ToString(CultureInfo.InvariantCulture);
            var reportHistory = new[]
            {
                new { ReportName = "Sales Summary by Date", DateRange = $"{reportFrom:M/d/yyyy} - {reportTo:M/d/yyyy}", Format = format.Text, GeneratedBy = _session.DisplayName, GeneratedOn = DateTime.Now.ToString("M/d/yyyy h:mm tt"), FileStatus = "Ready", Actions = "Preview / Save / Print" },
                new { ReportName = "Shift Log", DateRange = $"{reportFrom:M/d/yyyy} - {reportTo:M/d/yyyy}", Format = format.Text, GeneratedBy = _session.DisplayName, GeneratedOn = DateTime.Now.ToString("M/d/yyyy h:mm tt"), FileStatus = "Ready", Actions = "Preview / Save / Print" },
                new { ReportName = "Cash On Hand Report", DateRange = $"{reportFrom:M/d/yyyy} - {reportTo:M/d/yyyy}", Format = format.Text, GeneratedBy = _session.DisplayName, GeneratedOn = DateTime.Now.ToString("M/d/yyyy h:mm tt"), FileStatus = "Ready", Actions = "Preview / Save / Print" },
                new { ReportName = "Check Payouts", DateRange = $"{reportFrom:M/d/yyyy} - {reportTo:M/d/yyyy}", Format = format.Text, GeneratedBy = _session.DisplayName, GeneratedOn = DateTime.Now.ToString("M/d/yyyy h:mm tt"), FileStatus = "Ready", Actions = "Preview / Save / Print" },
                new { ReportName = "Profit & Loss Statement", DateRange = $"{reportFrom:M/d/yyyy} - {reportTo:M/d/yyyy}", Format = format.Text, GeneratedBy = _session.DisplayName, GeneratedOn = DateTime.Now.ToString("M/d/yyyy h:mm tt"), FileStatus = "Ready", Actions = "Preview / Save / Print" }
            }.ToList();
            if (LicenseRuntime.HasService("Payroll"))
                reportHistory.Add(new { ReportName = "Payroll", DateRange = $"{reportFrom:M/d/yyyy} - {reportTo:M/d/yyyy}", Format = format.Text, GeneratedBy = _session.DisplayName, GeneratedOn = DateTime.Now.ToString("M/d/yyyy h:mm tt"), FileStatus = "Ready", Actions = "Preview / Save / Print" });
            grid.DataSource = reportHistory;
        }

        var previousPeriod = "Current Month";
        var changingPeriod = false;
        period.SelectedIndexChanged += (_, _) =>
        {
            if (changingPeriod)
                return;

            var today = DateTime.Today;
            var firstDayOfWeek = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
            var daysFromWeekStart = (7 + (int)today.DayOfWeek - (int)firstDayOfWeek) % 7;
            var currentWeekStart = today.AddDays(-daysFromWeekStart);

            switch (period.Text)
            {
                case "Today":
                    reportFrom = reportTo = today;
                    break;
                case "Yesterday":
                    reportFrom = reportTo = today.AddDays(-1);
                    break;
                case "Current Week":
                    reportFrom = currentWeekStart;
                    reportTo = today;
                    break;
                case "Previous Week":
                    reportFrom = currentWeekStart.AddDays(-7);
                    reportTo = currentWeekStart.AddDays(-1);
                    break;
                case "Current Month":
                    reportFrom = new DateTime(today.Year, today.Month, 1);
                    reportTo = today;
                    break;
                case "Previous Month":
                    reportTo = new DateTime(today.Year, today.Month, 1).AddDays(-1);
                    reportFrom = new DateTime(reportTo.Year, reportTo.Month, 1);
                    break;
                case "Current Year":
                    reportFrom = new DateTime(today.Year, 1, 1);
                    reportTo = today;
                    break;
                case "Previous Year":
                    reportFrom = new DateTime(today.Year - 1, 1, 1);
                    reportTo = new DateTime(today.Year - 1, 12, 31);
                    break;
                case "Custom":
                    if (!TrySelectCustomReportPeriod(reportFrom, reportTo, out var customFrom, out var customTo))
                    {
                        changingPeriod = true;
                        period.SelectedItem = previousPeriod;
                        changingPeriod = false;
                        return;
                    }
                    reportFrom = customFrom;
                    reportTo = customTo;
                    break;
            }

            previousPeriod = period.Text;
            previewReport();
        };

        generate.Click += async (_, _) =>
        {
            await OpenReportViewerAsync(type.Text, DateOnly.FromDateTime(reportFrom), DateOnly.FromDateTime(reportTo));
            previewReport();
        };
        print.Click += async (_, _) =>
        {
            await GenerateSelectedReportAsync(type.Text, DateOnly.FromDateTime(reportFrom), DateOnly.FromDateTime(reportTo), printAfter: true);
            previewReport();
        };
        emailReport.Click += async (_, _) =>
        {
            await OpenReportViewerAsync(type.Text, DateOnly.FromDateTime(reportFrom), DateOnly.FromDateTime(reportTo));
            previewReport();
        };
        exportAll.Click += async (_, _) =>
        {
            await ExportAllReportsToFolderAsync(DateOnly.FromDateTime(reportFrom), DateOnly.FromDateTime(reportTo));
            previewReport();
        };
        grid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= grid.Rows.Count)
                return;

            var reportName = Convert.ToString(grid.Rows[e.RowIndex].Cells["ReportName"].Value, CultureInfo.CurrentCulture);
            if (string.IsNullOrWhiteSpace(reportName))
                return;

            await OpenReportViewerAsync(reportName, DateOnly.FromDateTime(reportFrom), DateOnly.FromDateTime(reportTo));
            previewReport();
        };
        previewReport();
        return ModuleShell("\uE749", "Reports", "Generate, export, and review store reports.", root);
    }

    private bool TrySelectCustomReportPeriod(DateTime currentFrom, DateTime currentTo, out DateTime selectedFrom, out DateTime selectedTo)
    {
        using var dialog = new Form
        {
            Text = "Select Custom Report Period",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ClientSize = new Size(590, 360),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            BackColor = WinTheme.Bg
        };
        dialog.Icon = WinTheme.TryLoadIcon();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Bg,
            Padding = new Padding(18),
            ColumnCount = 2,
            RowCount = 3
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        dialog.Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Text = "FROM DATE",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Copper,
            Font = WinTheme.BoldFont(10),
            TextAlign = ContentAlignment.MiddleCenter
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "TO DATE",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Copper,
            Font = WinTheme.BoldFont(10),
            TextAlign = ContentAlignment.MiddleCenter
        }, 1, 0);

        var fromCalendar = new MonthCalendar
        {
            MaxSelectionCount = 1,
            SelectionStart = currentFrom,
            SelectionEnd = currentFrom,
            ShowToday = true,
            ShowTodayCircle = true,
            Anchor = AnchorStyles.None
        };
        var toCalendar = new MonthCalendar
        {
            MaxSelectionCount = 1,
            SelectionStart = currentTo,
            SelectionEnd = currentTo,
            ShowToday = true,
            ShowTodayCircle = true,
            Anchor = AnchorStyles.None
        };
        layout.Controls.Add(fromCalendar, 0, 1);
        layout.Controls.Add(toCalendar, 1, 1);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = WinTheme.Bg,
            Padding = new Padding(0, 8, 0, 0)
        };
        layout.Controls.Add(actions, 0, 2);
        layout.SetColumnSpan(actions, 2);

        var apply = WinTheme.Button("Use This Period", true);
        apply.Width = 170;
        apply.DialogResult = DialogResult.OK;
        var cancel = WinTheme.Button("Cancel");
        cancel.Width = 120;
        cancel.DialogResult = DialogResult.Cancel;
        actions.Controls.Add(apply);
        actions.Controls.Add(cancel);
        dialog.AcceptButton = apply;
        dialog.CancelButton = cancel;

        while (dialog.ShowDialog(this) == DialogResult.OK)
        {
            selectedFrom = fromCalendar.SelectionStart.Date;
            selectedTo = toCalendar.SelectionStart.Date;
            if (selectedTo >= selectedFrom)
                return true;

            MessageBox.Show(dialog, "To date must be on or after From date.", "Custom Report Period", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        selectedFrom = currentFrom;
        selectedTo = currentTo;
        return false;
    }

    private async Task OpenReportViewerAsync(string reportName, DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            MessageBox.Show(this, "To date must be on or after From date.", "Reports", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var recipient = await ResolveSavedReportRecipientAsync();
        using var viewer = new ReportViewerForm(
            $"{reportName} - {from:M/d/yyyy} to {to:M/d/yyyy}",
            outputPath => SaveReportPdfAsync(reportName, from, to, outputPath),
            (outputPath, email) => EmailReportPdfAsync(reportName, from, to, outputPath, email),
            recipient);
        viewer.ShowDialog(this);
    }

    private async Task<string> ResolveSavedReportRecipientAsync()
    {
        await using var db = CreateDb();
        var currentUserEmail = await db.Users.AsNoTracking()
            .Where(user => user.Id == _session.UserId && user.IsActive)
            .Select(user => user.Email)
            .FirstOrDefaultAsync();
        if (!string.IsNullOrWhiteSpace(currentUserEmail))
            return currentUserEmail.Trim();

        return (await db.Users.AsNoTracking()
                .Where(user => user.IsActive && user.Role == UserRole.OwnerAdmin && user.Email != "")
                .OrderBy(user => user.Id)
                .Select(user => user.Email)
                .FirstOrDefaultAsync())
            ?.Trim() ?? "";
    }

    private async Task EmailReportPdfAsync(
        string reportName,
        DateOnly from,
        DateOnly to,
        string pdfPath,
        string recipient)
    {
        if (!File.Exists(pdfPath))
            throw new InvalidOperationException("The report PDF could not be found.");
        var bytes = await File.ReadAllBytesAsync(pdfPath);
        using var client = new LiveBankSyncClient();
        await client.EmailReportAsync(
            recipient,
            reportName,
            $"{from:M/d/yyyy} to {to:M/d/yyyy}",
            Path.GetFileName(pdfPath),
            bytes);
    }

    private async Task SaveReportPdfAsync(string reportName, DateOnly from, DateOnly to, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Path.GetTempPath());
        var selectedType = ResolveReportType(reportName);
        if (selectedType == ReportType.All)
        {
            await _reportService.GenerateAllReportsBundlePdfAsync(from, to, outputPath);
            return;
        }

        await GenerateReportPdfAsync(selectedType, from, to, outputPath);
    }

    private async Task GenerateSelectedReportAsync(string reportName, DateOnly from, DateOnly to, bool printAfter)
    {
        if (to < from)
        {
            MessageBox.Show(this, "To date must be on or after From date.", "Reports", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var selectedType = ResolveReportType(reportName);
        if (selectedType == ReportType.All && !printAfter)
        {
            await ExportAllReportsToFolderAsync(from, to);
            return;
        }

        var outputPath = printAfter
            ? Path.Combine(Path.GetTempPath(), "HisabKitab_Reports", $"HisabKitab_{SafeReportFilePart(reportName)}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf")
            : "";

        if (!printAfter)
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Save Report PDF",
                Filter = "PDF files (*.pdf)|*.pdf",
                FileName = $"HisabKitab_{SafeReportFilePart(reportName)}_{DateTime.Now:yyyyMMdd_HHmm}.pdf"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            outputPath = dialog.FileName;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Path.GetTempPath());
        await GenerateReportPdfAsync(selectedType, from, to, outputPath);

        if (printAfter)
            PrintGeneratedPdf(outputPath);
        else
        {
            OpenGeneratedFile(outputPath);
            MessageBox.Show(this, $"Report saved:\n{outputPath}", "Reports", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private Task QuickViewReportAsync(string reportName)
    {
        var from = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        var to = DateOnly.FromDateTime(DateTime.Today);
        return OpenReportViewerAsync(reportName, from, to);
    }

    private static ReportType ResolveReportType(string? reportName)
    {
        var normalized = Regex.Replace(reportName ?? "", @"[^A-Za-z0-9]+", "").ToLowerInvariant();
        return normalized switch
        {
            "all" => ReportType.All,
            "cashonhand" or "cashonhandreport" => ReportType.CashOnHand,
            "checkpayout" or "checkpayouts" => ReportType.CheckPayouts,
            "salessummary" or "salessummarybydate" => ReportType.SalesSummaryByDate,
            "profitloss" or "profitandloss" or "profitlossstatement" or "profitandlossstatement" => ReportType.ProfitLoss,
            "payroll" or "payrollreport" => ReportType.Payroll,
            _ => ReportType.ShiftLog
        };
    }

    private static string SafeReportFilePart(string value)
    {
        var safe = Regex.Replace(value ?? "Report", @"[^A-Za-z0-9]+", "");
        return string.IsNullOrWhiteSpace(safe) ? "Report" : safe;
    }

    private async Task GenerateProfitLossPdfAsync()
    {
        var from = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        var to = DateOnly.FromDateTime(DateTime.Today);
        using var dialog = new SaveFileDialog
        {
            Title = "Save Profit & Loss PDF",
            Filter = "PDF files (*.pdf)|*.pdf",
            FileName = $"HisabKitab_ProfitLoss_{DateTime.Now:yyyyMMdd_HHmm}.pdf"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        await _reportService.GenerateProfitLossPdfAsync(from, to, dialog.FileName);
        MessageBox.Show(this, $"Report saved:\n{dialog.FileName}", "Profit & Loss", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private Task GenerateReportPdfAsync(ReportType type, DateOnly from, DateOnly to, string path)
    {
        if (type == ReportType.All)
            return _reportService.GenerateAllReportsBundlePdfAsync(from, to, path);

        return type switch
        {
            ReportType.CashOnHand => _reportService.GenerateCashOnHandPdfAsync(from, to, path),
            ReportType.CheckPayouts => _reportService.GenerateCheckPayoutsPdfAsync(from, to, path),
            ReportType.SalesSummaryByDate => _reportService.GenerateSalesSummaryByDatePdfAsync(from, to, path),
            ReportType.ProfitLoss => _reportService.GenerateProfitLossPdfAsync(from, to, path),
            ReportType.Payroll => _reportService.GeneratePayrollPdfAsync(from, to, path),
            _ => _reportService.GenerateShiftLogPdfAsync(from, to, path)
        };
    }

    private async Task<IReadOnlyList<string>> GenerateAllReportsAsync(DateOnly from, DateOnly to, string selectedPath)
    {
        var folder = Path.GetDirectoryName(selectedPath);
        if (string.IsNullOrWhiteSpace(folder))
            folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture);
        var bundle = selectedPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            ? selectedPath
            : Path.Combine(folder, $"HisabKitab_AllReportsBundle_{stamp}.pdf");

        await _reportService.GenerateAllReportsBundlePdfAsync(from, to, bundle);
        return new[] { bundle };
    }

    private async Task ExportAllReportsToFolderAsync(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            MessageBox.Show(this, "To date must be on or after From date.", "Reports", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var folder = new FolderBrowserDialog
        {
            Description = "Select a folder for the report bundle",
            UseDescriptionForTitle = true
        };
        if (folder.ShowDialog(this) != DialogResult.OK)
            return;

        var marker = Path.Combine(folder.SelectedPath, $"HisabKitab_AllReports_{DateTime.Now:yyyyMMdd_HHmm}.pdf");
        var files = await GenerateAllReportsAsync(from, to, marker);
        OpenGeneratedFile(folder.SelectedPath);
        MessageBox.Show(this, $"Report bundle saved:\n{folder.SelectedPath}\n\nFiles created: {files.Count}", "Reports", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void PrintGeneratedPdf(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "print"
            });
        }
        catch
        {
            OpenGeneratedFile(path);
            MessageBox.Show(this, "Windows could not send the PDF directly to the printer, so the report was opened for printing.", "Reports", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private static void OpenGeneratedFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (!File.Exists(path) && !Directory.Exists(path))
            return;

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void UploadPosReport(DateTimePicker date, TextBox employee, TextBox shift, TextBox cash, TextBox card, TextBox net, TextBox tax, TextBox drop, TextBox status)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select POS Report",
            Filter = "POS Reports (*.xlsx;*.pdf)|*.xlsx;*.pdf|Excel (*.xlsx)|*.xlsx|PDF (*.pdf)|*.pdf",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            var data = _posImporter.Import(dialog.FileName);
            if (data.ReportDate.HasValue)
                date.Value = data.ReportDate.Value.ToDateTime(TimeOnly.MinValue);
            if (!string.IsNullOrWhiteSpace(data.Employee))
                employee.Text = data.Employee;
            if (!string.IsNullOrWhiteSpace(data.ShiftOrBatch))
                shift.Text = data.ShiftOrBatch;

            cash.Text = data.CashTotal.ToString("0.00", CultureInfo.CurrentCulture);
            card.Text = data.CardTotal.ToString("0.00", CultureInfo.CurrentCulture);
            net.Text = data.NetSales.ToString("0.00", CultureInfo.CurrentCulture);
            tax.Text = data.TaxTotal.ToString("0.00", CultureInfo.CurrentCulture);
            status.Text = $"Imported: {data.DetectedType}";
            status.ForeColor = WinTheme.Green;
            drop.Focus();
            drop.SelectAll();
        }
        catch (Exception ex)
        {
            status.Text = AppBootstrap.RedactSensitiveText(ex.Message);
            status.ForeColor = WinTheme.Red;
        }
    }

    private void OpenSelectedPurchaseFile(DataGridView grid)
    {
        var id = SelectedId(grid);
        if (id is null)
        {
            MessageBox.Show(this, "Select a purchase invoice first.", "Purchases", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var db = CreateDb();
        var invoice = db.PurchaseInvoices.AsNoTracking().FirstOrDefault(x => x.Id == id.Value && x.StoreId == _currentStoreId);
        if (invoice is null || string.IsNullOrWhiteSpace(invoice.FilePath) || !File.Exists(invoice.FilePath))
        {
            MessageBox.Show(this, "The selected invoice does not have an attached file.", "Purchases", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Process.Start(new ProcessStartInfo(invoice.FilePath) { UseShellExecute = true });
    }

    private async Task UpdateBankTransactionAsync(int id, string? category, string? checkNumber)
    {
        await EnsureBankStatementTablesAsync();
        await using var conn = CreateBankConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();

        var assignments = new List<string>();
        if (!string.IsNullOrWhiteSpace(category)) assignments.Add("Category=@cat");
        if (checkNumber is not null) assignments.Add("CheckNumber=@chk");
        if (assignments.Count == 0) return;

        cmd.CommandText = $"UPDATE BankStatementTransactions SET {string.Join(", ", assignments)} WHERE Id=@id AND StoreId=@sid";
        AddParam(cmd, "@id", id);
        AddParam(cmd, "@sid", _currentStoreId);
        if (!string.IsNullOrWhiteSpace(category)) AddParam(cmd, "@cat", category);
        if (checkNumber is not null) AddParam(cmd, "@chk", string.IsNullOrWhiteSpace(checkNumber) ? DBNull.Value : checkNumber.Trim());
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task RecategorizeBankStatementAsync(int month, int year)
    {
        await EnsureBankStatementTablesAsync();
        await using var conn = CreateBankConnection();
        await conn.OpenAsync();
        var rows = await LoadBankStatementRowsAsync(month, year);
        foreach (var row in rows)
        {
            var category = CategorizeBankTransaction(row.Description, row.CheckNumber);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE BankStatementTransactions SET Category=@cat WHERE Id=@id AND StoreId=@sid";
            AddParam(cmd, "@cat", category);
            AddParam(cmd, "@id", row.Id);
            AddParam(cmd, "@sid", _currentStoreId);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private string? PromptText(string title, string message, string value = "")
    {
        using var form = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(460, 150),
            BackColor = WinTheme.Bg
        };
        var label = new Label
        {
            Text = message,
            Left = 14,
            Top = 14,
            Width = 430,
            Height = 28,
            ForeColor = WinTheme.Text,
            Font = WinTheme.BodyFont(10)
        };
        var text = WinTheme.TextBox();
        text.Left = 14;
        text.Top = 48;
        text.Width = 430;
        text.Text = value;
        var ok = WinTheme.Button("OK", true);
        ok.Left = 238;
        ok.Top = 96;
        ok.Width = 95;
        ok.DialogResult = DialogResult.OK;
        var cancel = WinTheme.Button("Cancel");
        cancel.Left = 348;
        cancel.Top = 96;
        cancel.Width = 95;
        cancel.DialogResult = DialogResult.Cancel;
        form.Controls.Add(label);
        form.Controls.Add(text);
        form.Controls.Add(ok);
        form.Controls.Add(cancel);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(this) == DialogResult.OK ? text.Text.Trim() : null;
    }

    private async Task ImportPurchaseInvoiceAsync(Func<Task> refreshAsync)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import Purchase Invoice",
            Filter = "Invoice files (*.pdf;*.xlsx;*.xls;*.csv)|*.pdf;*.xlsx;*.xls;*.csv|All files (*.*)|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var result = await _invoiceImportService.ImportAsync(dialog.FileName);
        if (!result.Success)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, result.Warnings.DefaultIfEmpty("Invoice import failed.")), "Import Invoice", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var imported = result.Invoices.Count > 0
            ? result.Invoices
            : new List<ImportedInvoice>
            {
                new()
                {
                    VendorName = result.VendorName,
                    InvoiceNumber = result.InvoiceNumber,
                    InvoiceDate = result.InvoiceDate,
                    Total = result.Total,
                    Lines = result.Lines
                }
            };

        if (!ConfirmPurchaseInvoiceImport(result, imported, dialog.FileName))
            return;

        var count = 0;
        foreach (var invoice in imported)
        {
            var lines = invoice.Lines ?? new List<PurchaseInvoiceLine>();
            var total = invoice.Total ?? (lines.Count > 0 ? lines.Sum(x => x.Quantity * x.UnitCost) : 0m);
            await _purchaseService.AddInvoiceAsync(
                _currentStoreId,
                invoice.InvoiceDate ?? DateOnly.FromDateTime(DateTime.Today),
                null,
                string.IsNullOrWhiteSpace(invoice.VendorName) ? result.VendorName : invoice.VendorName,
                string.IsNullOrWhiteSpace(invoice.InvoiceNumber) ? result.InvoiceNumber : invoice.InvoiceNumber,
                total,
                string.Join("; ", result.Warnings),
                dialog.FileName,
                lines,
                _session.UserId,
                _session.DisplayName);
            count++;
        }

        await refreshAsync();
        MessageBox.Show(this, $"Imported {count} invoice(s).", "Import Invoice", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private bool ConfirmPurchaseInvoiceImport(
        InvoiceImportResult result,
        IReadOnlyCollection<ImportedInvoice> invoices,
        string sourceFile)
    {
        var invalid = invoices
            .Where(invoice =>
                string.IsNullOrWhiteSpace(invoice.VendorName)
                || invoice.VendorName.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
                || invoice.InvoiceDate is null
                || invoice.Total is null or <= 0m
                || invoice.Lines.Count == 0)
            .ToList();

        if (invalid.Count > 0)
        {
            MessageBox.Show(
                this,
                "This invoice was not saved because one or more required values could not be verified.\n\n" +
                "Required: vendor, invoice number, invoice date, positive invoice total, and at least one line item.\n" +
                "Please review the original PDF or use a supported vendor template.",
                "Invoice Requires Review",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        var summary = new StringBuilder();
        summary.AppendLine("Review the extracted invoice information before it is saved:");
        summary.AppendLine();

        foreach (var invoice in invoices.Take(10))
        {
            summary.AppendLine($"{invoice.VendorName}  |  Invoice {invoice.InvoiceNumber}");
            summary.AppendLine(
                $"Date: {invoice.InvoiceDate:MM/dd/yyyy}  |  Rows: {invoice.Lines.Count}  |  Total: {invoice.Total:C}");
            summary.AppendLine();
        }

        if (invoices.Count > 10)
            summary.AppendLine($"+ {invoices.Count - 10} additional invoice(s)");

        if (result.Warnings.Count > 0)
        {
            summary.AppendLine("Parser notes:");
            foreach (var warning in result.Warnings.Take(5))
                summary.AppendLine($"• {warning}");
            summary.AppendLine();
        }

        summary.AppendLine($"Source: {Path.GetFileName(sourceFile)}");
        summary.AppendLine();
        summary.Append("Does this match the PDF?");

        return MessageBox.Show(
                   this,
                   summary.ToString(),
                   "Confirm Invoice Import",
                   MessageBoxButtons.YesNo,
                   MessageBoxIcon.Question,
                   MessageBoxDefaultButton.Button2)
               == DialogResult.Yes;
    }

    private async Task ImportProductCostsAsync(Func<Task> refreshAsync)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Upload Invoice For Product Costs",
            Filter = "Invoice files (*.pdf;*.xlsx;*.xls;*.csv)|*.pdf;*.xlsx;*.xls;*.csv|All files (*.*)|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var result = await _invoiceImportService.ImportCostsOnlyAsync(dialog.FileName);
        if (!result.Success || result.Lines.Count == 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, result.Warnings.DefaultIfEmpty("No product costs were found in this file.")), "Product Costs", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!ConfirmProductCostImport(result, dialog.FileName))
            return;

        var vendorName = string.IsNullOrWhiteSpace(result.VendorName) ? Path.GetFileNameWithoutExtension(dialog.FileName) : result.VendorName;
        var invoiceNumber = string.IsNullOrWhiteSpace(result.InvoiceNumber) ? $"IMPORT-{DateTime.Now:yyyyMMdd-HHmm}" : result.InvoiceNumber;
        var invoiceDate = result.InvoiceDate ?? DateOnly.FromDateTime(DateTime.Today);
        var (upserts, alerts) = await _purchaseService.ImportProductCostsAsync(_currentStoreId, vendorName, invoiceNumber, invoiceDate, result.Lines);
        await refreshAsync();
        MessageBox.Show(this, $"Updated {upserts} product cost(s).\nCreated {alerts} price alert(s).", "Product Costs", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private bool ConfirmProductCostImport(InvoiceImportResult result, string sourceFile)
    {
        if (result.Lines.Any(line => string.IsNullOrWhiteSpace(line.ProductName) || line.UnitCost <= 0m))
        {
            MessageBox.Show(
                this,
                "Product costs were not updated because one or more rows are missing a product description or positive unit cost.",
                "Invoice Requires Review",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        var summary = new StringBuilder();
        summary.AppendLine("Review these extracted product costs before updating price history:");
        summary.AppendLine();
        summary.AppendLine($"Vendor: {result.VendorName}");
        summary.AppendLine($"Invoice: {result.InvoiceNumber}");
        summary.AppendLine($"Rows: {result.Lines.Count}");
        summary.AppendLine();

        foreach (var line in result.Lines.Take(6))
        {
            var code = string.IsNullOrWhiteSpace(line.ItemCode) ? "(no SKU)" : line.ItemCode;
            var name = line.ProductName.Length > 48 ? $"{line.ProductName[..48]}…" : line.ProductName;
            summary.AppendLine($"{code}  |  {name}  |  {line.UnitCost:C}");
        }

        if (result.Lines.Count > 6)
            summary.AppendLine($"+ {result.Lines.Count - 6} additional product(s)");

        summary.AppendLine();
        summary.AppendLine($"Source: {Path.GetFileName(sourceFile)}");
        summary.AppendLine();
        summary.Append("Update product costs and create price alerts?");

        return MessageBox.Show(
                   this,
                   summary.ToString(),
                   "Confirm Product Cost Import",
                   MessageBoxButtons.YesNo,
                   MessageBoxIcon.Question,
                   MessageBoxDefaultButton.Button2)
               == DialogResult.Yes;
    }

    private async Task PrintSelectedCheckAsync(DataGridView grid)
    {
        var id = SelectedId(grid);
        if (id is null)
        {
            MessageBox.Show(this, "Select a check payout first.", "Print Check", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var db = CreateDb();
        var check = await db.CheckPayouts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id.Value && x.StoreId == _currentStoreId);
        if (check is null)
            return;

        try
        {
            _checkPrintService.PrintCheck(new CheckPrintRequest
            {
                Date = check.Date.ToDateTime(TimeOnly.MinValue),
                PayeeName = check.VendorName,
                Amount = check.CheckAmount,
                Memo = check.Description,
                Reference = check.CheckNumber
            });
            MessageBox.Show(this, "Check sent to printer.", "Print Check", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, AppBootstrap.RedactSensitiveText(ex.Message), "Print Check", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string CurrentStoreConnectionString() => _storeConnections.GetCurrentConnectionString();

    private static bool LooksLikeSqlServer(string connectionString)
        => connectionString.Contains("Server=", StringComparison.OrdinalIgnoreCase)
           || connectionString.Contains("Initial Catalog=", StringComparison.OrdinalIgnoreCase)
           || connectionString.Contains("Database=", StringComparison.OrdinalIgnoreCase) && !connectionString.Contains(".db", StringComparison.OrdinalIgnoreCase);

    private DbConnection CreateBankConnection()
    {
        var connectionString = CurrentStoreConnectionString();
        return LooksLikeSqlServer(connectionString)
            ? new SqlConnection(connectionString)
            : new SqliteConnection(connectionString);
    }

    private static void AddParam(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private async Task EnsureBankStatementTablesAsync()
    {
        await using var conn = CreateBankConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        if (conn is SqlConnection)
        {
            cmd.CommandText = @"IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'BankStatementTransactions')
CREATE TABLE [dbo].[BankStatementTransactions] (
    [Id] INT IDENTITY(1,1) PRIMARY KEY,
    [StoreId] INT NOT NULL DEFAULT 1,
    [Date] DATE NOT NULL,
    [Description] NVARCHAR(500) NOT NULL DEFAULT '',
    [Credit] DECIMAL(18,2) NOT NULL DEFAULT 0,
    [Debit] DECIMAL(18,2) NOT NULL DEFAULT 0,
    [CheckNumber] NVARCHAR(20) NULL,
    [Category] NVARCHAR(50) NOT NULL DEFAULT 'Other',
    [ExternalTransactionId] NVARCHAR(200) NULL,
    [Source] NVARCHAR(30) NOT NULL DEFAULT 'Statement Import',
    [IsMatched] BIT NOT NULL DEFAULT 0,
    [MatchReference] NVARCHAR(200) NOT NULL DEFAULT '',
    [IncludeInProfitLoss] BIT NOT NULL DEFAULT 0,
    [ImportedUtc] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [CreatedByName] NVARCHAR(100) NOT NULL DEFAULT ''
);
IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'BankConnections')
CREATE TABLE [dbo].[BankConnections] (
    [Id] INT IDENTITY(1,1) PRIMARY KEY,
    [StoreId] INT NOT NULL,
    [ConnectionId] NVARCHAR(200) NOT NULL,
    [Provider] NVARCHAR(50) NOT NULL DEFAULT '',
    [InstitutionName] NVARCHAR(200) NOT NULL DEFAULT '',
    [AccountName] NVARCHAR(200) NOT NULL DEFAULT '',
    [AccountMask] NVARCHAR(20) NOT NULL DEFAULT '',
    [Status] NVARCHAR(40) NOT NULL DEFAULT '',
    [LastSyncedUtc] DATETIME2 NULL,
    [LastError] NVARCHAR(500) NOT NULL DEFAULT '',
    [UpdatedUtc] DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);";
        }
        else
        {
            cmd.CommandText = @"CREATE TABLE IF NOT EXISTS BankStatementTransactions (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    StoreId INTEGER NOT NULL DEFAULT 1,
    Date TEXT NOT NULL,
    Description TEXT NOT NULL DEFAULT '',
    Credit REAL NOT NULL DEFAULT 0,
    Debit REAL NOT NULL DEFAULT 0,
    CheckNumber TEXT,
    Category TEXT NOT NULL DEFAULT 'Other',
    ExternalTransactionId TEXT,
    Source TEXT NOT NULL DEFAULT 'Statement Import',
    IsMatched INTEGER NOT NULL DEFAULT 0,
    MatchReference TEXT NOT NULL DEFAULT '',
    IncludeInProfitLoss INTEGER NOT NULL DEFAULT 0,
    ImportedUtc TEXT NOT NULL DEFAULT (datetime('now')),
    CreatedByName TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS BankConnections (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    StoreId INTEGER NOT NULL,
    ConnectionId TEXT NOT NULL,
    Provider TEXT NOT NULL DEFAULT '',
    InstitutionName TEXT NOT NULL DEFAULT '',
    AccountName TEXT NOT NULL DEFAULT '',
    AccountMask TEXT NOT NULL DEFAULT '',
    Status TEXT NOT NULL DEFAULT '',
    LastSyncedUtc TEXT,
    LastError TEXT NOT NULL DEFAULT '',
    UpdatedUtc TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(StoreId, ConnectionId)
);";
        }
        await cmd.ExecuteNonQueryAsync();

        await EnsureBankStatementSchemaAsync(conn);
    }

    private async Task EnsureBankStatementSchemaAsync(DbConnection conn)
    {
        if (conn is SqlConnection)
        {
            await ExecuteBankSchemaCommandAsync(conn, @"
IF COL_LENGTH('dbo.BankStatementTransactions', 'StoreId') IS NULL
    ALTER TABLE [dbo].[BankStatementTransactions] ADD [StoreId] INT NOT NULL CONSTRAINT DF_BankStatementTransactions_StoreId DEFAULT 1;
IF COL_LENGTH('dbo.BankStatementTransactions', 'CheckNumber') IS NULL
    ALTER TABLE [dbo].[BankStatementTransactions] ADD [CheckNumber] NVARCHAR(20) NULL;
IF COL_LENGTH('dbo.BankStatementTransactions', 'Category') IS NULL
    ALTER TABLE [dbo].[BankStatementTransactions] ADD [Category] NVARCHAR(50) NOT NULL CONSTRAINT DF_BankStatementTransactions_Category DEFAULT 'Other';
IF COL_LENGTH('dbo.BankStatementTransactions', 'ImportedUtc') IS NULL
    ALTER TABLE [dbo].[BankStatementTransactions] ADD [ImportedUtc] DATETIME2 NOT NULL CONSTRAINT DF_BankStatementTransactions_ImportedUtc DEFAULT GETUTCDATE();
IF COL_LENGTH('dbo.BankStatementTransactions', 'CreatedByName') IS NULL
    ALTER TABLE [dbo].[BankStatementTransactions] ADD [CreatedByName] NVARCHAR(100) NOT NULL CONSTRAINT DF_BankStatementTransactions_CreatedByName DEFAULT '';
IF COL_LENGTH('dbo.BankStatementTransactions', 'ExternalTransactionId') IS NULL
    ALTER TABLE [dbo].[BankStatementTransactions] ADD [ExternalTransactionId] NVARCHAR(200) NULL;
IF COL_LENGTH('dbo.BankStatementTransactions', 'Source') IS NULL
    ALTER TABLE [dbo].[BankStatementTransactions] ADD [Source] NVARCHAR(30) NOT NULL CONSTRAINT DF_BankStatementTransactions_Source DEFAULT 'Statement Import';
IF COL_LENGTH('dbo.BankStatementTransactions', 'IsMatched') IS NULL
    ALTER TABLE [dbo].[BankStatementTransactions] ADD [IsMatched] BIT NOT NULL CONSTRAINT DF_BankStatementTransactions_IsMatched DEFAULT 0;
IF COL_LENGTH('dbo.BankStatementTransactions', 'MatchReference') IS NULL
    ALTER TABLE [dbo].[BankStatementTransactions] ADD [MatchReference] NVARCHAR(200) NOT NULL CONSTRAINT DF_BankStatementTransactions_MatchReference DEFAULT '';
IF COL_LENGTH('dbo.BankStatementTransactions', 'IncludeInProfitLoss') IS NULL
    ALTER TABLE [dbo].[BankStatementTransactions] ADD [IncludeInProfitLoss] BIT NOT NULL CONSTRAINT DF_BankStatementTransactions_IncludeInProfitLoss DEFAULT 0;
IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'BankConnections')
CREATE TABLE [dbo].[BankConnections] (
    [Id] INT IDENTITY(1,1) PRIMARY KEY,
    [StoreId] INT NOT NULL,
    [ConnectionId] NVARCHAR(200) NOT NULL,
    [Provider] NVARCHAR(50) NOT NULL DEFAULT '',
    [InstitutionName] NVARCHAR(200) NOT NULL DEFAULT '',
    [AccountName] NVARCHAR(200) NOT NULL DEFAULT '',
    [AccountMask] NVARCHAR(20) NOT NULL DEFAULT '',
    [Status] NVARCHAR(40) NOT NULL DEFAULT '',
    [LastSyncedUtc] DATETIME2 NULL,
    [LastError] NVARCHAR(500) NOT NULL DEFAULT '',
    [UpdatedUtc] DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);
");
            await ExecuteBankSchemaCommandAsync(conn, @"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_BankConnections_Store_Connection' AND object_id = OBJECT_ID('dbo.BankConnections'))
    CREATE UNIQUE INDEX UX_BankConnections_Store_Connection ON dbo.BankConnections(StoreId, ConnectionId);");
            await ExecuteBankSchemaCommandAsync(conn, @"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_BankStatementTransactions_Store_External' AND object_id = OBJECT_ID('dbo.BankStatementTransactions'))
    EXEC(N'CREATE UNIQUE INDEX UX_BankStatementTransactions_Store_External ON dbo.BankStatementTransactions(StoreId, ExternalTransactionId) WHERE ExternalTransactionId IS NOT NULL');");
            return;
        }

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(BankStatementTransactions)";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns.Add(reader["name"]?.ToString() ?? "");
        }

        async Task addColumn(string columnName, string definition, string? backfill = null)
        {
            if (columns.Contains(columnName))
                return;
            await ExecuteBankSchemaCommandAsync(conn, $"ALTER TABLE BankStatementTransactions ADD COLUMN {definition}");
            columns.Add(columnName);
            if (!string.IsNullOrWhiteSpace(backfill))
                await ExecuteBankSchemaCommandAsync(conn, backfill);
        }

        await addColumn("StoreId", "StoreId INTEGER NOT NULL DEFAULT 1");
        await addColumn("CheckNumber", "CheckNumber TEXT");
        await addColumn("Category", "Category TEXT NOT NULL DEFAULT 'Other'");
        await addColumn("ImportedUtc", "ImportedUtc TEXT", "UPDATE BankStatementTransactions SET ImportedUtc = datetime('now') WHERE ImportedUtc IS NULL");
        await addColumn("CreatedByName", "CreatedByName TEXT NOT NULL DEFAULT ''");
        await addColumn("ExternalTransactionId", "ExternalTransactionId TEXT");
        await addColumn("Source", "Source TEXT NOT NULL DEFAULT 'Statement Import'");
        await addColumn("IsMatched", "IsMatched INTEGER NOT NULL DEFAULT 0");
        await addColumn("MatchReference", "MatchReference TEXT NOT NULL DEFAULT ''");
        await addColumn("IncludeInProfitLoss", "IncludeInProfitLoss INTEGER NOT NULL DEFAULT 0");
        await ExecuteBankSchemaCommandAsync(conn, @"CREATE TABLE IF NOT EXISTS BankConnections (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    StoreId INTEGER NOT NULL,
    ConnectionId TEXT NOT NULL,
    Provider TEXT NOT NULL DEFAULT '',
    InstitutionName TEXT NOT NULL DEFAULT '',
    AccountName TEXT NOT NULL DEFAULT '',
    AccountMask TEXT NOT NULL DEFAULT '',
    Status TEXT NOT NULL DEFAULT '',
    LastSyncedUtc TEXT,
    LastError TEXT NOT NULL DEFAULT '',
    UpdatedUtc TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(StoreId, ConnectionId)
)");
        await ExecuteBankSchemaCommandAsync(conn,
            "CREATE UNIQUE INDEX IF NOT EXISTS UX_BankStatementTransactions_Store_External ON BankStatementTransactions(StoreId, ExternalTransactionId) WHERE ExternalTransactionId IS NOT NULL");
    }

    private static async Task ExecuteBankSchemaCommandAsync(DbConnection conn, string commandText)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = commandText;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<bool> BankStatementColumnExistsAsync(DbConnection conn, string columnName)
    {
        if (conn is SqlConnection)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CASE WHEN EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'BankStatementTransactions' AND COLUMN_NAME = @columnName) THEN 1 ELSE 0 END";
            AddParam(cmd, "@columnName", columnName);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1;
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(BankStatementTransactions)";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader["name"]?.ToString(), columnName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static string BankStatementDatePeriodFilter(DbConnection conn)
        => conn is SqlConnection
            ? "[Date] >= @fromDate AND [Date] < @toDate"
            : "Date >= @fromDate AND Date < @toDate";

    private static void AddBankStatementDatePeriodParams(DbCommand cmd, DbConnection conn, int month, int year)
    {
        var from = new DateTime(year, month, 1);
        var to = from.AddMonths(1);
        AddParam(cmd, "@fromDate", conn is SqlConnection ? from : from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        AddParam(cmd, "@toDate", conn is SqlConnection ? to : to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private async Task<List<BankStatementRow>> LoadBankStatementRowsAsync(int month, int year)
    {
        var rows = new List<BankStatementRow>();
        await EnsureBankStatementTablesAsync();
        await using var conn = CreateBankConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = conn is SqlConnection
            ? $"SELECT Id, [Date], [Description], Credit, Debit, CheckNumber, Category, Source, IsMatched, MatchReference, IncludeInProfitLoss FROM BankStatementTransactions WHERE StoreId=@sid AND {BankStatementDatePeriodFilter(conn)} ORDER BY [Date]"
            : $"SELECT Id, Date, Description, Credit, Debit, CheckNumber, Category, Source, IsMatched, MatchReference, IncludeInProfitLoss FROM BankStatementTransactions WHERE StoreId=@sid AND {BankStatementDatePeriodFilter(conn)} ORDER BY Date";
        AddParam(cmd, "@sid", _currentStoreId);
        AddBankStatementDatePeriodParams(cmd, conn, month, year);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var parsedDate = DateTime.TryParse(reader[1]?.ToString(), out var date) ? date.Date : DateTime.MinValue;
            rows.Add(new BankStatementRow(
                Convert.ToInt32(reader[0]),
                parsedDate,
                reader[2]?.ToString() ?? "",
                Convert.ToDecimal(reader[3]),
                Convert.ToDecimal(reader[4]),
                reader.IsDBNull(5) ? "" : reader[5]?.ToString() ?? "",
                reader[6]?.ToString() ?? "Other",
                parsedDate.Month > 0 ? parsedDate.Month : month,
                parsedDate.Year > 1 ? parsedDate.Year : year,
                reader.IsDBNull(7) ? "Statement Import" : reader[7]?.ToString() ?? "Statement Import",
                !reader.IsDBNull(8) && Convert.ToBoolean(reader[8]),
                reader.IsDBNull(9) ? "" : reader[9]?.ToString() ?? "",
                !reader.IsDBNull(10) && Convert.ToBoolean(reader[10])));
        }
        return rows;
    }

    private async Task<LocalBankConnectionStatus?> LoadLiveBankStatusAsync()
    {
        await EnsureBankStatementTablesAsync();
        await using var conn = CreateBankConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = conn is SqlConnection
            ? "SELECT TOP 1 InstitutionName, AccountName, AccountMask, Status, LastSyncedUtc, LastError FROM BankConnections WHERE StoreId=@sid ORDER BY UpdatedUtc DESC"
            : "SELECT InstitutionName, AccountName, AccountMask, Status, LastSyncedUtc, LastError FROM BankConnections WHERE StoreId=@sid ORDER BY UpdatedUtc DESC LIMIT 1";
        AddParam(cmd, "@sid", _currentStoreId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        DateTime? synced = null;
        if (!reader.IsDBNull(4) && DateTime.TryParse(reader[4]?.ToString(), out var parsed))
            synced = parsed;
        return new LocalBankConnectionStatus(
            reader[0]?.ToString() ?? "",
            reader[1]?.ToString() ?? "",
            reader[2]?.ToString() ?? "",
            reader[3]?.ToString() ?? "",
            synced,
            reader[5]?.ToString() ?? "");
    }

    private async Task ClearLocalBankConnectionStatusAsync()
    {
        await EnsureBankStatementTablesAsync();
        await using var conn = CreateBankConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM BankConnections WHERE StoreId=@sid";
        AddParam(cmd, "@sid", _currentStoreId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task UpdateBankProfitLossInclusionAsync(int id, bool include)
    {
        await EnsureBankStatementTablesAsync();
        await using var conn = CreateBankConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE BankStatementTransactions SET IncludeInProfitLoss=@include WHERE Id=@id AND StoreId=@sid";
        AddParam(cmd, "@include", include);
        AddParam(cmd, "@id", id);
        AddParam(cmd, "@sid", _currentStoreId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task MatchBankTransactionAsync(int id, string matchReference)
    {
        await EnsureBankStatementTablesAsync();
        await using var conn = CreateBankConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        var matched = !string.IsNullOrWhiteSpace(matchReference);
        cmd.CommandText = @"UPDATE BankStatementTransactions
SET IsMatched=@matched, MatchReference=@reference,
    IncludeInProfitLoss=CASE WHEN @matched=1 THEN 0 ELSE IncludeInProfitLoss END
WHERE Id=@id AND StoreId=@sid";
        AddParam(cmd, "@matched", matched);
        AddParam(cmd, "@reference", matchReference.Trim());
        AddParam(cmd, "@id", id);
        AddParam(cmd, "@sid", _currentStoreId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SaveLiveBankSyncAsync(LiveBankSyncResult result)
    {
        await EnsureBankStatementTablesAsync();
        await using var conn = CreateBankConnection();
        await conn.OpenAsync();

        foreach (var connection in result.Connections)
        {
            await using var command = conn.CreateCommand();
            if (conn is SqlConnection)
            {
                command.CommandText = @"UPDATE BankConnections SET Provider=@provider, InstitutionName=@institution,
AccountName=@account, AccountMask=@mask, Status=@status, LastSyncedUtc=@synced, LastError=@error, UpdatedUtc=GETUTCDATE()
WHERE StoreId=@sid AND ConnectionId=@connectionId;
IF @@ROWCOUNT = 0
INSERT INTO BankConnections (StoreId, ConnectionId, Provider, InstitutionName, AccountName, AccountMask, Status, LastSyncedUtc, LastError)
VALUES (@sid, @connectionId, @provider, @institution, @account, @mask, @status, @synced, @error);";
            }
            else
            {
                command.CommandText = @"INSERT INTO BankConnections
(StoreId, ConnectionId, Provider, InstitutionName, AccountName, AccountMask, Status, LastSyncedUtc, LastError, UpdatedUtc)
VALUES (@sid, @connectionId, @provider, @institution, @account, @mask, @status, @synced, @error, datetime('now'))
ON CONFLICT(StoreId, ConnectionId) DO UPDATE SET
Provider=excluded.Provider, InstitutionName=excluded.InstitutionName, AccountName=excluded.AccountName,
AccountMask=excluded.AccountMask, Status=excluded.Status, LastSyncedUtc=excluded.LastSyncedUtc,
LastError=excluded.LastError, UpdatedUtc=datetime('now');";
            }
            AddParam(command, "@sid", _currentStoreId);
            AddParam(command, "@connectionId", connection.ConnectionId);
            AddParam(command, "@provider", connection.Provider);
            AddParam(command, "@institution", connection.InstitutionName);
            AddParam(command, "@account", connection.AccountName);
            AddParam(command, "@mask", connection.AccountMask);
            AddParam(command, "@status", connection.Status);
            AddParam(command, "@synced", connection.LastSyncedUtc ?? result.SyncedUtc);
            AddParam(command, "@error", connection.LastError);
            await command.ExecuteNonQueryAsync();
        }

        foreach (var externalId in result.RemovedTransactionIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct())
        {
            await using var command = conn.CreateCommand();
            command.CommandText = "DELETE FROM BankStatementTransactions WHERE StoreId=@sid AND ExternalTransactionId=@externalId";
            AddParam(command, "@sid", _currentStoreId);
            AddParam(command, "@externalId", externalId);
            await command.ExecuteNonQueryAsync();
        }

        foreach (var transaction in result.Added.Concat(result.Modified)
                     .Where(x => !string.IsNullOrWhiteSpace(x.ExternalTransactionId))
                     .GroupBy(x => x.ExternalTransactionId, StringComparer.OrdinalIgnoreCase)
                     .Select(x => x.Last()))
        {
            await using var command = conn.CreateCommand();
            if (conn is SqlConnection)
            {
                command.CommandText = @"UPDATE BankStatementTransactions SET [Date]=@date, [Description]=@description,
Credit=@credit, Debit=@debit, CheckNumber=@check, Category=@category, Source='Live Bank',
ImportedUtc=GETUTCDATE(), CreatedByName=@by
WHERE StoreId=@sid AND ExternalTransactionId=@externalId;
IF @@ROWCOUNT = 0
INSERT INTO BankStatementTransactions
(StoreId, [Date], [Description], Credit, Debit, CheckNumber, Category, ExternalTransactionId, Source, CreatedByName, IncludeInProfitLoss)
VALUES (@sid, @date, @description, @credit, @debit, @check, @category, @externalId, 'Live Bank', @by, @include);";
            }
            else
            {
                command.CommandText = @"INSERT INTO BankStatementTransactions
(StoreId, Date, Description, Credit, Debit, CheckNumber, Category, ExternalTransactionId, Source, ImportedUtc, CreatedByName, IncludeInProfitLoss)
VALUES (@sid, @date, @description, @credit, @debit, @check, @category, @externalId, 'Live Bank', datetime('now'), @by, @include)
ON CONFLICT(StoreId, ExternalTransactionId) DO UPDATE SET
Date=excluded.Date, Description=excluded.Description, Credit=excluded.Credit, Debit=excluded.Debit,
CheckNumber=excluded.CheckNumber, Category=excluded.Category, Source='Live Bank',
ImportedUtc=datetime('now'), CreatedByName=excluded.CreatedByName;";
            }
            AddParam(command, "@sid", _currentStoreId);
            AddParam(command, "@externalId", transaction.ExternalTransactionId);
            AddParam(command, "@date", conn is SqlConnection
                ? transaction.Date.Date
                : transaction.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            AddParam(command, "@description", SafeBankDescription(transaction.Description));
            AddParam(command, "@credit", Math.Max(0, transaction.Credit));
            AddParam(command, "@debit", Math.Max(0, transaction.Debit));
            AddParam(command, "@check", string.IsNullOrWhiteSpace(transaction.CheckNumber) ? DBNull.Value : transaction.CheckNumber);
            AddParam(command, "@category", string.IsNullOrWhiteSpace(transaction.Category) ? "Other" : transaction.Category);
            AddParam(command, "@by", _session.DisplayName);
            AddParam(command, "@include", DefaultIncludeInProfitLoss(transaction.Category, transaction.Credit, transaction.Debit));
            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task<(int Month, int Year)?> ImportBankStatementAsync(int month, int year, Func<Task> refreshAsync)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import Bank Statement",
            Filter = "Bank statement files (*.pdf;*.xlsx;*.xls;*.csv)|*.pdf;*.xlsx;*.xls;*.csv|PDF files (*.pdf)|*.pdf|Excel/CSV files (*.xlsx;*.xls;*.csv)|*.xlsx;*.xls;*.csv|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return null;

        if (TryDetectBankStatementPeriod(dialog.FileName, out var detectedMonth, out var detectedYear))
        {
            month = detectedMonth;
            year = detectedYear;
        }

        var rows = ParseBankStatementFile(dialog.FileName, month, year);
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "No transactions were found. Make sure the file has date, description, debit/credit or amount columns.", "Bank Statement", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        var msg = $"Import {rows.Count} transaction(s) for {new DateTime(year, month, 1):MMMM yyyy}?\nThis replaces existing imported rows for that month.";
        if (MessageBox.Show(this, msg, "Bank Statement", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return null;

        await EnsureBankStatementTablesAsync();
        await ClearBankStatementMonthAsync(month, year, importedOnly: true);
        await using var conn = CreateBankConnection();
        await conn.OpenAsync();
        var hasCreatedByName = await BankStatementColumnExistsAsync(conn, "CreatedByName");
        foreach (var row in rows)
        {
            await using var cmd = conn.CreateCommand();
            var columns = conn is SqlConnection
                ? new List<string> { "StoreId", "[Date]", "[Description]", "Credit", "Debit", "CheckNumber", "Category", "IncludeInProfitLoss" }
                : new List<string> { "StoreId", "Date", "Description", "Credit", "Debit", "CheckNumber", "Category", "IncludeInProfitLoss" };
            var values = new List<string> { "@sid", "@d", "@desc", "@cr", "@dr", "@chk", "@cat", "@include" };
            if (hasCreatedByName)
            {
                columns.Add("CreatedByName");
                values.Add("@by");
            }

            cmd.CommandText = $"INSERT INTO BankStatementTransactions ({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)})";
            AddParam(cmd, "@sid", _currentStoreId);
            AddParam(cmd, "@d", conn is SqlConnection ? row.Date : row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            AddParam(cmd, "@desc", SafeBankDescription(row.Description));
            AddParam(cmd, "@cr", row.Credit);
            AddParam(cmd, "@dr", row.Debit);
            AddParam(cmd, "@chk", string.IsNullOrWhiteSpace(row.CheckNumber) ? DBNull.Value : row.CheckNumber);
            AddParam(cmd, "@cat", row.Category);
            AddParam(cmd, "@include", DefaultIncludeInProfitLoss(row.Category, row.Credit, row.Debit));
            if (hasCreatedByName)
                AddParam(cmd, "@by", _session.DisplayName);
            await cmd.ExecuteNonQueryAsync();
        }
        await refreshAsync();
        return (month, year);
    }

    private async Task ClearBankStatementMonthAsync(int month, int year, bool importedOnly = false)
    {
        await EnsureBankStatementTablesAsync();
        await using var conn = CreateBankConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM BankStatementTransactions WHERE StoreId=@sid AND {BankStatementDatePeriodFilter(conn)}"
                          + (importedOnly ? " AND (Source IS NULL OR Source <> 'Live Bank')" : "");
        AddParam(cmd, "@sid", _currentStoreId);
        AddBankStatementDatePeriodParams(cmd, conn, month, year);
        await cmd.ExecuteNonQueryAsync();
    }

    private static bool TryDetectBankStatementPeriod(string path, out int month, out int year)
    {
        month = 0;
        year = 0;
        var fileNameText = Path.GetFileNameWithoutExtension(path);
        if (TryParseBankStatementMonthYear(fileNameText, out month, out year))
            return true;

        var text = fileNameText;
        try
        {
            if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                using var document = PdfDocument.Open(path);
                text = $"{text} {string.Join(" ", document.GetPages().Take(2).Select(p => p.Text))}";
            }
        }
        catch
        {
            text = Path.GetFileNameWithoutExtension(path);
        }

        text = Regex.Replace(text, @"\s+", " ");
        var match = Regex.Match(text, @"(?:Statement\s+Ending|Ending\s+Balance\s+as\s+of)\s*:?\s*(?<date>(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\s+\d{1,2},?\s+\d{4}|\d{1,2}/\d{1,2}/\d{2,4})", RegexOptions.IgnoreCase);
        if (!match.Success)
            match = Regex.Match(text, @"(?:Statement\s+Ending|Ending\s+Balance\s+as\s+of)\s*:?\s*(?<month>Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\s+(?<year>\d{4})\b", RegexOptions.IgnoreCase);
        if (!match.Success)
            match = Regex.Match(text, @"(?:Statement\s+Date)\s*:?\s*(?<date>(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\s+\d{1,2},?\s+\d{4}|\d{1,2}/\d{1,2}/\d{2,4})", RegexOptions.IgnoreCase);
        if (!match.Success)
            match = Regex.Match(text, @"\b(?<month>Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\s+(?<year>\d{4})\b", RegexOptions.IgnoreCase);

        if (match.Success && match.Groups["date"].Success && DateTime.TryParse(match.Groups["date"].Value, CultureInfo.CurrentCulture, DateTimeStyles.None, out var detectedDate))
        {
            month = detectedDate.Month;
            year = detectedDate.Year;
            return true;
        }

        if (match.Success && match.Groups["month"].Success && match.Groups["year"].Success
            && DateTime.TryParse($"{match.Groups["month"].Value} 1, {match.Groups["year"].Value}", CultureInfo.CurrentCulture, DateTimeStyles.None, out detectedDate))
        {
            month = detectedDate.Month;
            year = detectedDate.Year;
            return true;
        }

        return false;
    }

    private static bool TryParseBankStatementMonthYear(string text, out int month, out int year)
    {
        month = 0;
        year = 0;
        var match = Regex.Match(
            text ?? "",
            @"\b(?<month>Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\s+(?<year>\d{4})\b",
            RegexOptions.IgnoreCase);
        if (!match.Success)
            return false;

        if (!DateTime.TryParse($"{match.Groups["month"].Value} 1, {match.Groups["year"].Value}", CultureInfo.CurrentCulture, DateTimeStyles.None, out var detectedDate))
            return false;

        month = detectedDate.Month;
        year = detectedDate.Year;
        return true;
    }

    private static List<BankStatementRow> ParseBankStatementFile(string path, int month, int year)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".csv" => ParseBankCsv(File.ReadAllLines(path), month, year),
            ".xlsx" or ".xls" => ParseBankExcel(path, month, year),
            ".pdf" => ParseBankPdf(path, month, year),
            _ => new List<BankStatementRow>()
        };
    }

    private static List<BankStatementRow> ParseBankPdf(string path, int month, int year)
    {
        var lines = new List<string>();
        using var document = PdfDocument.Open(path);
        foreach (var page in document.GetPages())
        {
            var wordLines = page.GetWords()
                .Select(word => new PdfWordLine(
                    word.Text,
                    word.BoundingBox.Left,
                    Math.Round(word.BoundingBox.Bottom / 2d) * 2d))
                .GroupBy(word => word.Line)
                .OrderByDescending(group => group.Key);

            foreach (var group in wordLines)
            {
                var line = string.Join(" ", group
                    .OrderBy(word => word.Left)
                    .Select(word => word.Text));
                line = Regex.Replace(line, @"\s+", " ").Trim();
                if (!string.IsNullOrWhiteSpace(line))
                    lines.Add(line);
            }
        }

        return ParseBankPdfLines(lines, month, year);
    }

    private readonly record struct PdfWordLine(string Text, double Left, double Line);

    private static List<BankStatementRow> ParseBankPdfLines(IEnumerable<string> lines, int month, int year)
    {
        var parsed = new List<BankStatementRow>();
        const string monthNames = @"Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?";
        var datePattern = new Regex(@$"^\s*(?:\d+\s+)?(?<date>\d{{1,2}}[/-]\d{{1,2}}(?:[/-]\d{{2,4}})?|(?:{monthNames})\s*\d{{1,2}}(?:,?\s+\d{{2,4}})?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var moneyPattern = new Regex(@"(?<!\w)\(?-?\$?\d{1,3}(?:,\d{3})*(?:\.\d{2})\)?", RegexOptions.Compiled);
        var lastTransactionIndex = -1;
        var currentSection = "";

        foreach (var rawLine in lines)
        {
            var rawUpper = Regex.Replace(rawLine ?? "", @"\s+", " ").Trim().ToUpperInvariant();
            if (IsBankPdfNonTransactionSectionLine(rawUpper))
            {
                currentSection = "";
                continue;
            }

            var line = CleanBankPdfLine(rawLine ?? "");
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var upper = line.ToUpperInvariant();
            if (IsBankPdfNonTransactionSectionLine(upper))
            {
                currentSection = "";
                continue;
            }
            if (IsBankPdfDebitSectionLine(upper))
            {
                currentSection = "Debit";
                continue;
            }
            if (IsBankPdfCreditSectionLine(upper))
            {
                currentSection = "Credit";
                continue;
            }
            if (IsBankPdfHeaderLine(line))
                continue;
            if (string.IsNullOrWhiteSpace(currentSection))
                continue;

            foreach (var transactionLine in SplitBankPdfTransactionCandidates(line))
            {
                var transactionUpper = transactionLine.ToUpperInvariant();
                if (IsBankPdfNonTransactionSectionLine(transactionUpper) || IsBankPdfHeaderLine(transactionLine))
                    continue;

                var dateMatch = datePattern.Match(transactionLine);
                if (!dateMatch.Success || !TryParseBankDate(dateMatch.Groups["date"].Value, month, year, out var date))
                {
                    if (lastTransactionIndex >= 0 && IsBankPdfContinuationLine(transactionLine))
                    {
                        var previous = parsed[lastTransactionIndex];
                        var continuationDescription = SafeBankDescription($"{previous.Description} {transactionLine}");
                        parsed[lastTransactionIndex] = previous with
                        {
                            Description = continuationDescription,
                            Category = CategorizeBankTransaction(continuationDescription, previous.CheckNumber)
                        };
                    }
                    continue;
                }
                if (date.Month != month || date.Year != year)
                    continue;

                var moneyMatches = moneyPattern.Matches(transactionLine).Cast<Match>().Select(m => m.Value).ToList();
                if (moneyMatches.Count == 0)
                    continue;

                var values = moneyMatches.Select(ParseMoney).Where(v => v != 0).ToList();
                if (values.Count == 0)
                    continue;

                var amount = SelectBankPdfTransactionAmount(values);
                var isCredit = transactionUpper.Contains("DEPOSIT", StringComparison.Ordinal)
                               || transactionUpper.Contains("CREDIT", StringComparison.Ordinal)
                               || transactionUpper.Contains("INTEREST", StringComparison.Ordinal)
                               || transactionUpper.Contains("REFUND", StringComparison.Ordinal);
                var isDebit = transactionUpper.Contains("WITHDRAW", StringComparison.Ordinal)
                              || transactionUpper.Contains("DEBIT", StringComparison.Ordinal)
                              || transactionUpper.Contains("PAYMENT", StringComparison.Ordinal)
                              || transactionUpper.Contains("PURCHASE", StringComparison.Ordinal)
                              || transactionUpper.Contains("CHECK", StringComparison.Ordinal)
                              || transactionUpper.Contains("FEE", StringComparison.Ordinal);

                var credit = 0m;
                var debit = 0m;
                if (currentSection == "Credit")
                    credit = Math.Abs(amount);
                else if (currentSection == "Debit")
                    debit = Math.Abs(amount);
                else if (amount < 0 || isDebit && !isCredit)
                    debit = Math.Abs(amount);
                else
                    credit = Math.Abs(amount);

                var description = transactionLine;
                description = datePattern.Replace(description, "", 1);
                foreach (var money in moneyMatches)
                    description = description.Replace(money, "", StringComparison.Ordinal);
                description = Regex.Replace(description, @"\s+", " ").Trim();
                description = SafeBankDescription(description);
                if (string.IsNullOrWhiteSpace(description))
                    description = "Imported bank transaction";

                var check = Regex.Match(transactionLine, @"\b(?:CHK|CHECK|CK)\s*#?\s*(\d{3,})\b", RegexOptions.IgnoreCase);
                if (!check.Success)
                    check = Regex.Match(transactionLine, @"^\s*(?:\d{1,2}[/-]\d{1,2}(?:[/-]\d{2,4})?|(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\s*\d{1,2}(?:,?\s+\d{2,4})?)\s+(\d{3,})\b", RegexOptions.IgnoreCase);
                parsed.Add(new BankStatementRow(
                    0,
                    date.Date,
                    description,
                    credit,
                    debit,
                    check.Success ? check.Groups[1].Value : "",
                    CategorizeBankTransaction(description, check.Success ? check.Groups[1].Value : ""),
                    month,
                    year));
                lastTransactionIndex = parsed.Count - 1;
            }
        }

        return parsed;
    }

    private static decimal SelectBankPdfTransactionAmount(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0)
            return 0m;

        // Bank statements often include a running balance as the last money column.
        // The real transaction amount is therefore the previous money value.
        return values.Count >= 2 ? values[^2] : values[^1];
    }

    private static IEnumerable<string> SplitBankPdfTransactionCandidates(string line)
    {
        var matches = Regex.Matches(
                line,
                @"\b(?:\d{1,2}[/-]\d{1,2}(?:[/-]\d{2,4})?|(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\s*\d{1,2}(?:,?\s+\d{2,4})?)\b",
                RegexOptions.IgnoreCase)
            .Cast<Match>()
            .ToList();

        if (matches.Count == 0)
        {
            yield return line;
            yield break;
        }

        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : line.Length;
            var candidate = line[start..end].Trim();
            if (!string.IsNullOrWhiteSpace(candidate))
                yield return candidate;
        }
    }

    private static string CleanBankPdfLine(string line)
    {
        line = Regex.Replace(line ?? "", @"\s+", " ").Trim();
        line = Regex.Replace(line, @"^[A-Z]{8,}\s+(?=(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\s*\d)", "", RegexOptions.IgnoreCase);
        line = Regex.Replace(line, @"^\d+\s+(?=(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec|\d{1,2}[/-])\b)", "", RegexOptions.IgnoreCase);
        var embeddedDate = Regex.Match(
            line,
            @"\b(?:\d{1,2}[/-]\d{1,2}(?:[/-]\d{2,4})?|(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\s*\d{1,2}(?:,?\s+\d{2,4})?)\b",
            RegexOptions.IgnoreCase);
        if (embeddedDate.Success && embeddedDate.Index > 0 && CountBankDateTokens(line) == 1)
            line = line[embeddedDate.Index..].Trim();
        return line.Trim();
    }

    private static bool IsBankPdfContinuationLine(string line)
    {
        if (line.Length < 4 || Regex.IsMatch(line, @"^[A-Z]{8,}$"))
            return false;

        var upper = line.ToUpperInvariant();
        return upper.Contains("MERCHANT", StringComparison.Ordinal)
               || upper.Contains("SERVICE", StringComparison.Ordinal)
               || upper.Contains("PAYMENT", StringComparison.Ordinal)
               || upper.Contains("PURCHASE", StringComparison.Ordinal)
               || upper.Contains("CHASE", StringComparison.Ordinal)
               || upper.Contains("COMED", StringComparison.Ordinal)
               || upper.Contains("DEP", StringComparison.Ordinal)
               || upper.Contains("CRD", StringComparison.Ordinal)
               || upper.Contains("ACH", StringComparison.Ordinal)
               || upper.Contains("APPALACHIAN", StringComparison.Ordinal);
    }

    private static bool IsBankPdfHeaderLine(string line)
    {
        var upper = line.ToUpperInvariant();
        return upper.Contains("ACCOUNT NUMBER", StringComparison.Ordinal)
               || upper.Contains("ACCOUNT:", StringComparison.Ordinal)
               || upper.Contains("STATEMENT PERIOD", StringComparison.Ordinal)
               || upper.Contains("STATEMENT DATE", StringComparison.Ordinal)
               || upper.Contains("DAILY BALANCE", StringComparison.Ordinal)
               || upper.Contains("BEGINNING BALANCE", StringComparison.Ordinal)
               || upper.Contains("ENDING BALANCE", StringComparison.Ordinal)
               || upper.Contains("BALANCE SUMMARY", StringComparison.Ordinal)
               || upper.Contains("CHECKING ACCOUNT", StringComparison.Ordinal)
               || upper.Contains("MEMBER FDIC", StringComparison.Ordinal)
               || upper.Contains("CUSTOMER SERVICE", StringComparison.Ordinal)
               || upper.Contains("ROUTING", StringComparison.Ordinal)
               || upper.Contains("WWW.", StringComparison.Ordinal)
               || upper.Contains("PO BOX", StringComparison.Ordinal)
               || upper.Contains("DEPOSITS AND OTHER ADDITIONS", StringComparison.Ordinal)
               || upper.Contains("WITHDRAWALS AND OTHER SUBTRACTIONS", StringComparison.Ordinal)
               || upper.Contains("PAGE", StringComparison.Ordinal)
               || upper.Contains("DEBITS", StringComparison.Ordinal)
               || upper.Contains("CREDITS", StringComparison.Ordinal)
               || upper.Contains("ADDITIONS", StringComparison.Ordinal)
               || upper.Contains("BALANCE", StringComparison.Ordinal)
               || upper.Contains("DESCRIPTION", StringComparison.Ordinal)
               || upper.Contains("TRANSACTION DETAIL", StringComparison.Ordinal)
               || upper.Contains("CONTINUED", StringComparison.Ordinal);
    }

    private static bool IsBankPdfNonTransactionSectionLine(string upperLine)
    {
        return upperLine.Contains("BALANCE SUMMARY", StringComparison.Ordinal)
               || upperLine.Contains("BEGINNING BALANCE", StringComparison.Ordinal)
               || upperLine.Contains("ENDING BALANCE", StringComparison.Ordinal)
               || upperLine.Contains("ANALYSIS OR MAINTENANCE FEES", StringComparison.Ordinal)
               || upperLine.Contains("NUMBER OF DAYS IN STATEMENT PERIOD", StringComparison.Ordinal)
               || upperLine.Contains("DAILY BALANCE", StringComparison.Ordinal)
               || upperLine.Contains("DAILY BALANCES", StringComparison.Ordinal)
               || upperLine.Contains("DATE BALANCE", StringComparison.Ordinal)
               || upperLine.Contains("CHECK IMAGE", StringComparison.Ordinal)
               || upperLine.Contains("CHECK IMAGES", StringComparison.Ordinal)
               || upperLine.Contains("LAST STATEMENT", StringComparison.Ordinal)
               || upperLine.Contains("STATEMENT ENDING", StringComparison.Ordinal)
               || upperLine.Contains("D:\\", StringComparison.Ordinal)
               || upperLine.Equals("CHECKS", StringComparison.Ordinal)
               || upperLine.Contains("R-CHECK HAS BEEN RETURNED", StringComparison.Ordinal)
               || upperLine.Contains("* INDICATES A BREAK", StringComparison.Ordinal)
               || upperLine.Contains("DATE CHECK# AMOUNT", StringComparison.Ordinal);
    }

    private static bool IsBankPdfDebitSectionLine(string upperLine)
    {
        return upperLine.Contains("DEBITS", StringComparison.Ordinal)
            || upperLine.Contains("WITHDRAWALS", StringComparison.Ordinal)
            || upperLine.Contains("SUBTRACTIONS", StringComparison.Ordinal)
            || upperLine.Contains("CHECKS PAID", StringComparison.Ordinal)
            || upperLine.Contains("PAYMENTS AND PURCHASES", StringComparison.Ordinal);
    }

    private static bool IsBankPdfCreditSectionLine(string upperLine)
    {
        return upperLine.Contains("CREDITS", StringComparison.Ordinal)
            || upperLine.Contains("DEPOSITS", StringComparison.Ordinal)
            || upperLine.Contains("ADDITIONS", StringComparison.Ordinal)
            || upperLine.Contains("INTEREST PAID", StringComparison.Ordinal);
    }

    private static int CountBankDateTokens(string line)
    {
        return Regex.Matches(
            line,
            @"\b\d{1,2}[/-]\d{1,2}(?:[/-]\d{2,4})?\b|\b(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\s*\d{1,2}(?:,?\s+\d{2,4})?\b",
            RegexOptions.IgnoreCase).Count;
    }

    private static string SafeBankDescription(string description)
    {
        description = Regex.Replace(description ?? "", @"\s+", " ").Trim();
        const int maxDescriptionLength = 180;
        return description.Length <= maxDescriptionLength
            ? description
            : description[..maxDescriptionLength];
    }

    private static List<BankStatementRow> ParseBankExcel(string path, int month, int year)
    {
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.First();
        var rows = new List<string[]>();
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        var lastCol = Math.Min(ws.LastColumnUsed()?.ColumnNumber() ?? 0, 40);
        for (var r = 1; r <= lastRow; r++)
            rows.Add(Enumerable.Range(1, lastCol).Select(c => ws.Cell(r, c).GetFormattedString().Trim()).ToArray());
        return ParseBankRows(rows, month, year);
    }

    private static List<BankStatementRow> ParseBankCsv(IEnumerable<string> lines, int month, int year)
    {
        var rows = lines
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(SplitCsvLine)
            .ToList();
        return ParseBankRows(rows, month, year);
    }

    private static List<string> SplitCsvLine(string line)
    {
        var result = new List<string>();
        var current = "";
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current += '"';
                    i++;
                }
                else quoted = !quoted;
            }
            else if (ch == ',' && !quoted)
            {
                result.Add(current.Trim());
                current = "";
            }
            else current += ch;
        }
        result.Add(current.Trim());
        return result;
    }

    private static List<BankStatementRow> ParseBankRows(IEnumerable<IReadOnlyList<string>> sourceRows, int month, int year)
    {
        var rows = sourceRows.ToList();
        var headerIndex = rows.FindIndex(r => r.Any(c => HeaderMatches(c, "date")) && r.Any(c => HeaderMatches(c, "description", "details", "memo", "payee")));
        if (headerIndex < 0) headerIndex = 0;
        var header = rows[headerIndex].Select(x => x.Trim().ToLowerInvariant()).ToList();
        int Find(params string[] names) => header.FindIndex(h => names.Any(n => h.Contains(n)));
        var dateCol = Find("date", "posted");
        var descCol = Find("description", "details", "memo", "payee", "transaction");
        var creditCol = Find("credit", "deposit", "addition");
        var debitCol = Find("debit", "withdrawal", "payment", "subtraction");
        var amountCol = Find("amount");
        var checkCol = Find("check", "chk");
        var parsed = new List<BankStatementRow>();
        for (var i = headerIndex + 1; i < rows.Count; i++)
        {
            var r = rows[i];
            string Cell(int idx) => idx >= 0 && idx < r.Count ? r[idx].Trim() : "";
            if (!TryParseBankDate(Cell(dateCol), month, year, out var date))
                continue;
            if (date.Month != month || date.Year != year)
                continue;
            var desc = SafeBankDescription(Cell(descCol));
            if (string.IsNullOrWhiteSpace(desc))
                desc = "Imported bank transaction";
            var credit = ParseMoney(Cell(creditCol));
            var debit = ParseMoney(Cell(debitCol));
            if (credit == 0 && debit == 0 && amountCol >= 0)
            {
                var amount = ParseMoney(Cell(amountCol));
                if (amount >= 0) credit = amount;
                else debit = Math.Abs(amount);
            }
            if (credit == 0 && debit == 0)
                continue;
            var check = Cell(checkCol);
            parsed.Add(new BankStatementRow(0, date, desc, credit, debit, check, CategorizeBankTransaction(desc, check), month, year));
        }
        return parsed;
    }

    private static bool HeaderMatches(string value, params string[] names)
        => names.Any(name => value.Contains(name, StringComparison.OrdinalIgnoreCase));

    private static bool TryParseBankDate(string text, int month, int year, out DateTime date)
    {
        text = text.Trim();
        text = Regex.Replace(text, @"^([A-Za-z]{3,9})\s*(\d{1,2})$", "$1 $2");
        if (Regex.IsMatch(text, @"^[A-Za-z]{3,9}\s+\d{1,2}$")
            && DateTime.TryParse($"{text}, {year}", CultureInfo.CurrentCulture, DateTimeStyles.None, out date))
        {
            return date.Month == month && date.Year == year;
        }

        if (Regex.IsMatch(text, @"^\d{1,2}[/-]\d{1,2}$")
            && DateTime.TryParse($"{text}/{year}", CultureInfo.CurrentCulture, DateTimeStyles.None, out date))
        {
            return date.Month == month && date.Year == year;
        }

        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out date))
        {
            if (date.Year == 1) date = new DateTime(year, date.Month, date.Day);
            return true;
        }
        if (DateTime.TryParse($"{text}/{year}", CultureInfo.CurrentCulture, DateTimeStyles.None, out date))
            return date.Month == month || text.Contains('/');
        date = default;
        return false;
    }

    private static decimal ParseMoney(string text)
    {
        text = text.Replace("$", "").Replace(",", "").Replace("(", "-").Replace(")", "").Trim();
        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0m;
    }

    private static string CategorizeBankTransaction(string description, string checkNumber)
    {
        if (!string.IsNullOrWhiteSpace(checkNumber)) return "Check";
        var d = description.ToUpperInvariant();
        if (d.Contains("FEE")) return "Fees";
        if (d.Contains("DEPOSIT") || d.Contains("CREDIT")) return "Deposit";
        if (d.Contains("TRANSFER")) return "Transfer";
        if (d.Contains("PAYMENT") || d.Contains("PURCHASE") || d.Contains("DEBIT")) return "Payment";
        return "Other";
    }

    private static bool DefaultIncludeInProfitLoss(string category, decimal credit, decimal debit)
    {
        var normalized = (category ?? "").Trim().ToLowerInvariant();
        if (debit > 0)
        {
            return !normalized.Contains("transfer") &&
                   !normalized.Contains("credit card payment") &&
                   !normalized.Contains("loan principal") &&
                   !normalized.Contains("cash withdrawal") &&
                   !normalized.Contains("owner draw");
        }

        if (credit <= 0)
            return false;
        return normalized.Contains("income") ||
               normalized.Contains("interest") ||
               normalized.Contains("refund") ||
               normalized.Contains("cashback") ||
               normalized.Contains("reimbursement");
    }

    private Control BuildSimpleList<T>(string title, Func<AppDbContext, IQueryable<T>> query, Func<string, T> create) where T : class
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, BackColor = WinTheme.Bg, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, ForeColor = WinTheme.Copper, Font = WinTheme.HeaderFont(14) }, 0, 0);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var name = WinTheme.TextBox();
        name.Width = 280;
        var add = WinTheme.Button("Add", true);
        var del = WinTheme.Button("Delete Selected");
        del.Enabled = _session.IsAdmin;
        bar.Controls.Add(name);
        bar.Controls.Add(add);
        bar.Controls.Add(del);
        root.Controls.Add(bar, 0, 1);
        var grid = WinTheme.Grid();
        root.Controls.Add(grid, 0, 2);
        void refresh()
        {
            using var db = CreateDb();
            grid.DataSource = query(db).AsNoTracking().ToList();
            HideId(grid);
            HideColumn(grid, "StoreId");
        }
        add.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) return;
            using var db = CreateDb();
            db.Set<T>().Add(create(name.Text.Trim()));
            await db.SaveChangesAsync();
            name.Clear();
            refresh();
        };
        del.Click += async (_, _) => await DeleteSelectedAsync<T>(grid, refresh);
        refresh();
        return root;
    }

    private (TableLayoutPanel Root, TableLayoutPanel Fields, FlowLayoutPanel Actions, DataGridView Grid) EntryGridPage(bool withForm = true)
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = withForm ? 4 : 3, BackColor = WinTheme.Bg };
        if (withForm) root.RowStyles.Add(new RowStyle(SizeType.Absolute, 182));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 4, BackColor = WinTheme.Panel, Padding = new Padding(14, 10, 14, 10), Margin = new Padding(0, 0, 0, 8) };
        for (var i = 0; i < 3; i++) fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        for (var i = 0; i < 4; i++) fields.RowStyles.Add(new RowStyle(SizeType.Percent, 25f));
        fields.Paint += (_, e) =>
        {
            using var pen = new Pen(WinTheme.CopperDark);
            e.Graphics.DrawRectangle(pen, 0, 0, fields.Width - 1, fields.Height - 1);
        };
        if (withForm) root.Controls.Add(fields, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Bg, Padding = new Padding(0, 8, 0, 8), FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = true };
        root.Controls.Add(actions, 0, withForm ? 1 : 0);
        var grid = WinTheme.Grid();
        root.Controls.Add(grid, 0, withForm ? 2 : 1);
        root.Controls.Add(BuildGridFooter("Showing records for selected store"), 0, withForm ? 3 : 2);
        return (root, fields, actions, grid);
    }

    private static TextBox AddText(TableLayoutPanel fields, string label)
    {
        var box = WinTheme.TextBox();
        AddFieldControl(fields, label, box);
        return box;
    }

    private static DateTimePicker AddDate(TableLayoutPanel fields, string label)
    {
        var picker = WinTheme.DatePicker();
        AddFieldControl(fields, label, picker);
        return picker;
    }

    private static ComboBox AddCombo(TableLayoutPanel fields, string label, IEnumerable<string> values)
    {
        var combo = WinTheme.ComboBox();
        combo.Items.AddRange(values.Cast<object>().ToArray());
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        AddFieldControl(fields, label, combo);
        return combo;
    }

    private static TextBox AddTextAt(TableLayoutPanel fields, string label, int row, int col)
    {
        var box = WinTheme.TextBox();
        AddFieldControl(fields, label, box, row, col);
        return box;
    }

    private static TextBox AddReadOnlyTextAt(TableLayoutPanel fields, string label, string text, int row, int col)
    {
        var box = WinTheme.TextBox();
        box.Text = text;
        box.ReadOnly = true;
        box.ForeColor = WinTheme.Muted;
        AddFieldControl(fields, label, box, row, col);
        return box;
    }

    private static DateTimePicker AddDateAt(TableLayoutPanel fields, string label, int row, int col)
    {
        var picker = WinTheme.DatePicker();
        AddFieldControl(fields, label, picker, row, col);
        return picker;
    }

    private static ComboBox AddComboAt(TableLayoutPanel fields, string label, IEnumerable<string> values, int row, int col)
    {
        var combo = WinTheme.ComboBox();
        combo.Items.AddRange(values.Cast<object>().ToArray());
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        AddFieldControl(fields, label, combo, row, col);
        return combo;
    }

    private static void AddFieldControl(TableLayoutPanel fields, string labelText, Control input)
    {
        var index = fields.Controls.Count;
        var row = index / 3;
        var col = index % 3;
        AddFieldControl(fields, labelText, input, row, col);
    }

    private static void AddFieldControl(TableLayoutPanel fields, string labelText, Control input, int row, int col)
    {
        while (row >= fields.RowCount)
        {
            fields.RowCount++;
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        }

        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel, Padding = new Padding(8, 3, 8, 3), ColumnCount = 2, RowCount = 1 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var label = WinTheme.FixedLabel(labelText, accent: false, size: 9, bold: true);
        label.ForeColor = WinTheme.Muted;
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        input.Dock = DockStyle.Fill;
        input.Margin = Padding.Empty;
        panel.Controls.Add(label, 0, 0);
        panel.Controls.Add(input, 1, 0);
        fields.Controls.Add(panel, col, row);
    }

    private static Control BuildGridFooter(string statusText)
    {
        var footer = WinTheme.BorderedPanel(4);
        footer.Dock = DockStyle.Fill;
        footer.Margin = new Padding(0);
        footer.Resize += (_, _) =>
        {
            foreach (Control control in footer.Controls)
                control.Top = Math.Max(8, (footer.ClientSize.Height - control.Height) / 2);
        };

        var left = 8;
        foreach (var text in new[] { "First", "Prev", "1", "2", "Next", "Last" })
        {
            var button = WinTheme.Button(text, text == "1");
            button.Width = text.Length > 2 ? 64 : 40;
            button.Height = 32;
            button.Left = left;
            button.Top = 8;
            button.Font = WinTheme.BoldFont(8.5f);
            footer.Controls.Add(button);
            left += button.Width + 6;
        }

        var status = new Label
        {
            Text = statusText,
            AutoSize = false,
            Left = left + 8,
            Top = 8,
            Width = 420,
            Height = 32,
            ForeColor = WinTheme.Muted,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = WinTheme.BodyFont(9)
        };
        footer.Controls.Add(status);

        var export = WinTheme.Button("Export to Excel");
        export.Width = 170;
        export.Height = 32;
        export.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        export.Left = 0;
        export.Top = 8;
        export.Font = WinTheme.BoldFont(9);
        var refresh = WinTheme.Button("Refresh");
        refresh.Width = 90;
        refresh.Height = 32;
        refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        refresh.Left = 0;
        refresh.Top = 8;
        refresh.Font = WinTheme.BoldFont(9);
        footer.Controls.Add(export);
        footer.Controls.Add(refresh);
        export.Click += (_, _) =>
        {
            if (export.FindForm() is MainForm form)
                form.ExportCurrentVisibleGridToExcel();
        };
        refresh.Click += (_, _) =>
        {
            if (refresh.FindForm() is MainForm form)
                form.ShowModule(form._currentModule);
        };

        void placeRightButtons()
        {
            export.Left = Math.Max(0, footer.ClientSize.Width - export.Width - 10);
            refresh.Left = Math.Max(0, export.Left - refresh.Width - 10);
            export.Top = refresh.Top = Math.Max(8, (footer.ClientSize.Height - export.Height) / 2);
        }
        footer.Resize += (_, _) => placeRightButtons();
        footer.HandleCreated += (_, _) => placeRightButtons();
        return footer;
    }

    private void ExportCurrentVisibleGridToExcel()
    {
        var grid = FindLargestVisibleGrid(_content) ?? FindLargestVisibleGrid(this);
        if (grid is null)
        {
            MessageBox.Show(this, "No table is available to export from this section.", "Export to Excel", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var columns = grid.Columns.Cast<DataGridViewColumn>()
            .Where(c => c.Visible && !string.Equals(c.HeaderText, "Id", StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.DisplayIndex)
            .ToList();
        var rows = grid.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow).ToList();
        if (columns.Count == 0 || rows.Count == 0)
        {
            MessageBox.Show(this, "There are no rows to export from this section.", "Export to Excel", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "Export to Excel",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            FileName = $"HisabKitab_{SafeReportFilePart(_currentModule)}_{DateTime.Now:yyyyMMdd_HHmm}.xlsx"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add(SafeWorksheetName(_currentModule));
        for (var c = 0; c < columns.Count; c++)
        {
            var cell = worksheet.Cell(1, c + 1);
            cell.Value = columns[c].HeaderText;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E0A157");
            cell.Style.Font.FontColor = XLColor.Black;
        }

        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < columns.Count; c++)
            {
                var raw = rows[r].Cells[columns[c].Name].Value;
                worksheet.Cell(r + 2, c + 1).Value = Convert.ToString(raw, CultureInfo.CurrentCulture) ?? "";
            }
        }

        worksheet.Columns().AdjustToContents();
        workbook.SaveAs(dialog.FileName);
        MessageBox.Show(this, $"Excel export saved:\n{dialog.FileName}", "Export to Excel", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static DataGridView? FindLargestVisibleGrid(Control root)
    {
        DataGridView? best = null;
        var bestArea = 0;
        foreach (Control child in root.Controls)
        {
            if (child is DataGridView grid && grid.Visible)
            {
                var area = Math.Max(1, grid.Width) * Math.Max(1, grid.Height);
                if (area > bestArea)
                {
                    best = grid;
                    bestArea = area;
                }
            }

            var nested = FindLargestVisibleGrid(child);
            if (nested is not null)
            {
                var area = Math.Max(1, nested.Width) * Math.Max(1, nested.Height);
                if (area > bestArea)
                {
                    best = nested;
                    bestArea = area;
                }
            }
        }

        return best;
    }

    private static string SafeWorksheetName(string value)
    {
        var cleaned = Regex.Replace(value, @"[\[\]\:\*\?\/\\]", " ").Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = "Report";
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }

    private static Label AddActionStat(FlowLayoutPanel host, string title, Color valueColor)
    {
        var card = WinTheme.BorderedPanel(6);
        card.Width = 205;
        card.Height = 54;
        card.Margin = new Padding(5, 0, 5, 0);
        var titleLabel = new Label { Text = title, Dock = DockStyle.Top, Height = 20, ForeColor = Color.White, Font = WinTheme.BoldFont(8), TextAlign = ContentAlignment.MiddleCenter };
        var value = new Label { Text = "$0.00", Dock = DockStyle.Fill, ForeColor = valueColor, Font = WinTheme.HeaderFont(12), TextAlign = ContentAlignment.MiddleCenter };
        card.Controls.Add(value);
        card.Controls.Add(titleLabel);
        host.Controls.Add(card);
        return value;
    }

    private static TableLayoutPanel SectionRoot(params int[] absoluteRows)
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = absoluteRows.Length + 2, ColumnCount = 1, BackColor = WinTheme.Bg, Padding = new Padding(2) };
        foreach (var height in absoluteRows)
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        return root;
    }

    private static Label MetricCard(Control host, string title, string value, Color valueColor, string subtitle = "", int width = 250, int height = 92)
    {
        var card = WinTheme.BorderedPanel(10);
        card.Width = width;
        card.Height = height;
        card.Margin = new Padding(6);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = WinTheme.Panel };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height < 80 ? 20 : 25));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height < 80 ? 18 : 22));
        card.Controls.Add(layout);
        layout.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, ForeColor = Color.White, Font = WinTheme.BoldFont(9), TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true }, 0, 0);
        var valueLabel = new Label { Text = value, Dock = DockStyle.Fill, ForeColor = valueColor, Font = WinTheme.HeaderFont(height < 80 ? 12.5f : 16), TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true, UseCompatibleTextRendering = true };
        layout.Controls.Add(valueLabel, 0, 1);
        layout.Controls.Add(new Label { Text = subtitle, Dock = DockStyle.Fill, ForeColor = WinTheme.Muted, Font = WinTheme.BodyFont(8.5f), TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true }, 0, 2);
        host.Controls.Add(card);
        return valueLabel;
    }

    private static Label MetricCard(TableLayoutPanel host, int col, int row, string title, string value, Color valueColor, string subtitle = "")
    {
        var card = WinTheme.BorderedPanel(10);
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(6);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = WinTheme.Panel };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        card.Controls.Add(layout);
        layout.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, ForeColor = Color.White, Font = WinTheme.BoldFont(9), TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true }, 0, 0);
        var valueLabel = new Label { Text = value, Dock = DockStyle.Fill, ForeColor = valueColor, Font = WinTheme.HeaderFont(13.5f), TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true, UseCompatibleTextRendering = true };
        layout.Controls.Add(valueLabel, 0, 1);
        layout.Controls.Add(new Label { Text = subtitle, Dock = DockStyle.Fill, ForeColor = WinTheme.Muted, Font = WinTheme.BodyFont(8.5f), TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true }, 0, 2);
        host.Controls.Add(card, col, row);
        return valueLabel;
    }

    private static TableLayoutPanel FormPanel(int columns, int rows, int padding = 12)
    {
        var shell = WinTheme.BorderedPanel(padding);
        shell.Dock = DockStyle.Fill;
        shell.Margin = new Padding(4, 6, 4, 6);
        var form = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = rows, BackColor = WinTheme.Panel };
        for (var i = 0; i < columns; i++)
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
        for (var i = 0; i < rows; i++)
            form.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
        shell.Controls.Add(form);
        return form;
    }

    private static void AddSectionButton(FlowLayoutPanel actions, string text, EventHandler click, bool filled = false, int width = 170, bool enabled = true)
    {
        var button = MockActionButton("", text, filled, width);
        button.Enabled = enabled;
        button.Click += click;
        actions.Controls.Add(button);
    }

    private static void AddSectionButton(FlowLayoutPanel actions, string text, Func<Task> click, bool filled = false, int width = 170, bool enabled = true)
    {
        var button = MockActionButton("", text, filled, width);
        button.Enabled = enabled;
        button.Click += async (_, _) =>
        {
            button.Enabled = false;
            try { await click(); }
            finally { button.Enabled = enabled; }
        };
        actions.Controls.Add(button);
    }

    private static string MoneyText(decimal value) => value.ToString("C2", CultureInfo.CurrentCulture);

    private static string PercentText(decimal value) => value.ToString("0.00", CultureInfo.CurrentCulture) + "%";

    private static string NextCheckNumber(IEnumerable<CheckPayout> rows)
    {
        var numbers = rows.Select(x => int.TryParse(x.CheckNumber, out var n) ? n : 0);
        var next = numbers.DefaultIfEmpty(0).Max() + 1;
        return next <= 1 ? "1" : next.ToString(CultureInfo.InvariantCulture);
    }

    private static decimal Money(string text)
        => decimal.TryParse(text.Replace("$", "").Replace(",", "").Trim(), out var value) ? value : 0m;

    private static void HideId(DataGridView grid)
    {
        if (grid.Columns.Contains("Id"))
            grid.Columns["Id"]!.Visible = false;
        grid.DataBindingComplete += (_, _) =>
        {
            if (grid.Columns.Contains("Id"))
                grid.Columns["Id"]!.Visible = false;
        };
    }

    private static void HideColumn(DataGridView grid, string name)
    {
        if (grid.Columns.Contains(name))
            grid.Columns[name]!.Visible = false;
        grid.DataBindingComplete += (_, _) =>
        {
            if (grid.Columns.Contains(name))
                grid.Columns[name]!.Visible = false;
        };
    }

    private static int? SelectedId(DataGridView grid)
    {
        if (grid.CurrentRow is null || !grid.Columns.Contains("Id"))
            return null;
        return int.TryParse(grid.CurrentRow.Cells["Id"].Value?.ToString(), out var id) ? id : null;
    }

    private static int? SelectedBankTransactionId(DataGridView grid)
    {
        if (grid.Columns.Contains("Select"))
        {
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (Convert.ToBoolean(row.Cells["Select"].Value))
                {
                    grid.CurrentCell = row.Cells["Select"];
                    break;
                }
            }
        }
        return SelectedId(grid);
    }

    private static List<T> EffectiveRows<T>(
        IEnumerable<T> all,
        Func<T, bool> isCorrection,
        Func<T, int?> correctsId,
        Func<T, int> id,
        Func<T, DateTime> createdUtc)
    {
        var originals = all.Where(x => !isCorrection(x)).ToList();
        var latestCorrections = all.Where(isCorrection)
            .Select(x => (row: x, target: correctsId(x)))
            .Where(x => x.target.HasValue)
            .GroupBy(x => x.target!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => createdUtc(x.row)).First().row);

        var result = new List<T>(originals.Count);
        foreach (var original in originals)
        {
            result.Add(latestCorrections.TryGetValue(id(original), out var correction)
                ? correction
                : original);
        }
        return result;
    }

    private async Task SyncShiftLogCashDropsToCashOnHandRecentAsync(int daysBack)
    {
        if (_currentStoreId <= 0 || _syncingShiftDrops) return;

        _syncingShiftDrops = true;
        try
        {
            var from = DateOnly.FromDateTime(DateTime.Today.AddDays(-Math.Max(1, daysBack)));
            using var db = CreateDb();
            var dates = await db.ShiftLogs.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId && x.Date >= from)
                .Select(x => x.Date)
                .Distinct()
                .ToListAsync();

            foreach (var date in dates.OrderBy(x => x))
                await SyncShiftLogCashDropsToCashOnHandAsync(date);
        }
        finally
        {
            _syncingShiftDrops = false;
        }
    }

    private async Task SyncShiftLogCashDropsToCashOnHandAsync(DateOnly date)
    {
        if (_currentStoreId <= 0) return;

        using var db = CreateDb();
        var rows = await db.ShiftLogs
            .Where(x => x.StoreId == _currentStoreId && x.Date == date)
            .OrderBy(x => x.CreatedUtc)
            .ToListAsync();

        var effective = EffectiveRows(rows, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);

        Vendor? autoVendor = null;
        Purpose? autoPurpose = null;
        try
        {
            autoVendor = await db.Vendors.FirstOrDefaultAsync(x => x.StoreId == _currentStoreId && x.Name == "Shift Log");
            if (autoVendor is null)
            {
                autoVendor = new Vendor { StoreId = _currentStoreId, Name = "Shift Log" };
                db.Vendors.Add(autoVendor);
                await db.SaveChangesAsync();
            }

            autoPurpose = await db.Purposes.FirstOrDefaultAsync(x => x.StoreId == _currentStoreId && x.Name == "Cash Drop");
            if (autoPurpose is null)
            {
                autoPurpose = new Purpose { StoreId = _currentStoreId, Name = "Cash Drop" };
                db.Purposes.Add(autoPurpose);
                await db.SaveChangesAsync();
            }
        }
        catch (SqlException ex) when (ex.Number == 207 || ex.Number == 208)
        {
            Debug.WriteLine($"Vendor/Purpose sync skipped: {ex.Message}");
        }

        var desired = new List<CashOnHandEntry>();
        foreach (var shift in effective)
        {
            if (shift.CashDropReceived == 0m) continue;

            var logicalId = shift.IsCorrection && shift.CorrectsId.HasValue ? shift.CorrectsId.Value : shift.Id;
            var description = $"Auto: Cash Drop from Shift Log (Shift {shift.ShiftNo})";
            if (!string.IsNullOrWhiteSpace(shift.Employee))
                description += $" - {shift.Employee}";

            desired.Add(new CashOnHandEntry
            {
                StoreId = _currentStoreId,
                Date = date,
                CashAdded = shift.CashDropReceived,
                IsPayout = false,
                PayoutAmount = 0m,
                VendorId = autoVendor?.Id,
                PurposeId = autoPurpose?.Id,
                Description = description,
                Reference = $"SHIFTLOG:{logicalId}",
                CreatedUtc = DateTime.UtcNow
            });
        }

        var existing = await db.CashOnHand
            .Where(x => x.StoreId == _currentStoreId && x.Date == date && x.Reference != null && x.Reference.StartsWith("SHIFTLOG:"))
            .ToListAsync();
        var legacy = await db.CashOnHand
            .Where(x => x.StoreId == _currentStoreId && x.Date == date && x.Reference == "AUTO_SHIFT_DROPS")
            .ToListAsync();
        if (legacy.Count > 0)
            db.CashOnHand.RemoveRange(legacy);

        var desiredRefs = desired.Select(x => x.Reference).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingByRef = existing.ToDictionary(x => x.Reference, StringComparer.OrdinalIgnoreCase);
        foreach (var row in desired)
        {
            if (existingByRef.TryGetValue(row.Reference, out var current))
            {
                current.CashAdded = row.CashAdded;
                current.IsPayout = false;
                current.PayoutAmount = 0m;
                current.VendorId = row.VendorId;
                current.PurposeId = row.PurposeId;
                current.Description = row.Description;
            }
            else
            {
                db.CashOnHand.Add(row);
            }
        }

        foreach (var row in existing)
        {
            if (!desiredRefs.Contains(row.Reference))
                db.CashOnHand.Remove(row);
        }

        await db.SaveChangesAsync();
    }

    private async Task DeleteSelectedAsync<T>(DataGridView grid, Action refresh) where T : class
    {
        if (!_session.IsAdmin)
        {
            MessageBox.Show(this, "Only Owner/Admin accounts can delete records.", "Access Restricted", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var id = SelectedId(grid);
        if (id is null) return;
        if (MessageBox.Show(this, "Delete selected record?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        using var db = CreateDb();
        var entity = await db.Set<T>().FindAsync(id.Value);
        if (entity is null) return;
        db.Set<T>().Remove(entity);
        await db.SaveChangesAsync();
        refresh();
    }

    private async Task<Vendor?> FindOrCreateVendorAsync(AppDbContext db, string name)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) return null;
        var existing = await db.Vendors.FirstOrDefaultAsync(x => x.StoreId == _currentStoreId && x.Name == name);
        if (existing is not null) return existing;
        var vendor = new Vendor { StoreId = _currentStoreId, Name = name };
        db.Vendors.Add(vendor);
        await db.SaveChangesAsync();
        return vendor;
    }

    private async Task<Purpose?> FindOrCreatePurposeAsync(AppDbContext db, string name)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) return null;
        var existing = await db.Purposes.FirstOrDefaultAsync(x => x.StoreId == _currentStoreId && x.Name == name);
        if (existing is not null) return existing;
        var purpose = new Purpose { StoreId = _currentStoreId, Name = name };
        db.Purposes.Add(purpose);
        await db.SaveChangesAsync();
        return purpose;
    }
}

internal sealed record ChartPoint(string Label, decimal Value);

internal sealed record BreakdownItem(string Label, decimal Value, Color Color);

internal sealed record DashboardTransaction(
    DateOnly Date,
    string Type,
    string Reference,
    string Description,
    string Category,
    decimal Amount,
    string Status);

internal sealed class OperationsSparkline : Control
{
    private IReadOnlyList<decimal> _values;
    private readonly Color _lineColor;
    private readonly string _caption;

    public OperationsSparkline(IReadOnlyList<decimal> values, Color lineColor, string caption)
    {
        _values = values;
        _lineColor = lineColor;
        _caption = caption;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(247, 250, 253);
        MinimumSize = new Size(100, 40);
    }

    public void SetValues(IReadOnlyList<decimal> values)
    {
        _values = values;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var captionRect = new Rectangle(8, 2, Math.Max(20, Width - 16), 15);
        TextRenderer.DrawText(
            e.Graphics,
            _caption,
            WinTheme.BoldFont(7),
            captionRect,
            WinTheme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        var graph = new Rectangle(8, 19, Math.Max(20, Width - 16), Math.Max(12, Height - 24));
        using var baseline = new Pen(Color.FromArgb(218, 226, 234));
        e.Graphics.DrawLine(baseline, graph.Left, graph.Bottom, graph.Right, graph.Bottom);
        if (_values.Count == 0)
        {
            TextRenderer.DrawText(
                e.Graphics,
                "No activity",
                WinTheme.BodyFont(7),
                graph,
                WinTheme.Muted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            return;
        }

        var min = Math.Min(0m, _values.Min());
        var max = Math.Max(0m, _values.Max());
        if (max == min)
            max = min + 1m;

        var points = _values.Select((value, index) =>
        {
            var x = _values.Count == 1
                ? graph.Left + graph.Width / 2f
                : graph.Left + index * graph.Width / (float)(_values.Count - 1);
            var y = graph.Bottom - (float)((value - min) / (max - min)) * graph.Height;
            return new PointF(x, y);
        }).ToArray();

        if (min < 0 && max > 0)
        {
            var zeroY = graph.Bottom - (float)((0m - min) / (max - min)) * graph.Height;
            using var zeroPen = new Pen(Color.FromArgb(202, 213, 223)) { DashStyle = DashStyle.Dot };
            e.Graphics.DrawLine(zeroPen, graph.Left, zeroY, graph.Right, zeroY);
        }

        using var fill = new SolidBrush(Color.FromArgb(28, _lineColor));
        using var line = new Pen(_lineColor, 2.2f);
        if (points.Length == 1)
        {
            e.Graphics.FillEllipse(fill, points[0].X - 4, points[0].Y - 4, 8, 8);
            e.Graphics.DrawEllipse(line, points[0].X - 3, points[0].Y - 3, 6, 6);
            return;
        }

        using var areaPath = new GraphicsPath();
        areaPath.AddLines(points);
        areaPath.AddLine(points[^1].X, points[^1].Y, points[^1].X, graph.Bottom);
        areaPath.AddLine(points[^1].X, graph.Bottom, points[0].X, graph.Bottom);
        areaPath.CloseFigure();
        e.Graphics.FillPath(fill, areaPath);
        e.Graphics.DrawLines(line, points);
    }
}

internal sealed class CheckPreviewPanel : Control
{
    public string Payee { get; set; } = "Vendor";
    public decimal Amount { get; set; }
    public string CheckNumber { get; set; } = "Next";

    public CheckPreviewPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(235, 240, 244);
        ForeColor = Color.Black;
        MinimumSize = new Size(280, 130);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(4, 4, Width - 8, Height - 8);
        using var bg = new SolidBrush(Color.FromArgb(240, 244, 248));
        using var border = new Pen(Color.FromArgb(80, 96, 112));
        e.Graphics.FillRectangle(bg, bounds);
        e.Graphics.DrawRectangle(border, bounds);

        var amountText = Amount == 0 ? "$ 0.00" : Amount.ToString("C2", CultureInfo.CurrentCulture);
        TextRenderer.DrawText(e.Graphics, "PAY TO THE", WinTheme.BoldFont(7), new Rectangle(bounds.Left + 12, bounds.Top + 14, 70, 18), Color.FromArgb(50, 60, 70));
        TextRenderer.DrawText(e.Graphics, "ORDER OF", WinTheme.BoldFont(7), new Rectangle(bounds.Left + 12, bounds.Top + 30, 70, 18), Color.FromArgb(50, 60, 70));
        TextRenderer.DrawText(e.Graphics, Payee, WinTheme.BoldFont(10), new Rectangle(bounds.Left + 88, bounds.Top + 19, bounds.Width - 190, 28), Color.Black, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, amountText, WinTheme.BoldFont(11), new Rectangle(bounds.Right - 118, bounds.Top + 18, 104, 30), Color.Black, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        using var linePen = new Pen(Color.FromArgb(125, 140, 150));
        e.Graphics.DrawLine(linePen, bounds.Left + 86, bounds.Top + 50, bounds.Right - 14, bounds.Top + 50);
        e.Graphics.DrawLine(linePen, bounds.Left + 14, bounds.Top + 82, bounds.Right - 132, bounds.Top + 82);
        TextRenderer.DrawText(e.Graphics, "DOLLARS", WinTheme.BoldFont(7), new Rectangle(bounds.Right - 116, bounds.Top + 68, 86, 22), Color.FromArgb(50, 60, 70));
        TextRenderer.DrawText(e.Graphics, "Bank: Store Operating Account", WinTheme.BodyFont(7), new Rectangle(bounds.Left + 14, bounds.Bottom - 38, 180, 18), Color.FromArgb(45, 55, 65));
        TextRenderer.DrawText(e.Graphics, "Check No:", WinTheme.BoldFont(7), new Rectangle(bounds.Right - 118, bounds.Bottom - 38, 64, 18), Color.FromArgb(45, 55, 65));
        TextRenderer.DrawText(e.Graphics, CheckNumber, WinTheme.BoldFont(8), new Rectangle(bounds.Right - 54, bounds.Bottom - 38, 48, 18), Color.Black, TextFormatFlags.Right);
    }
}

internal sealed class KpiCardPanel : Panel
{
    private readonly string _title;
    private readonly string _value;
    private readonly string _subtitle;
    private readonly Color _valueColor;

    public KpiCardPanel(string title, string value, string subtitle, Color valueColor)
    {
        _title = title;
        _value = value;
        _subtitle = subtitle;
        _valueColor = valueColor;
        DoubleBuffered = true;
        BackColor = WinTheme.Panel;
        MinimumSize = new Size(160, 92);
        Padding = new Padding(14, 10, 14, 10);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var borderPen = new Pen(WinTheme.CopperDark);
        e.Graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);

        var inner = new Rectangle(Padding.Left, Padding.Top, Width - Padding.Horizontal, Height - Padding.Vertical);
        var titleRect = new Rectangle(inner.Left, inner.Top, inner.Width, 22);
        var valueRect = new Rectangle(inner.Left, inner.Top + 20, inner.Width, Math.Max(46, inner.Height - 42));
        var subRect = new Rectangle(inner.Left, inner.Bottom - 22, inner.Width, 22);

        TextRenderer.DrawText(e.Graphics, _title, WinTheme.BoldFont(10), titleRect, WinTheme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        using var valueFont = FitFont(e.Graphics, _value, WinTheme.HeaderFont(34), valueRect.Size);
        TextRenderer.DrawText(e.Graphics, _value, valueFont, valueRect, _valueColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(e.Graphics, _subtitle, WinTheme.BodyFont(9), subRect, WinTheme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private static Font FitFont(Graphics graphics, string text, Font preferred, Size available)
    {
        var size = preferred.Size;
        while (size > 11)
        {
            using var probe = new Font(preferred.FontFamily, size, preferred.Style);
            var measured = TextRenderer.MeasureText(graphics, text, probe, available, TextFormatFlags.NoPadding);
            if (measured.Width <= available.Width && measured.Height <= available.Height)
                return new Font(preferred.FontFamily, size, preferred.Style);
            size -= 1f;
        }
        return new Font(preferred.FontFamily, size, preferred.Style);
    }
}

internal sealed class SalesLineChart : Control
{
    private readonly IReadOnlyList<ChartPoint> _points;

    public SalesLineChart(IReadOnlyList<ChartPoint> points)
    {
        _points = points;
        DoubleBuffered = true;
        BackColor = WinTheme.Panel;
        ForeColor = WinTheme.Text;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var area = new Rectangle(46, 20, Math.Max(40, Width - 76), Math.Max(40, Height - 56));
        using var gridPen = new Pen(Color.FromArgb(45, 69, 88));
        using var axisPen = new Pen(Color.FromArgb(75, 94, 112));
        using var linePen = new Pen(Color.FromArgb(229, 126, 45), 3);
        using var fillBrush = new SolidBrush(Color.FromArgb(229, 126, 45));
        using var labelBrush = new SolidBrush(WinTheme.Muted);

        for (var i = 0; i <= 4; i++)
        {
            var y = area.Bottom - i * area.Height / 4;
            e.Graphics.DrawLine(gridPen, area.Left, y, area.Right, y);
        }
        e.Graphics.DrawLine(axisPen, area.Left, area.Bottom, area.Right, area.Bottom);
        e.Graphics.DrawLine(axisPen, area.Left, area.Top, area.Left, area.Bottom);

        if (_points.Count == 0)
        {
            TextRenderer.DrawText(e.Graphics, "No sales data for this month", WinTheme.BodyFont(11), area, WinTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        var max = Math.Max(1m, _points.Max(x => x.Value));
        var step = _points.Count == 1 ? 0f : area.Width / (float)(_points.Count - 1);
        var chartPoints = _points.Select((point, index) =>
        {
            var x = area.Left + index * step;
            var y = area.Bottom - (float)(point.Value / max) * area.Height;
            return new PointF(x, y);
        }).ToArray();

        if (chartPoints.Length == 1)
        {
            e.Graphics.FillEllipse(fillBrush, chartPoints[0].X - 4, chartPoints[0].Y - 4, 8, 8);
        }
        else
        {
            e.Graphics.DrawLines(linePen, chartPoints);
            foreach (var point in chartPoints)
                e.Graphics.FillEllipse(fillBrush, point.X - 4, point.Y - 4, 8, 8);
        }

        var first = _points.First().Label;
        var last = _points.Last().Label;
        TextRenderer.DrawText(e.Graphics, first, WinTheme.BodyFont(9), new Point(area.Left - 3, area.Bottom + 8), WinTheme.Muted);
        TextRenderer.DrawText(e.Graphics, last, WinTheme.BodyFont(9), new Point(area.Right - 24, area.Bottom + 8), WinTheme.Muted);
        TextRenderer.DrawText(e.Graphics, max.ToString("C0"), WinTheme.BodyFont(9), new Point(2, area.Top - 4), WinTheme.Muted);
        TextRenderer.DrawText(e.Graphics, "$0", WinTheme.BodyFont(9), new Point(18, area.Bottom - 10), WinTheme.Muted);
    }
}

internal sealed class DonutBreakdownControl : Control
{
    private readonly IReadOnlyList<BreakdownItem> _items;

    public DonutBreakdownControl(IReadOnlyList<BreakdownItem> items)
    {
        _items = items;
        DoubleBuffered = true;
        BackColor = WinTheme.Panel;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var total = _items.Sum(x => x.Value);
        if (total <= 0)
        {
            TextRenderer.DrawText(e.Graphics, "No payout data for this month", WinTheme.BodyFont(11), ClientRectangle, WinTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        var donutSize = Math.Min(Height - 36, Width / 2 - 26);
        donutSize = Math.Max(120, donutSize);
        var donut = new Rectangle(24, 20, donutSize, donutSize);
        var start = -90f;
        foreach (var item in _items)
        {
            var sweep = (float)(item.Value / total * 360m);
            using var pen = new Pen(item.Color, Math.Max(18, donutSize / 8))
            {
                StartCap = LineCap.Flat,
                EndCap = LineCap.Flat
            };
            e.Graphics.DrawArc(pen, donut, start, sweep);
            start += sweep;
        }

        TextRenderer.DrawText(e.Graphics, "Total", WinTheme.BodyFont(10), new Rectangle(donut.Left, donut.Top + donut.Height / 2 - 28, donut.Width, 24), WinTheme.Muted, TextFormatFlags.HorizontalCenter);
        TextRenderer.DrawText(e.Graphics, total.ToString("C0"), WinTheme.HeaderFont(15), new Rectangle(donut.Left, donut.Top + donut.Height / 2 - 4, donut.Width, 32), WinTheme.Text, TextFormatFlags.HorizontalCenter);

        var legendX = donut.Right + 34;
        var legendY = donut.Top + 6;
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var y = legendY + i * 38;
            using var swatch = new SolidBrush(item.Color);
            e.Graphics.FillRectangle(swatch, legendX, y + 5, 13, 13);
            var percent = item.Value / total;
            TextRenderer.DrawText(e.Graphics, item.Label, WinTheme.BodyFont(10), new Rectangle(legendX + 24, y, Math.Max(120, Width - legendX - 180), 24), WinTheme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(e.Graphics, item.Value.ToString("C0"), WinTheme.BoldFont(10), new Rectangle(Width - 154, y, 78, 24), WinTheme.Text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(e.Graphics, percent.ToString("P1"), WinTheme.BodyFont(10), new Rectangle(Width - 70, y, 58, 24), WinTheme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
    }
}

internal sealed class StoreDbContextFactory : IDbContextFactory<AppDbContext>
{
    private readonly StoreConnectionService _storeConnections;

    public StoreDbContextFactory(StoreConnectionService storeConnections)
    {
        _storeConnections = storeConnections;
    }

    public AppDbContext CreateDbContext() => _storeConnections.CreateDbContext();
}

internal sealed record BankMonthItem(int Value, string Name)
{
    public override string ToString() => Name;
}

internal sealed record BankStatementRow(
    int Id,
    DateTime Date,
    string Description,
    decimal Credit,
    decimal Debit,
    string CheckNumber,
    string Category,
    int StatementMonth,
    int StatementYear,
    string Source = "Statement Import",
    bool IsMatched = false,
    string MatchReference = "",
    bool IncludeInProfitLoss = false);

internal sealed class PurchaseInvoiceGridRow
{
    public int Id { get; set; }
    public bool Select { get; set; }
    public DateOnly Date { get; set; }
    public string Vendor { get; set; } = "";
    public string Invoice { get; set; } = "";
    public decimal Total { get; set; }
    public string Attachment { get; set; } = "";
    public string Status { get; set; } = "";
}

internal sealed class BankStatementGridRow
{
    public int Id { get; set; }
    public bool IncludeInProfitLoss { get; set; }
    public DateTime Date { get; set; }
    public string Source { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string Category { get; set; } = "";
    public bool Matched { get; set; }
    public string MatchReference { get; set; } = "";
    public string Check { get; set; } = "";
}

internal sealed class CheckPayoutGridRow
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public string Vendor { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public string Check { get; set; } = "";
    public bool Cleared { get; set; }
}

internal sealed record LocalBankConnectionStatus(
    string InstitutionName,
    string AccountName,
    string AccountMask,
    string Status,
    DateTime? LastSyncedUtc,
    string LastError);

internal static class EntryGridPageExtensions
{
    public static void AddButton(this (TableLayoutPanel Root, TableLayoutPanel Fields, FlowLayoutPanel Actions, DataGridView Grid) page, string text, Action action, bool filled = false, bool adminOnly = false)
    {
        var button = WinTheme.Button(text, filled);
        button.Width = Math.Max(165, text.Length * 12);
        button.Height = 44;
        button.Enabled = !adminOnly || ProgramServices.TryGet<SessionState>(out var s) && s.IsAdmin;
        button.Click += (_, _) => action();
        page.Actions.Controls.Add(button);
    }

    public static void AddButton(this (TableLayoutPanel Root, TableLayoutPanel Fields, FlowLayoutPanel Actions, DataGridView Grid) page, string text, Func<Task> action, bool filled = false, bool adminOnly = false)
    {
        var button = WinTheme.Button(text, filled);
        button.Width = Math.Max(165, text.Length * 12);
        button.Height = 44;
        button.Enabled = !adminOnly || ProgramServices.TryGet<SessionState>(out var s) && s.IsAdmin;
        button.Click += async (_, _) =>
        {
            button.Enabled = false;
            try { await action(); }
            catch (Exception ex) { MessageBox.Show(AppBootstrap.RedactSensitiveText(ex.Message), text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { button.Enabled = true; }
        };
        page.Actions.Controls.Add(button);
    }
}
