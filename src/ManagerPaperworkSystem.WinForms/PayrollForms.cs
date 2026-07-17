using System.Diagnostics;
using System.ComponentModel;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.Reports.Pdf;
using Microsoft.EntityFrameworkCore;
using UglyToad.PdfPig;

namespace ManagerPaperworkSystem.WinForms;

internal static class PayrollUi
{
    public static void Prepare(Form form, string title, Size size)
    {
        WinTheme.Apply(form);
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.Text = title;
        form.StartPosition = FormStartPosition.CenterParent;
        form.Size = size;
        form.MinimumSize = new Size(Math.Min(size.Width, 1000), Math.Min(size.Height, 720));
        form.Icon = WinTheme.TryLoadIcon();
    }

    public static Label Heading(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = WinTheme.Copper,
        Font = WinTheme.HeaderFont(14),
        TextAlign = ContentAlignment.MiddleLeft
    };

    public static TextBox TextBox(bool password = false) => new()
    {
        Width = 230,
        Height = 30,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.White,
        ForeColor = WinTheme.Text,
        Font = WinTheme.BodyFont(10),
        UseSystemPasswordChar = password
    };

    public static NumericUpDown Number(decimal max = 1_000_000m, int decimals = 2) => new()
    {
        Width = 230,
        Height = 30,
        Maximum = max,
        DecimalPlaces = decimals,
        ThousandsSeparator = true,
        Font = WinTheme.BodyFont(10),
        BackColor = Color.White,
        ForeColor = WinTheme.Text
    };

    public static ComboBox Combo<T>() where T : struct, Enum
    {
        var combo = WinTheme.ComboBox();
        combo.Width = 230;
        combo.FlatStyle = FlatStyle.Standard;
        combo.DataSource = Enum.GetValues<T>();
        return combo;
    }

    public static Control Field(string label, Control input, int width = 260)
    {
        var panel = new TableLayoutPanel
        {
            Width = width,
            Height = 66,
            Margin = new Padding(5, 3, 5, 3),
            Padding = new Padding(0, 0, 6, 3),
            BackColor = WinTheme.Panel,
            ColumnCount = 1,
            RowCount = 2
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var caption = new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            AutoEllipsis = true,
            ForeColor = WinTheme.BlueDark,
            Font = WinTheme.BoldFont(8.5f),
            Margin = Padding.Empty
        };
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 2, 0, 2);
        panel.Controls.Add(caption, 0, 0);
        panel.Controls.Add(input, 0, 1);
        return panel;
    }

    public static Button Button(string text, bool filled = false, int width = 160)
    {
        var button = WinTheme.Button(text, filled);
        button.Width = width;
        button.Height = 42;
        button.Margin = new Padding(5);
        return button;
    }
}

internal sealed class EmployeeManagerForm : Form
{
    private readonly Func<AppDbContext> _createDb;
    private readonly int _storeId;
    private readonly string _user;
    private readonly DataGridView _grid = WinTheme.Grid();
    private readonly DataGridView _documents = WinTheme.Grid();
    private readonly TextBox _number = PayrollUi.TextBox();
    private readonly TextBox _first = PayrollUi.TextBox();
    private readonly TextBox _middle = PayrollUi.TextBox();
    private readonly TextBox _last = PayrollUi.TextBox();
    private readonly TextBox _address = PayrollUi.TextBox();
    private readonly TextBox _city = PayrollUi.TextBox();
    private readonly TextBox _state = PayrollUi.TextBox();
    private readonly TextBox _zip = PayrollUi.TextBox();
    private readonly TextBox _phone = PayrollUi.TextBox();
    private readonly TextBox _email = PayrollUi.TextBox();
    private readonly TextBox _ssn = PayrollUi.TextBox(true);
    private readonly NumericUpDown _payRate = PayrollUi.Number(decimals: 4);
    private readonly ComboBox _payType = PayrollUi.Combo<EmployeePayType>();
    private readonly ComboBox _frequency = PayrollUi.Combo<PayFrequency>();
    private readonly ComboBox _filingStatus = PayrollUi.Combo<FederalFilingStatus>();
    private readonly CheckBox _overtime = new() { Text = "Overtime eligible", Checked = true, AutoSize = true };
    private readonly CheckBox _multipleJobs = new() { Text = "W-4 Step 2 multiple jobs", AutoSize = true };
    private readonly CheckBox _federalExempt = new() { Text = "Federal withholding exempt", AutoSize = true };
    private readonly NumericUpDown _dependents = PayrollUi.Number();
    private readonly NumericUpDown _otherIncome = PayrollUi.Number();
    private readonly NumericUpDown _deductions = PayrollUi.Number();
    private readonly NumericUpDown _extraFederal = PayrollUi.Number();
    private readonly NumericUpDown _ilLine1 = PayrollUi.Number(99, 0);
    private readonly NumericUpDown _ilLine2 = PayrollUi.Number(99, 0);
    private readonly NumericUpDown _ilExtra = PayrollUi.Number();
    private readonly DateTimePicker _hireDate = new() { Width = 230, Format = DateTimePickerFormat.Short };
    private readonly TextBox _workState = PayrollUi.TextBox();
    private int? _employeeId;

    public EmployeeManagerForm(Func<AppDbContext> createDb, int storeId, string user)
    {
        _createDb = createDb;
        _storeId = storeId;
        _user = user;
        PayrollUi.Prepare(this, "Employees & Payroll Onboarding - HISAB KITAB", new Size(1480, 900));
        ConfigureEmployeeGrid();

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, BackColor = WinTheme.Bg, Padding = new Padding(16) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 240));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(PayrollUi.Heading("EMPLOYEE DIRECTORY  •  PERSONAL DATA IS ENCRYPTED AND ADMIN-ONLY"), 0, 0);
        root.Controls.Add(_grid, 0, 1);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = WinTheme.BodyFont(10),
            SizeMode = TabSizeMode.Fixed,
            ItemSize = new Size(235, 36),
            Padding = new Point(14, 6),
            Multiline = false
        };
        tabs.TabPages.Add(BuildPersonalTab());
        tabs.TabPages.Add(BuildTaxTab());
        tabs.TabPages.Add(BuildDocumentsTab());
        root.Controls.Add(tabs, 0, 2);
        Controls.Add(root);

        _grid.SelectionChanged += async (_, _) => await SelectEmployeeAsync();
        Shown += async (_, _) => { await RefreshAsync(); NewEmployee(); };
    }

    private TabPage BuildPersonalTab()
    {
        var tab = new TabPage("Employee & Pay Details") { BackColor = WinTheme.Panel, Padding = new Padding(14) };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, BackColor = WinTheme.Panel };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        var fields = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, BackColor = WinTheme.Panel, Padding = new Padding(4) };
        fields.Controls.AddRange(new Control[]
        {
            PayrollUi.Field("EMPLOYEE NUMBER *", _number), PayrollUi.Field("FIRST NAME *", _first),
            PayrollUi.Field("MIDDLE INITIAL", _middle), PayrollUi.Field("LAST NAME *", _last),
            PayrollUi.Field("ADDRESS", _address, 390), PayrollUi.Field("CITY", _city),
            PayrollUi.Field("STATE", _state, 120), PayrollUi.Field("ZIP", _zip, 150),
            PayrollUi.Field("PHONE", _phone), PayrollUi.Field("EMAIL", _email, 330),
            PayrollUi.Field("SOCIAL SECURITY NUMBER", _ssn), PayrollUi.Field("HIRE DATE", _hireDate),
            PayrollUi.Field("PAY TYPE", _payType), PayrollUi.Field("PAY RATE (HOURLY OR ANNUAL)", _payRate),
            PayrollUi.Field("PAY FREQUENCY", _frequency), PayrollUi.Field("WORK STATE", _workState, 150),
            PayrollUi.Field("OVERTIME", _overtime, 220)
        });
        root.Controls.Add(fields, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel, FlowDirection = FlowDirection.RightToLeft };
        var save = PayrollUi.Button("SAVE EMPLOYEE", true, 190); save.Click += async (_, _) => await SaveAsync();
        var add = PayrollUi.Button("ADD NEW", false); add.Click += (_, _) => NewEmployee();
        var deactivate = PayrollUi.Button("DEACTIVATE", false); deactivate.Click += async (_, _) => await DeactivateAsync();
        actions.Controls.AddRange(new Control[] { save, add, deactivate });
        root.Controls.Add(actions, 0, 1);
        tab.Controls.Add(root);
        return tab;
    }

    private TabPage BuildTaxTab()
    {
        var tab = new TabPage("Federal & Illinois Withholding") { BackColor = WinTheme.Panel, Padding = new Padding(14) };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, BackColor = WinTheme.Panel };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        var fields = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, BackColor = WinTheme.Panel, Padding = new Padding(4) };
        fields.Controls.AddRange(new Control[]
        {
            PayrollUi.Field("FEDERAL FILING STATUS", _filingStatus, 300),
            PayrollUi.Field("MULTIPLE JOBS", _multipleJobs, 260),
            PayrollUi.Field("DEPENDENTS CREDIT (W-4 STEP 3)", _dependents),
            PayrollUi.Field("OTHER INCOME (STEP 4A)", _otherIncome),
            PayrollUi.Field("DEDUCTIONS (STEP 4B)", _deductions),
            PayrollUi.Field("EXTRA FEDERAL / PAY PERIOD", _extraFederal),
            PayrollUi.Field("FEDERAL EXEMPT", _federalExempt, 260),
            PayrollUi.Field("IL-W-4 LINE 1 ALLOWANCES", _ilLine1),
            PayrollUi.Field("IL-W-4 LINE 2 ALLOWANCES", _ilLine2),
            PayrollUi.Field("EXTRA ILLINOIS / PAY PERIOD", _ilExtra)
        });
        root.Controls.Add(fields, 0, 0);
        var note = new Label { Dock = DockStyle.Fill, Text = "2026 calculations use IRS Publication 15-T automated percentage method and Illinois IL-700-T. Review imported values before payroll.", ForeColor = WinTheme.Muted, TextAlign = ContentAlignment.MiddleLeft, Font = WinTheme.BodyFont(9.5f) };
        root.Controls.Add(note, 0, 1);
        tab.Controls.Add(root);
        return tab;
    }

    private TabPage BuildDocumentsTab()
    {
        var tab = new TabPage("Documents") { BackColor = WinTheme.Panel, Padding = new Padding(12) };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, BackColor = WinTheme.Panel };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = WinTheme.Panel };
        var email = PayrollUi.Button("EMAIL W-4 LINK", true, 180); email.Click += (_, _) => EmailW4();
        var importW4 = PayrollUi.Button("IMPORT COMPLETED W-4", true, 220); importW4.Click += async (_, _) => await ImportW4Async();
        var state = PayrollUi.Button("ATTACH IL-W-4", false, 180); state.Click += async (_, _) => await AttachAsync(EmployeeDocumentType.StateWithholding);
        var id = PayrollUi.Button("ATTACH ID / LICENSE", false, 200); id.Click += async (_, _) => await AttachAsync(EmployeeDocumentType.DriversLicenseOrId);
        var i9 = PayrollUi.Button("ATTACH I-9", false, 150); i9.Click += async (_, _) => await AttachAsync(EmployeeDocumentType.FormI9);
        var open = PayrollUi.Button("OPEN SELECTED", false, 170); open.Click += async (_, _) => await OpenDocumentAsync();
        actions.Controls.AddRange(new Control[] { email, importW4, state, id, i9, open });
        root.Controls.Add(actions, 0, 0);
        root.Controls.Add(_documents, 0, 1);
        tab.Controls.Add(root);
        return tab;
    }

    private async Task RefreshAsync()
    {
        await using var db = _createDb();
        var rows = await db.Employees.AsNoTracking().Where(x => x.StoreId == _storeId).OrderByDescending(x => x.IsActive).ThenBy(x => x.LastName).ToListAsync();
        _grid.DataSource = rows.Select(x => new { x.Id, x.EmployeeNumber, Name = x.FullName, x.Phone, x.Email, SSN = x.MaskedSsn, PayRate = x.PayRate, PayType = x.PayType.ToString(), Frequency = x.PayFrequency.ToString(), Status = x.IsActive ? "Active" : "Inactive" }).ToList();
    }

    private void ConfigureEmployeeGrid()
    {
        _grid.AutoGenerateColumns = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.ColumnHeadersHeight = 44;
        _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        _grid.Columns.Clear();
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Id", HeaderText = "ID", Visible = false });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "EmployeeNumber", HeaderText = "Employee #", FillWeight = 95, MinimumWidth = 125 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Name", HeaderText = "Employee Name", FillWeight = 135, MinimumWidth = 165 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Phone", HeaderText = "Phone", FillWeight = 95, MinimumWidth = 125 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Email", HeaderText = "Email", FillWeight = 150, MinimumWidth = 180 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "SSN", HeaderText = "SSN", FillWeight = 75, MinimumWidth = 100 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "PayRate", HeaderText = "Pay Rate", FillWeight = 75, MinimumWidth = 100, DefaultCellStyle = new DataGridViewCellStyle { Format = "C4" } });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "PayType", HeaderText = "Pay Type", FillWeight = 75, MinimumWidth = 100 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Frequency", HeaderText = "Frequency", FillWeight = 80, MinimumWidth = 105 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Status", HeaderText = "Status", FillWeight = 70, MinimumWidth = 90 });
    }

    private async Task SelectEmployeeAsync()
    {
        if (_grid.CurrentRow?.DataBoundItem is null) return;
        var property = _grid.CurrentRow.DataBoundItem.GetType().GetProperty("Id");
        if (property?.GetValue(_grid.CurrentRow.DataBoundItem) is not int id) return;
        await using var db = _createDb();
        var e = await db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.StoreId == _storeId);
        if (e is null) return;
        _employeeId = e.Id;
        _number.Text = e.EmployeeNumber; _first.Text = e.FirstName; _middle.Text = e.MiddleInitial; _last.Text = e.LastName;
        _address.Text = e.Address; _city.Text = e.City; _state.Text = e.State; _zip.Text = e.Zip; _phone.Text = e.Phone; _email.Text = e.Email;
        _ssn.Text = e.MaskedSsn; _payRate.Value = Clamp(_payRate, e.PayRate); _payType.SelectedItem = e.PayType; _frequency.SelectedItem = e.PayFrequency;
        _overtime.Checked = e.IsOvertimeEligible; _workState.Text = e.WorkState; _hireDate.Value = e.HireDate.ToDateTime(TimeOnly.MinValue);
        _filingStatus.SelectedItem = e.FederalFilingStatus; _multipleJobs.Checked = e.FederalMultipleJobs; _federalExempt.Checked = e.FederalExempt;
        _dependents.Value = Clamp(_dependents, e.FederalDependentsCredit); _otherIncome.Value = Clamp(_otherIncome, e.FederalOtherIncome);
        _deductions.Value = Clamp(_deductions, e.FederalDeductions); _extraFederal.Value = Clamp(_extraFederal, e.FederalExtraWithholding);
        _ilLine1.Value = Clamp(_ilLine1, e.IllinoisLine1Allowances); _ilLine2.Value = Clamp(_ilLine2, e.IllinoisLine2Allowances); _ilExtra.Value = Clamp(_ilExtra, e.IllinoisExtraWithholding);
        await RefreshDocumentsAsync();
    }

    private void NewEmployee()
    {
        _employeeId = null;
        foreach (var box in new[] { _number, _first, _middle, _last, _address, _city, _state, _zip, _phone, _email, _ssn }) box.Clear();
        _state.Text = "IL"; _workState.Text = "IL"; _payRate.Value = 0; _payType.SelectedItem = EmployeePayType.Hourly; _frequency.SelectedItem = PayFrequency.Biweekly;
        _overtime.Checked = true; _filingStatus.SelectedItem = FederalFilingStatus.SingleOrMarriedFilingSeparately; _multipleJobs.Checked = false; _federalExempt.Checked = false;
        _dependents.Value = _otherIncome.Value = _deductions.Value = _extraFederal.Value = _ilLine1.Value = _ilLine2.Value = _ilExtra.Value = 0;
        _documents.DataSource = null;
        _ = AssignNextNumberAsync();
    }

    private async Task AssignNextNumberAsync()
    {
        await using var db = _createDb();
        var count = await db.Employees.CountAsync(x => x.StoreId == _storeId);
        if (_employeeId is null && string.IsNullOrWhiteSpace(_number.Text)) _number.Text = $"EMP-{count + 1:0000}";
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_number.Text) || string.IsNullOrWhiteSpace(_first.Text) || string.IsNullOrWhiteSpace(_last.Text))
        { MessageBox.Show(this, "Employee number, first name, and last name are required.", "Employee", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        await using var db = _createDb();
        var e = _employeeId.HasValue ? await db.Employees.FirstOrDefaultAsync(x => x.Id == _employeeId && x.StoreId == _storeId) : null;
        e ??= new Employee { StoreId = _storeId, CreatedUtc = DateTime.UtcNow };
        if (e.Id == 0) db.Employees.Add(e);
        e.EmployeeNumber = _number.Text.Trim(); e.FirstName = _first.Text.Trim(); e.MiddleInitial = _middle.Text.Trim(); e.LastName = _last.Text.Trim();
        e.Address = _address.Text.Trim(); e.City = _city.Text.Trim(); e.State = _state.Text.Trim().ToUpperInvariant(); e.Zip = _zip.Text.Trim(); e.Phone = _phone.Text.Trim(); e.Email = _email.Text.Trim();
        var ssnDigits = Regex.Replace(_ssn.Text, @"\D", "");
        if (ssnDigits.Length == 9) { e.EncryptedSsn = PayrollSensitiveDataProtector.ProtectText(ssnDigits); e.SsnLast4 = ssnDigits[^4..]; }
        e.PayRate = _payRate.Value; e.PayType = (EmployeePayType)(_payType.SelectedItem ?? EmployeePayType.Hourly); e.PayFrequency = (PayFrequency)(_frequency.SelectedItem ?? PayFrequency.Biweekly);
        e.IsOvertimeEligible = _overtime.Checked; e.WorkState = string.IsNullOrWhiteSpace(_workState.Text) ? e.State : _workState.Text.Trim().ToUpperInvariant(); e.HireDate = DateOnly.FromDateTime(_hireDate.Value);
        e.FederalFilingStatus = (FederalFilingStatus)(_filingStatus.SelectedItem ?? FederalFilingStatus.SingleOrMarriedFilingSeparately); e.FederalMultipleJobs = _multipleJobs.Checked;
        e.FederalDependentsCredit = _dependents.Value; e.FederalOtherIncome = _otherIncome.Value; e.FederalDeductions = _deductions.Value; e.FederalExtraWithholding = _extraFederal.Value; e.FederalExempt = _federalExempt.Checked;
        e.IllinoisLine1Allowances = decimal.ToInt32(_ilLine1.Value); e.IllinoisLine2Allowances = decimal.ToInt32(_ilLine2.Value); e.IllinoisExtraWithholding = _ilExtra.Value; e.UpdatedUtc = DateTime.UtcNow;
        try { await db.SaveChangesAsync(); _employeeId = e.Id; await RefreshAsync(); await RefreshDocumentsAsync(); }
        catch (Exception ex) { MessageBox.Show(this, AppBootstrap.RedactSensitiveText(ex.Message), "Employee Save Failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private async Task DeactivateAsync()
    {
        if (!_employeeId.HasValue) return;
        await using var db = _createDb();
        var e = await db.Employees.FirstOrDefaultAsync(x => x.Id == _employeeId && x.StoreId == _storeId);
        if (e is null) return;
        e.IsActive = false; e.TerminationDate = DateOnly.FromDateTime(DateTime.Today); e.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(); await RefreshAsync();
    }

    private void EmailW4()
    {
        if (string.IsNullOrWhiteSpace(_email.Text) || !MailAddress.TryCreate(_email.Text.Trim(), out _))
        { MessageBox.Show(this, "Enter a valid employee email address first.", "Email W-4", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        var subject = Uri.EscapeDataString("Please complete your 2026 Form W-4");
        var body = Uri.EscapeDataString("Please download, complete, sign, and return the official 2026 IRS Form W-4:\r\nhttps://www.irs.gov/pub/irs-pdf/fw4.pdf\r\n\r\nBecause this form contains your Social Security number, use a secure delivery method when returning it.");
        Process.Start(new ProcessStartInfo($"mailto:{_email.Text.Trim()}?subject={subject}&body={body}") { UseShellExecute = true });
    }

    private async Task ImportW4Async()
    {
        if (!_employeeId.HasValue) { MessageBox.Show(this, "Save the employee before importing documents."); return; }
        using var dialog = new OpenFileDialog { Filter = "PDF documents (*.pdf)|*.pdf", Title = "Import completed employee W-4" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await AttachFileAsync(dialog.FileName, EmployeeDocumentType.FederalW4);
        var extracted = TryExtractW4(dialog.FileName);
        if (!string.IsNullOrWhiteSpace(extracted.Ssn)) _ssn.Text = extracted.Ssn;
        if (!string.IsNullOrWhiteSpace(extracted.Zip)) _zip.Text = extracted.Zip;
        await using var db = _createDb();
        var e = await db.Employees.FirstAsync(x => x.Id == _employeeId && x.StoreId == _storeId);
        e.W4OnFile = true; e.UpdatedUtc = DateTime.UtcNow; await db.SaveChangesAsync();
        MessageBox.Show(this, "The encrypted W-4 was attached. Any recognizable SSN/ZIP values were placed in the form for administrator review. Confirm every field before saving.", "W-4 Imported", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task AttachAsync(EmployeeDocumentType type)
    {
        if (!_employeeId.HasValue) { MessageBox.Show(this, "Save the employee before attaching documents."); return; }
        using var dialog = new OpenFileDialog { Filter = "Documents (*.pdf;*.png;*.jpg;*.jpeg)|*.pdf;*.png;*.jpg;*.jpeg|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK) await AttachFileAsync(dialog.FileName, type);
    }

    private async Task AttachFileAsync(string path, EmployeeDocumentType type)
    {
        var clear = await File.ReadAllBytesAsync(path);
        try
        {
            await using var db = _createDb();
            db.EmployeeDocuments.Add(new EmployeeDocument { StoreId = _storeId, EmployeeId = _employeeId!.Value, DocumentType = type, FileName = Path.GetFileName(path), ContentType = Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? "application/pdf" : "image", EncryptedContent = PayrollSensitiveDataProtector.Protect(clear), CreatedByName = _user });
            await db.SaveChangesAsync(); await RefreshDocumentsAsync();
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(clear); }
    }

    private async Task RefreshDocumentsAsync()
    {
        if (!_employeeId.HasValue) { _documents.DataSource = null; return; }
        await using var db = _createDb();
        var docs = await db.EmployeeDocuments.AsNoTracking().Where(x => x.EmployeeId == _employeeId && x.StoreId == _storeId).OrderByDescending(x => x.CreatedUtc).ToListAsync();
        _documents.DataSource = docs.Select(x => new { x.Id, Type = x.DocumentType.ToString(), x.FileName, AddedBy = x.CreatedByName, Added = x.CreatedUtc.ToLocalTime().ToString("MM/dd/yyyy h:mm tt") }).ToList();
    }

    private async Task OpenDocumentAsync()
    {
        if (_documents.CurrentRow?.DataBoundItem is null) return;
        var id = (int)(_documents.CurrentRow.DataBoundItem.GetType().GetProperty("Id")?.GetValue(_documents.CurrentRow.DataBoundItem) ?? 0);
        await using var db = _createDb();
        var doc = await db.EmployeeDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.StoreId == _storeId);
        if (doc is null) return;
        try
        {
            var clear = PayrollSensitiveDataProtector.Unprotect(doc.EncryptedContent);
            var safeName = Regex.Replace(doc.FileName, @"[^A-Za-z0-9._-]", "_");
            var path = Path.Combine(Path.GetTempPath(), $"HisabKitab_Employee_{Guid.NewGuid():N}_{safeName}");
            await File.WriteAllBytesAsync(path, clear); System.Security.Cryptography.CryptographicOperations.ZeroMemory(clear);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (CryptographicException)
        { MessageBox.Show(this, "This document was encrypted on a different licensed PC. Open it from the PC that imported it.", "Protected Document", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private static (string Ssn, string Zip) TryExtractW4(string path)
    {
        try
        {
            using var pdf = PdfDocument.Open(path);
            var text = string.Join("\n", pdf.GetPages().Select(x => x.Text));
            var ssn = Regex.Match(text, @"\b\d{3}[- ]?\d{2}[- ]?\d{4}\b").Value;
            var zip = Regex.Match(text, @"\b\d{5}(?:-\d{4})?\b").Value;
            return (ssn, zip);
        }
        catch { return ("", ""); }
    }

    private static decimal Clamp(NumericUpDown input, decimal value) => Math.Min(input.Maximum, Math.Max(input.Minimum, value));
}

internal sealed class ScheduleBuilderForm : Form
{
    private readonly Func<AppDbContext> _createDb;
    private readonly int _storeId;
    private readonly string _user;
    private readonly ComboBox _employee = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly DateTimePicker _from = new() { Format = DateTimePickerFormat.Short };
    private readonly DateTimePicker _to = new() { Format = DateTimePickerFormat.Short };
    private readonly TableLayoutPanel _days = new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 5,
        Dock = DockStyle.Top,
        BackColor = Color.White,
        Padding = new Padding(0)
    };
    private readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };
    private readonly Label _status = new() { Dock = DockStyle.Fill, ForeColor = WinTheme.Muted, TextAlign = ContentAlignment.MiddleLeft };
    private readonly List<ScheduleDayEditor> _editors = new();

    public ScheduleBuilderForm(Func<AppDbContext> createDb, int storeId, string user)
    {
        _createDb = createDb;
        _storeId = storeId;
        _user = user;
        PayrollUi.Prepare(this, "Add Employee Schedule - HISAB KITAB", new Size(980, 820));
        MinimumSize = new Size(880, 720);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var monday = today.AddDays(-daysSinceMonday);
        _from.Value = monday.ToDateTime(TimeOnly.MinValue);
        _to.Value = monday.AddDays(6).ToDateTime(TimeOnly.MinValue);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 5,
            ColumnCount = 1,
            BackColor = WinTheme.Bg,
            Padding = new Padding(18)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.Controls.Add(PayrollUi.Heading("ADD SCHEDULE  •  SELECT AN EMPLOYEE AND DATE PERIOD"), 0, 0);

        var selectors = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, BackColor = WinTheme.Panel };
        selectors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        selectors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        selectors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        selectors.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        selectors.Controls.Add(PayrollUi.Field("EMPLOYEE", _employee, 320), 0, 0);
        selectors.Controls.Add(PayrollUi.Field("PERIOD START", _from, 210), 1, 0);
        selectors.Controls.Add(PayrollUi.Field("PERIOD END", _to, 210), 2, 0);
        var load = PayrollUi.Button("LOAD DATES", true, 155);
        load.Anchor = AnchorStyles.None;
        load.Click += async (_, _) => await LoadDatesAsync();
        selectors.Controls.Add(load, 3, 0);
        root.Controls.Add(selectors, 0, 1);

        var headings = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, BackColor = WinTheme.BlueDark };
        headings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));
        headings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37));
        headings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
        headings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
        headings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        foreach (var caption in new[] { "WORK", "DATE", "START TIME", "END TIME", "HOURS" })
            headings.Controls.Add(new Label { Text = caption, Dock = DockStyle.Fill, ForeColor = Color.White, Font = WinTheme.BoldFont(9), TextAlign = ContentAlignment.MiddleCenter });
        root.Controls.Add(headings, 0, 2);

        _days.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));
        _days.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37));
        _days.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
        _days.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
        _days.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        _scroll.Controls.Add(_days);
        root.Controls.Add(_scroll, 0, 3);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, BackColor = WinTheme.Panel };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        footer.Controls.Add(_status, 0, 0);
        var close = PayrollUi.Button("CLOSE", false, 150);
        close.Click += (_, _) => Close();
        var save = PayrollUi.Button("SAVE EMPLOYEE SCHEDULE", true, 205);
        save.Click += async (_, _) => await SaveAsync();
        footer.Controls.Add(close, 1, 0);
        footer.Controls.Add(save, 2, 0);
        root.Controls.Add(footer, 0, 4);
        Controls.Add(root);

        Shown += async (_, _) =>
        {
            await LoadEmployeesAsync();
            await LoadDatesAsync();
        };
    }

    private async Task LoadEmployeesAsync()
    {
        await using var db = _createDb();
        var employees = await db.Employees.AsNoTracking()
            .Where(x => x.StoreId == _storeId && x.IsActive)
            .OrderBy(x => x.FirstName).ThenBy(x => x.LastName)
            .ToListAsync();
        _employee.DisplayMember = "FullName";
        _employee.ValueMember = "Id";
        _employee.DataSource = employees;
    }

    private async Task LoadDatesAsync()
    {
        if (_employee.SelectedValue is not int employeeId) return;
        var from = DateOnly.FromDateTime(_from.Value);
        var to = DateOnly.FromDateTime(_to.Value);
        if (to < from)
        {
            MessageBox.Show(this, "Period end must be on or after period start.", "Add Schedule", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (to.DayNumber - from.DayNumber > 30)
        {
            MessageBox.Show(this, "Select a schedule period of 31 days or less.", "Add Schedule", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        await using var db = _createDb();
        var existing = await db.ScheduleShifts.AsNoTracking()
            .Where(x => x.StoreId == _storeId && x.EmployeeId == employeeId && x.ShiftDate >= from && x.ShiftDate <= to)
            .OrderBy(x => x.ShiftDate).ThenBy(x => x.StartTime)
            .ToListAsync();

        _days.SuspendLayout();
        _days.Controls.Clear();
        _days.RowStyles.Clear();
        _editors.Clear();
        var count = to.DayNumber - from.DayNumber + 1;
        _days.RowCount = count;
        for (var offset = 0; offset < count; offset++)
        {
            var date = from.AddDays(offset);
            var shift = existing.FirstOrDefault(x => x.ShiftDate == date);
            var editor = new ScheduleDayEditor(date, shift);
            _editors.Add(editor);
            _days.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            _days.Controls.Add(editor.Include, 0, offset);
            _days.Controls.Add(editor.DateLabel, 1, offset);
            _days.Controls.Add(editor.Start, 2, offset);
            _days.Controls.Add(editor.End, 3, offset);
            _days.Controls.Add(editor.HoursLabel, 4, offset);
        }
        _days.ResumeLayout(true);
        _status.Text = $"{count} date(s) loaded. Check WORK for each day this employee is scheduled.";
    }

    private async Task SaveAsync()
    {
        if (_employee.SelectedValue is not int employeeId)
        {
            MessageBox.Show(this, "Select an employee.", "Add Schedule", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_editors.Count == 0)
        {
            MessageBox.Show(this, "Load the schedule dates first.", "Add Schedule", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        await using var db = _createDb();
        var from = _editors.Min(x => x.Date);
        var to = _editors.Max(x => x.Date);
        var existing = await db.ScheduleShifts
            .Where(x => x.StoreId == _storeId && x.EmployeeId == employeeId && x.ShiftDate >= from && x.ShiftDate <= to)
            .OrderBy(x => x.StartTime)
            .ToListAsync();
        var saved = 0;
        var removed = 0;
        var locked = 0;

        foreach (var editor in _editors)
        {
            var shift = existing.FirstOrDefault(x => x.ShiftDate == editor.Date);
            if (!editor.Include.Checked)
            {
                if (shift?.Status == ScheduleShiftStatus.Draft)
                {
                    db.ScheduleShifts.Remove(shift);
                    removed++;
                }
                else if (shift is not null)
                {
                    locked++;
                }
                continue;
            }

            if (shift is not null && shift.Status != ScheduleShiftStatus.Draft)
            {
                locked++;
                continue;
            }
            shift ??= new ScheduleShift { StoreId = _storeId, EmployeeId = employeeId, ShiftDate = editor.Date };
            if (shift.Id == 0) db.ScheduleShifts.Add(shift);
            shift.StartTime = TimeOnly.FromDateTime(editor.Start.Value).ToTimeSpan();
            shift.EndTime = TimeOnly.FromDateTime(editor.End.Value).ToTimeSpan();
            shift.UnpaidBreakMinutes = 0;
            shift.Status = ScheduleShiftStatus.Draft;
            shift.UpdatedByName = _user;
            shift.UpdatedUtc = DateTime.UtcNow;
            saved++;
        }
        await db.SaveChangesAsync();
        _status.Text = $"Saved {saved} shift(s); removed {removed} draft shift(s)." +
                       (locked > 0 ? $" {locked} published/completed shift(s) were left unchanged." : "");
        await LoadDatesAsync();
    }

    private sealed class ScheduleDayEditor
    {
        public DateOnly Date { get; }
        public CheckBox Include { get; }
        public Label DateLabel { get; }
        public DateTimePicker Start { get; }
        public DateTimePicker End { get; }
        public Label HoursLabel { get; }

        public ScheduleDayEditor(DateOnly date, ScheduleShift? existing)
        {
            Date = date;
            Include = new CheckBox { Checked = existing is not null, Dock = DockStyle.Fill, CheckAlign = ContentAlignment.MiddleCenter };
            DateLabel = new Label
            {
                Text = date.ToString("dddd, MMMM d, yyyy"),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
                Font = WinTheme.BoldFont(9),
                ForeColor = WinTheme.BlueDark
            };
            Start = TimePicker(existing?.StartTime ?? TimeSpan.FromHours(9));
            End = TimePicker(existing?.EndTime ?? TimeSpan.FromHours(17));
            HoursLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = WinTheme.Text };
            Start.ValueChanged += (_, _) => RefreshHours();
            End.ValueChanged += (_, _) => RefreshHours();
            RefreshHours();
        }

        private static DateTimePicker TimePicker(TimeSpan value) => new()
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "h:mm tt",
            ShowUpDown = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(8),
            Value = DateTime.Today.Add(value),
            Font = WinTheme.BodyFont(10)
        };

        private void RefreshHours()
        {
            var start = TimeOnly.FromDateTime(Start.Value).ToTimeSpan();
            var end = TimeOnly.FromDateTime(End.Value).ToTimeSpan();
            if (end <= start) end = end.Add(TimeSpan.FromDays(1));
            HoursLabel.Text = $"{(end - start).TotalHours:0.##}";
        }
    }
}

internal sealed class ScheduleManagerForm : Form
{
    private readonly Func<AppDbContext> _createDb;
    private readonly int _storeId;
    private readonly string _user;
    private readonly DataGridView _grid = WinTheme.Grid();
    private readonly ComboBox _employee = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly DateTimePicker _date = new() { Format = DateTimePickerFormat.Short, Width = 150 };
    private readonly DateTimePicker _start = new() { Format = DateTimePickerFormat.Time, ShowUpDown = true, Width = 150, Value = DateTime.Today.AddHours(9) };
    private readonly DateTimePicker _end = new() { Format = DateTimePickerFormat.Time, ShowUpDown = true, Width = 150, Value = DateTime.Today.AddHours(17) };
    private readonly NumericUpDown _break = PayrollUi.Number(480, 0);
    private readonly ComboBox _status = PayrollUi.Combo<ScheduleShiftStatus>();
    private readonly TextBox _notes = PayrollUi.TextBox();
    private int? _shiftId;

    public ScheduleManagerForm(Func<AppDbContext> createDb, int storeId, string user)
    {
        _createDb = createDb; _storeId = storeId; _user = user;
        PayrollUi.Prepare(this, "Employee Scheduling - HISAB KITAB", new Size(1320, 820));
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, BackColor = WinTheme.Bg, Padding = new Padding(16) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(PayrollUi.Heading("EMPLOYEE SCHEDULE  •  PUBLISHED HOURS FLOW INTO PAYROLL"), 0, 0);
        var fields = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel, WrapContents = false, AutoScroll = true };
        fields.Controls.AddRange(new Control[] { PayrollUi.Field("EMPLOYEE", _employee, 280), PayrollUi.Field("SHIFT DATE", _date, 170), PayrollUi.Field("START", _start, 170), PayrollUi.Field("END", _end, 170), PayrollUi.Field("UNPAID BREAK MINUTES", _break, 210), PayrollUi.Field("STATUS", _status, 190), PayrollUi.Field("NOTES", _notes, 300) });
        root.Controls.Add(fields, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoScroll = true };
        var save = PayrollUi.Button("SAVE SHIFT", true); save.Click += async (_, _) => await SaveAsync();
        var add = PayrollUi.Button("ADD NEW"); add.Click += (_, _) => Clear();
        var publishPeriod = PayrollUi.Button("PUBLISH PERIOD / EXPORT PDF", true, 260); publishPeriod.Click += async (_, _) => await PublishPeriodAndExportAsync();
        var delete = PayrollUi.Button("DELETE DRAFT"); delete.Click += async (_, _) => await DeleteAsync();
        var textWeek = PayrollUi.Button("TEXT PUBLISHED WEEK", true, 220); textWeek.Click += async (_, _) => await TextPublishedScheduleAsync();
        actions.Controls.AddRange(new Control[] { publishPeriod, textWeek, save, add, delete }); root.Controls.Add(actions, 0, 2); root.Controls.Add(_grid, 0, 3); Controls.Add(root);
        _grid.SelectionChanged += async (_, _) => await SelectAsync();
        Shown += async (_, _) => { await LoadEmployeesAsync(); await RefreshAsync(); };
    }

    private async Task LoadEmployeesAsync()
    {
        await using var db = _createDb();
        var employees = await db.Employees.AsNoTracking().Where(x => x.StoreId == _storeId && x.IsActive).OrderBy(x => x.LastName).ToListAsync();
        _employee.DisplayMember = "FullName"; _employee.ValueMember = "Id"; _employee.DataSource = employees;
    }

    private async Task RefreshAsync()
    {
        await using var db = _createDb();
        var employees = await db.Employees.AsNoTracking().Where(x => x.StoreId == _storeId).ToDictionaryAsync(x => x.Id, x => x.FullName);
        var from = DateOnly.FromDateTime(DateTime.Today.AddDays(-30)); var to = DateOnly.FromDateTime(DateTime.Today.AddDays(90));
        var rows = await db.ScheduleShifts.AsNoTracking().Where(x => x.StoreId == _storeId && x.ShiftDate >= from && x.ShiftDate <= to).OrderBy(x => x.ShiftDate).ThenBy(x => x.StartTime).ToListAsync();
        _grid.DataSource = rows.Select(x => new { x.Id, Date = x.ShiftDate.ToString("ddd MM/dd/yyyy"), Employee = employees.GetValueOrDefault(x.EmployeeId), Start = DateTime.Today.Add(x.StartTime).ToString("h:mm tt"), End = DateTime.Today.Add(x.EndTime).ToString("h:mm tt"), Hours = x.ScheduledHours, Break = x.UnpaidBreakMinutes, Status = x.Status.ToString(), x.Notes }).ToList();
    }

    private async Task SelectAsync()
    {
        if (_grid.CurrentRow?.DataBoundItem is null) return;
        var id = (int)(_grid.CurrentRow.DataBoundItem.GetType().GetProperty("Id")?.GetValue(_grid.CurrentRow.DataBoundItem) ?? 0);
        await using var db = _createDb(); var shift = await db.ScheduleShifts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.StoreId == _storeId); if (shift is null) return;
        _shiftId = shift.Id; _employee.SelectedValue = shift.EmployeeId; _date.Value = shift.ShiftDate.ToDateTime(TimeOnly.MinValue); _start.Value = DateTime.Today.Add(shift.StartTime); _end.Value = DateTime.Today.Add(shift.EndTime); _break.Value = Math.Clamp(shift.UnpaidBreakMinutes, 0, 480); _status.SelectedItem = shift.Status; _notes.Text = shift.Notes;
    }

    private async Task SaveAsync()
    {
        if (_employee.SelectedValue is not int employeeId) { MessageBox.Show(this, "Select an employee."); return; }
        await using var db = _createDb(); var shift = _shiftId.HasValue ? await db.ScheduleShifts.FirstOrDefaultAsync(x => x.Id == _shiftId && x.StoreId == _storeId) : null;
        if (shift is not null && shift.Status == ScheduleShiftStatus.Completed) { MessageBox.Show(this, "Completed shifts are locked. Create an adjustment in Payroll instead."); return; }
        shift ??= new ScheduleShift { StoreId = _storeId }; if (shift.Id == 0) db.ScheduleShifts.Add(shift);
        shift.EmployeeId = employeeId; shift.ShiftDate = DateOnly.FromDateTime(_date.Value); shift.StartTime = TimeOnly.FromDateTime(_start.Value).ToTimeSpan(); shift.EndTime = TimeOnly.FromDateTime(_end.Value).ToTimeSpan(); shift.UnpaidBreakMinutes = decimal.ToInt32(_break.Value); shift.Status = (ScheduleShiftStatus)(_status.SelectedItem ?? ScheduleShiftStatus.Draft); shift.Notes = _notes.Text.Trim(); shift.UpdatedByName = _user; shift.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(); _shiftId = shift.Id; await RefreshAsync();
    }

    private async Task PublishPeriodAndExportAsync()
    {
        var selected = DateOnly.FromDateTime(_date.Value);
        var daysSinceMonday = ((int)selected.DayOfWeek + 6) % 7;
        var defaultFrom = selected.AddDays(-daysSinceMonday);
        if (!SchedulePublishRangeDialog.TrySelect(this, defaultFrom, defaultFrom.AddDays(6), out var from, out var to)) return;

        await using var db = _createDb();
        var shifts = await db.ScheduleShifts
            .Where(x => x.StoreId == _storeId && x.ShiftDate >= from && x.ShiftDate <= to &&
                        (x.Status == ScheduleShiftStatus.Draft || x.Status == ScheduleShiftStatus.Published))
            .OrderBy(x => x.ShiftDate).ThenBy(x => x.StartTime)
            .ToListAsync();
        if (shifts.Count == 0)
        {
            MessageBox.Show(this, "No draft or published shifts exist in the selected period.", "Publish Schedule", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var save = new SaveFileDialog
        {
            Title = "Export Published Schedule",
            Filter = "PDF files (*.pdf)|*.pdf",
            FileName = $"Schedule_{from:yyyyMMdd}_{to:yyyyMMdd}.pdf",
            AddExtension = true,
            DefaultExt = "pdf"
        };
        if (save.ShowDialog(this) != DialogResult.OK) return;

        foreach (var shift in shifts.Where(x => x.Status == ScheduleShiftStatus.Draft))
        {
            shift.Status = ScheduleShiftStatus.Published;
            shift.UpdatedByName = _user;
            shift.UpdatedUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();

        var employees = await db.Employees.AsNoTracking()
            .Where(x => x.StoreId == _storeId && x.IsActive)
            .ToDictionaryAsync(x => x.Id);
        var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(x => x.Id == _storeId)
                    ?? await db.Stores.AsNoTracking().FirstOrDefaultAsync();
        SchedulePdf.Generate(store?.Name ?? "Business", from, to, shifts, employees, save.FileName);
        await RefreshAsync();
        MessageBox.Show(this,
            $"The schedule was published and exported successfully.\n\n{save.FileName}",
            "Schedule Published",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private async Task DeleteAsync()
    {
        if (!_shiftId.HasValue) return; await using var db = _createDb(); var shift = await db.ScheduleShifts.FirstOrDefaultAsync(x => x.Id == _shiftId && x.StoreId == _storeId); if (shift is null) return; if (shift.Status != ScheduleShiftStatus.Draft) { MessageBox.Show(this, "Only draft shifts can be deleted. Cancel published shifts instead."); return; } db.ScheduleShifts.Remove(shift); await db.SaveChangesAsync(); Clear(); await RefreshAsync();
    }

    private async Task TextPublishedScheduleAsync()
    {
        var selectedDate = DateOnly.FromDateTime(_date.Value);
        var defaultFrom = selectedDate.AddDays(-(int)selectedDate.DayOfWeek);
        if (!ScheduleSendRangeDialog.TrySelect(this, defaultFrom, defaultFrom.AddDays(6), out var from, out var to)) return;
        await using var db = _createDb();
        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings is null || !settings.SmsGatewayEnabled || string.IsNullOrWhiteSpace(settings.SmsGatewayUrl))
        {
            MessageBox.Show(this, "Configure and enable the Android SMS gateway first.", "Schedule Texting", MessageBoxButtons.OK, MessageBoxIcon.Information);
            using var setup = new ScheduleSmsSettingsForm(_createDb); setup.ShowDialog(this); return;
        }
        var existing = await db.ScheduleNotifications.CountAsync(x => x.StoreId == _storeId && x.ScheduleFrom == from && x.ScheduleTo == to && x.Status == "Sent");
        if (existing > 0 && MessageBox.Show(this, $"{existing} schedule text(s) were already sent for this period. Send updated schedules again?", "Resend Schedule", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        var shifts = await db.ScheduleShifts.AsNoTracking().Where(x => x.StoreId == _storeId && x.ShiftDate >= from && x.ShiftDate <= to && x.Status == ScheduleShiftStatus.Published).OrderBy(x => x.ShiftDate).ThenBy(x => x.StartTime).ToListAsync();
        if (shifts.Count == 0) { MessageBox.Show(this, "No published shifts exist in the selected period.", "Schedule Texting", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        var employeeIds = shifts.Select(x => x.EmployeeId).Distinct().ToList();
        var employees = await db.Employees.AsNoTracking().Where(x => x.StoreId == _storeId && employeeIds.Contains(x.Id) && x.IsActive).ToDictionaryAsync(x => x.Id);
        var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(x => x.Id == _storeId) ?? await db.Stores.AsNoTracking().FirstOrDefaultAsync();
        string password;
        try { password = PayrollSensitiveDataProtector.UnprotectText(settings.SmsGatewayPasswordEncrypted); }
        catch { MessageBox.Show(this, "The gateway password cannot be decrypted on this PC. Open SMS SETUP and save it again.", "Schedule Texting", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }

        var sent = 0; var failed = 0;
        foreach (var group in shifts.GroupBy(x => x.EmployeeId))
        {
            if (!employees.TryGetValue(group.Key, out var employee)) continue;
            var message = BuildScheduleMessage(store?.Name ?? "Your workplace", employee, from, to, group);
            var log = new ScheduleNotification { StoreId = _storeId, EmployeeId = employee.Id, ScheduleFrom = from, ScheduleTo = to, PhoneNumber = employee.Phone, MessageText = message, CreatedByName = _user };
            db.ScheduleNotifications.Add(log); await db.SaveChangesAsync();
            try
            {
                var response = await ScheduleSmsGatewayService.SendAsync(settings.SmsGatewayUrl, settings.SmsGatewayUsername, password, employee.Phone, message);
                log.Status = "Sent"; log.SentUtc = DateTime.UtcNow; log.GatewayResponse = response; sent++;
            }
            catch (Exception ex) { log.Status = "Failed"; log.GatewayResponse = ex.Message; failed++; }
            await db.SaveChangesAsync();
        }
        MessageBox.Show(this, $"Schedule texting finished.\n\nSent: {sent}\nFailed: {failed}\n\nFailures are retained in the notification log for review.", "Schedule Texting", MessageBoxButtons.OK, failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private static string BuildScheduleMessage(string storeName, Employee employee, DateOnly from, DateOnly to, IEnumerable<ScheduleShift> shifts)
    {
        var lines = shifts.Select(shift => $"{shift.ShiftDate:ddd M/d} {DateTime.Today.Add(shift.StartTime):h:mm tt}-{DateTime.Today.Add(shift.EndTime):h:mm tt}" + (shift.UnpaidBreakMinutes > 0 ? $" ({shift.UnpaidBreakMinutes}m break)" : ""));
        return $"{storeName}: {employee.FirstName}'s schedule {from:M/d}-{to:M/d}:\n{string.Join("; ", lines)}\nContact your manager for changes.";
    }

    private void Clear() { _shiftId = null; _date.Value = DateTime.Today; _start.Value = DateTime.Today.AddHours(9); _end.Value = DateTime.Today.AddHours(17); _break.Value = 0; _status.SelectedItem = ScheduleShiftStatus.Draft; _notes.Clear(); }
}

internal static class SchedulePublishRangeDialog
{
    public static bool TrySelect(IWin32Window owner, DateOnly initialFrom, DateOnly initialTo, out DateOnly from, out DateOnly to)
    {
        using var dialog = new Form
        {
            Text = "Publish and Export Schedule",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(600, 320),
            MinimumSize = new Size(600, 320),
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = WinTheme.Bg,
            Font = WinTheme.BodyFont(10),
            AutoScaleMode = AutoScaleMode.Dpi,
            Icon = WinTheme.TryLoadIcon()
        };
        var start = new DateTimePicker { Format = DateTimePickerFormat.Short, Value = initialFrom.ToDateTime(TimeOnly.MinValue), Dock = DockStyle.Fill };
        var end = new DateTimePicker { Format = DateTimePickerFormat.Short, Value = initialTo.ToDateTime(TimeOnly.MinValue), Dock = DockStyle.Fill };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 2, Padding = new Padding(20), BackColor = WinTheme.Bg };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        var heading = PayrollUi.Heading("PUBLISH SCHEDULE AND EXPORT PDF");
        root.Controls.Add(heading, 0, 0);
        root.SetColumnSpan(heading, 2);
        root.Controls.Add(PayrollUi.Field("SCHEDULE FROM", start, 260), 0, 1);
        root.Controls.Add(PayrollUi.Field("SCHEDULE TO", end, 260), 1, 1);
        var info = new Label
        {
            Text = "All draft shifts in this period will be published. The PDF will show each employee and the exact selected start and end times.",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Muted,
            Padding = new Padding(7),
            TextAlign = ContentAlignment.MiddleLeft
        };
        root.Controls.Add(info, 0, 2);
        root.SetColumnSpan(info, 2);
        var publish = PayrollUi.Button("PUBLISH AND EXPORT PDF", true, 250);
        publish.DialogResult = DialogResult.OK;
        var cancel = PayrollUi.Button("CANCEL", false, 200);
        cancel.DialogResult = DialogResult.Cancel;
        root.Controls.Add(publish, 0, 3);
        root.Controls.Add(cancel, 1, 3);
        dialog.Controls.Add(root);
        dialog.AcceptButton = publish;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(owner) != DialogResult.OK)
        {
            from = default;
            to = default;
            return false;
        }

        from = DateOnly.FromDateTime(start.Value);
        to = DateOnly.FromDateTime(end.Value);
        if (to < from)
        {
            MessageBox.Show(owner, "Schedule end must be on or after schedule start.", "Publish Schedule", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        return true;
    }
}

internal static class ScheduleSendRangeDialog
{
    public static bool TrySelect(IWin32Window owner, DateOnly initialFrom, DateOnly initialTo, out DateOnly from, out DateOnly to)
    {
        using var dialog = new Form { Text = "Text Published Schedule", StartPosition = FormStartPosition.CenterParent, Size = new Size(560, 300), MinimumSize = new Size(560, 300), MaximizeBox = false, MinimizeBox = false, BackColor = WinTheme.Bg, Font = WinTheme.BodyFont(10) };
        var start = new DateTimePicker { Format = DateTimePickerFormat.Short, Value = initialFrom.ToDateTime(TimeOnly.MinValue), Dock = DockStyle.Fill };
        var end = new DateTimePicker { Format = DateTimePickerFormat.Short, Value = initialTo.ToDateTime(TimeOnly.MinValue), Dock = DockStyle.Fill };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 2, Padding = new Padding(18) }; root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 75)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        var heading = PayrollUi.Heading("TEXT PUBLISHED SCHEDULES"); root.Controls.Add(heading,0,0); root.SetColumnSpan(heading,2); root.Controls.Add(PayrollUi.Field("FROM",start,240),0,1); root.Controls.Add(PayrollUi.Field("TO",end,240),1,1);
        var info = new Label { Text = "One text will be generated per employee containing every published shift in this date range.", Dock = DockStyle.Fill, ForeColor = WinTheme.Muted, Padding = new Padding(6) }; root.Controls.Add(info,0,2); root.SetColumnSpan(info,2);
        var send = PayrollUi.Button("SEND SCHEDULE TEXTS",true,240); send.DialogResult=DialogResult.OK; var cancel=PayrollUi.Button("CANCEL"); cancel.DialogResult=DialogResult.Cancel; root.Controls.Add(send,0,3); root.Controls.Add(cancel,1,3); dialog.Controls.Add(root); dialog.AcceptButton=send; dialog.CancelButton=cancel;
        if(dialog.ShowDialog(owner)!=DialogResult.OK){from=default;to=default;return false;} from=DateOnly.FromDateTime(start.Value);to=DateOnly.FromDateTime(end.Value);if(to<from){MessageBox.Show(owner,"To date must be on or after From date.");return false;}return true;
    }
}

internal sealed class ManualHoursRow
{
    [Browsable(false)] public int EmployeeId { get; set; }
    public string Employee { get; set; } = "";
    public decimal RegularHours { get; set; }
    public decimal OvertimeHours { get; set; }
    public decimal HolidayHours { get; set; }
    public string Notes { get; set; } = "";
}

internal sealed class EmployeeHoursForm : Form
{
    private readonly Func<AppDbContext> _createDb;
    private readonly int _storeId;
    private readonly string _user;
    private readonly DateTimePicker _from = new() { Format = DateTimePickerFormat.Short };
    private readonly DateTimePicker _to = new() { Format = DateTimePickerFormat.Short };
    private readonly ComboBox _frequency = PayrollUi.Combo<PayFrequency>();
    private readonly DataGridView _grid = WinTheme.Grid();
    private readonly BindingList<ManualHoursRow> _rows = new();

    public EmployeeHoursForm(Func<AppDbContext> createDb, int storeId, string user, DateOnly? initialFrom = null, DateOnly? initialTo = null, PayFrequency? initialFrequency = null)
    {
        _createDb = createDb; _storeId = storeId; _user = user;
        PayrollUi.Prepare(this, "Enter Employee Hours - HISAB KITAB", new Size(1180, 760));
        var today = DateOnly.FromDateTime(DateTime.Today); var weekStart = today.AddDays(-(int)today.DayOfWeek);
        _from.Value = (initialFrom ?? weekStart.AddDays(-7)).ToDateTime(TimeOnly.MinValue);
        _to.Value = (initialTo ?? weekStart.AddDays(-1)).ToDateTime(TimeOnly.MinValue);
        _frequency.SelectedItem = initialFrequency ?? PayFrequency.Weekly;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, BackColor = WinTheme.Bg, Padding = new Padding(16) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        root.Controls.Add(PayrollUi.Heading("ENTER EMPLOYEE HOURS  •  SELECT A PAYROLL PERIOD AND ENTER HOURS MANUALLY"), 0, 0);
        var filters = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel, WrapContents = false, AutoScroll = true };
        filters.Controls.AddRange(new Control[] { PayrollUi.Field("PERIOD START", _from, 210), PayrollUi.Field("PERIOD END", _to, 210), PayrollUi.Field("PAY FREQUENCY", _frequency, 260) }); root.Controls.Add(filters, 0, 1);
        var info = new Label { Text = "Use this for owners, managers, or employees whose hours are not available from Scheduling. Saved values automatically load into Run Payroll for the exact period.", Dock = DockStyle.Fill, ForeColor = WinTheme.Muted, Font = WinTheme.BodyFont(9.5f), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 8, 0) }; root.Controls.Add(info, 0, 2);
        ConfigureGrid(); _grid.DataSource = _rows; root.Controls.Add(_grid, 0, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 7, 0, 0) };
        var save = PayrollUi.Button("SAVE PERIOD HOURS", true, 220); save.Click += async (_, _) => await SaveAsync();
        var load = PayrollUi.Button("LOAD EMPLOYEES", true, 200); load.Click += async (_, _) => await LoadAsync();
        var close = PayrollUi.Button("CLOSE"); close.Click += (_, _) => Close(); actions.Controls.AddRange(new Control[] { save, load, close }); root.Controls.Add(actions, 0, 4);
        Controls.Add(root); Shown += async (_, _) => await LoadAsync();
    }

    private void ConfigureGrid()
    {
        _grid.ReadOnly = false; _grid.AutoGenerateColumns = false; _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; _grid.EditMode = DataGridViewEditMode.EditOnEnter;
        _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False; _grid.ColumnHeadersHeight = 42;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ManualHoursRow.Employee), HeaderText = "Employee", ReadOnly = true, FillWeight = 180, MinimumWidth = 220 });
        _grid.Columns.Add(HoursColumn(nameof(ManualHoursRow.RegularHours), "Regular Hours"));
        _grid.Columns.Add(HoursColumn(nameof(ManualHoursRow.OvertimeHours), "Overtime Hours"));
        _grid.Columns.Add(HoursColumn(nameof(ManualHoursRow.HolidayHours), "Holiday Hours"));
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ManualHoursRow.Notes), HeaderText = "Admin Notes / Reason", FillWeight = 220, MinimumWidth = 260, DefaultCellStyle = EditableStyle() });
    }

    private static DataGridViewTextBoxColumn HoursColumn(string property, string heading) => new() { DataPropertyName = property, HeaderText = heading, FillWeight = 100, MinimumWidth = 130, DefaultCellStyle = EditableStyle("N2") };
    private static DataGridViewCellStyle EditableStyle(string format = "") => new() { BackColor = Color.FromArgb(232, 248, 236), ForeColor = WinTheme.Text, Format = format };

    private async Task LoadAsync()
    {
        _grid.EndEdit(); var start = DateOnly.FromDateTime(_from.Value); var end = DateOnly.FromDateTime(_to.Value);
        if (end < start) { MessageBox.Show(this, "Period end must be on or after period start."); return; }
        var frequency = (PayFrequency)(_frequency.SelectedItem ?? PayFrequency.Weekly);
        await using var db = _createDb();
        var employees = await db.Employees.AsNoTracking().Where(x => x.StoreId == _storeId && x.IsActive && x.PayFrequency == frequency).OrderBy(x => x.LastName).ThenBy(x => x.FirstName).ToListAsync();
        var ids = employees.Select(x => x.Id).ToList();
        var saved = await db.EmployeePeriodHours.AsNoTracking().Where(x => x.StoreId == _storeId && ids.Contains(x.EmployeeId) && x.PeriodStart == start && x.PeriodEnd == end).ToDictionaryAsync(x => x.EmployeeId);
        _rows.RaiseListChangedEvents = false; _rows.Clear();
        foreach (var employee in employees)
        {
            saved.TryGetValue(employee.Id, out var hours);
            _rows.Add(new ManualHoursRow { EmployeeId = employee.Id, Employee = employee.FullName, RegularHours = hours?.RegularHours ?? 0, OvertimeHours = hours?.OvertimeHours ?? 0, HolidayHours = hours?.HolidayHours ?? 0, Notes = hours?.Notes ?? "" });
        }
        _rows.RaiseListChangedEvents = true; _rows.ResetBindings();
        if (_rows.Count == 0) MessageBox.Show(this, "No active employees use the selected pay frequency.", "Employee Hours", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task SaveAsync()
    {
        _grid.EndEdit(); var start = DateOnly.FromDateTime(_from.Value); var end = DateOnly.FromDateTime(_to.Value);
        if (end < start) { MessageBox.Show(this, "Period end must be on or after period start."); return; }
        if (_rows.Any(x => x.RegularHours < 0 || x.OvertimeHours < 0 || x.HolidayHours < 0)) { MessageBox.Show(this, "Hours cannot be negative."); return; }
        await using var db = _createDb(); var ids = _rows.Select(x => x.EmployeeId).ToList();
        var existing = await db.EmployeePeriodHours.Where(x => x.StoreId == _storeId && ids.Contains(x.EmployeeId) && x.PeriodStart == start && x.PeriodEnd == end).ToDictionaryAsync(x => x.EmployeeId);
        foreach (var row in _rows)
        {
            if (!existing.TryGetValue(row.EmployeeId, out var hours)) { hours = new EmployeePeriodHours { StoreId = _storeId, EmployeeId = row.EmployeeId, PeriodStart = start, PeriodEnd = end }; db.EmployeePeriodHours.Add(hours); }
            hours.RegularHours = row.RegularHours; hours.OvertimeHours = row.OvertimeHours; hours.HolidayHours = row.HolidayHours; hours.Notes = row.Notes.Trim(); hours.UpdatedByName = _user; hours.UpdatedUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(); MessageBox.Show(this, $"Hours saved for {start:MM/dd/yyyy} - {end:MM/dd/yyyy}. They will load automatically in Run Payroll.", "Employee Hours Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}

internal sealed class PayrollWorkRow
{
    public int EmployeeId { get; set; }
    public string Employee { get; set; } = "";
    public decimal Scheduled { get; set; }
    public decimal Regular { get; set; }
    public decimal Overtime { get; set; }
    public decimal Holiday { get; set; }
    public decimal Bonus { get; set; }
    public decimal CashAdvance { get; set; }
    public decimal OtherDeduction { get; set; }
    public string OverrideReason { get; set; } = "";
    public decimal Gross { get; set; }
    [Browsable(false)] public decimal RegularPay { get; set; }
    [Browsable(false)] public decimal OvertimePay { get; set; }
    [Browsable(false)] public decimal HolidayPay { get; set; }
    public decimal Federal { get; set; }
    public decimal SocialSecurity { get; set; }
    public decimal Medicare { get; set; }
    public decimal State { get; set; }
    public decimal Net { get; set; }
    public string CheckNumber { get; set; } = "";
}

internal sealed class PayrollRunForm : Form
{
    private readonly Func<AppDbContext> _createDb;
    private readonly int _storeId;
    private readonly string _user;
    private readonly DateTimePicker _from = new() { Format = DateTimePickerFormat.Short, Width = 140 };
    private readonly DateTimePicker _to = new() { Format = DateTimePickerFormat.Short, Width = 140 };
    private readonly DateTimePicker _payDate = new() { Format = DateTimePickerFormat.Short, Width = 140 };
    private readonly ComboBox _frequency = PayrollUi.Combo<PayFrequency>();
    private readonly NumericUpDown _firstCheck = PayrollUi.Number(9_999_999, 0);
    private readonly DataGridView _grid = WinTheme.Grid();
    private readonly BindingList<PayrollWorkRow> _rows = new();
    private readonly Label _totals = new() { Dock = DockStyle.Fill, ForeColor = WinTheme.BlueDark, Font = WinTheme.BoldFont(10), TextAlign = ContentAlignment.MiddleLeft };
    private int? _runId;

    public PayrollRunForm(Func<AppDbContext> createDb, int storeId, string user)
    {
        _createDb = createDb; _storeId = storeId; _user = user;
        PayrollUi.Prepare(this, "Run Payroll - HISAB KITAB", new Size(1600, 900));
        var today = DateTime.Today; var weekStart = today.AddDays(-(int)today.DayOfWeek);
        _from.Value = weekStart.AddDays(-7); _to.Value = weekStart.AddDays(-1); _payDate.Value = today; _frequency.SelectedItem = PayFrequency.Weekly; _firstCheck.Value = 1001;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, BackColor = WinTheme.Bg, Padding = new Padding(14) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        root.Controls.Add(PayrollUi.Heading("RUN PAYROLL  •  REVIEW SCHEDULED HOURS, ADJUST, CALCULATE, APPROVE, AND FINALIZE"), 0, 0);
        var filters = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel, WrapContents = false, AutoScroll = true };
        filters.Controls.AddRange(new Control[] { PayrollUi.Field("PERIOD START", _from, 170), PayrollUi.Field("PERIOD END", _to, 170), PayrollUi.Field("PAY DATE", _payDate, 170), PayrollUi.Field("PAY FREQUENCY", _frequency), PayrollUi.Field("FIRST CHECK NUMBER", _firstCheck, 210) });
        root.Controls.Add(filters, 0, 1);
        var loadBar = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel, ColumnCount = 4, RowCount = 1, Padding = new Padding(0, 6, 0, 6) };
        loadBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280)); loadBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230)); loadBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230)); loadBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var load = PayrollUi.Button("LOAD EMPLOYEES & SCHEDULE", true, 260); load.Click += async (_, _) => await LoadRowsAsync();
        var hours = PayrollUi.Button("ENTER / EDIT HOURS", false, 210); hours.Click += async (_, _) => await OpenHoursAsync();
        var calculate = PayrollUi.Button("CALCULATE PAYROLL", true, 210); calculate.Click += async (_, _) => await CalculateAsync();
        load.Dock = DockStyle.Fill; hours.Dock = DockStyle.Fill; calculate.Dock = DockStyle.Fill; loadBar.Controls.Add(load, 0, 0); loadBar.Controls.Add(hours, 1, 0); loadBar.Controls.Add(calculate, 2, 0); loadBar.Controls.Add(_totals, 3, 0); root.Controls.Add(loadBar, 0, 2);

        _grid.ReadOnly = false; _grid.AutoGenerateColumns = true; _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None; _grid.ScrollBars = ScrollBars.Both; _grid.DataSource = _rows; _grid.AllowUserToAddRows = false; _grid.EditMode = DataGridViewEditMode.EditOnEnter;
        _grid.DataBindingComplete += (_, _) => ConfigureGrid();
        root.Controls.Add(_grid, 0, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel, FlowDirection = FlowDirection.RightToLeft };
        var finalize = PayrollUi.Button("APPROVE & FINALIZE", true, 220); finalize.Click += async (_, _) => await FinalizeAsync();
        var preview = PayrollUi.Button("PREVIEW CHECKS & STUBS", true, 240); preview.Click += async (_, _) => await PreviewAsync();
        var draft = PayrollUi.Button("SAVE DRAFT", false, 170); draft.Click += async (_, _) => await SaveDraftAsync();
        var close = PayrollUi.Button("CLOSE"); close.Click += (_, _) => Close();
        actions.Controls.AddRange(new Control[] { finalize, preview, draft, close }); root.Controls.Add(actions, 0, 4); Controls.Add(root);
        Shown += async (_, _) => await LoadRowsAsync();
    }

    private void ConfigureGrid()
    {
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.ColumnHeadersHeight = 44;
        foreach (DataGridViewColumn column in _grid.Columns)
        {
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
            column.MinimumWidth = 80;
            column.DefaultCellStyle.Format = column.Name is nameof(PayrollWorkRow.Employee) or nameof(PayrollWorkRow.OverrideReason) or nameof(PayrollWorkRow.CheckNumber) ? "" : "N2";
            column.ReadOnly = column.Name is nameof(PayrollWorkRow.Employee) or nameof(PayrollWorkRow.Scheduled) or nameof(PayrollWorkRow.Gross) or nameof(PayrollWorkRow.Federal) or nameof(PayrollWorkRow.SocialSecurity) or nameof(PayrollWorkRow.Medicare) or nameof(PayrollWorkRow.State) or nameof(PayrollWorkRow.Net) or nameof(PayrollWorkRow.EmployeeId);
            column.Visible = column.Name != nameof(PayrollWorkRow.EmployeeId);
            if (!column.ReadOnly) column.DefaultCellStyle.BackColor = Color.FromArgb(232, 248, 236);
        }
        ConfigureColumn(nameof(PayrollWorkRow.Employee), "Employee", 210);
        ConfigureColumn(nameof(PayrollWorkRow.Scheduled), "Scheduled Hours", 115);
        ConfigureColumn(nameof(PayrollWorkRow.Regular), "Regular Hours (Editable)", 155);
        ConfigureColumn(nameof(PayrollWorkRow.Overtime), "Overtime Hours (Editable)", 165);
        ConfigureColumn(nameof(PayrollWorkRow.Holiday), "Holiday Hours (Editable)", 155);
        ConfigureColumn(nameof(PayrollWorkRow.Bonus), "Bonus Pay", 110);
        ConfigureColumn(nameof(PayrollWorkRow.CashAdvance), "Cash Advance", 125);
        ConfigureColumn(nameof(PayrollWorkRow.OtherDeduction), "Other Deduction", 135);
        ConfigureColumn(nameof(PayrollWorkRow.OverrideReason), "Admin Override Reason", 240);
        ConfigureColumn(nameof(PayrollWorkRow.Gross), "Gross Pay", 110);
        ConfigureColumn(nameof(PayrollWorkRow.Federal), "Federal Tax", 115);
        ConfigureColumn(nameof(PayrollWorkRow.SocialSecurity), "Social Security", 125);
        ConfigureColumn(nameof(PayrollWorkRow.Medicare), "Medicare", 100);
        ConfigureColumn(nameof(PayrollWorkRow.State), "State Tax", 100);
        ConfigureColumn(nameof(PayrollWorkRow.Net), "Net Pay", 110);
        ConfigureColumn(nameof(PayrollWorkRow.CheckNumber), "Check #", 100);
    }

    private void ConfigureColumn(string name, string heading, int width)
    {
        if (_grid.Columns[name] is not { } column) return;
        column.HeaderText = heading; column.Width = width;
    }

    private async Task OpenHoursAsync()
    {
        if (_runId.HasValue) { MessageBox.Show(this, "This payroll draft is already saved. Period hours cannot be replaced from the separate entry screen."); return; }
        var frequency = (PayFrequency)(_frequency.SelectedItem ?? PayFrequency.Weekly);
        using var form = new EmployeeHoursForm(_createDb, _storeId, _user, DateOnly.FromDateTime(_from.Value), DateOnly.FromDateTime(_to.Value), frequency);
        form.ShowDialog(this); await LoadRowsAsync();
    }

    private async Task LoadRowsAsync()
    {
        if (_runId.HasValue) { MessageBox.Show(this, "This payroll draft is already saved. Close and reopen Run Payroll to start another period."); return; }
        var start = DateOnly.FromDateTime(_from.Value); var end = DateOnly.FromDateTime(_to.Value);
        if (end < start) { MessageBox.Show(this, "Period end must be on or after period start."); return; }
        var frequency = (PayFrequency)(_frequency.SelectedItem ?? PayFrequency.Weekly);
        await using var db = _createDb();
        var employees = await db.Employees.AsNoTracking().Where(x => x.StoreId == _storeId && x.IsActive && x.PayFrequency == frequency).OrderBy(x => x.LastName).ToListAsync();
        var employeeIds = employees.Select(x => x.Id).ToList();
        var shifts = await db.ScheduleShifts.AsNoTracking().Where(x => x.StoreId == _storeId && employeeIds.Contains(x.EmployeeId) && x.ShiftDate >= start && x.ShiftDate <= end && (x.Status == ScheduleShiftStatus.Published || x.Status == ScheduleShiftStatus.Completed)).ToListAsync();
        var manualHours = await db.EmployeePeriodHours.AsNoTracking().Where(x => x.StoreId == _storeId && employeeIds.Contains(x.EmployeeId) && x.PeriodStart == start && x.PeriodEnd == end).ToDictionaryAsync(x => x.EmployeeId);
        _rows.RaiseListChangedEvents = false; _rows.Clear(); var check = decimal.ToInt32(_firstCheck.Value);
        foreach (var employee in employees)
        {
            var employeeShifts = shifts.Where(x => x.EmployeeId == employee.Id).ToList();
            var scheduled = employeeShifts.Sum(x => x.ScheduledHours);
            var (regular, overtime) = SplitOvertimeByWorkweek(employeeShifts, employee.IsOvertimeEligible);
            decimal holiday = 0; var reason = "";
            if (manualHours.TryGetValue(employee.Id, out var manual))
            {
                regular = manual.RegularHours; overtime = manual.OvertimeHours; holiday = manual.HolidayHours;
                reason = string.IsNullOrWhiteSpace(manual.Notes) ? $"Manual period hours entered by {manual.UpdatedByName}" : manual.Notes;
            }
            _rows.Add(new PayrollWorkRow { EmployeeId = employee.Id, Employee = employee.FullName, Scheduled = scheduled, Regular = regular, Overtime = overtime, Holiday = holiday, OverrideReason = reason, CheckNumber = (check++).ToString() });
        }
        _rows.RaiseListChangedEvents = true; _rows.ResetBindings(); UpdateTotals();
        if (_rows.Count == 0) MessageBox.Show(this, "No active employees use the selected pay frequency. Add employees or change the pay frequency.", "Run Payroll", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static (decimal Regular, decimal Overtime) SplitOvertimeByWorkweek(IEnumerable<ScheduleShift> shifts, bool overtimeEligible)
    {
        decimal regular = 0, overtime = 0;
        foreach (var week in shifts.GroupBy(x => x.ShiftDate.AddDays(-(int)x.ShiftDate.DayOfWeek)))
        {
            var hours = week.Sum(x => x.ScheduledHours);
            if (overtimeEligible) { regular += Math.Min(40, hours); overtime += Math.Max(0, hours - 40); }
            else regular += hours;
        }
        return (regular, overtime);
    }

    private async Task<bool> CalculateAsync()
    {
        _grid.EndEdit();
        if (_rows.Count == 0) return false;
        var employeeIds = _rows.Select(x => x.EmployeeId).ToList(); var payDate = DateOnly.FromDateTime(_payDate.Value);
        if (payDate.Year != PayrollCalculator2026.TaxYear)
        {
            MessageBox.Show(this, $"Payroll tax tables are currently certified for {PayrollCalculator2026.TaxYear}. Install the new tax-year update before calculating a {payDate.Year} payroll.", "Tax Table Update Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        await using var db = _createDb();
        var employees = await db.Employees.AsNoTracking().Where(x => employeeIds.Contains(x.Id) && x.StoreId == _storeId).ToDictionaryAsync(x => x.Id);
        var unsupported = employees.Values.FirstOrDefault(x => !x.WorkState.Equals("IL", StringComparison.OrdinalIgnoreCase));
        if (unsupported is not null)
        {
            MessageBox.Show(this, $"{unsupported.FullName} uses work state {unsupported.WorkState}. This release includes verified Illinois withholding only, so payroll was not calculated.", "State Tax Table Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        var prior = await db.PayrollEntries.AsNoTracking().Where(x => employeeIds.Contains(x.EmployeeId) && x.PayrollRun!.StoreId == _storeId && x.PayrollRun.Status == PayrollRunStatus.Finalized && x.PayrollRun.TaxYear == payDate.Year && x.PayrollRun.PayDate < payDate).GroupBy(x => x.EmployeeId).Select(x => new { EmployeeId = x.Key, Gross = x.Sum(y => y.GrossPay) }).ToDictionaryAsync(x => x.EmployeeId, x => x.Gross);
        foreach (var row in _rows)
        {
            if (!employees.TryGetValue(row.EmployeeId, out var employee)) continue;
            var calculation = PayrollCalculator2026.Calculate(employee, Math.Max(0, row.Regular), Math.Max(0, row.Overtime), Math.Max(0, row.Holiday), Math.Max(0, row.Bonus), Math.Max(0, row.CashAdvance), Math.Max(0, row.OtherDeduction), prior.GetValueOrDefault(row.EmployeeId));
            row.RegularPay = calculation.RegularPay; row.OvertimePay = calculation.OvertimePay; row.HolidayPay = calculation.HolidayPay;
            row.Gross = calculation.GrossPay; row.Federal = calculation.FederalWithholding; row.SocialSecurity = calculation.SocialSecurity; row.Medicare = calculation.Medicare; row.State = calculation.StateWithholding; row.Net = calculation.NetPay;
        }
        _rows.ResetBindings(); UpdateTotals();
        return true;
    }

    private async Task SaveDraftAsync()
    {
        if (!ValidateRows() || !await CalculateAsync()) return;
        var start = DateOnly.FromDateTime(_from.Value); var end = DateOnly.FromDateTime(_to.Value); var payDate = DateOnly.FromDateTime(_payDate.Value);
        await using var db = _createDb(); await using var tx = await db.Database.BeginTransactionAsync();
        var overlapping = await db.PayrollRuns.AsNoTracking().AnyAsync(x => x.StoreId == _storeId && x.Id != (_runId ?? 0) && x.Status != PayrollRunStatus.Voided && x.PeriodStart <= end && x.PeriodEnd >= start);
        if (overlapping) { MessageBox.Show(this, "Another non-voided payroll overlaps this pay period. Open Payroll History and void or correct that run first.", "Duplicate Payroll Prevented", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        PayrollRun run;
        if (_runId.HasValue)
        {
            run = await db.PayrollRuns.Include(x => x.Entries).FirstAsync(x => x.Id == _runId && x.StoreId == _storeId);
            if (run.Status != PayrollRunStatus.Draft) throw new InvalidOperationException("Only draft payroll can be edited.");
            db.PayrollEntries.RemoveRange(run.Entries);
        }
        else
        {
            run = new PayrollRun { StoreId = _storeId, CreatedByName = _user, CreatedUtc = DateTime.UtcNow, Status = PayrollRunStatus.Draft };
            db.PayrollRuns.Add(run);
        }
        run.PeriodStart = start; run.PeriodEnd = end; run.PayDate = payDate; run.PayFrequency = (PayFrequency)(_frequency.SelectedItem ?? PayFrequency.Weekly); run.TaxYear = payDate.Year;
        await db.SaveChangesAsync();
        var employeeIds = _rows.Select(x => x.EmployeeId).ToList();
        var prior = await PriorYtdAsync(db, employeeIds, payDate, run.Id);
        foreach (var row in _rows)
        {
            var ytd = prior.GetValueOrDefault(row.EmployeeId) ?? new Ytd(0, 0, 0, 0, 0);
            db.PayrollEntries.Add(new PayrollEntry
            {
                PayrollRunId = run.Id, EmployeeId = row.EmployeeId, EmployeeName = row.Employee,
                PayRate = await db.Employees.Where(x => x.Id == row.EmployeeId).Select(x => x.PayRate).FirstAsync(),
                PayType = await db.Employees.Where(x => x.Id == row.EmployeeId).Select(x => x.PayType).FirstAsync(),
                ScheduledHours = row.Scheduled, RegularHours = row.Regular, OvertimeHours = row.Overtime, HolidayHours = row.Holiday,
                RegularPay = row.RegularPay, OvertimePay = row.OvertimePay, HolidayPay = row.HolidayPay, BonusPay = row.Bonus,
                CashAdvanceDeduction = row.CashAdvance, OtherDeduction = row.OtherDeduction,
                GrossPay = row.Gross, FederalWithholding = row.Federal, SocialSecurityWithholding = row.SocialSecurity, MedicareWithholding = row.Medicare, StateWithholding = row.State, NetPay = row.Net,
                GrossPayYtd = ytd.Gross + row.Gross, FederalWithholdingYtd = ytd.Federal + row.Federal, SocialSecurityWithholdingYtd = ytd.SocialSecurity + row.SocialSecurity, MedicareWithholdingYtd = ytd.Medicare + row.Medicare, StateWithholdingYtd = ytd.State + row.State,
                CheckNumber = row.CheckNumber, OverrideReason = row.OverrideReason
            });
        }
        db.PayrollAuditEntries.Add(new PayrollAuditEntry { StoreId = _storeId, PayrollRunId = run.Id, Action = _runId.HasValue ? "Draft Updated" : "Draft Created", Details = $"{_rows.Count} employee payroll entries", PerformedByName = _user });
        await db.SaveChangesAsync(); await tx.CommitAsync(); _runId = run.Id;
        MessageBox.Show(this, $"Payroll draft #{run.Id} was saved.", "Payroll", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task FinalizeAsync()
    {
        await SaveDraftAsync();
        if (!_runId.HasValue) return;
        if (MessageBox.Show(this, "Finalize this payroll? Finalized hours and calculations are locked. Corrections must be made by voiding and creating a replacement payroll.", "Finalize Payroll", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        await using var db = _createDb(); await using var tx = await db.Database.BeginTransactionAsync();
        var run = await db.PayrollRuns.FirstAsync(x => x.Id == _runId && x.StoreId == _storeId);
        if (run.Status != PayrollRunStatus.Draft) { MessageBox.Show(this, "This payroll is no longer a draft."); return; }
        run.Status = PayrollRunStatus.Finalized; run.ApprovedByName = _user; run.ApprovedUtc = DateTime.UtcNow; run.FinalizedByName = _user; run.FinalizedUtc = DateTime.UtcNow;
        var shifts = await db.ScheduleShifts.Where(x => x.StoreId == _storeId && x.ShiftDate >= run.PeriodStart && x.ShiftDate <= run.PeriodEnd && x.Status == ScheduleShiftStatus.Published).ToListAsync();
        foreach (var shift in shifts) { shift.Status = ScheduleShiftStatus.Completed; shift.UpdatedByName = _user; shift.UpdatedUtc = DateTime.UtcNow; }
        db.PayrollAuditEntries.Add(new PayrollAuditEntry { StoreId = _storeId, PayrollRunId = run.Id, Action = "Approved and Finalized", Details = "Payroll locked; associated published shifts marked completed.", PerformedByName = _user });
        await db.SaveChangesAsync(); await tx.CommitAsync();
        MessageBox.Show(this, $"Payroll #{run.Id} was finalized. The check-and-stub packet will now open.", "Payroll Finalized", MessageBoxButtons.OK, MessageBoxIcon.Information);
        await ShowPacketAsync(run.Id);
    }

    private async Task PreviewAsync()
    {
        await SaveDraftAsync(); if (_runId.HasValue) await ShowPacketAsync(_runId.Value);
    }

    private async Task ShowPacketAsync(int runId)
    {
        using var viewer = new ReportViewerForm($"Payroll Check & Stub Packet - Run #{runId}", path => GeneratePacketAsync(runId, path));
        viewer.ShowDialog(this); await Task.CompletedTask;
    }

    private async Task GeneratePacketAsync(int runId, string path)
    {
        await using var db = _createDb();
        var run = await db.PayrollRuns.AsNoTracking().Include(x => x.Entries).FirstAsync(x => x.Id == runId && x.StoreId == _storeId);
        var ids = run.Entries.Select(x => x.EmployeeId).ToList(); var employees = await db.Employees.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(x => x.Id == _storeId) ?? await db.Stores.AsNoTracking().FirstOrDefaultAsync();
        PayrollPacketPdf.Generate(store?.Name ?? "Business", store?.Address ?? "", run, run.Entries, employees, path);
    }

    private bool ValidateRows()
    {
        _grid.EndEdit();
        if (_rows.Count == 0) { MessageBox.Show(this, "Load employees first."); return false; }
        foreach (var row in _rows)
        {
            if (row.Regular < 0 || row.Overtime < 0 || row.Holiday < 0) { MessageBox.Show(this, $"Hours cannot be negative for {row.Employee}."); return false; }
            if (Math.Abs(row.Regular + row.Overtime - row.Scheduled) > .01m && string.IsNullOrWhiteSpace(row.OverrideReason)) row.OverrideReason = row.Scheduled == 0 ? $"Manual hours entered by {_user}" : $"Scheduled hours adjusted by {_user}";
        }
        return true;
    }

    private async Task<Dictionary<int, Ytd>> PriorYtdAsync(AppDbContext db, IReadOnlyCollection<int> employeeIds, DateOnly payDate, int excludingRunId)
    {
        var rows = await db.PayrollEntries.AsNoTracking().Where(x => employeeIds.Contains(x.EmployeeId) && x.PayrollRunId != excludingRunId && x.PayrollRun!.StoreId == _storeId && x.PayrollRun.Status == PayrollRunStatus.Finalized && x.PayrollRun.TaxYear == payDate.Year && x.PayrollRun.PayDate <= payDate)
            .GroupBy(x => x.EmployeeId).Select(g => new { Id = g.Key, Gross = g.Sum(x => x.GrossPay), Federal = g.Sum(x => x.FederalWithholding), Social = g.Sum(x => x.SocialSecurityWithholding), Medicare = g.Sum(x => x.MedicareWithholding), State = g.Sum(x => x.StateWithholding) }).ToListAsync();
        return rows.ToDictionary(x => x.Id, x => new Ytd(x.Gross, x.Federal, x.Social, x.Medicare, x.State));
    }

    private void UpdateTotals() => _totals.Text = $"Employees: {_rows.Count}    Gross: {_rows.Sum(x => x.Gross):C2}    Taxes: {_rows.Sum(x => x.Federal + x.SocialSecurity + x.Medicare + x.State):C2}    Net: {_rows.Sum(x => x.Net):C2}";
    private sealed record Ytd(decimal Gross, decimal Federal, decimal SocialSecurity, decimal Medicare, decimal State);
}

internal sealed class PayrollHistoryForm : Form
{
    private readonly Func<AppDbContext> _createDb; private readonly int _storeId; private readonly string _user; private readonly DataGridView _grid = WinTheme.Grid();
    public PayrollHistoryForm(Func<AppDbContext> createDb, int storeId, string user)
    {
        _createDb = createDb; _storeId = storeId; _user = user; PayrollUi.Prepare(this, "Payroll History - HISAB KITAB", new Size(1250, 780));
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, BackColor = WinTheme.Bg, Padding = new Padding(14) }; root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        root.Controls.Add(PayrollUi.Heading("PAYROLL HISTORY  •  FINALIZED RUNS ARE IMMUTABLE"), 0, 0); root.Controls.Add(_grid, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel, FlowDirection = FlowDirection.RightToLeft };
        var preview = PayrollUi.Button("PREVIEW CHECKS & STUBS", true, 240); preview.Click += async (_, _) => await PreviewAsync();
        var voidRun = PayrollUi.Button("VOID SELECTED RUN", false, 200); voidRun.Click += async (_, _) => await VoidAsync();
        var close = PayrollUi.Button("CLOSE"); close.Click += (_, _) => Close(); actions.Controls.AddRange(new Control[] { preview, voidRun, close }); root.Controls.Add(actions, 0, 2); Controls.Add(root); Shown += async (_, _) => await RefreshAsync();
    }
    private async Task RefreshAsync() { await using var db = _createDb(); var runs = await db.PayrollRuns.AsNoTracking().Where(x => x.StoreId == _storeId).OrderByDescending(x => x.PayDate).ToListAsync(); _grid.DataSource = runs.Select(x => new { x.Id, Period = $"{x.PeriodStart:MM/dd/yyyy} - {x.PeriodEnd:MM/dd/yyyy}", PayDate = x.PayDate.ToString("MM/dd/yyyy"), Frequency = x.PayFrequency.ToString(), Status = x.Status.ToString(), CreatedBy = x.CreatedByName, ApprovedBy = x.ApprovedByName }).ToList(); }
    private int SelectedId() => _grid.CurrentRow?.DataBoundItem is { } item ? (int)(item.GetType().GetProperty("Id")?.GetValue(item) ?? 0) : 0;
    private async Task PreviewAsync() { var id = SelectedId(); if (id == 0) return; using var viewer = new ReportViewerForm($"Payroll Check & Stub Packet - Run #{id}", path => GeneratePacketAsync(id, path)); viewer.ShowDialog(this); await Task.CompletedTask; }
    private async Task GeneratePacketAsync(int id, string path) { await using var db = _createDb(); var run = await db.PayrollRuns.AsNoTracking().Include(x => x.Entries).FirstAsync(x => x.Id == id && x.StoreId == _storeId); var ids = run.Entries.Select(x => x.EmployeeId).ToList(); var employees = await db.Employees.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id); var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(x => x.Id == _storeId) ?? await db.Stores.AsNoTracking().FirstOrDefaultAsync(); PayrollPacketPdf.Generate(store?.Name ?? "Business", store?.Address ?? "", run, run.Entries, employees, path); }
    private async Task VoidAsync() { var id = SelectedId(); if (id == 0) return; if (MessageBox.Show(this, "Void this payroll run? The original record and audit history will remain.", "Void Payroll", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return; await using var db = _createDb(); var run = await db.PayrollRuns.FirstOrDefaultAsync(x => x.Id == id && x.StoreId == _storeId); if (run is null || run.Status == PayrollRunStatus.Voided) return; run.Status = PayrollRunStatus.Voided; db.PayrollAuditEntries.Add(new PayrollAuditEntry { StoreId = _storeId, PayrollRunId = id, Action = "Voided", Details = "Payroll run voided; records retained.", PerformedByName = _user }); await db.SaveChangesAsync(); await RefreshAsync(); }
}
