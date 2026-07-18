using System.Text.Json;

namespace HisabKitabWorks.ClientAccountManager.WinForms;

internal sealed class NewClientAccountForm : Form
{
    private readonly ClientAccountService _service;
    private readonly TextBox _business = DeveloperTheme.TextBox();
    private readonly TextBox _owner = DeveloperTheme.TextBox();
    private readonly TextBox _email = DeveloperTheme.TextBox();
    private readonly TextBox _phone = DeveloperTheme.TextBox();
    private readonly TextBox _guid = DeveloperTheme.TextBox();
    private readonly TextBox _zip = DeveloperTheme.TextBox();
    private readonly TextBox _addressState = DeveloperTheme.TextBox();
    private readonly ComboBox _payrollState = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Font = DeveloperTheme.Body(10.5f) };
    private readonly TextBox _address = DeveloperTheme.TextBox();
    private readonly ComboBox _database = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, Font = DeveloperTheme.Body(10.5f) };
    private readonly NumericUpDown _pcs = DeveloperTheme.Number();
    private readonly NumericUpDown _businesses = DeveloperTheme.Number();
    private readonly NumericUpDown _monthlyFee = new() { Dock = DockStyle.Fill, DecimalPlaces = 2, Minimum = 0, Maximum = 100000, Font = DeveloperTheme.Body(10.5f) };
    private readonly DateTimePicker _expires = new() { Dock = DockStyle.Fill, Format = DateTimePickerFormat.Short, Value = DateTime.Today.AddMonths(1), Font = DeveloperTheme.Body(10.5f) };
    private readonly CheckBox _accounting = new() { Text = "Core Accounting (required)", Checked = true, Enabled = false, AutoSize = true, Font = DeveloperTheme.Bold() };
    private readonly CheckBox _payroll = new() { Text = "Payroll add-on", AutoSize = true, Font = DeveloperTheme.Bold(), ForeColor = DeveloperTheme.Blue };
    private readonly CheckBox _scheduling = new() { Text = "Scheduling add-on", AutoSize = true, Font = DeveloperTheme.Bold(), ForeColor = DeveloperTheme.Blue };
    private readonly CheckBox _monthlyReports = new() { Text = "Automatic monthly reports", AutoSize = true, Font = DeveloperTheme.Bold(), ForeColor = DeveloperTheme.Blue };
    private readonly TextBox _monthlyReportEmail = DeveloperTheme.TextBox();
    private readonly NumericUpDown _monthlyReportDay = new() { Dock = DockStyle.Fill, Minimum = 1, Maximum = 28, Value = 3, Font = DeveloperTheme.Body(10.5f) };
    private readonly Label _taxStatus = DeveloperTheme.Label("Select a payroll processing state.", false, DeveloperTheme.Muted);
    private readonly Label _status = DeveloperTheme.Label("Enter the new client details.", false, DeveloperTheme.Muted);

    private static readonly string[] UsStateCodes =
    [
        "AL","AK","AZ","AR","CA","CO","CT","DE","FL","GA","HI","ID","IL","IN","IA","KS","KY",
        "LA","ME","MD","MA","MI","MN","MS","MO","MT","NE","NV","NH","NJ","NM","NY","NC","ND",
        "OH","OK","OR","PA","RI","SC","SD","TN","TX","UT","VT","VA","WA","WV","WI","WY","DC"
    ];

    public ClientAccount? SavedAccount { get; private set; }

    public NewClientAccountForm(ClientAccountService service, IEnumerable<string> databases)
    {
        _service = service;
        Text = "HISAB KITAB WORKS - New Client Account";
        Icon = DeveloperTheme.Icon();
        BackColor = DeveloperTheme.Bg;
        Font = DeveloperTheme.Body();
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(980, 900);
        MinimumSize = new Size(820, 720);
        AutoScaleMode = AutoScaleMode.Dpi;
        MaximizeBox = true;

        _guid.CharacterCasing = CharacterCasing.Upper;
        _zip.MaxLength = 5;
        _addressState.ReadOnly = true;
        _addressState.TabStop = false;
        _addressState.BackColor = DeveloperTheme.PaleBlue;
        _payrollState.Items.AddRange(UsStateCodes.Cast<object>().ToArray());
        foreach (var database in databases.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            _database.Items.Add(database);

        Controls.Add(BuildLayout());
        _guid.TextChanged += (_, _) => UpdateAddressState();
        _payrollState.SelectedIndexChanged += (_, _) => UpdateTaxStatus();
        _monthlyReports.CheckedChanged += (_, _) =>
        {
            _monthlyReportEmail.Enabled = _monthlyReports.Checked;
            _monthlyReportDay.Enabled = _monthlyReports.Checked;
        };
        _monthlyReportEmail.Enabled = false;
        _monthlyReportDay.Enabled = false;
        Shown += (_, _) => _business.Focus();
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = DeveloperTheme.Bg,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 4
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildContent(), 0, 1);
        root.Controls.Add(BuildActions(), 0, 2);
        _status.BackColor = Color.White;
        _status.Padding = new Padding(12, 0, 12, 0);
        root.Controls.Add(_status, 0, 3);
        return root;
    }

    private Control BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 8, 20, 8) };
        panel.Paint += (_, e) => DeveloperTheme.Gradient(e, panel.ClientRectangle);
        var logo = new PictureBox { Image = DeveloperTheme.Logo(), SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Left, Width = 100, BackColor = Color.Transparent };
        var text = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, ColumnCount = 1, RowCount = 2, Padding = new Padding(14, 5, 0, 5) };
        text.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        text.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        text.Controls.Add(new Label { Text = "NEW CLIENT ACCOUNT", ForeColor = Color.White, BackColor = Color.Transparent, Font = DeveloperTheme.Bold(20), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        text.Controls.Add(new Label { Text = "BUSINESS • SERVICES • PAYROLL STATE • LICENSING", ForeColor = DeveloperTheme.Orange, BackColor = Color.Transparent, Font = DeveloperTheme.Bold(10.5f), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        panel.Controls.Add(text);
        panel.Controls.Add(logo);
        return panel;
    }

    private Control BuildContent()
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White, Padding = new Padding(18, 12, 18, 12) };
        var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 13, BackColor = Color.White };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        for (var i = 1; i <= 9; i++) content.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));

        var intro = DeveloperTheme.Label("Enter the client information, choose purchased services, and assign the payroll-processing state.", false, DeveloperTheme.Muted);
        intro.Padding = new Padding(4, 0, 4, 0);
        content.Controls.Add(intro, 0, 0);
        content.SetColumnSpan(intro, 2);
        AddField(content, 1, "CLIENT / BUSINESS NAME *", _business, 0);
        AddField(content, 1, "OWNER NAME *", _owner, 1);
        AddField(content, 2, "EMAIL", _email, 0);
        AddField(content, 2, "PHONE", _phone, 1);
        AddField(content, 3, "PRIMARY STORE GUID *", _guid, 0, 2);
        AddField(content, 4, "STORE ZIP *", _zip, 0);
        AddField(content, 4, "BUSINESS ADDRESS STATE (FROM GUID)", _addressState, 1);
        AddField(content, 5, "PAYROLL PROCESSING STATE *  (MAY DIFFER FROM ADDRESS)", _payrollState, 0, 2);
        AddField(content, 6, "STORE ADDRESS", _address, 0, 2);
        AddField(content, 7, "SQL BUSINESS DATABASE *", _database, 0, 2);
        AddField(content, 8, "PAID PC SEATS", _pcs, 0);
        AddField(content, 8, "BUSINESS SLOTS", _businesses, 1);
        AddField(content, 9, "MONTHLY CHARGE", _monthlyFee, 0);
        AddField(content, 9, "SUBSCRIPTION EXPIRES", _expires, 1);

        var servicesTitle = DeveloperTheme.Label("PURCHASED SERVICES", true, DeveloperTheme.Orange);
        content.Controls.Add(servicesTitle, 0, 10);
        content.SetColumnSpan(servicesTitle, 2);
        var services = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, Padding = new Padding(4, 6, 4, 6) };
        services.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        services.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        services.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        services.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 23));
        services.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        services.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (var checkBox in new[] { _accounting, _payroll, _scheduling, _monthlyReports }) { checkBox.Dock = DockStyle.Fill; checkBox.Margin = new Padding(4); }
        services.Controls.Add(_accounting, 0, 0);
        services.Controls.Add(_payroll, 1, 0);
        services.Controls.Add(_scheduling, 2, 0);
        services.Controls.Add(_monthlyReports, 3, 0);
        services.Controls.Add(DeveloperTheme.Label("REPORT EMAIL", true), 0, 1);
        services.Controls.Add(_monthlyReportEmail, 1, 1); services.SetColumnSpan(_monthlyReportEmail, 2);
        services.Controls.Add(_monthlyReportDay, 3, 1);
        content.Controls.Add(services, 0, 11);
        content.SetColumnSpan(services, 2);

        var taxCard = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = DeveloperTheme.PaleBlue, Padding = new Padding(10, 4, 10, 4) };
        taxCard.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        taxCard.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        taxCard.Controls.Add(DeveloperTheme.Label("The payroll state is developer-assigned and locked into the signed client license.", false, DeveloperTheme.Blue), 0, 0);
        taxCard.Controls.Add(_taxStatus, 0, 1);
        content.Controls.Add(taxCard, 0, 12);
        content.SetColumnSpan(taxCard, 2);
        scroll.Controls.Add(content);
        return scroll;
    }

    private Control BuildActions()
    {
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = DeveloperTheme.Bg, Padding = new Padding(0, 8, 0, 0) };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        var save = DeveloperTheme.Button("CREATE CLIENT ACCOUNT", true);
        var cancel = DeveloperTheme.Button("CANCEL");
        save.Margin = new Padding(0, 0, 6, 0);
        cancel.Margin = new Padding(6, 0, 0, 0);
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => Close();
        actions.Controls.Add(save, 0, 0);
        actions.Controls.Add(cancel, 1, 0);
        return actions;
    }

    private static void AddField(TableLayoutPanel form, int row, string label, Control control, int column, int span = 1)
    {
        var box = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(4, 1, 4, 4), Margin = Padding.Empty };
        box.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        box.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, 1, 0, 1);
        box.Controls.Add(DeveloperTheme.Label(label, true), 0, 0);
        box.Controls.Add(control, 0, 1);
        form.Controls.Add(box, column, row);
        if (span > 1) form.SetColumnSpan(box, span);
    }

    private void UpdateAddressState()
    {
        var state = StateFromStoreGuid(_guid.Text);
        _addressState.Text = state ?? "";
        if (_payrollState.SelectedIndex < 0 && state is not null)
            _payrollState.SelectedItem = state;
    }

    private void UpdateTaxStatus()
    {
        var state = SelectedPayrollState();
        if (string.IsNullOrWhiteSpace(state))
        {
            _taxStatus.Text = "TAX RULE STATUS: Select the state in which this client will process payroll.";
            _taxStatus.ForeColor = DeveloperTheme.Muted;
            return;
        }

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "TaxRules", "us-payroll-2026.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var version = root.GetProperty("Version").GetString() ?? "unknown";
            var year = root.GetProperty("Federal").GetProperty("TaxYear").GetInt32();
            var rule = root.GetProperty("States").EnumerateArray()
                .FirstOrDefault(item => string.Equals(item.GetProperty("StateCode").GetString(), state, StringComparison.OrdinalIgnoreCase));
            var verified = rule.ValueKind != JsonValueKind.Undefined && rule.GetProperty("Verified").GetBoolean();
            _taxStatus.Text = verified
                ? $"TAX RULE STATUS: READY — Federal {year} + {state} verified in package {version}."
                : $"TAX RULE STATUS: {state} is assigned, but payroll remains blocked until its verified rule update is released.";
            _taxStatus.ForeColor = verified ? DeveloperTheme.Green : DeveloperTheme.OrangeDark;
        }
        catch
        {
            _taxStatus.Text = $"TAX RULE STATUS: {state} assigned. Tax-package status is unavailable.";
            _taxStatus.ForeColor = DeveloperTheme.OrangeDark;
        }
    }

    private void Save()
    {
        try
        {
            _status.Text = "Creating client account…";
            _status.ForeColor = DeveloperTheme.Blue;
            Cursor = Cursors.WaitCursor;
            var services = string.Join(',', new[] { "Accounting", _payroll.Checked ? "Payroll" : "", _scheduling.Checked ? "Scheduling" : "", _monthlyReports.Checked ? "MonthlyReports" : "" }.Where(x => x.Length > 0));
            SavedAccount = _service.Save(new ClientAccount(
                0, 0, _business.Text.Trim(), _owner.Text.Trim(), _email.Text.Trim(), _phone.Text.Trim(),
                _guid.Text.Trim(), _zip.Text.Trim(), _address.Text.Trim(), _database.Text.Trim(), "",
                (int)_pcs.Value, (int)_businesses.Value, _monthlyFee.Value, _expires.Value.Date, services,
                true, SelectedPayrollState(), _monthlyReportEmail.Text.Trim(), (int)_monthlyReportDay.Value));
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            _status.ForeColor = DeveloperTheme.Red;
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private string SelectedPayrollState() => (_payrollState.SelectedItem?.ToString() ?? "").Trim().ToUpperInvariant();

    private static string? StateFromStoreGuid(string value)
    {
        var parts = (value ?? "").Trim().ToUpperInvariant().Split('_');
        return parts.Length == 4 && UsStateCodes.Contains(parts[0], StringComparer.Ordinal) ? parts[0] : null;
    }
}
