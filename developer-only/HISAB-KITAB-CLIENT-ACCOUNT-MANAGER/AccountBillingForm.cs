using System.ComponentModel;

namespace HisabKitabWorks.ClientAccountManager.WinForms;

internal sealed class AccountBillingForm : Form
{
    private readonly ClientAccountService _service;
    private readonly int _initialCustomerId;
    private readonly BindingList<ClientAccount> _accounts = new();
    private readonly BindingList<AccountInvoice> _invoices = new();
    private readonly BindingList<AccountPayment> _payments = new();
    private readonly DataGridView _accountGrid = Grid();
    private readonly DataGridView _invoiceGrid = Grid();
    private readonly DataGridView _paymentGrid = Grid();
    private readonly Label _selected = DeveloperTheme.Label("Select an account.", true, DeveloperTheme.Muted);
    private readonly Label _monthly = DeveloperTheme.Label("$0.00 monthly", true, DeveloperTheme.Green);
    private readonly Label _status = DeveloperTheme.Label("Ready.", false, DeveloperTheme.Muted);
    private readonly DateTimePicker _billingMonth = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "MMMM yyyy", ShowUpDown = true, Dock = DockStyle.Fill, Font = DeveloperTheme.Body(10.5f) };
    private readonly DateTimePicker _invoiceDate = new() { Format = DateTimePickerFormat.Short, Dock = DockStyle.Fill, Font = DeveloperTheme.Body(10.5f) };
    private readonly DateTimePicker _dueDate = new() { Format = DateTimePickerFormat.Short, Dock = DockStyle.Fill, Font = DeveloperTheme.Body(10.5f) };
    private readonly DateTimePicker _paymentDate = new() { Format = DateTimePickerFormat.Short, Dock = DockStyle.Fill, Font = DeveloperTheme.Body(10.5f) };
    private readonly NumericUpDown _paymentAmount = new() { DecimalPlaces = 2, Maximum = 1_000_000, ThousandsSeparator = true, Dock = DockStyle.Fill, Font = DeveloperTheme.Body(10.5f) };
    private readonly ComboBox _paymentMethod = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Font = DeveloperTheme.Body(10.5f) };
    private readonly TextBox _paymentReference = DeveloperTheme.TextBox();
    private readonly NumericUpDown _accountingRate = MonthlyRate();
    private readonly NumericUpDown _payrollRate = MonthlyRate();
    private readonly NumericUpDown _schedulingRate = MonthlyRate();
    private readonly NumericUpDown _monthlyReportsRate = MonthlyRate();
    private ClientAccount? _account;
    private AccountInvoice? _invoice;

    public AccountBillingForm(ClientAccountService service, int initialCustomerId)
    {
        _service = service;
        _initialCustomerId = initialCustomerId;
        Text = "HISAB KITAB WORKS - Account Payments & Invoices";
        Icon = DeveloperTheme.Icon();
        BackColor = DeveloperTheme.Bg;
        Font = DeveloperTheme.Body();
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1480, 920);
        MinimumSize = new Size(1220, 780);
        AutoScaleMode = AutoScaleMode.Dpi;
        _dueDate.Value = DateTime.Today.AddDays(8);
        _paymentMethod.DataSource = new[] { "Check", "Zelle", "Cash", "ACH / Bank Transfer", "Credit Card", "Other" };
        _accountingRate.ValueChanged += (_, _) => UpdateMonthlyLabel();
        _payrollRate.ValueChanged += (_, _) => UpdateMonthlyLabel();
        _schedulingRate.ValueChanged += (_, _) => UpdateMonthlyLabel();
        _monthlyReportsRate.ValueChanged += (_, _) => UpdateMonthlyLabel();
        ConfigureGrids();
        Controls.Add(BuildLayout());
        Shown += (_, _) => LoadAccounts();
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Padding = new Padding(18), BackColor = DeveloperTheme.Bg };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.Controls.Add(BuildHeader(), 0, 0);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(0, 12, 0, 0) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 430));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.Controls.Add(BuildAccountsPanel(), 0, 0);
        body.Controls.Add(BuildInvoicePanel(), 1, 0);
        root.Controls.Add(body, 0, 1);
        _status.BackColor = Color.White;
        _status.Padding = new Padding(12, 0, 0, 0);
        root.Controls.Add(_status, 0, 2);
        return root;
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 8, 20, 8) };
        header.Paint += (_, e) => DeveloperTheme.Gradient(e, header.ClientRectangle);
        var logo = new PictureBox { Image = DeveloperTheme.Logo(), SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Left, Width = 105, BackColor = Color.Transparent };
        var labels = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(16, 3, 0, 3), BackColor = Color.Transparent };
        labels.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        labels.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        labels.Controls.Add(new Label { Text = "ACCOUNT PAYMENTS & INVOICES", Dock = DockStyle.Fill, ForeColor = Color.White, Font = DeveloperTheme.Bold(20), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        labels.Controls.Add(new Label { Text = "MONTHLY SERVICE PRICING  •  INVOICES  •  PAYMENT HISTORY", Dock = DockStyle.Fill, ForeColor = DeveloperTheme.Orange, Font = DeveloperTheme.Bold(10.5f), TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        header.Controls.Add(labels);
        header.Controls.Add(logo);
        return header;
    }

    private Control BuildAccountsPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(14), RowCount = 6 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 250));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        var heading = DeveloperTheme.Label("CLIENT ACCOUNTS", true, DeveloperTheme.Orange);
        heading.Font = DeveloperTheme.Bold(14);
        panel.Controls.Add(heading, 0, 0);
        panel.Controls.Add(_accountGrid, 0, 1);
        _selected.BackColor = DeveloperTheme.PaleBlue;
        _selected.Padding = new Padding(8, 0, 8, 0);
        _selected.AutoEllipsis = true;
        panel.Controls.Add(_selected, 0, 2);
        panel.Controls.Add(BuildServicePriceEditor(), 0, 3);
        var save = DeveloperTheme.Button("SAVE MONTHLY SERVICE PRICES", true);
        save.Click += (_, _) => SavePrices();
        panel.Controls.Add(save, 0, 4);
        _monthly.TextAlign = ContentAlignment.MiddleCenter;
        panel.Controls.Add(_monthly, 0, 5);
        return panel;
    }

    private Control BuildServicePriceEditor()
    {
        var card = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 7, ColumnCount = 2, Padding = new Padding(8), BackColor = DeveloperTheme.PaleBlue };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var instruction = DeveloperTheme.Label("ENTER MONTHLY PRICE FOR EACH ACTIVE SERVICE", true, DeveloperTheme.OrangeDark);
        instruction.Font = DeveloperTheme.Bold(9);
        card.Controls.Add(instruction, 0, 0);
        card.SetColumnSpan(instruction, 2);
        AddRateRow(card, 1, "CORE ACCOUNTING", _accountingRate);
        AddRateRow(card, 2, "PAYROLL ADD-ON", _payrollRate);
        AddRateRow(card, 3, "SCHEDULING ADD-ON", _schedulingRate);
        AddRateRow(card, 4, "AUTOMATIC MONTHLY REPORTS", _monthlyReportsRate);
        var flat = DeveloperTheme.Label(
            $"STANDARD: Accounting {StandardServicePricing.Accounting:C2} • Payroll {StandardServicePricing.Payroll:C2} • Scheduling {StandardServicePricing.Scheduling:C2} • Reports {StandardServicePricing.MonthlyReports:C2}",
            true,
            DeveloperTheme.Green);
        flat.Font = DeveloperTheme.Body(7.7f);
        card.Controls.Add(flat, 0, 5);
        card.SetColumnSpan(flat, 2);
        var help = DeveloperTheme.Label(
            $"Flat monthly pricing — no active-employee fee. One-time software license remains {StandardServicePricing.OneTimeLicenseFee:C0}.",
            false,
            DeveloperTheme.Muted);
        help.Font = DeveloperTheme.Body(8);
        card.Controls.Add(help, 0, 6);
        card.SetColumnSpan(help, 2);
        return card;
    }

    private static void AddRateRow(TableLayoutPanel card, int row, string caption, NumericUpDown input)
    {
        var label = DeveloperTheme.Label(caption, true, DeveloperTheme.Blue);
        label.Padding = new Padding(5, 0, 0, 0);
        input.Margin = new Padding(4, 5, 4, 5);
        card.Controls.Add(label, 0, row);
        card.Controls.Add(input, 1, row);
    }

    private Control BuildInvoicePanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(14), RowCount = 8 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 46));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 54));
        var heading = DeveloperTheme.Label("INVOICE & PAYMENT MANAGEMENT", true, DeveloperTheme.Orange);
        heading.Font = DeveloperTheme.Bold(14);
        panel.Controls.Add(heading, 0, 0);

        var invoiceFields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        invoiceFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        invoiceFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        invoiceFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        AddField(invoiceFields, 0, "BILLING MONTH", _billingMonth);
        AddField(invoiceFields, 1, "INVOICE DATE", _invoiceDate);
        AddField(invoiceFields, 2, "DUE DATE", _dueDate);
        panel.Controls.Add(invoiceFields, 0, 1);

        var invoiceActions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        invoiceActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        invoiceActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));
        invoiceActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));
        var create = DeveloperTheme.Button("GENERATE MONTHLY INVOICE", true);
        create.Click += (_, _) => CreateInvoice();
        var export = DeveloperTheme.Button("EXPORT SELECTED PDF");
        export.Click += (_, _) => ExportInvoice();
        var refresh = DeveloperTheme.Button("REFRESH INVOICES");
        refresh.Click += (_, _) => RefreshInvoices();
        invoiceActions.Controls.Add(create, 0, 0);
        invoiceActions.Controls.Add(export, 1, 0);
        invoiceActions.Controls.Add(refresh, 2, 0);
        panel.Controls.Add(invoiceActions, 0, 2);
        panel.Controls.Add(_invoiceGrid, 0, 3);

        var paymentFields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4 };
        paymentFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        paymentFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        paymentFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        paymentFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));
        AddField(paymentFields, 0, "PAYMENT DATE", _paymentDate);
        AddField(paymentFields, 1, "AMOUNT", _paymentAmount);
        AddField(paymentFields, 2, "METHOD", _paymentMethod);
        AddField(paymentFields, 3, "REFERENCE / CHECK #", _paymentReference);
        panel.Controls.Add(paymentFields, 0, 4);

        var paymentActions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        paymentActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        paymentActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        var record = DeveloperTheme.Button("RECORD PAYMENT", true);
        record.Click += (_, _) => RecordPayment();
        var selectedInvoice = DeveloperTheme.Label("Select an invoice before recording payment.", false, DeveloperTheme.Muted);
        selectedInvoice.Name = "SelectedInvoiceHelp";
        selectedInvoice.Padding = new Padding(10, 0, 0, 0);
        paymentActions.Controls.Add(record, 0, 0);
        paymentActions.Controls.Add(selectedInvoice, 1, 0);
        panel.Controls.Add(paymentActions, 0, 5);
        var paymentsHeading = DeveloperTheme.Label("PAYMENT HISTORY FOR SELECTED INVOICE", true, DeveloperTheme.Blue);
        panel.Controls.Add(paymentsHeading, 0, 6);
        panel.Controls.Add(_paymentGrid, 0, 7);
        return panel;
    }

    private static void AddField(TableLayoutPanel host, int column, string caption, Control input)
    {
        var cell = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(4, 0, 4, 4) };
        cell.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        cell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        cell.Controls.Add(DeveloperTheme.Label(caption, true), 0, 0);
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 2, 0, 2);
        cell.Controls.Add(input, 0, 1);
        host.Controls.Add(cell, column, 0);
    }

    private void ConfigureGrids()
    {
        _accountGrid.AutoGenerateColumns = false;
        _accountGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ClientAccount.BusinessName), HeaderText = "CLIENT", FillWeight = 42 });
        _accountGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ClientAccount.EnabledServices), HeaderText = "SERVICES", FillWeight = 38 });
        _accountGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ClientAccount.MonthlyFee), HeaderText = "MONTHLY", FillWeight = 20, DefaultCellStyle = new DataGridViewCellStyle { Format = "C2" } });
        _accountGrid.SelectionChanged += (_, _) => SelectAccount();

        _invoiceGrid.AutoGenerateColumns = false;
        _invoiceGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(AccountInvoice.InvoiceNumber), HeaderText = "INVOICE #", FillWeight = 27 });
        _invoiceGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(AccountInvoice.InvoiceDate), HeaderText = "DATE", FillWeight = 14, DefaultCellStyle = new DataGridViewCellStyle { Format = "MM/dd/yyyy" } });
        _invoiceGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(AccountInvoice.DueDate), HeaderText = "DUE", FillWeight = 14, DefaultCellStyle = new DataGridViewCellStyle { Format = "MM/dd/yyyy" } });
        _invoiceGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(AccountInvoice.Subtotal), HeaderText = "TOTAL", FillWeight = 14, DefaultCellStyle = new DataGridViewCellStyle { Format = "C2" } });
        _invoiceGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(AccountInvoice.AmountPaid), HeaderText = "PAID", FillWeight = 14, DefaultCellStyle = new DataGridViewCellStyle { Format = "C2" } });
        _invoiceGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(AccountInvoice.BalanceDue), HeaderText = "BALANCE", FillWeight = 15, DefaultCellStyle = new DataGridViewCellStyle { Format = "C2" } });
        _invoiceGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(AccountInvoice.Status), HeaderText = "STATUS", FillWeight = 16 });
        _invoiceGrid.SelectionChanged += (_, _) => SelectInvoice();

        _paymentGrid.AutoGenerateColumns = false;
        _paymentGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(AccountPayment.PaymentDate), HeaderText = "DATE", FillWeight = 20, DefaultCellStyle = new DataGridViewCellStyle { Format = "MM/dd/yyyy" } });
        _paymentGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(AccountPayment.Amount), HeaderText = "AMOUNT", FillWeight = 20, DefaultCellStyle = new DataGridViewCellStyle { Format = "C2" } });
        _paymentGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(AccountPayment.Method), HeaderText = "METHOD", FillWeight = 25 });
        _paymentGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(AccountPayment.ReferenceNumber), HeaderText = "REFERENCE", FillWeight = 35 });
    }

    private static DataGridView Grid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            RowHeadersVisible = false
        };
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = DeveloperTheme.Blue;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        grid.ColumnHeadersDefaultCellStyle.Font = DeveloperTheme.Bold(9);
        grid.DefaultCellStyle.Font = DeveloperTheme.Body(9);
        grid.RowTemplate.Height = 30;
        return grid;
    }

    private void LoadAccounts(int selectCustomerId = 0)
    {
        try
        {
            if (selectCustomerId <= 0)
                selectCustomerId = _account?.CustomerId ?? _initialCustomerId;
            _accounts.RaiseListChangedEvents = false;
            _accounts.Clear();
            foreach (var account in _service.LoadAccounts()) _accounts.Add(account);
            _accounts.RaiseListChangedEvents = true;
            _accounts.ResetBindings();
            _accountGrid.DataSource = _accounts;
            if (selectCustomerId > 0)
            {
                foreach (DataGridViewRow row in _accountGrid.Rows)
                    if (row.DataBoundItem is ClientAccount account && account.CustomerId == selectCustomerId)
                    {
                        row.Selected = true;
                        _accountGrid.CurrentCell = row.Cells[0];
                        break;
                    }
            }
            SelectAccount();
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private void SelectAccount()
    {
        if (_accountGrid.CurrentRow?.DataBoundItem is not ClientAccount account) return;
        _account = account;
        _selected.Text = $"{account.BusinessName}  •  {account.EnabledServices}";
        LoadPrices();
        RefreshInvoices();
    }

    private void LoadPrices()
    {
        if (_account is null) return;
        var prices = _service.LoadServicePrices(_account).ToDictionary(x => x.ServiceName, StringComparer.OrdinalIgnoreCase);
        LoadRate(_accountingRate, prices.GetValueOrDefault("Accounting"));
        LoadRate(_payrollRate, prices.GetValueOrDefault("Payroll"));
        LoadRate(_schedulingRate, prices.GetValueOrDefault("Scheduling"));
        LoadRate(_monthlyReportsRate, prices.GetValueOrDefault("MonthlyReports"));
        UpdateMonthlyLabel();
    }

    private bool SavePrices()
    {
        if (_account is null) { SetStatus("Select an account first.", true); return false; }
        try
        {
            var prices = new[]
            {
                new ServicePrice("Accounting", _accountingRate.Enabled, _accountingRate.Value),
                new ServicePrice("Payroll", _payrollRate.Enabled, _payrollRate.Value),
                new ServicePrice("Scheduling", _schedulingRate.Enabled, _schedulingRate.Value),
                new ServicePrice("MonthlyReports", _monthlyReportsRate.Enabled, _monthlyReportsRate.Value)
            };
            _service.SaveServicePrices(_account, prices);
            var id = _account.CustomerId;
            var refreshed = _service.LoadAccounts().First(x => x.CustomerId == id);
            _account = refreshed;
            UpdateMonthlyLabel();
            SetStatus($"Monthly service pricing saved for {refreshed.BusinessName}.", false);
            LoadAccounts(id);
            return true;
        }
        catch (Exception ex) { SetStatus(ex.Message, true); return false; }
    }

    private void UpdateMonthlyLabel() =>
        _monthly.Text = $"{EnabledRate(_accountingRate) + EnabledRate(_payrollRate) + EnabledRate(_schedulingRate) + EnabledRate(_monthlyReportsRate):C2} TOTAL MONTHLY CHARGE";

    private void CreateInvoice()
    {
        if (_account is null) { SetStatus("Select an account first.", true); return; }
        try
        {
            if (!SavePrices()) return;
            if (_account is null) return;
            var first = new DateTime(_billingMonth.Value.Year, _billingMonth.Value.Month, 1);
            var last = first.AddMonths(1).AddDays(-1);
            var created = _service.CreateInvoice(_account, _invoiceDate.Value.Date, _dueDate.Value.Date, first, last);
            RefreshInvoices(created.Id);
            SetStatus($"Invoice {created.InvoiceNumber} created. Select EXPORT SELECTED PDF to save it.", false);
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private void RefreshInvoices(int selectInvoiceId = 0)
    {
        if (_account is null) return;
        try
        {
            _invoices.RaiseListChangedEvents = false;
            _invoices.Clear();
            foreach (var invoice in _service.LoadInvoices(_account.CustomerId)) _invoices.Add(invoice);
            _invoices.RaiseListChangedEvents = true;
            _invoices.ResetBindings();
            _invoiceGrid.DataSource = _invoices;
            if (selectInvoiceId > 0)
            {
                foreach (DataGridViewRow row in _invoiceGrid.Rows)
                    if (row.DataBoundItem is AccountInvoice invoice && invoice.Id == selectInvoiceId)
                    {
                        row.Selected = true;
                        _invoiceGrid.CurrentCell = row.Cells[0];
                        break;
                    }
            }
            SelectInvoice();
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private void SelectInvoice()
    {
        _invoice = _invoiceGrid.CurrentRow?.DataBoundItem as AccountInvoice;
        _payments.Clear();
        if (_invoice is null) return;
        foreach (var payment in _service.LoadPayments(_invoice.Id)) _payments.Add(payment);
        _paymentGrid.DataSource = _payments;
        _paymentAmount.Value = Math.Min(_paymentAmount.Maximum, _invoice.BalanceDue);
        var help = Controls.Find("SelectedInvoiceHelp", true).FirstOrDefault() as Label;
        if (help is not null)
            help.Text = $"{_invoice.InvoiceNumber}  •  Balance due: {_invoice.BalanceDue:C2}  •  {_invoice.Status}";
    }

    private void RecordPayment()
    {
        if (_invoice is null) { SetStatus("Select an invoice first.", true); return; }
        try
        {
            _service.RecordPayment(_invoice, _paymentDate.Value.Date, _paymentAmount.Value, _paymentMethod.Text, _paymentReference.Text, "");
            var id = _invoice.Id;
            _paymentReference.Clear();
            RefreshInvoices(id);
            SetStatus($"Payment recorded for {_invoice?.InvoiceNumber ?? "the selected invoice"}.", false);
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private void ExportInvoice()
    {
        if (_account is null || _invoice is null) { SetStatus("Select an invoice first.", true); return; }
        try
        {
            using var save = new SaveFileDialog
            {
                Title = "Export HISAB KITAB WORKS Invoice",
                Filter = "PDF files (*.pdf)|*.pdf",
                FileName = $"{_invoice.InvoiceNumber}.pdf",
                AddExtension = true,
                DefaultExt = "pdf"
            };
            if (save.ShowDialog(this) != DialogResult.OK) return;
            var data = _service.LoadInvoiceDocument(_account, _invoice.Id);
            var logo = Path.Combine(AppContext.BaseDirectory, "Assets", "HisabKitab_Logo.png");
            AccountInvoicePdf.Generate(data, logo, save.FileName);
            SetStatus($"Invoice exported to {save.FileName}", false);
            MessageBox.Show(this, $"Invoice exported successfully.\n\n{save.FileName}", "Invoice Exported", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private void SetStatus(string text, bool error)
    {
        _status.Text = text;
        _status.ForeColor = error ? DeveloperTheme.Red : DeveloperTheme.Green;
    }

    private static NumericUpDown MonthlyRate() => new()
    {
        DecimalPlaces = 2,
        Minimum = 0,
        Maximum = 100_000,
        ThousandsSeparator = true,
        Dock = DockStyle.Fill,
        Font = DeveloperTheme.Bold(10.5f),
        BackColor = Color.White,
        ForeColor = DeveloperTheme.Text
    };

    private static void LoadRate(NumericUpDown input, ServicePrice? price)
    {
        input.Enabled = price?.Enabled == true;
        input.Value = Math.Min(input.Maximum, Math.Max(input.Minimum, price?.MonthlyRate ?? 0m));
        input.BackColor = input.Enabled ? Color.White : Color.FromArgb(232, 235, 239);
    }

    private static decimal EnabledRate(NumericUpDown input) => input.Enabled ? input.Value : 0m;
}
