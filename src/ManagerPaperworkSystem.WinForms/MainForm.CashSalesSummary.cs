using System.Diagnostics;
using System.Globalization;
using ManagerPaperworkSystem.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ManagerPaperworkSystem.WinForms;

internal sealed partial class MainForm
{
    private Control BuildCashSalesSummary()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 5,
            ColumnCount = 1,
            BackColor = WinTheme.Bg,
            Padding = new Padding(2)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 126));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        var filterCard = WinTheme.BorderedPanel(10);
        filterCard.Dock = DockStyle.Fill;
        filterCard.Margin = new Padding(4, 0, 4, 6);
        root.Controls.Add(filterCard, 0, 0);

        var filters = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 8,
            RowCount = 1,
            BackColor = WinTheme.Panel
        };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        filterCard.Controls.Add(filters);

        var from = WinTheme.DatePicker();
        from.Value = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var to = WinTheme.DatePicker();
        to.Value = DateTime.Today;
        filters.Controls.Add(FilterLabel("FROM"), 0, 0);
        filters.Controls.Add(from, 1, 0);
        filters.Controls.Add(FilterLabel("TO"), 2, 0);
        filters.Controls.Add(to, 3, 0);

        var import = WinTheme.Button("IMPORT POS PDF", true);
        import.Dock = DockStyle.Fill;
        import.Margin = new Padding(6);
        filters.Controls.Add(import, 5, 0);

        var autoSync = WinTheme.Button("POS AUTO SYNC");
        autoSync.Dock = DockStyle.Fill;
        autoSync.Margin = new Padding(6);
        autoSync.Enabled = _session.IsAdmin;
        filters.Controls.Add(autoSync, 6, 0);

        var refresh = WinTheme.Button("REFRESH");
        refresh.Dock = DockStyle.Fill;
        refresh.Margin = new Padding(6);
        filters.Controls.Add(refresh, 7, 0);

        var cards = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 1,
            BackColor = WinTheme.Bg,
            Margin = Padding.Empty
        };
        for (var index = 0; index < 6; index++)
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 6f));
        root.Controls.Add(cards, 0, 1);

        var grossValue = AddPosSummaryCard(cards, 0, "GROSS SALES", WinTheme.Copper);
        var netValue = AddPosSummaryCard(cards, 1, "NET SALES", WinTheme.Green);
        var taxValue = AddPosSummaryCard(cards, 2, "SALES TAX", WinTheme.Copper);
        var cashValue = AddPosSummaryCard(cards, 3, "CASH SALES", WinTheme.Blue);
        var cardValue = AddPosSummaryCard(cards, 4, "CARD SALES", WinTheme.Blue);
        var profitValue = AddPosSummaryCard(cards, 5, "DEPARTMENT PROFIT", WinTheme.Green);

        var reconcileCard = WinTheme.BorderedPanel(8);
        reconcileCard.Dock = DockStyle.Fill;
        reconcileCard.Margin = new Padding(4, 2, 4, 6);
        root.Controls.Add(reconcileCard, 0, 2);

        var reconcile = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 7,
            RowCount = 2,
            BackColor = WinTheme.Panel,
            Padding = new Padding(4)
        };
        reconcile.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        reconcile.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        reconcile.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15));
        reconcile.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15));
        reconcile.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15));
        reconcile.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        reconcile.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15));
        reconcile.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 8));
        reconcile.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 8));
        reconcileCard.Controls.Add(reconcile);

        var expectedCash = SectionTextBox("$0.00", readOnly: true, rightAlign: true);
        var cashDrop = SectionTextBox("0.00", rightAlign: true);
        var registerPayout = SectionTextBox("0.00", rightAlign: true);
        var payoutReason = SectionTextBox();
        var variance = SectionTextBox("$0.00", readOnly: true, rightAlign: true);
        var saveReconciliation = WinTheme.Button("SAVE", true);
        var resetReconciliation = WinTheme.Button("RESET");
        AddReconciliationField(reconcile, "EXPECTED CASH", expectedCash, 0);
        AddReconciliationField(reconcile, "CASH DROP", cashDrop, 1);
        AddReconciliationField(reconcile, "REGISTER PAYOUT", registerPayout, 2);
        AddReconciliationField(reconcile, "PAYOUT REASON", payoutReason, 3);
        AddReconciliationField(reconcile, "OVER / SHORT", variance, 4);
        reconcile.Controls.Add(saveReconciliation, 5, 1);
        reconcile.Controls.Add(resetReconciliation, 6, 1);
        saveReconciliation.Dock = DockStyle.Fill;
        resetReconciliation.Dock = DockStyle.Fill;
        saveReconciliation.Margin = new Padding(4);
        resetReconciliation.Margin = new Padding(4);
        saveReconciliation.Enabled = false;
        resetReconciliation.Enabled = false;

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = WinTheme.BoldFont(9.5f),
            Padding = new Point(20, 8),
            Margin = new Padding(4, 2, 4, 2)
        };
        root.Controls.Add(tabs, 0, 3);

        var reportsGrid = WinTheme.Grid();
        var tenderGrid = WinTheme.Grid();
        var hourlyGrid = WinTheme.Grid();
        var departmentGrid = WinTheme.Grid();
        var statisticsGrid = WinTheme.Grid();
        tabs.TabPages.Add(BuildPosTab("IMPORTED REPORTS", reportsGrid));
        tabs.TabPages.Add(BuildPosTab("TENDER BREAKDOWN", tenderGrid));
        tabs.TabPages.Add(BuildPosTab("HOURLY SALES", hourlyGrid));
        tabs.TabPages.Add(BuildPosTab("DEPARTMENT SALES", departmentGrid));
        tabs.TabPages.Add(BuildPosTab("SYSTEM STATISTICS", statisticsGrid));

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = WinTheme.Bg
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        root.Controls.Add(footer, 0, 4);

        var status = new Label
        {
            Text = "Import the POS report, then enter the cash drop and any register payout to reconcile expected cash.",
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Muted,
            Font = WinTheme.BodyFont(9),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        footer.Controls.Add(status, 0, 0);

        var openPdf = WinTheme.Button("OPEN PDF");
        openPdf.Dock = DockStyle.Fill;
        openPdf.Margin = new Padding(5, 2, 5, 2);
        footer.Controls.Add(openPdf, 1, 0);

        var delete = WinTheme.Button("DELETE");
        delete.Dock = DockStyle.Fill;
        delete.Margin = new Padding(5, 2, 5, 2);
        delete.Enabled = _session.IsAdmin;
        footer.Controls.Add(delete, 2, 0);

        var shiftLog = WinTheme.Button("OPEN SHIFT LOG", true);
        shiftLog.Dock = DockStyle.Fill;
        shiftLog.Margin = new Padding(5, 2, 5, 2);
        shiftLog.Click += (_, _) => ShowModule("Shift Cash Drop");
        footer.Controls.Add(shiftLog, 3, 0);

        int? selectedSummaryId = null;

        void UpdateVariance(decimal expected)
        {
            var difference = Money(cashDrop.Text) + Money(registerPayout.Text) - expected;
            variance.Text = MoneyText(difference);
            variance.ForeColor = difference > 0m
                ? WinTheme.Green
                : difference < 0m
                    ? WinTheme.Red
                    : WinTheme.Text;
        }

        async Task LoadDetailAsync(int? summaryId)
        {
            selectedSummaryId = summaryId;
            if (summaryId is null)
            {
                tenderGrid.DataSource = null;
                hourlyGrid.DataSource = null;
                departmentGrid.DataSource = null;
                statisticsGrid.DataSource = null;
                SetPosSummaryCards(null);
                expectedCash.Text = "$0.00";
                cashDrop.Text = "0.00";
                registerPayout.Text = "0.00";
                payoutReason.Clear();
                UpdateVariance(0m);
                saveReconciliation.Enabled = false;
                resetReconciliation.Enabled = false;
                return;
            }

            await using var db = CreateDb();
            var summary = await db.PosSalesSummaries.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == summaryId.Value && item.StoreId == _currentStoreId);
            if (summary is null)
            {
                SetPosSummaryCards(null);
                saveReconciliation.Enabled = false;
                resetReconciliation.Enabled = false;
                return;
            }

            grossValue.Text = MoneyText(summary.GrossSales);
            netValue.Text = MoneyText(summary.NetSales);
            taxValue.Text = MoneyText(summary.Taxes);
            cashValue.Text = MoneyText(summary.CashSales);
            cardValue.Text = MoneyText(summary.CardSales);
            profitValue.Text = MoneyText(summary.DepartmentProfit);
            expectedCash.Text = MoneyText(summary.CashSales);
            cashDrop.Text = summary.CashDropReceived.ToString("0.00", CultureInfo.CurrentCulture);
            registerPayout.Text = summary.RegisterPayout.ToString("0.00", CultureInfo.CurrentCulture);
            payoutReason.Text = summary.PayoutReason;
            UpdateVariance(summary.CashSales);
            saveReconciliation.Enabled = true;
            resetReconciliation.Enabled = true;

            tenderGrid.DataSource = await db.PosSalesTenderLines.AsNoTracking()
                .Where(line => line.PosSalesSummaryId == summaryId.Value)
                .OrderByDescending(line => line.Amount)
                .Select(line => new
                {
                    Tender = line.TenderType,
                    Transactions = line.TransactionCount,
                    line.Amount
                })
                .ToListAsync();
            FormatCurrencyColumns(tenderGrid, "Amount");

            hourlyGrid.DataSource = await db.PosSalesHourlyLines.AsNoTracking()
                .Where(line => line.PosSalesSummaryId == summaryId.Value)
                .OrderBy(line => line.Id)
                .Select(line => new
                {
                    Period = line.TimePeriod,
                    Transactions = line.TransactionCount,
                    line.Amount
                })
                .ToListAsync();
            FormatCurrencyColumns(hourlyGrid, "Amount");

            departmentGrid.DataSource = await db.PosSalesDepartmentLines.AsNoTracking()
                .Where(line => line.PosSalesSummaryId == summaryId.Value)
                .OrderByDescending(line => line.Sales)
                .Select(line => new
                {
                    line.Department,
                    Qty = line.Quantity,
                    SalesAmount = line.Sales,
                    line.Cost,
                    line.Profit,
                    ProfitPercent = line.ProfitPercent,
                    SalesPercent = line.SalesPercent
                })
                .ToListAsync();
            FormatCurrencyColumns(departmentGrid, "SalesAmount", "Cost", "Profit");
            FormatPercentColumns(departmentGrid, "ProfitPercent", "SalesPercent");

            statisticsGrid.DataSource = new[]
            {
                new { Metric = "Tender transactions", Value = summary.TenderTransactionCount.ToString("N0", CultureInfo.CurrentCulture) },
                new { Metric = "Customer transactions", Value = summary.CustomerTransactionCount.ToString("N0", CultureInfo.CurrentCulture) },
                new { Metric = "Average sale", Value = MoneyText(summary.CustomerAverageSale) },
                new { Metric = "User logins", Value = summary.UserLoginCount.ToString("N0", CultureInfo.CurrentCulture) },
                new { Metric = "Delete / void count", Value = summary.DeleteVoidCount.ToString("N0", CultureInfo.CurrentCulture) },
                new { Metric = "No sale count", Value = summary.NoSaleCount.ToString("N0", CultureInfo.CurrentCulture) },
                new { Metric = "Void / delete amount", Value = MoneyText(summary.VoidDeleteAmount) },
                new { Metric = "Total discount", Value = MoneyText(summary.TotalDiscount) },
                new { Metric = "Taxable sales", Value = MoneyText(summary.TaxableSales) },
                new { Metric = "Non-taxable sales", Value = MoneyText(summary.NonTaxableSales) },
                new { Metric = "Department cost", Value = MoneyText(summary.DepartmentCost) },
                new { Metric = "Department profit margin", Value = PercentText(summary.DepartmentProfitPercent) }
            };

            status.Text = summary.IsReconciled
                ? $"{summary.SourceSystem} report reconciled by {summary.ReconciledByName} on {summary.ReconciledUtc?.ToLocalTime():M/d/yyyy h:mm tt}."
                : $"{summary.SourceSystem} report for {summary.ReportFrom:M/d/yyyy} - {summary.ReportTo:M/d/yyyy}. Enter the manager's cash drop and any register payout.";
        }

        void SetPosSummaryCards(PosSalesSummary? _)
        {
            grossValue.Text = "$0.00";
            netValue.Text = "$0.00";
            taxValue.Text = "$0.00";
            cashValue.Text = "$0.00";
            cardValue.Text = "$0.00";
            profitValue.Text = "$0.00";
        }

        async Task RefreshAsync()
        {
            if (from.Value.Date > to.Value.Date)
            {
                status.Text = "The From date must be earlier than or equal to the To date.";
                return;
            }

            var fromDate = DateOnly.FromDateTime(from.Value);
            var toDate = DateOnly.FromDateTime(to.Value);
            await using var db = CreateDb();
            var rows = await db.PosSalesSummaries.AsNoTracking()
                .Where(item =>
                    item.StoreId == _currentStoreId &&
                    item.ReportTo >= fromDate &&
                    item.ReportFrom <= toDate)
                .OrderByDescending(item => item.ReportTo)
                .ThenByDescending(item => item.ImportedUtc)
                .Select(item => new
                {
                    item.Id,
                    From = item.ReportFrom,
                    To = item.ReportTo,
                    Store = item.ReportedStoreName,
                    Gross = item.GrossSales,
                    Net = item.NetSales,
                    Tax = item.Taxes,
                    ExpectedCash = item.CashSales,
                    Drop = item.CashDropReceived,
                    RegisterPayout = item.RegisterPayout,
                    Variance = item.CashDropReceived + item.RegisterPayout - item.CashSales,
                    PayoutReason = item.PayoutReason,
                    Cards = item.CardSales,
                    Profit = item.DepartmentProfit,
                    Transactions = item.CustomerTransactionCount,
                    Status = item.IsReconciled ? "Reconciled" : "Needs Review",
                    Imported = item.ImportedUtc
                })
                .ToListAsync();
            reportsGrid.DataSource = rows;
            HideId(reportsGrid);
            FormatCurrencyColumns(reportsGrid, "Gross", "Net", "Tax", "ExpectedCash", "Drop", "RegisterPayout", "Variance", "Cards", "Profit");

            var keepId = rows.Any(row => row.Id == selectedSummaryId)
                ? selectedSummaryId
                : rows.FirstOrDefault()?.Id;
            if (keepId is null)
            {
                await LoadDetailAsync(null);
                status.Text = "No Cash and Sales Summary reports were found for this period.";
                return;
            }

            foreach (DataGridViewRow row in reportsGrid.Rows)
            {
                if (Convert.ToInt32(row.Cells["Id"].Value, CultureInfo.InvariantCulture) == keepId.Value)
                {
                    row.Selected = true;
                    reportsGrid.CurrentCell = row.Cells.Cast<DataGridViewCell>().First(cell => cell.Visible);
                    break;
                }
            }
            await LoadDetailAsync(keepId);
        }

        reportsGrid.CellFormatting += (_, eventArgs) =>
        {
            if (eventArgs.RowIndex < 0 ||
                reportsGrid.Columns[eventArgs.ColumnIndex].Name != "Variance" ||
                eventArgs.Value is not decimal amount)
                return;

            eventArgs.CellStyle ??= new DataGridViewCellStyle(reportsGrid.DefaultCellStyle);
            eventArgs.CellStyle.ForeColor = amount > 0m
                ? WinTheme.Green
                : amount < 0m
                    ? WinTheme.Red
                    : WinTheme.Text;
            eventArgs.CellStyle.Font = WinTheme.BoldFont(9);
        };

        reportsGrid.SelectionChanged += async (_, _) =>
        {
            if (reportsGrid.CurrentRow?.Cells["Id"].Value is not { } rawId ||
                !int.TryParse(rawId.ToString(), out var id) ||
                id == selectedSummaryId)
                return;
            await LoadDetailAsync(id);
        };

        cashDrop.TextChanged += (_, _) => UpdateVariance(Money(expectedCash.Text));
        registerPayout.TextChanged += (_, _) => UpdateVariance(Money(expectedCash.Text));
        resetReconciliation.Click += async (_, _) => await LoadDetailAsync(selectedSummaryId);
        saveReconciliation.Click += async (_, _) =>
        {
            if (selectedSummaryId is null)
            {
                MessageBox.Show(this, "Select an imported report first.", "Cash Reconciliation",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var dropAmount = Money(cashDrop.Text);
            var payoutAmount = Money(registerPayout.Text);
            if (dropAmount < 0m || payoutAmount < 0m)
            {
                MessageBox.Show(this, "Cash drop and register payout cannot be negative.", "Cash Reconciliation",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (payoutAmount > 0m && string.IsNullOrWhiteSpace(payoutReason.Text))
            {
                MessageBox.Show(this, "Enter the reason for the register payout.", "Cash Reconciliation",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                payoutReason.Focus();
                return;
            }

            await using var db = CreateDb();
            var summary = await db.PosSalesSummaries
                .FirstOrDefaultAsync(item => item.Id == selectedSummaryId.Value && item.StoreId == _currentStoreId);
            if (summary is null)
                return;

            if (!summary.IsReconciled)
            {
                var manualEntries = await db.ShiftLogs.AsNoTracking()
                    .CountAsync(item =>
                        item.StoreId == _currentStoreId &&
                        item.Date == summary.ReportTo &&
                        item.PosSalesSummaryId == null);
                if (manualEntries > 0 &&
                    MessageBox.Show(this,
                        $"There are already {manualEntries} manual Shift Cash Drop record(s) dated {summary.ReportTo:M/d/yyyy}.\n\n" +
                        "Saving this POS summary as an accounting entry may count the same sales twice. Continue only if this report is not already represented by those shift records.",
                        "Possible Duplicate Sales",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
            }

            summary.CashDropReceived = dropAmount;
            summary.RegisterPayout = payoutAmount;
            summary.PayoutReason = payoutReason.Text.Trim();
            summary.IsReconciled = true;
            summary.ReconciledByUserId = _session.UserId;
            summary.ReconciledByName = _session.DisplayName;
            summary.ReconciledUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();

            await SyncPosSalesSummaryToShiftLogAsync(summary.Id);
            await RefreshAsync();
            status.Text = $"Cash summary reconciled. Expected {summary.CashSales:C2}; accounted for " +
                          $"{(summary.CashDropReceived + summary.RegisterPayout):C2}; variance {summary.CashVariance:C2}.";
        };

        import.Click += async (_, _) =>
        {
            using var picker = new OpenFileDialog
            {
                Title = "Import AdventPOS Cash and Sales Summary",
                Filter = "PDF reports (*.pdf)|*.pdf",
                Multiselect = false
            };
            if (picker.ShowDialog(this) != DialogResult.OK)
                return;

            import.Enabled = false;
            try
            {
                status.Text = "Reading and validating the POS report...";
                var parsed = await CashSalesSummaryPdfImporter.ImportAsync(picker.FileName);
                if (parsed.ReportFrom is null || parsed.ReportTo is null)
                    throw new InvalidOperationException("The report date range could not be read.");
                if (parsed.NetSales <= 0m || parsed.GrossSales <= 0m)
                    throw new InvalidOperationException("The PDF did not contain valid gross and net sales totals.");
                if (parsed.TenderLines.Count == 0)
                    throw new InvalidOperationException("The PDF did not contain a valid tender breakdown.");

                await using var duplicateDb = CreateDb();
                if (await duplicateDb.PosSalesSummaries.AsNoTracking().AnyAsync(item =>
                        item.StoreId == _currentStoreId &&
                        item.SourceFileSha256 == parsed.SourceFileSha256))
                {
                    MessageBox.Show(this,
                        "This exact POS report has already been imported for the selected store.",
                        "Duplicate POS Report",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    status.Text = "Duplicate report was not imported.";
                    return;
                }

                var warningText = parsed.Warnings.Count == 0
                    ? "All report totals passed the available reconciliation checks."
                    : string.Join(Environment.NewLine, parsed.Warnings.Select(warning => "- " + warning));
                var confirmation =
                    $"Store shown: {parsed.StoreName}\n" +
                    $"Period: {parsed.ReportFrom:M/d/yyyy} - {parsed.ReportTo:M/d/yyyy}\n\n" +
                    $"Gross sales: {parsed.GrossSales:C2}\n" +
                    $"Net sales: {parsed.NetSales:C2}\n" +
                    $"Tax: {parsed.Taxes:C2}\n" +
                    $"Cash: {parsed.CashSales:C2}\n" +
                    $"Cards: {parsed.CardSales:C2}\n" +
                    $"Department profit: {parsed.DepartmentProfit:C2}\n\n" +
                    warningText +
                    "\n\nImport this consolidated report?";
                if (MessageBox.Show(this, confirmation, "Review POS Summary",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    status.Text = "Import cancelled.";
                    return;
                }

                var reportFolder = Path.Combine(_paths.AppDataDirectory, "POS Cash Sales Summaries", _currentStoreId.ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(reportFolder);
                var storedName =
                    $"{parsed.ReportFrom:yyyyMMdd}_{parsed.ReportTo:yyyyMMdd}_{parsed.SourceFileSha256[..12]}.pdf";
                var storedPath = Path.Combine(reportFolder, storedName);
                File.Copy(picker.FileName, storedPath, overwrite: false);

                var entity = new PosSalesSummary
                {
                    StoreId = _currentStoreId,
                    ReportFrom = parsed.ReportFrom.Value,
                    ReportTo = parsed.ReportTo.Value,
                    SourceSystem = parsed.SourceSystem,
                    ReportedStoreName = parsed.StoreName,
                    SourceFileName = parsed.SourceFileName,
                    SourceFilePath = storedPath,
                    SourceFileSha256 = parsed.SourceFileSha256,
                    TenderTransactionCount = parsed.TenderTransactionCount,
                    GrossAmountReceived = parsed.GrossAmountReceived,
                    GiftCardRedeemed = parsed.GiftCardRedeemed,
                    NonRevenueReceived = parsed.NonRevenueReceived,
                    NonRevenueReturned = parsed.NonRevenueReturned,
                    NonRevenueAmount = parsed.NonRevenueAmount,
                    GrossSales = parsed.GrossSales,
                    Taxes = parsed.Taxes,
                    NetSales = parsed.NetSales,
                    TaxableSales = parsed.TaxableSales,
                    NonTaxableSales = parsed.NonTaxableSales,
                    RoundingOffset = parsed.RoundingOffset,
                    CashSales = parsed.CashSales,
                    CardSales = parsed.CardSales,
                    CustomerTransactionCount = parsed.CustomerTransactionCount,
                    CustomerAverageSale = parsed.CustomerAverageSale,
                    UserLoginCount = parsed.UserLoginCount,
                    DeleteVoidCount = parsed.DeleteVoidCount,
                    NoSaleCount = parsed.NoSaleCount,
                    VoidDeleteAmount = parsed.VoidDeleteAmount,
                    TotalDiscount = parsed.TotalDiscount,
                    DepartmentQuantity = parsed.DepartmentQuantity,
                    DepartmentSales = parsed.DepartmentSales,
                    DepartmentCost = parsed.DepartmentCost,
                    DepartmentProfit = parsed.DepartmentProfit,
                    DepartmentProfitPercent = parsed.DepartmentProfitPercent,
                    ImportedByUserId = _session.UserId,
                    ImportedByName = _session.DisplayName,
                    TenderLines = parsed.TenderLines.Select(line => new PosSalesTenderLine
                    {
                        TenderType = line.TenderType,
                        TransactionCount = line.TransactionCount,
                        Amount = line.Amount
                    }).ToList(),
                    HourlyLines = parsed.HourlyLines.Select(line => new PosSalesHourlyLine
                    {
                        TimePeriod = line.TimePeriod,
                        TransactionCount = line.TransactionCount,
                        Amount = line.Amount
                    }).ToList(),
                    DepartmentLines = parsed.DepartmentLines.Select(line => new PosSalesDepartmentLine
                    {
                        Department = line.Department,
                        Quantity = line.Quantity,
                        Sales = line.Sales,
                        Cost = line.Cost,
                        Profit = line.Profit,
                        ProfitPercent = line.ProfitPercent,
                        SalesPercent = line.SalesPercent
                    }).ToList()
                };

                await using var saveDb = CreateDb();
                saveDb.PosSalesSummaries.Add(entity);
                await saveDb.SaveChangesAsync();
                selectedSummaryId = entity.Id;
                from.Value = entity.ReportFrom.ToDateTime(TimeOnly.MinValue);
                to.Value = entity.ReportTo.ToDateTime(TimeOnly.MinValue);
                await RefreshAsync();
                status.Text = $"Imported {parsed.SourceFileName}. Enter the cash drop and any register payout, then click SAVE.";
            }
            catch (Exception exception)
            {
                status.Text = "The POS report was not imported.";
                MessageBox.Show(this, exception.Message, "Cash and Sales Summary Import",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                import.Enabled = true;
            }
        };

        autoSync.Click += async (_, _) =>
        {
            using var form = _services.GetRequiredService<PortalSyncSetupForm>();
            form.ShowDialog(this);
            await RefreshAsync();
        };
        refresh.Click += async (_, _) => await RefreshAsync();
        from.ValueChanged += async (_, _) => await RefreshAsync();
        to.ValueChanged += async (_, _) => await RefreshAsync();

        openPdf.Click += async (_, _) =>
        {
            if (selectedSummaryId is null)
            {
                MessageBox.Show(this, "Select an imported report first.", "Cash and Sales Summary",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            await using var db = CreateDb();
            var path = await db.PosSalesSummaries.AsNoTracking()
                .Where(item => item.Id == selectedSummaryId.Value && item.StoreId == _currentStoreId)
                .Select(item => item.SourceFilePath)
                .FirstOrDefaultAsync();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                MessageBox.Show(this, "The stored PDF could not be found.", "Cash and Sales Summary",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        };

        delete.Click += async (_, _) =>
        {
            if (selectedSummaryId is null)
            {
                MessageBox.Show(this, "Select an imported report first.", "Cash and Sales Summary",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this,
                    "Delete this imported summary and its tender, hourly, and department details?",
                    "Delete POS Summary", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            await using var db = CreateDb();
            var entity = await db.PosSalesSummaries
                .FirstOrDefaultAsync(item => item.Id == selectedSummaryId.Value && item.StoreId == _currentStoreId);
            if (entity is null)
                return;
            var storedPath = entity.SourceFilePath;
            var linkedShiftDates = await db.ShiftLogs
                .Where(item => item.StoreId == _currentStoreId && item.PosSalesSummaryId == entity.Id)
                .Select(item => item.Date)
                .Distinct()
                .ToListAsync();
            var linkedShifts = await db.ShiftLogs
                .Where(item => item.StoreId == _currentStoreId && item.PosSalesSummaryId == entity.Id)
                .ToListAsync();
            db.ShiftLogs.RemoveRange(linkedShifts);
            db.PosSalesSummaries.Remove(entity);
            await db.SaveChangesAsync();
            foreach (var date in linkedShiftDates)
                await SyncShiftLogCashDropsToCashOnHandAsync(date);
            if (!string.IsNullOrWhiteSpace(storedPath) && File.Exists(storedPath))
            {
                try { File.Delete(storedPath); }
                catch { /* The database record is authoritative; a locked audit copy can be removed later. */ }
            }
            selectedSummaryId = null;
            await RefreshAsync();
            status.Text = "The imported POS summary and its linked accounting entry were deleted.";
        };

        _pendingModuleActivation = RefreshAsync;
        return ModuleShell("\uE9D2", "Cash & Sales Summary",
            "Import POS sales, reconcile the manager's cash drop and register payouts, and track over or short variance.", root);
    }

    private async Task SyncPosSalesSummaryToShiftLogAsync(int summaryId)
    {
        await using var db = CreateDb();
        var summary = await db.PosSalesSummaries
            .FirstOrDefaultAsync(item => item.Id == summaryId && item.StoreId == _currentStoreId);
        if (summary is null)
            return;

        var shift = await db.ShiftLogs
            .FirstOrDefaultAsync(item =>
                item.StoreId == _currentStoreId &&
                item.PosSalesSummaryId == summary.Id);
        var oldDate = shift?.Date;

        if (!summary.IsReconciled)
        {
            if (shift is null)
                return;
            db.ShiftLogs.Remove(shift);
            await db.SaveChangesAsync();
            await SyncShiftLogCashDropsToCashOnHandAsync(oldDate!.Value);
            return;
        }

        if (shift is null)
        {
            shift = new ShiftLogEntry
            {
                StoreId = _currentStoreId,
                PosSalesSummaryId = summary.Id,
                CreatedByUserId = summary.ReconciledByUserId ?? _session.UserId,
                CreatedByName = string.IsNullOrWhiteSpace(summary.ReconciledByName)
                    ? _session.DisplayName
                    : summary.ReconciledByName
            };
            db.ShiftLogs.Add(shift);
        }

        shift.Date = summary.ReportTo;
        shift.Employee = string.IsNullOrWhiteSpace(summary.ReconciledByName)
            ? _session.DisplayName
            : summary.ReconciledByName;
        shift.ShiftNo = "POS Cash & Sales Summary";
        shift.CashTotal = summary.CashSales;
        shift.CardTotal = summary.CardSales;
        shift.NetSales = summary.NetSales;
        shift.Tax = summary.Taxes;
        shift.CashDropReceived = summary.CashDropReceived;
        shift.RegisterPayout = summary.RegisterPayout;
        shift.PayoutReason = summary.PayoutReason;
        await db.SaveChangesAsync();

        if (oldDate.HasValue && oldDate.Value != shift.Date)
            await SyncShiftLogCashDropsToCashOnHandAsync(oldDate.Value);
        await SyncShiftLogCashDropsToCashOnHandAsync(shift.Date);
    }

    private static void AddReconciliationField(
        TableLayoutPanel host,
        string labelText,
        Control control,
        int column)
    {
        host.Controls.Add(new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Text,
            Font = WinTheme.BoldFont(8.5f),
            TextAlign = ContentAlignment.BottomLeft,
            AutoEllipsis = true,
            Margin = new Padding(4, 0, 4, 0)
        }, column, 0);
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(4, 2, 4, 4);
        host.Controls.Add(control, column, 1);
    }

    private static Label FilterLabel(string text)
        => new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Text,
            Font = WinTheme.BoldFont(9),
            TextAlign = ContentAlignment.MiddleCenter
        };

    private static Label AddPosSummaryCard(TableLayoutPanel host, int column, string title, Color color)
    {
        var card = WinTheme.BorderedPanel(6);
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(4);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            BackColor = WinTheme.Panel
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        card.Controls.Add(layout);
        layout.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            ForeColor = WinTheme.Muted,
            Font = WinTheme.BoldFont(8.5f),
            TextAlign = ContentAlignment.BottomCenter,
            AutoEllipsis = true
        }, 0, 0);
        var value = new Label
        {
            Text = "$0.00",
            Dock = DockStyle.Fill,
            ForeColor = color,
            Font = WinTheme.HeaderFont(14),
            TextAlign = ContentAlignment.TopCenter,
            AutoEllipsis = true
        };
        layout.Controls.Add(value, 0, 1);
        host.Controls.Add(card, column, 0);
        return value;
    }

    private static TabPage BuildPosTab(string title, Control content)
    {
        var page = new TabPage(title)
        {
            BackColor = WinTheme.Bg,
            Padding = new Padding(4)
        };
        content.Dock = DockStyle.Fill;
        page.Controls.Add(content);
        return page;
    }

    private static void FormatCurrencyColumns(DataGridView grid, params string[] columnNames)
    {
        foreach (var name in columnNames)
        {
            if (grid.Columns.Contains(name))
                grid.Columns[name].DefaultCellStyle.Format = "C2";
        }
    }

    private static void FormatPercentColumns(DataGridView grid, params string[] columnNames)
    {
        foreach (var name in columnNames)
        {
            if (grid.Columns.Contains(name))
                grid.Columns[name].DefaultCellStyle.Format = "0.00'%'";
        }
    }
}
