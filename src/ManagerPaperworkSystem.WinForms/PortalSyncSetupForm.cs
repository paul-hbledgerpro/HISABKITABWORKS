using ManagerPaperworkSystem.Core.Services;

namespace ManagerPaperworkSystem.WinForms;

internal sealed class PortalSyncSetupForm : Form
{
    private readonly IAppPaths _paths;
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
    {
        _paths = paths;
        _document = PortalSyncSettingsStore.Load();
        _licensedBusinesses = LicensedBusinessService.Load()
            .OrderByDescending(item => item.IsPrimary)
            .ThenBy(item => item.BusinessName)
            .ToList();

        WinTheme.Apply(this);
        Text = "POS Portal Auto Sync - HISAB KITAB";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(960, 720);
        Size = new Size(1080, 790);
        AutoScaleMode = AutoScaleMode.Dpi;
        Controls.Add(BuildContent());

        _portalPassword.UseSystemPasswordChar = true;
        _storePassword.UseSystemPasswordChar = true;
        _portalUrl.Text = "https://posweboffice.com/";
        _runTime.Value = DateTime.Today.AddHours(1).AddMinutes(15);
        _business.DataSource = _licensedBusinesses.ToList();
        _business.DisplayMember = nameof(LicensedBusinessConnection.BusinessName);
        _business.SelectedIndexChanged += (_, _) => LoadSelectedBusiness();
        if (_business.Items.Count > 0)
            LoadSelectedBusiness();
    }

    private Control BuildContent()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Bg,
            Padding = new Padding(24),
            ColumnCount = 1,
            RowCount = 4
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));

        var heading = new Panel { Dock = DockStyle.Fill, BackColor = WinTheme.BlueDark };
        heading.Controls.Add(new Label
        {
            Text = "AUTOMATIC POS REPORT SYNC",
            Dock = DockStyle.Top,
            Height = 58,
            Padding = new Padding(24, 14, 0, 0),
            ForeColor = Color.White,
            Font = WinTheme.HeaderFont(22)
        });
        heading.Controls.Add(new Label
        {
            Text = "One-time Google Chrome enrollment • encrypted credentials • unattended daily import",
            Dock = DockStyle.Bottom,
            Height = 36,
            Padding = new Padding(26, 0, 0, 10),
            ForeColor = Color.FromArgb(205, 224, 244),
            Font = WinTheme.BodyFont(10)
        });
        root.Controls.Add(heading, 0, 0);

        var card = WinTheme.BorderedPanel(14);
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(0, 14, 0, 8);
        root.Controls.Add(card, 0, 1);

        var form = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Panel,
            Padding = new Padding(22, 18, 22, 18),
            ColumnCount = 4,
            RowCount = 8
        };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        for (var row = 0; row < 7; row++)
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        form.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(form);

        AddField(form, "LICENSED HISAB KITAB STORE *", _business, 0, 0, 2);
        AddField(form, "DAILY RUN TIME", _runTime, 2, 0, 2);
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

        form.Controls.Add(new Label
        {
            Text =
                "ONE-TIME SETUP\n" +
                "1. Save the settings.  2. Open the dedicated Chrome profile.  " +
                "3. Complete any AdventPOS verification and select the correct store.  " +
                "4. Close Chrome and use TEST / SYNC NOW.\n\n" +
                "After that, Windows runs the sync daily. If the PC is off, HISAB KITAB catches up the next time it opens.",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Text,
            Font = WinTheme.BodyFont(10),
            Padding = new Padding(8, 12, 8, 4)
        }, 0, 6);
        form.SetColumnSpan(form.GetControlFromPosition(0, 6)!, 4);

        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = WinTheme.Bg,
            ColumnCount = 4,
            RowCount = 1
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16));
        root.Controls.Add(actions, 0, 2);

        var save = ActionButton("SAVE SETUP", true);
        var enroll = ActionButton("OPEN ONE-TIME CHROME");
        var test = ActionButton("TEST / SYNC NOW", true);
        var close = ActionButton("CLOSE");
        actions.Controls.Add(save, 0, 0);
        actions.Controls.Add(enroll, 1, 0);
        actions.Controls.Add(test, 2, 0);
        actions.Controls.Add(close, 3, 0);

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
        test.Click += async (_, _) =>
        {
            try
            {
                SaveSettings(showConfirmation: false);
                ToggleActions(actions, false);
                _status.Text = "Opening the protected Chrome profile and requesting yesterday's report...";
                var results = await PortalSyncService.RunDueAsync(_paths, true, true);
                _status.Text = results.Count == 0
                    ? "No enabled store configuration was found."
                    : string.Join("  ", results.Select(result => result.Message));
                if (results.Any(result => !result.Success))
                    MessageBox.Show(this, _status.Text, "POS Auto Sync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception exception)
            {
                ShowError(exception);
            }
            finally
            {
                ToggleActions(actions, true);
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
        settings.BusinessName = business.BusinessName;
        settings.StoreGuid = business.StoreGuid;
        settings.DatabaseName = business.DatabaseName;
        settings.PortalUrl = _portalUrl.Text.Trim();
        settings.PortalStoreName = _portalStore.Text.Trim();
        settings.PortalEmail = _email.Text.Trim();
        settings.PortalPassword = _portalPassword.Text;
        settings.StoreUserName = _storeUser.Text.Trim();
        settings.StorePassword = _storePassword.Text;
        settings.Enabled = _enabled.Checked;
        settings.DailyHour = _runTime.Value.Hour;
        settings.DailyMinute = _runTime.Value.Minute;
        if (!_document.Stores.Contains(settings))
            _document.Stores.Add(settings);
        PortalSyncSettingsStore.Save(_document);

        if (settings.Enabled)
            PortalSyncService.EnsureDailyTask(
                settings.Id,
                new TimeOnly(settings.DailyHour, settings.DailyMinute));

        _status.Text =
            $"Saved for {business.BusinessName}. Daily Windows task: {_runTime.Value:h:mm tt}. " +
            $"Last result: {settings.LastStatus}";
        if (showConfirmation)
            MessageBox.Show(this,
                "The protected store settings and daily Windows task were saved.",
                "POS Auto Sync",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
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
        _enabled.Checked = settings?.Enabled ?? true;
        _runTime.Value = DateTime.Today
            .AddHours(settings?.DailyHour ?? 1)
            .AddMinutes(settings?.DailyMinute ?? 15);
        _status.Text = settings is null
            ? $"No automatic POS setup exists for {business.BusinessName}."
            : $"Last result: {settings.LastStatus}";
    }

    private PortalStoreSyncSettings? FindSettings(LicensedBusinessConnection business) =>
        _document.Stores.FirstOrDefault(item =>
            string.Equals(item.DatabaseName, business.DatabaseName, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(business.StoreGuid) &&
             string.Equals(item.StoreGuid, business.StoreGuid, StringComparison.OrdinalIgnoreCase)));

    private void ShowError(Exception exception)
    {
        _status.Text = AppBootstrap.RedactSensitiveText(exception.Message);
        MessageBox.Show(this, _status.Text, "POS Auto Sync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static void ToggleActions(Control root, bool enabled)
    {
        foreach (Control control in root.Controls)
            control.Enabled = enabled;
    }

    private static Button ActionButton(string text, bool primary = false)
    {
        var button = WinTheme.Button(text, primary);
        button.Dock = DockStyle.Fill;
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
