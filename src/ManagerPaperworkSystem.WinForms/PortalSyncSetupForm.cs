using ManagerPaperworkSystem.Core.Services;

namespace ManagerPaperworkSystem.WinForms;

internal sealed class PortalSyncSetupForm : Form
{
    private readonly IAppPaths _paths;
    private readonly PortalSyncReportKind _reportKind;
    private readonly CancellationTokenSource _syncCancellation = new();
    private bool _syncRunning;
    private readonly ComboBox _business = WinTheme.ComboBox();
    private readonly TextBox _portalUrl = WinTheme.TextBox();
    private readonly TextBox _portalStore = WinTheme.TextBox();
    private readonly TextBox _email = WinTheme.TextBox();
    private readonly TextBox _portalPassword = WinTheme.TextBox();
    private readonly TextBox _storeUser = WinTheme.TextBox();
    private readonly TextBox _storePassword = WinTheme.TextBox();
    private readonly DateTimePicker _runTime = new()
    {
        Format = DateTimePickerFormat.Time,
        ShowUpDown = true,
        Dock = DockStyle.Fill,
        Font = WinTheme.BodyFont(10)
    };
    private readonly DateTimePicker _historicalFrom = new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "MM/dd/yyyy",
        Dock = DockStyle.Fill,
        Font = WinTheme.BodyFont(10)
    };
    private readonly DateTimePicker _historicalThrough = new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "MM/dd/yyyy",
        Dock = DockStyle.Fill,
        Font = WinTheme.BodyFont(10)
    };
    private readonly Label _zBatchMode = new()
    {
        Text = "NEXT BATCH AUTOMATIC",
        Dock = DockStyle.Fill,
        ForeColor = WinTheme.Blue,
        Font = WinTheme.BoldFont(9.5f),
        TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly CheckBox _enabled = new()
    {
        Text = "Enable unattended daily download and import",
        AutoSize = true,
        Checked = true,
        ForeColor = WinTheme.Text,
        Font = WinTheme.BoldFont(9.5f)
    };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill,
        ForeColor = WinTheme.Muted,
        Font = WinTheme.BodyFont(9.5f),
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true
    };
    private readonly PortalSyncSettingsDocument _document;
    private readonly IReadOnlyList<LicensedBusinessConnection> _licensedBusinesses;

    public PortalSyncSetupForm(IAppPaths paths)
        : this(paths, PortalSyncReportKind.CashSalesSummary)
    {
    }

    public PortalSyncSetupForm(IAppPaths paths, PortalSyncReportKind reportKind)
        : this(paths, reportKind, 0, "")
    {
    }

    public PortalSyncSetupForm(
        IAppPaths paths,
        PortalSyncReportKind reportKind,
        int preferredBusinessId,
        string preferredDatabaseName)
    {
        _paths = paths;
        _reportKind = reportKind;
        _document = PortalSyncSettingsStore.Load();
        _licensedBusinesses = StoreDirectoryPreferencesStore.GetOrderedBusinesses(
            LicensedBusinessService.Load());

        WinTheme.Apply(this);
        Text = $"{ReportDisplayName} Auto Sync - HISAB KITAB";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(760, 650);
        Size = new Size(980, 780);
        AutoScaleMode = AutoScaleMode.Dpi;
        Controls.Add(BuildContent());

        _portalPassword.UseSystemPasswordChar = true;
        _storePassword.UseSystemPasswordChar = true;
        _portalUrl.Text = "https://posweboffice.com/";
        _runTime.Value = DateTime.Today.AddHours(1).AddMinutes(15);
        var yesterday = DateTime.Today.AddDays(-1);
        _historicalFrom.MaxDate = yesterday;
        _historicalThrough.MaxDate = yesterday;
        _historicalFrom.Value = DateTime.Today.AddDays(-30);
        _historicalThrough.Value = yesterday;
        _enabled.Text = $"Enable unattended daily {ReportDisplayName.ToLowerInvariant()} sync";
        _zBatchMode.Text = _reportKind == PortalSyncReportKind.ZReports
            ? "NEXT BATCH AUTOMATIC"
            : "NEXT DATE AUTOMATIC";
        _business.DataSource = _licensedBusinesses.ToList();
        _business.DisplayMember = nameof(LicensedBusinessConnection.BusinessName);
        _business.SelectedIndexChanged += (_, _) => LoadSelectedBusiness();
        var preferredBusiness = _licensedBusinesses.FirstOrDefault(business =>
                                    preferredBusinessId > 0 &&
                                    business.BusinessId == preferredBusinessId)
                                ?? _licensedBusinesses.FirstOrDefault(business =>
                                    !string.IsNullOrWhiteSpace(preferredDatabaseName) &&
                                    string.Equals(
                                        business.DatabaseName,
                                        preferredDatabaseName,
                                        StringComparison.OrdinalIgnoreCase));
        if (preferredBusiness is not null)
            _business.SelectedItem = preferredBusiness;
        else if (_business.Items.Count > 0)
            _business.SelectedIndex = 0;
        if (_business.SelectedItem is not null)
            LoadSelectedBusiness();

        FormClosing += (_, _) =>
        {
            if (_syncRunning)
                _syncCancellation.Cancel();
        };
        FormClosed += (_, _) =>
        {
            if (!_syncRunning)
                _syncCancellation.Dispose();
        };
    }

    private string ReportDisplayName =>
        _reportKind == PortalSyncReportKind.CashSalesSummary
            ? "Cash & Sales Summary"
            : "Z Reports";

    private Control BuildContent()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Bg,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 4
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));

        var heading = new Panel { Dock = DockStyle.Fill, BackColor = WinTheme.BlueDark };
        heading.Controls.Add(new Label
        {
            Text = $"AUTOMATIC {ReportDisplayName.ToUpperInvariant()} SYNC",
            Dock = DockStyle.Top,
            Height = 50,
            Padding = new Padding(18, 10, 0, 0),
            ForeColor = Color.White,
            Font = WinTheme.HeaderFont(19)
        });
        heading.Controls.Add(new Label
        {
            Text = "Separate per-store schedule • encrypted credentials • unattended daily import",
            Dock = DockStyle.Bottom,
            Height = 32,
            Padding = new Padding(20, 0, 0, 8),
            ForeColor = Color.FromArgb(205, 224, 244),
            Font = WinTheme.BodyFont(10)
        });
        root.Controls.Add(heading, 0, 0);

        var card = WinTheme.BorderedPanel(14);
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(0, 14, 0, 8);
        card.AutoScroll = true;
        root.Controls.Add(card, 0, 1);

        var form = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Panel,
            Padding = new Padding(16, 12, 16, 12),
            ColumnCount = 4,
            RowCount = 8,
            AutoScroll = true,
            AutoScrollMinSize = new Size(680, 550)
        };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        for (var row = 0; row < 6; row++)
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        form.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        form.RowStyles.Add(new RowStyle(SizeType.Absolute, 144));
        card.Controls.Add(form);

        AddField(form, "LICENSED HISAB KITAB STORE *", _business, 0, 0, 2);
        AddField(form, "DAILY RUN TIME", _runTime, 2, 0, 1);
        AddField(form, "SYNC CURSOR", _zBatchMode, 3, 0, 1);
        AddField(form, "ADVENTPOS WEB PORTAL", _portalUrl, 0, 1, 4);
        AddField(form, "PORTAL EMAIL *", _email, 0, 2, 2);
        AddField(form, "PORTAL PASSWORD *", _portalPassword, 2, 2, 2);
        AddField(form, "ADVENTPOS STORE NAME *", _portalStore, 0, 3, 4);
        AddField(form, "STORE USER NAME", _storeUser, 0, 4, 2);
        AddField(form, "STORE PASSWORD", _storePassword, 2, 4, 2);
        form.Controls.Add(_enabled, 0, 5);
        form.SetColumnSpan(_enabled, 4);
        _enabled.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        _enabled.Margin = new Padding(6, 12, 6, 0);
        AddField(form, "GO BACK TO DATE (CALENDAR)", _historicalFrom, 0, 6, 2);
        AddField(form, "IMPORT THROUGH (CALENDAR)", _historicalThrough, 2, 6, 2);

        form.Controls.Add(new Label
        {
            Text =
                "ONE-TIME SETUP\n" +
                "1. Save the settings.  2. Open the dedicated Chrome profile.  " +
                "3. Complete any AdventPOS verification and select the correct store.  " +
                "4. Close Chrome and use TEST / SYNC NOW.\n\n" +
                (_reportKind == PortalSyncReportKind.CashSalesSummary
                    ? "This schedule fetches only Cash & Sales Summary reports. It resumes with the calendar day after " +
                      "the latest summary already imported. Cash drop is supplied separately from matching Z-report " +
                      "rows in Shift Cash Drop."
                    : "This schedule fetches only Close-Out Z Reports. It resumes with the next AdventPOS batch after " +
                      "the highest numeric Shift/Batch already present in Shift Cash Drop.") +
                " If the PC is off, HISAB KITAB catches up automatically the next time Windows can run the task.\n\n" +
                "DEVELOPER HISTORICAL BACKFILL\n" +
                "Choose a past date range and click BACKFILL PAST REPORTS. This imports only the selected store and " +
                "does not move its normal daily sync cursor backward.",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Text,
            Font = WinTheme.BodyFont(10),
            Padding = new Padding(8, 12, 8, 4)
        }, 0, 7);
        form.SetColumnSpan(form.GetControlFromPosition(0, 7)!, 4);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Bg,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoScroll = true,
            Padding = new Padding(0, 5, 0, 5)
        };
        root.Controls.Add(actions, 0, 2);

        var save = ActionButton("SAVE SETUP", true, 180);
        var enroll = ActionButton("ONE-TIME SETUP", false, 205);
        var test = ActionButton(
            _reportKind == PortalSyncReportKind.CashSalesSummary
                ? "SYNC CASH & SALES NOW"
                : "SYNC Z REPORTS NOW",
            true,
            215);
        var backfill = ActionButton("BACKFILL PAST REPORTS", false, 230);
        var close = ActionButton("CLOSE", false, 120);
        actions.Controls.Add(save);
        actions.Controls.Add(enroll);
        actions.Controls.Add(test);
        actions.Controls.Add(backfill);
        actions.Controls.Add(close);

        save.Click += (_, _) => SaveSettings(showConfirmation: true);
        enroll.Click += (_, _) =>
        {
            try
            {
                var settings = SaveSettings(showConfirmation: false);
                PortalSyncService.OpenEnrollmentChrome(settings);
                _status.Text =
                    "Dedicated Chrome opened. Complete verification/store selection once, then close that Chrome window.";
            }
            catch (Exception exception)
            {
                ShowError(exception);
            }
        };
        test.Click += async (_, _) => await RunSelectedSyncAsync(actions);
        backfill.Click += (_, _) =>
        {
            var from = DateOnly.FromDateTime(_historicalFrom.Value);
            var through = DateOnly.FromDateTime(_historicalThrough.Value);
            var yesterday = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
            if (through > yesterday)
            {
                MessageBox.Show(
                    this,
                    "Historical backfill can run only through yesterday.",
                    $"{ReportDisplayName} Historical Backfill",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
            if (through < from)
            {
                MessageBox.Show(
                    this,
                    "The historical through date must be on or after the start date.",
                    $"{ReportDisplayName} Historical Backfill",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
            if (through.DayNumber - from.DayNumber > 365)
            {
                MessageBox.Show(
                    this,
                    "Historical backfill is limited to 366 days per run. Choose a shorter range and run another backfill if needed.",
                    $"{ReportDisplayName} Historical Backfill",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(
                    this,
                    $"Backfill {ReportDisplayName} for the selected store from " +
                    $"{from:M/d/yyyy} through {through:M/d/yyyy}?\r\n\r\n" +
                    "The process will continue in the background and preserve the normal daily sync cursor. " +
                    "You can continue using HISAB KITAB after it starts.",
                    $"{ReportDisplayName} Historical Backfill",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }
            try
            {
                var selectedSettings = SaveSettings(showConfirmation: false);
                var processId = PortalSyncService.StartHistoricalBackfill(
                    selectedSettings.Id,
                    _reportKind,
                    from,
                    through);
                _status.Text =
                    $"Background {ReportDisplayName} backfill started for " +
                    $"{from:M/d/yyyy} - {through:M/d/yyyy}. Process {processId}. " +
                    "Progress is saved automatically; reopen this setup to see the latest result.";
                MessageBox.Show(
                    this,
                    _status.Text + "\r\n\r\nThis setup window will now close, but the import will continue.",
                    $"{ReportDisplayName} Historical Backfill",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                Close();
            }
            catch (Exception exception)
            {
                ShowError(exception);
            }
        };
        close.Click += (_, _) => Close();

        var statusCard = WinTheme.BorderedPanel(8);
        statusCard.Dock = DockStyle.Fill;
        statusCard.Margin = new Padding(0, 8, 0, 0);
        statusCard.Controls.Add(_status);
        _status.Padding = new Padding(12, 0, 12, 0);
        root.Controls.Add(statusCard, 0, 3);
        return root;
    }

    private async Task RunSelectedSyncAsync(
        Control actions,
        DateOnly? historicalFrom = null,
        DateOnly? historicalThrough = null)
    {
        try
        {
            var selectedSettings = SaveSettings(showConfirmation: false);
            _syncRunning = true;
            ToggleActions(actions, false);
            _status.Text = historicalFrom.HasValue
                ? $"Waiting for any automatic run to finish, then backfilling {ReportDisplayName} " +
                  $"from {historicalFrom:M/d/yyyy} through {historicalThrough:M/d/yyyy}..."
                : $"Waiting for any automatic run to finish, then requesting {ReportDisplayName}...";
            var results = await PortalSyncService.RunDueAsync(
                _paths,
                true,
                true,
                onlyStoreConfigurationId: selectedSettings.Id,
                onlyReportKind: _reportKind,
                waitForExistingRun: true,
                historicalStartDate: historicalFrom,
                historicalEndDate: historicalThrough,
                cancellationToken: _syncCancellation.Token);
            if (!CanUpdateWindow())
                return;

            if (historicalFrom.HasValue)
            {
                var succeeded = results.Count(result => result.Success);
                var failed = results.Count - succeeded;
                _status.Text =
                    $"{ReportDisplayName} historical backfill completed for " +
                    $"{historicalFrom:M/d/yyyy} - {historicalThrough:M/d/yyyy}. " +
                    $"Successful: {succeeded}; Failed: {failed}.";
                MessageBox.Show(
                    this,
                    failed == 0
                        ? _status.Text
                        : _status.Text + "\r\n\r\n" +
                          string.Join("\r\n", results
                              .Where(result => !result.Success)
                              .Select(result => result.Message)
                              .Take(8)),
                    $"{ReportDisplayName} Historical Backfill",
                    MessageBoxButtons.OK,
                    failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            else
            {
                _status.Text = results.Count == 0
                    ? "No enabled store configuration was found."
                    : string.Join("  ", results.Select(result => result.Message));
                if (results.Any(result => !result.Success))
                {
                    MessageBox.Show(
                        this,
                        _status.Text,
                        $"{ReportDisplayName} Auto Sync",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Closing this setup window intentionally cancels its visible run.
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            _syncRunning = false;
            if (CanUpdateWindow())
                ToggleActions(actions, true);
            else
                _syncCancellation.Dispose();
        }
    }

    private PortalStoreSyncSettings SaveSettings(bool showConfirmation)
    {
        if (_business.SelectedItem is not LicensedBusinessConnection business)
            throw new InvalidOperationException("Select a licensed HISAB KITAB store.");
        if (string.IsNullOrWhiteSpace(_portalUrl.Text) ||
            !Uri.TryCreate(_portalUrl.Text.Trim(), UriKind.Absolute, out _))
            throw new InvalidOperationException("Enter a valid AdventPOS web portal address.");
        if (string.IsNullOrWhiteSpace(_portalStore.Text))
            throw new InvalidOperationException("Enter the store name exactly as it appears in AdventPOS.");
        if (string.IsNullOrWhiteSpace(_email.Text) || string.IsNullOrWhiteSpace(_portalPassword.Text))
            throw new InvalidOperationException(
                "Portal email and password are required so daily sync can recover after a portal session expires.");
        if (string.IsNullOrWhiteSpace(_storeUser.Text) || string.IsNullOrWhiteSpace(_storePassword.Text))
            throw new InvalidOperationException(
                "The AdventPOS store user name and password are required for unattended daily sign-in.");

        var settings = FindSettings(business) ?? new PortalStoreSyncSettings();
        PortalSyncSettingsStore.BindToBusiness(settings, business);
        settings.PortalUrl = _portalUrl.Text.Trim();
        settings.PortalStoreName = _portalStore.Text.Trim();
        settings.PortalEmail = _email.Text.Trim();
        settings.PortalPassword = _portalPassword.Text;
        settings.StoreUserName = _storeUser.Text.Trim();
        settings.StorePassword = _storePassword.Text;
        if (_reportKind == PortalSyncReportKind.CashSalesSummary)
        {
            settings.CashSalesSummaryEnabled = _enabled.Checked;
            settings.CashSalesDailyHour = _runTime.Value.Hour;
            settings.CashSalesDailyMinute = _runTime.Value.Minute;
        }
        else
        {
            settings.ZReportsEnabled = _enabled.Checked;
            settings.ZReportsDailyHour = _runTime.Value.Hour;
            settings.ZReportsDailyMinute = _runTime.Value.Minute;
        }
        settings.Enabled = settings.CashSalesSummaryEnabled || settings.ZReportsEnabled;
        settings.DailyHour = settings.CashSalesDailyHour;
        settings.DailyMinute = settings.CashSalesDailyMinute;
        if (!_document.Stores.Contains(settings))
            _document.Stores.Add(settings);
        PortalSyncSettingsStore.Save(_document);

        PortalSyncScheduleResult? scheduleResult = null;
        if (settings.IsEnabled(_reportKind))
            scheduleResult = PortalSyncService.EnsureDailyTask(
                settings.Id,
                _reportKind,
                settings.GetRunTime(_reportKind));
        else
            PortalSyncService.RemoveDailyTask(settings.Id, _reportKind);

        var scheduleStatus = scheduleResult?.Message ?? "Automatic sync is disabled for this report.";
        _status.Text =
            $"Saved {ReportDisplayName} sync for {business.BusinessName}. " +
            $"{scheduleStatus} " +
            $"Last result: {settings.GetLastStatus(_reportKind)}";
        if (showConfirmation)
            MessageBox.Show(this,
                $"The protected {ReportDisplayName} settings were saved.\r\n\r\n{scheduleStatus}",
                $"{ReportDisplayName} Auto Sync",
                MessageBoxButtons.OK,
                scheduleResult is { WindowsTaskCreated: false }
                    ? MessageBoxIcon.Warning
                    : MessageBoxIcon.Information);
        return settings;
    }

    private void LoadSelectedBusiness()
    {
        if (_business.SelectedItem is not LicensedBusinessConnection business)
            return;
        var settings = FindSettings(business);
        _portalUrl.Text = settings?.PortalUrl ?? "https://posweboffice.com/";
        _portalStore.Text = settings?.PortalStoreName ?? business.BusinessName;
        _email.Text = settings?.PortalEmail ?? "";
        _portalPassword.Text = settings?.PortalPassword ?? "";
        _storeUser.Text = settings?.StoreUserName ?? "";
        _storePassword.Text = settings?.StorePassword ?? "";
        _enabled.Checked = settings?.IsEnabled(_reportKind) ?? true;
        var runTime = settings?.GetRunTime(_reportKind) ??
                      (_reportKind == PortalSyncReportKind.CashSalesSummary
                          ? new TimeOnly(1, 15)
                          : new TimeOnly(1, 30));
        _runTime.Value = DateTime.Today.Add(runTime.ToTimeSpan());
        _status.Text = settings is null
            ? $"No automatic {ReportDisplayName} setup exists for {business.BusinessName}."
            : $"Last {ReportDisplayName} result: {settings.GetLastStatus(_reportKind)}";
    }

    private PortalStoreSyncSettings? FindSettings(LicensedBusinessConnection business) =>
        PortalSyncSettingsStore.FindForBusiness(_document.Stores, business);

    private void ShowError(Exception exception)
    {
        if (!CanUpdateWindow())
            return;
        _status.Text = AppBootstrap.RedactSensitiveText(exception.Message);
        MessageBox.Show(this, _status.Text, $"{ReportDisplayName} Auto Sync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private bool CanUpdateWindow() =>
        !IsDisposed && !Disposing && IsHandleCreated;

    private static void ToggleActions(Control root, bool enabled)
    {
        foreach (Control control in root.Controls)
            control.Enabled = enabled;
    }

    private static Button ActionButton(string text, bool primary = false, int width = 180)
    {
        var button = WinTheme.Button(text, primary);
        button.Width = width;
        button.Height = 44;
        button.Margin = new Padding(5);
        return button;
    }

    private static void AddField(
        TableLayoutPanel form,
        string label,
        Control control,
        int column,
        int row,
        int span)
    {
        var host = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Panel,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(6, 2, 6, 2)
        };
        host.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        host.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Text,
            Font = WinTheme.BoldFont(9),
            TextAlign = ContentAlignment.BottomLeft
        }, 0, 0);
        control.Dock = DockStyle.Fill;
        host.Controls.Add(control, 0, 1);
        form.Controls.Add(host, column, row);
        form.SetColumnSpan(host, span);
    }
}
