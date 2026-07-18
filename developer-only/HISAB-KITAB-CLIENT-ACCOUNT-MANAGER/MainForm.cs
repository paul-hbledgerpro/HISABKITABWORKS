using System.Diagnostics;
using System.ComponentModel;

namespace HisabKitabWorks.ClientAccountManager.WinForms;

internal sealed class MainForm : Form
{
    private readonly TextBox _server = DeveloperTheme.TextBox();
    private readonly TextBox _username = DeveloperTheme.TextBox();
    private readonly TextBox _password = DeveloperTheme.TextBox(true);
    private readonly Button _connect = DeveloperTheme.Button("CONNECT", true);
    private readonly Label _connection = DeveloperTheme.Label("●  Not connected", false, DeveloperTheme.Muted);
    private readonly TextBox _business = DeveloperTheme.TextBox();
    private readonly TextBox _owner = DeveloperTheme.TextBox();
    private readonly TextBox _email = DeveloperTheme.TextBox();
    private readonly TextBox _phone = DeveloperTheme.TextBox();
    private readonly TextBox _guid = DeveloperTheme.TextBox();
    private readonly TextBox _zip = DeveloperTheme.TextBox();
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
    private readonly CheckBox _active = new() { Text = "Account active", Checked = true, AutoSize = true, Font = DeveloperTheme.Bold() };
    private readonly TextBox _subscription = DeveloperTheme.TextBox();
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
    private readonly Label _selectedLicense = DeveloperTheme.Label("SELECT AN EXISTING ACCOUNT TO UPDATE ITS SERVICES", true, DeveloperTheme.Muted);
    private readonly Label _status = DeveloperTheme.Label("Connect to create or update a client account.", false, DeveloperTheme.Muted);
    private ClientAccountService? _service;
    private readonly BindingList<ClientAccount> _accounts = new();
    private int _customerId;
    private int _licenseId;
    private static readonly string[] UsStateCodes =
    [
        "AL","AK","AZ","AR","CA","CO","CT","DE","FL","GA","HI","ID","IL","IN","IA","KS","KY",
        "LA","ME","MD","MA","MI","MN","MS","MO","MT","NE","NV","NH","NJ","NM","NY","NC","ND",
        "OH","OK","OR","PA","RI","SC","SD","TN","TX","UT","VT","VA","WA","WV","WI","WY","DC"
    ];

    public MainForm()
    {
        Text = "HISAB KITAB WORKS - Developer Client Account Manager";
        Icon = DeveloperTheme.Icon(); BackColor = DeveloperTheme.Bg; Font = DeveloperTheme.Body();
        StartPosition = FormStartPosition.CenterScreen; Size = new Size(1450, 930); MinimumSize = new Size(1180, 760);
        AutoScaleMode = AutoScaleMode.Dpi; WindowState = FormWindowState.Normal;
        _server.Text = "hbstoreledger-server.database.windows.net"; _guid.CharacterCasing = CharacterCasing.Upper; _zip.MaxLength = 5; _subscription.ReadOnly = true;
        _payrollState.Items.AddRange(UsStateCodes.Cast<object>().ToArray());
        ConfigureGrid(); Controls.Add(BuildLayout()); WireEvents();
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 4, ColumnCount = 1, BackColor = DeveloperTheme.Bg };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 105)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 120)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.Controls.Add(BuildHeader(), 0, 0); root.Controls.Add(BuildConnection(), 0, 1);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(0, 12, 0, 0) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 610)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.Controls.Add(BuildEditor(), 0, 0); body.Controls.Add(BuildDirectory(), 1, 0); root.Controls.Add(body, 0, 2);
        _status.BackColor = Color.White; _status.Padding = new Padding(12, 0, 0, 0); root.Controls.Add(_status, 0, 3); return root;
    }

    private Control BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 8, 24, 8) };
        panel.Paint += (_, e) => DeveloperTheme.Gradient(e, panel.ClientRectangle);
        var logo = new PictureBox { Image = DeveloperTheme.Logo(), SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Left, Width = 112, BackColor = Color.Transparent };
        var titles = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, ColumnCount = 1, RowCount = 2, Padding = new Padding(18, 7, 0, 7) };
        titles.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        titles.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        var title = new Label { Text = "HISAB KITAB WORKS", ForeColor = Color.White, BackColor = Color.Transparent, Font = DeveloperTheme.Bold(22), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        var sub = new Label { Text = "DEVELOPER CLIENT ACCOUNT MANAGER  •  SERVICES & SUBSCRIPTIONS", ForeColor = DeveloperTheme.Orange, BackColor = Color.Transparent, Font = DeveloperTheme.Bold(10.5f), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        titles.Controls.Add(title, 0, 0); titles.Controls.Add(sub, 0, 1);
        panel.Controls.Add(titles); panel.Controls.Add(logo); return panel;
    }

    private Control BuildConnection()
    {
        var card = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(16), ColumnCount = 5, RowCount = 2 };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35)); card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20)); card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20)); card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); card.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        card.Controls.Add(DeveloperTheme.Label("LICENSING SQL SERVER", true, DeveloperTheme.Orange), 0, 0); card.Controls.Add(DeveloperTheme.Label("USERNAME", true, DeveloperTheme.Orange), 1, 0); card.Controls.Add(DeveloperTheme.Label("PASSWORD", true, DeveloperTheme.Orange), 2, 0);
        card.Controls.Add(_server, 0, 1); card.Controls.Add(_username, 1, 1); card.Controls.Add(_password, 2, 1); card.Controls.Add(_connect, 3, 1); card.Controls.Add(_connection, 4, 1); return card;
    }

    private Control BuildEditor()
    {
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(20, 14, 20, 14), ColumnCount = 1, RowCount = 6 };
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

        var heading = DeveloperTheme.Label("CLIENT ACCOUNT & PURCHASED SERVICES", true, DeveloperTheme.Orange); heading.Font = DeveloperTheme.Bold(14); outer.Controls.Add(heading, 0, 0);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White, Padding = new Padding(0, 0, 8, 0) };
        var form = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 10, BackColor = Color.White };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var i = 0; i < 10; i++) form.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        AddField(form, 0, "CLIENT / BUSINESS NAME *", _business, 0, 2); AddField(form, 1, "OWNER NAME *", _owner, 0, 2);
        AddField(form, 2, "EMAIL", _email, 0); AddField(form, 2, "PHONE", _phone, 1);
        AddField(form, 3, "PRIMARY STORE GUID *", _guid, 0, 2);
        AddField(form, 4, "STORE ZIP *", _zip, 0); AddField(form, 4, "PAYROLL STATE (LICENSE LOCKED) *", _payrollState, 1);
        AddField(form, 5, "STORE ADDRESS", _address, 0, 2); AddField(form, 6, "SQL BUSINESS DATABASE *", _database, 0, 2);
        AddField(form, 7, "PAID PC SEATS", _pcs, 0); AddField(form, 7, "BUSINESS SLOTS", _businesses, 1);
        AddField(form, 8, "MONTHLY CHARGE", _monthlyFee, 0); AddField(form, 8, "SUBSCRIPTION EXPIRES", _expires, 1);
        AddField(form, 9, "SUBSCRIPTION KEY", _subscription, 0, 2);
        scroll.Controls.Add(form); outer.Controls.Add(scroll, 0, 1);

        outer.Controls.Add(DeveloperTheme.Label("PURCHASED SERVICES", true, DeveloperTheme.Orange), 0, 2);
        var services = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(4, 3, 0, 3) };
        services.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); services.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        services.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); services.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        foreach (var box in new[] { _accounting, _payroll, _scheduling, _active }) { box.Dock = DockStyle.Fill; box.Margin = new Padding(2); }
        services.Controls.Add(_accounting, 0, 0); services.Controls.Add(_payroll, 1, 0); services.Controls.Add(_scheduling, 0, 1); services.Controls.Add(_active, 1, 1); outer.Controls.Add(services, 0, 3);
        var notice = DeveloperTheme.Label("For an existing account, use UPDATE SERVICES ONLY. Then generate an updated license for the same PC; it will not use another seat.", false, DeveloperTheme.Muted); notice.Padding = new Padding(5, 2, 5, 2); outer.Controls.Add(notice, 0, 4);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Padding = new Padding(0, 5, 0, 0) };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        var updateServices = DeveloperTheme.Button("UPDATE SERVICES ONLY", true); updateServices.Click += (_, _) => UpdateServicesOnly();
        var save = DeveloperTheme.Button("SAVE ALL DETAILS"); save.Click += (_, _) => SaveAccount(); var clear = DeveloperTheme.Button("NEW"); clear.Click += (_, _) => Clear(); var copy = DeveloperTheme.Button("COPY KEY"); copy.Click += (_, _) => CopyKey();
        actions.Controls.Add(updateServices,0,0); actions.Controls.Add(save,1,0); actions.Controls.Add(clear,2,0); actions.Controls.Add(copy,3,0);
        outer.Controls.Add(actions, 0, 5); return outer;
    }

    private Control BuildDirectory()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(18), RowCount = 4 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 50)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        var heading = DeveloperTheme.Label("CLIENT DIRECTORY", true, DeveloperTheme.Orange); heading.Font = DeveloperTheme.Bold(14); panel.Controls.Add(heading,0,0);
        _selectedLicense.BackColor = DeveloperTheme.PaleBlue; _selectedLicense.Padding = new Padding(10, 0, 10, 0); _selectedLicense.AutoEllipsis = true;
        panel.Controls.Add(_selectedLicense, 0, 1); panel.Controls.Add(_grid,0,2);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(0,8,0,0) };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,24)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,38)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,38));
        var refresh = DeveloperTheme.Button("REFRESH"); refresh.Click += (_,_) => RefreshAccounts();
        var billing = DeveloperTheme.Button("ACCOUNT PAYMENTS & INVOICES", true); billing.Click += (_,_) => OpenAccountBilling();
        var license = DeveloperTheme.Button("OPEN LICENSE GENERATOR"); license.Click += (_,_) => OpenLicenseGenerator();
        actions.Controls.Add(refresh,0,0); actions.Controls.Add(billing,1,0); actions.Controls.Add(license,2,0); panel.Controls.Add(actions,0,3); return panel;
    }

    private static void AddField(TableLayoutPanel form, int row, string label, Control control, int column, int span = 1)
    {
        var box = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(4, 1, 4, 5), Margin = Padding.Empty };
        box.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); box.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        control.Dock = DockStyle.Fill; control.Margin = new Padding(0, 1, 0, 1);
        box.Controls.Add(DeveloperTheme.Label(label,true),0,0); box.Controls.Add(control,0,1); form.Controls.Add(box,column,row); if(span>1) form.SetColumnSpan(box,span);
    }

    private void WireEvents()
    {
        _connect.Click += (_,_) => Connect();
        _grid.SelectionChanged += (_,_) => LoadSelected();
        _guid.TextChanged += (_, _) =>
        {
            var state = StateFromStoreGuid(_guid.Text);
            if (state is not null)
                _payrollState.SelectedItem = state;
        };
        foreach (var field in new[] {_server,_username,_password})
            field.TextChanged += (_,_) => { _service=null; _connection.Text="●  Connection changed"; _connection.ForeColor=DeveloperTheme.Muted; };
    }
    private void ConfigureGrid()
    {
        _grid.EnableHeadersVisualStyles=false; _grid.ColumnHeadersDefaultCellStyle.BackColor=DeveloperTheme.Blue; _grid.ColumnHeadersDefaultCellStyle.ForeColor=Color.White; _grid.ColumnHeadersDefaultCellStyle.Font=DeveloperTheme.Bold(); _grid.DefaultCellStyle.Font=DeveloperTheme.Body(9.5f); _grid.RowTemplate.Height=32; _grid.AutoGenerateColumns=false;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name=nameof(ClientAccount.CustomerId), DataPropertyName=nameof(ClientAccount.CustomerId), Visible=false });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name=nameof(ClientAccount.BusinessName), DataPropertyName=nameof(ClientAccount.BusinessName), HeaderText="CLIENT", FillWeight=120 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name=nameof(ClientAccount.StoreGuid), DataPropertyName=nameof(ClientAccount.StoreGuid), HeaderText="STORE GUID — MATCH THIS EXACTLY", FillWeight=190 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name=nameof(ClientAccount.EnabledServices), DataPropertyName=nameof(ClientAccount.EnabledServices), HeaderText="ENABLED SERVICES", FillWeight=130 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name=nameof(ClientAccount.MaxDevices), DataPropertyName=nameof(ClientAccount.MaxDevices), HeaderText="PCs", FillWeight=45 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name=nameof(ClientAccount.MaxBusinesses), DataPropertyName=nameof(ClientAccount.MaxBusinesses), HeaderText="STORES", FillWeight=55 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name=nameof(ClientAccount.ExpiresDate), DataPropertyName=nameof(ClientAccount.ExpiresDate), HeaderText="EXPIRES", FillWeight=75, DefaultCellStyle=new DataGridViewCellStyle { Format="MM/dd/yyyy" } });
    }

    private void Connect()
    {
        try { Cursor=Cursors.WaitCursor; var service=new ClientAccountService(_server.Text,_username.Text,_password.Text); service.ConnectAndUpgrade(); _service=service; _database.DataSource=service.Databases(); _connection.Text="●  Connected"; _connection.ForeColor=DeveloperTheme.Green; RefreshAccounts(); SetStatus("Connected. Create a client account and select its paid services before issuing the PC license.",false); }
        catch(Exception ex) { _service=null; _connection.Text="●  Connection failed"; _connection.ForeColor=DeveloperTheme.Red; SetStatus(ex.Message,true); }
        finally { Cursor=Cursors.Default; }
    }

    private void RefreshAccounts()
    {
        if(_service is null) return; try { _accounts.RaiseListChangedEvents=false; _accounts.Clear(); foreach(var item in _service.LoadAccounts()) _accounts.Add(item); _accounts.RaiseListChangedEvents=true; _accounts.ResetBindings(); _grid.DataSource=null; _grid.DataSource=_accounts; } catch(Exception ex){SetStatus(ex.Message,true);}
    }

    private void LoadSelected()
    {
        if(_grid.CurrentRow?.Cells[nameof(ClientAccount.CustomerId)].Value is not int id) return; var item=_accounts.FirstOrDefault(x=>x.CustomerId==id); if(item is null)return;
        _customerId=item.CustomerId; _licenseId=item.LicenseId; _business.Text=item.BusinessName; _owner.Text=item.OwnerName; _email.Text=item.Email; _phone.Text=item.Phone; _guid.Text=item.StoreGuid; _zip.Text=item.StoreZip; _payrollState.SelectedItem=string.IsNullOrWhiteSpace(item.PayrollState)?StateFromStoreGuid(item.StoreGuid):item.PayrollState; _address.Text=item.StoreAddress; _database.Text=item.DatabaseName; _pcs.Value=item.MaxDevices; _businesses.Value=item.MaxBusinesses; _monthlyFee.Value=item.MonthlyFee; _expires.Value=item.ExpiresDate; _subscription.Text=item.SubscriptionKey; _payroll.Checked=Has(item,"Payroll"); _scheduling.Checked=Has(item,"Scheduling"); _active.Checked=item.IsActive;
        _selectedLicense.Text=$"SELECTED: {item.StoreGuid}   •   PAYROLL STATE: {item.PayrollState}   •   SERVICES: {item.EnabledServices}";
        _selectedLicense.ForeColor=Has(item,"Payroll") || Has(item,"Scheduling") ? DeveloperTheme.Green : DeveloperTheme.OrangeDark;
    }

    private void SaveAccount()
    {
        if(_service is null){SetStatus("Connect first.",true);return;} try { var saved=_service.Save(ReadForm()); _customerId=saved.CustomerId; _licenseId=saved.LicenseId; _subscription.Text=saved.SubscriptionKey; RefreshAccounts(); SetStatus($"Client account saved. Subscription key {saved.SubscriptionKey} is ready for the License Generator.",false); } catch(Exception ex){SetStatus(ex.Message,true);}
    }
    private void UpdateServicesOnly()
    {
        if (_service is null) { SetStatus("Connect first.", true); return; }
        if (_customerId <= 0 || _licenseId <= 0) { SetStatus("Select the existing client account in Client Directory first.", true); return; }
        try
        {
            _service.UpdateServices(_customerId, _licenseId, Services(), SelectedPayrollState());
            var client = _business.Text.Trim();
            RefreshAccounts();
            SetStatus($"Purchased services updated for {client}. Open the License Generator and renew the same PC license.", false);
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }
    private ClientAccount ReadForm() => new(_customerId,_licenseId,_business.Text.Trim(),_owner.Text.Trim(),_email.Text.Trim(),_phone.Text.Trim(),_guid.Text.Trim(),_zip.Text.Trim(),_address.Text.Trim(),_database.Text.Trim(),_subscription.Text.Trim(),(int)_pcs.Value,(int)_businesses.Value,_monthlyFee.Value,_expires.Value.Date,Services(),_active.Checked,SelectedPayrollState());
    private string Services() => string.Join(',',new[]{"Accounting",_payroll.Checked?"Payroll":"",_scheduling.Checked?"Scheduling":""}.Where(x=>x.Length>0));
    private string SelectedPayrollState() => (_payrollState.SelectedItem?.ToString() ?? "").Trim().ToUpperInvariant();
    private static bool Has(ClientAccount item,string service)=>item.EnabledServices.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Contains(service,StringComparer.OrdinalIgnoreCase);
    private void Clear(){_customerId=0;_licenseId=0;foreach(var box in new[]{_business,_owner,_email,_phone,_guid,_zip,_address,_subscription})box.Clear();_payrollState.SelectedIndex=-1;_database.Text="";_pcs.Value=1;_businesses.Value=1;_monthlyFee.Value=0;_expires.Value=DateTime.Today.AddMonths(1);_payroll.Checked=false;_scheduling.Checked=false;_active.Checked=true;_selectedLicense.Text="NEW CLIENT ACCOUNT — NOT YET SAVED";_selectedLicense.ForeColor=DeveloperTheme.Muted;SetStatus("New client account.",false);}

    private static string? StateFromStoreGuid(string value)
    {
        var parts = (value ?? "").Trim().ToUpperInvariant().Split('_');
        return parts.Length == 4 && UsStateCodes.Contains(parts[0], StringComparer.Ordinal)
            ? parts[0]
            : null;
    }
    private void CopyKey(){if(string.IsNullOrWhiteSpace(_subscription.Text)){SetStatus("Save the client account first.",true);return;}Clipboard.SetText(_subscription.Text);SetStatus("Subscription key copied.",false);}
    private void OpenAccountBilling()
    {
        if (_service is null) { SetStatus("Connect to the licensing database first.", true); return; }
        using var form = new AccountBillingForm(_service, _customerId);
        form.ShowDialog(this);
        RefreshAccounts();
        SetStatus("Account payments and invoices refreshed.", false);
    }
    private void OpenLicenseGenerator()
    {
        try
        {
            var match = FindLicenseGenerator();
            if (match is null) { SetStatus("License Generator was not found. Build its Release project or place both developer tools together.", true); return; }
            Process.Start(new ProcessStartInfo(match) { UseShellExecute = true });
            SetStatus("License Generator opened. Renew the same PC to apply the updated services.", false);
        }
        catch (Exception ex) { SetStatus($"Could not open License Generator: {ex.Message}", true); }
    }

    private static string? FindLicenseGenerator()
    {
        const string executable = "HISAB KITAB WORKS License Generator.exe";
        var besideThisApp = Path.Combine(AppContext.BaseDirectory, executable);
        if (File.Exists(besideThisApp)) return besideThisApp;

        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && cursor is not null; depth++, cursor = cursor.Parent)
        {
            var project = Path.Combine(cursor.FullName, "HISAB-KITAB-LICENSE-GENERATOR");
            if (!Directory.Exists(project)) continue;
            var release = Directory.EnumerateFiles(project, executable, SearchOption.AllDirectories)
                .FirstOrDefault(path => path.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
            if (release is not null) return release;
        }
        return null;
    }
    private void SetStatus(string text,bool error){_status.Text=text;_status.ForeColor=error?DeveloperTheme.Red:DeveloperTheme.Green;}
}
