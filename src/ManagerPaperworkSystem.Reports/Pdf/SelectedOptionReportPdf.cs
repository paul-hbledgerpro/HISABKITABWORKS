using System.Globalization;
using ManagerPaperworkSystem.Core.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ManagerPaperworkSystem.Reports.Pdf;

public static class SelectedOptionReportPdf
{
    private const string Navy = "#071D3A";
    private const string Panel = "#FFFFFF";
    private const string PanelAlt = "#F4F7FA";
    private const string Copper = "#D98224";
    private const string Text = "#071D3A";
    private const string Muted = "#60748A";
    private const string Border = "#B8C7D6";
    private const string Green = "#187A35";
    private const string Red = "#D52B2B";
    private const string Blue = "#2769B3";

    public static void GenerateSalesSummary(string storeName, string storeAddress, DateOnly from, DateOnly to, IReadOnlyList<ShiftLogEntry> entries, string outputPath)
    {
        var rows = (entries ?? Array.Empty<ShiftLogEntry>())
            .GroupBy(x => x.Date)
            .Select(g => new
            {
                Date = g.Key,
                Cash = g.Sum(x => x.CashTotal),
                Card = g.Sum(x => x.CardTotal),
                Tax = g.Sum(x => x.Tax),
                Gross = g.Sum(x => x.GrossSales),
                Net = g.Sum(x => x.NetSales),
                Drop = g.Sum(x => x.CashDropReceived),
                Payout = g.Sum(x => x.RegisterPayout),
                Variance = g.Sum(x => x.Variance)
            })
            .OrderBy(x => x.Date)
            .ToList();

        Create(outputPath, PageSizes.Letter.Landscape(), page =>
        {
            BuildHeader(page, storeName, storeAddress, "Sales Summary by Date", "Daily sales totals with cash, card, tax, gross and net sales.", from, to);
            page.Content().PaddingHorizontal(14).PaddingVertical(8).Column(col =>
            {
                col.Spacing(8);
                col.Item().Row(row =>
                {
                    Metric(row, "Net Sales", Money(rows.Sum(x => x.Net)), "Month total", Green);
                    Metric(row, "Gross Sales", Money(rows.Sum(x => x.Gross)), "Cash + card + tax", Copper);
                    Metric(row, "Cash Drop", Money(rows.Sum(x => x.Drop)), "Deposited", Green);
                    Metric(row, "Variance", Money(rows.Sum(x => x.Variance)), "Drop + payout - cash", VarianceColor(rows.Sum(x => x.Variance)));
                });
                col.Item().Element(c => Table(c,
                    new[] { "Date", "Cash", "Card", "Tax", "Gross", "Net", "Drop", "Register Payout", "Variance" },
                    rows.Select(x => new[] { Date(x.Date), Money(x.Cash), Money(x.Card), Money(x.Tax), Money(x.Gross), Money(x.Net), Money(x.Drop), Money(x.Payout), Money(x.Variance) }).ToList(),
                    new[] { 1.05f, .9f, .9f, .8f, .9f, .9f, .9f, 1.15f, .9f },
                    varianceColumnIndex: 8));
            });
            BuildFooter(page);
        });
    }

    public static void GenerateShiftLog(string storeName, string storeAddress, DateOnly from, DateOnly to, IReadOnlyList<ShiftLogEntry> entries, string outputPath)
    {
        var rows = (entries ?? Array.Empty<ShiftLogEntry>()).OrderBy(x => x.Date).ThenBy(x => x.ShiftNo).ToList();

        Create(outputPath, PageSizes.Letter.Landscape(), page =>
        {
            BuildHeader(page, storeName, storeAddress, "Shift Log Report", "End-of-shift cash drop activity, payout reason, and variance review.", from, to);
            page.Content().PaddingHorizontal(14).PaddingVertical(8).Column(col =>
            {
                col.Spacing(8);
                col.Item().Row(row =>
                {
                    Metric(row, "Total Cash", Money(rows.Sum(x => x.CashTotal)), "POS reported", Copper);
                    Metric(row, "Cash Drop", Money(rows.Sum(x => x.CashDropReceived)), "Manager counted", Green);
                    Metric(row, "Payouts", Money(rows.Sum(x => x.RegisterPayout)), "Register payouts", Copper);
                    Metric(row, "Variance", Money(rows.Sum(x => x.Variance)), "Drop + payout - cash", VarianceColor(rows.Sum(x => x.Variance)));
                });
                col.Item().Element(c => Table(c,
                    new[] { "Date", "Shift", "Employee", "Cash", "Card", "Gross", "Drop", "Payout", "Reason", "Variance" },
                    rows.Select(x => new[] { Date(x.Date), x.ShiftNo, x.Employee, Money(x.CashTotal), Money(x.CardTotal), Money(x.GrossSales), Money(x.CashDropReceived), Money(x.RegisterPayout), x.PayoutReason, Money(x.Variance) }).ToList(),
                    new[] { .9f, .75f, 1.25f, .9f, .9f, .9f, .9f, .9f, 1.5f, .9f },
                    varianceColumnIndex: 9));
            });
            BuildFooter(page);
        });
    }

    public static void GenerateCashOnHand(string storeName, string storeAddress, DateOnly from, DateOnly to, IReadOnlyList<CashOnHandEntry> entries, string outputPath)
    {
        var rows = (entries ?? Array.Empty<CashOnHandEntry>()).OrderBy(x => x.Date).ThenBy(x => x.CreatedUtc).ToList();
        var added = rows.Sum(x => x.CashAdded);
        var payout = rows.Where(x => x.IsPayout).Sum(x => x.PayoutAmount);

        Create(outputPath, PageSizes.Letter.Landscape(), page =>
        {
            BuildHeader(page, storeName, storeAddress, "Cash On Hand Report", "Cash added, payouts, carry forward balance, and check references.", from, to);
            page.Content().PaddingHorizontal(14).PaddingVertical(8).Column(col =>
            {
                col.Spacing(8);
                col.Item().Row(row =>
                {
                    Metric(row, "Opening Balance", Money(0), "Beginning selected period", Copper);
                    Metric(row, "Cash Added", Money(added), "Selected period", Green);
                    Metric(row, "Payouts", Money(payout), "Cash payouts", Red);
                    Metric(row, "Closing", Money(added - payout), "Expected", added - payout < 0 ? Red : Green);
                });
                col.Item().Element(c => Table(c,
                    new[] { "Date", "Cash Added", "Is Payout", "Payout", "Vendor", "Purpose", "Description", "Check #" },
                    rows.Select(x => new[]
                    {
                        Date(x.Date),
                        Money(x.CashAdded),
                        x.IsPayout ? "Yes" : "No",
                        x.IsPayout ? Money(x.PayoutAmount) : Money(0),
                        x.Vendor?.Name ?? "",
                        x.Purpose?.Name ?? "",
                        x.Description,
                        x.Reference
                    }).ToList(),
                    new[] { .9f, 1f, .9f, 1f, 1.25f, 1.2f, 1.9f, .8f }));
            });
            BuildFooter(page);
        });
    }

    public static void GenerateCheckPayouts(string storeName, string storeAddress, DateOnly from, DateOnly to, IReadOnlyList<CheckPayout> entries, string outputPath)
    {
        var rows = (entries ?? Array.Empty<CheckPayout>()).OrderBy(x => x.Date).ThenBy(x => x.CheckNumber).ToList();
        var uncleared = rows.Where(x => !x.Cleared).Sum(x => x.CheckAmount);
        var cleared = rows.Where(x => x.Cleared).Sum(x => x.CheckAmount);
        var nextCheck = rows.Select(x => int.TryParse(x.CheckNumber, out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;

        Create(outputPath, PageSizes.Letter.Landscape(), page =>
        {
            BuildHeader(page, storeName, storeAddress, "Check Payouts Report", "Vendor check payout register, cleared status, and print controls.", from, to);
            page.Content().PaddingHorizontal(14).PaddingVertical(8).Column(col =>
            {
                col.Spacing(8);
                col.Item().Row(row =>
                {
                    Metric(row, "Uncleared Total", Money(uncleared), "Open checks", Red);
                    Metric(row, "Cleared This Period", Money(cleared), "Posted checks", Green);
                    Metric(row, "Next Check #", nextCheck.ToString(CultureInfo.InvariantCulture), "Ready to print", Copper);
                    Metric(row, "Checks Printed", rows.Count.ToString(CultureInfo.InvariantCulture), "Selected period", Blue);
                });
                col.Item().Element(c => Table(c,
                    new[] { "Date", "Vendor", "Description", "Amount", "Check #", "Cleared" },
                    rows.Select(x => new[] { Date(x.Date), x.VendorName, x.Description, Money(-Math.Abs(x.CheckAmount)), x.CheckNumber, x.Cleared ? "Cleared" : "Uncleared" }).ToList(),
                    new[] { .9f, 1.75f, 2.25f, 1f, .85f, .9f }));
            });
            BuildFooter(page);
        });
    }

    public static void GenerateProfitLoss(string storeName, string storeAddress, DateOnly from, DateOnly to, ProfitLossData data, string outputPath)
    {
        var netSales = data.GrossSales;
        var cogs = data.Purchases;
        var operatingExpenses = data.TotalExpenses - cogs;
        var netProfit = data.NetProfitLoss;

        Create(outputPath, PageSizes.Letter.Landscape(), page =>
        {
            BuildHeader(page, storeName, storeAddress, "Profit & Loss Statement", "Sales, COGS, expenses, net profit, and margin by period.", from, to);
            page.Content().PaddingHorizontal(14).PaddingVertical(8).Column(col =>
            {
                col.Spacing(8);
                col.Item().Row(row =>
                {
                    Metric(row, "Total Income", Money(data.TotalRevenue), "Sales + included bank income", Green);
                    Metric(row, "COGS", Money(cogs), "Purchases", Red);
                    Metric(row, "Expenses", Money(operatingExpenses), "Excludes COGS", Red);
                    Metric(row, "Net Profit", Money(netProfit), "Owner view", netProfit < 0 ? Red : Green);
                });
                var bankExpenses = data.TotalBankExpenses - data.Payroll;
                col.Item().Element(c => Table(c,
                    new[] { "Category", "Sales", "COGS", "Expense", "Net Profit", "Margin %" },
                    new List<string[]>
                    {
                        new[] { "Sales", Money(netSales), Money(0), Money(0), Money(netSales), "100.00%" },
                        new[] { "Bank Income", Money(data.BankIncome), Money(0), Money(0), Money(data.BankIncome), Percent(data.TotalRevenue == 0 ? 0 : data.BankIncome / data.TotalRevenue) },
                        new[] { "Cost of Goods Sold", Money(0), Money(cogs), Money(0), Money(-cogs), Percent(netSales == 0 ? 0 : cogs / netSales) },
                        new[] { "Cash Payouts", Money(0), Money(0), Money(data.CashPayouts), Money(-data.CashPayouts), Percent(data.TotalRevenue == 0 ? 0 : data.CashPayouts / data.TotalRevenue) },
                        new[] { "Check Payouts", Money(0), Money(0), Money(data.CheckPayouts), Money(-data.CheckPayouts), Percent(data.TotalRevenue == 0 ? 0 : data.CheckPayouts / data.TotalRevenue) },
                        new[] { "Payroll", Money(0), Money(0), Money(data.Payroll), Money(-data.Payroll), Percent(data.TotalRevenue == 0 ? 0 : data.Payroll / data.TotalRevenue) },
                        new[] { "Other Bank Expenses", Money(0), Money(0), Money(bankExpenses), Money(-bankExpenses), Percent(data.TotalRevenue == 0 ? 0 : bankExpenses / data.TotalRevenue) },
                        new[] { "Total", Money(data.TotalRevenue), Money(cogs), Money(operatingExpenses), Money(netProfit), Percent(data.TotalRevenue == 0 ? 0 : netProfit / data.TotalRevenue) }
                    },
                    new[] { 1.7f, 1f, 1f, 1f, 1f, .8f }));
            });
            BuildFooter(page);
        });
    }

    public static void GeneratePayroll(string storeName, string storeAddress, DateOnly from, DateOnly to, IReadOnlyList<PayrollRun> runs, string outputPath)
    {
        var finalized = (runs ?? Array.Empty<PayrollRun>()).Where(x => x.Status == PayrollRunStatus.Finalized).OrderBy(x => x.PayDate).ToList();
        var rows = finalized.SelectMany(run => run.Entries.Select(entry => new
        {
            Run = run.Id,
            run.PayDate,
            Period = $"{run.PeriodStart:MM/dd/yy} - {run.PeriodEnd:MM/dd/yy}",
            entry.EmployeeName,
            entry.GrossPay,
            Taxes = entry.FederalWithholding + entry.SocialSecurityWithholding + entry.MedicareWithholding + entry.StateWithholding,
            Deductions = entry.CashAdvanceDeduction + entry.OtherDeduction,
            entry.NetPay,
            entry.CheckNumber
        })).ToList();

        Create(outputPath, PageSizes.Letter.Landscape(), page =>
        {
            BuildHeader(page, storeName, storeAddress, "Payroll Report", "Finalized payroll wages, employee taxes, deductions, net pay, and check references.", from, to);
            page.Content().PaddingHorizontal(14).PaddingVertical(8).Column(col =>
            {
                col.Spacing(8);
                col.Item().Row(row =>
                {
                    Metric(row, "Finalized Runs", finalized.Count.ToString(CultureInfo.InvariantCulture), "Selected pay dates", Blue);
                    Metric(row, "Gross Payroll", Money(rows.Sum(x => x.GrossPay)), "P&L payroll expense", Copper);
                    Metric(row, "Employee Taxes", Money(rows.Sum(x => x.Taxes)), "Withheld", Red);
                    Metric(row, "Net Payroll", Money(rows.Sum(x => x.NetPay)), "Checks issued", Green);
                });
                col.Item().Element(c => Table(c,
                    new[] { "Pay Date", "Period", "Employee", "Gross", "Taxes", "Other Deductions", "Net Pay", "Check #" },
                    rows.Select(x => new[] { Date(x.PayDate), x.Period, x.EmployeeName, Money(x.GrossPay), Money(x.Taxes), Money(x.Deductions), Money(x.NetPay), x.CheckNumber }).ToList(),
                    new[] { .85f, 1.25f, 1.65f, .9f, .9f, 1.1f, .9f, .75f }));
            });
            BuildFooter(page);
        });
    }

    public static void GenerateAllReportsBundle(string storeName, string storeAddress, DateOnly from, DateOnly to,
        IReadOnlyList<ShiftLogEntry> shifts, IReadOnlyList<CashOnHandEntry> cash, IReadOnlyList<CheckPayout> checks,
        IReadOnlyList<PurchaseInvoice> purchases, decimal payroll, string outputPath)
    {
        Create(outputPath, PageSizes.Letter.Landscape(), page =>
        {
            BuildHeader(page, storeName, storeAddress, "All Reports Bundle", "Combined report packet: sales, shifts, cash, checks, purchases, and profit summary.", from, to);
            page.Content().PaddingHorizontal(14).PaddingVertical(8).Column(col =>
            {
                col.Spacing(8);
                col.Item().Row(row =>
                {
                    Metric(row, "Sales Summary", "Ready", Money(shifts.Sum(x => x.NetSales)), Green);
                    Metric(row, "Shift Log", "Ready", $"{shifts.Count} entries", Green);
                    Metric(row, "Cash On Hand", "Ready", Money(cash.Sum(x => x.CashAdded - x.PayoutAmount)), Green);
                });
                col.Item().Row(row =>
                {
                    Metric(row, "Check Payouts", "Ready", Money(checks.Sum(x => x.CheckAmount)), Green);
                    Metric(row, "Profit & Loss", "Ready", Money(shifts.Sum(x => x.NetSales) - purchases.Sum(x => x.Total) - cash.Sum(x => x.PayoutAmount) - checks.Sum(x => x.CheckAmount) - payroll), Green);
                    Metric(row, "Purchases", "Ready", Money(purchases.Sum(x => x.Total)), Green);
                });
                col.Item().Element(c => Table(c,
                    new[] { "#", "Report Name", "Purpose", "Included" },
                    new List<string[]>
                    {
                        new[] { "1", "Sales Summary by Date", "Daily sales totals with totals", "Yes" },
                        new[] { "2", "Shift Log", "Cash drop and register payout history", "Yes" },
                        new[] { "3", "Cash On Hand", "Cash drawer ledger and payouts", "Yes" },
                        new[] { "4", "Check Payouts", "Vendor check register", "Yes" },
                        new[] { "5", "Profit & Loss", "Owner operating statement", "Yes" },
                        new[] { "6", "Purchases", "Purchase invoice totals", "Yes" },
                        new[] { "7", "Payroll", "Finalized gross payroll expense", payroll == 0 ? "No activity" : "Yes" }
                    },
                    new[] { .45f, 1.8f, 3f, .8f }));
            });
            BuildFooter(page);
        });
    }

    private static void Create(string outputPath, PageSize pageSize, Action<PageDescriptor> content)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path is required.", nameof(outputPath));

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        Document.Create(container => container.Page(page =>
        {
            page.Size(pageSize);
            page.Margin(0);
            page.DefaultTextStyle(x => x.FontSize(9).FontColor(Text));
            page.PageColor(Colors.White);
            content(page);
        })).GeneratePdf(outputPath);
    }

    private static void BuildHeader(PageDescriptor page, string storeName, string storeAddress, string title, string subtitle, DateOnly from, DateOnly to)
    {
        var businessName = string.IsNullOrWhiteSpace(storeName) ? "BUSINESS" : storeName.Trim();
        page.Header().Background(Colors.White).PaddingHorizontal(18).PaddingTop(9).PaddingBottom(5).Column(col =>
        {
            col.Spacing(2);
            col.Item().Row(row =>
            {
                row.ConstantItem(150);
                row.RelativeItem().AlignCenter().Column(center =>
                {
                    center.Item().AlignCenter().Text(businessName).Bold().FontSize(16).FontColor(Navy);
                    if (!string.IsNullOrWhiteSpace(storeAddress))
                        center.Item().AlignCenter().Text(storeAddress.Trim()).FontSize(7).FontColor(Muted);
                });
                row.ConstantItem(150).AlignRight().Column(right =>
                {
                    right.Item().AlignRight().Text($"Generated {DateTime.Now:g}").FontSize(6.5f).FontColor(Muted);
                });
            });
            col.Item().PaddingHorizontal(150).PaddingTop(2).BorderTop(1).BorderColor(Copper);
            col.Item().AlignCenter().PaddingTop(2).Text(title.ToUpperInvariant()).Bold().FontSize(13).FontColor(Navy);
            col.Item().AlignCenter().Text($"{Date(from)} - {Date(to)}").Bold().FontSize(8).FontColor(Copper);
            col.Item().AlignCenter().Text(subtitle).FontSize(7).FontColor(Muted);
        });
    }

    private static void BuildFooter(PageDescriptor page)
    {
        page.Footer().Background(Colors.White).PaddingHorizontal(18).PaddingBottom(6).Column(col =>
        {
            col.Item().BorderTop(1).BorderColor(Copper).PaddingTop(4).Row(row =>
            {
                row.RelativeItem().AlignCenter().Text("Confidential Business Report").FontSize(6.5f).FontColor(Muted);
                row.ConstantItem(100).AlignRight().DefaultTextStyle(x => x.FontSize(6.5f)).Text(t =>
                {
                    t.Span("Page ").FontColor(Muted);
                    t.CurrentPageNumber().FontColor(Text);
                    t.Span(" of ").FontColor(Muted);
                    t.TotalPages().FontColor(Text);
                });
            });
        });
    }

    private static void Metric(RowDescriptor row, string title, string value, string subtitle, string color)
    {
        row.RelativeItem().PaddingRight(6).Background(Panel).Border(1).BorderColor(Border).PaddingVertical(7).PaddingHorizontal(9).Column(col =>
        {
            col.Item().Text(title.ToUpperInvariant()).Bold().FontSize(10).FontColor(Copper);
            col.Item().PaddingTop(2).Text(value).Bold().FontSize(18).FontColor(color);
            col.Item().PaddingTop(1).Text(subtitle).FontSize(8).FontColor(Muted);
        });
    }

    private static void Table(IContainer container, string[] headers, IReadOnlyList<string[]> rows, float[] widths, int? varianceColumnIndex = null)
    {
        if (rows.Count == 0)
        {
            container.Background(Panel).Border(1).BorderColor(Border).Padding(20).AlignCenter().Text("No records found for the selected period.").FontColor(Muted);
            return;
        }

        container.Background(Panel).Border(1).BorderColor(Border).Table(table =>
        {
            table.ColumnsDefinition(cols =>
            {
                foreach (var width in widths)
                    cols.RelativeColumn(width);
            });

            table.Header(header =>
            {
                foreach (var h in headers)
                    header.Cell().Element(HeaderCell).Text(h);
            });

            var even = false;
            foreach (var row in rows)
            {
                for (var i = 0; i < headers.Length; i++)
                {
                    var value = i < row.Length ? row[i] : "";
                    var color = varianceColumnIndex == i ? VarianceValueColor(value) : ValueColor(value);
                    table.Cell().Element(c => BodyCell(c, even)).Text(value).FontColor(color);
                }
                even = !even;
            }
        });
    }

    private static IContainer HeaderCell(IContainer c)
        => c.Background(Navy).PaddingVertical(5).PaddingHorizontal(5).DefaultTextStyle(x => x.Bold().FontColor(Colors.White).FontSize(9));

    private static IContainer BodyCell(IContainer c, bool even)
        => c.Background(even ? Panel : PanelAlt).BorderBottom(1).BorderColor(Border).PaddingVertical(3.5f).PaddingHorizontal(5).DefaultTextStyle(x => x.FontSize(8).FontColor(Text));

    private static string ValueColor(string value)
    {
        if (value.Contains('(') || value.StartsWith("-$", StringComparison.Ordinal) || value.Equals("Uncleared", StringComparison.OrdinalIgnoreCase))
            return Red;
        if (value.Equals("Cleared", StringComparison.OrdinalIgnoreCase) || value.Equals("Yes", StringComparison.OrdinalIgnoreCase) || value.Equals("Ready", StringComparison.OrdinalIgnoreCase))
            return Green;
        return Text;
    }

    private static string VarianceValueColor(string value)
    {
        var isNegative = value.Contains('(') || value.StartsWith("-", StringComparison.Ordinal);
        var normalized = value.Replace("(", "").Replace(")", "").Trim();
        if (!decimal.TryParse(normalized, NumberStyles.Currency, CultureInfo.CurrentCulture, out var amount))
            return Text;

        if (isNegative)
            amount = -Math.Abs(amount);
        return VarianceColor(amount);
    }

    private static string VarianceColor(decimal value)
        => value > 0 ? Green : value < 0 ? Red : Text;

    private static string Money(decimal value)
    {
        if (value < 0)
            return $"({Math.Abs(value).ToString("C2", CultureInfo.CurrentCulture)})";
        return value.ToString("C2", CultureInfo.CurrentCulture);
    }

    private static string Percent(decimal value)
        => value.ToString("P2", CultureInfo.CurrentCulture);

    private static string Date(DateOnly value)
        => value.ToString("M/d/yyyy", CultureInfo.CurrentCulture);
}
