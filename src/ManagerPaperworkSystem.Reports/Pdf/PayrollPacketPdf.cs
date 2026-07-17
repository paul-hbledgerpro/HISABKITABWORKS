using System.Globalization;
using ManagerPaperworkSystem.Core.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ManagerPaperworkSystem.Reports.Pdf;

/// <summary>
/// Produces a letter-size, three-part payroll sheet:
/// printable check on top and two matching payroll stubs below.
/// </summary>
public static class PayrollPacketPdf
{
    private const string Ink = "#111111";
    private const string Grid = "#D7D7D7";
    private const string Stripe = "#F4F4F4";
    private const string Header = "#FAFAFA";

    public static void Generate(
        string storeName,
        string storeAddress,
        PayrollRun run,
        IReadOnlyList<PayrollEntry> entries,
        IReadOnlyDictionary<int, Employee> employees,
        string outputPath)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        Document.Create(document =>
        {
            foreach (var entry in entries.OrderBy(x => x.EmployeeName))
            {
                employees.TryGetValue(entry.EmployeeId, out var employee);
                document.Page(page =>
                {
                    page.Size(PageSizes.Letter);
                    page.Margin(18);
                    page.DefaultTextStyle(x => x.FontFamily("Arial").FontSize(7).FontColor(Ink));
                    page.Content().Column(column =>
                    {
                        column.Spacing(6);
                        column.Item().Height(236).Element(c => Check(c, run, entry, employee));
                        column.Item().Height(250).Element(c => Stub(c, storeName, storeAddress, run, entry, employee));
                        column.Item().Height(250).Element(c => Stub(c, storeName, storeAddress, run, entry, employee, employerCopy: true));
                    });
                });
            }
        }).GeneratePdf(outputPath);
    }

    private static void Check(
        IContainer container,
        PayrollRun run,
        PayrollEntry entry,
        Employee? employee)
    {
        container.PaddingHorizontal(44).PaddingVertical(4).Column(column =>
        {
            column.Item().AlignRight().Width(110).Column(right =>
            {
                right.Item().AlignCenter().Text(run.PayDate.ToString("MM/dd/yyyy")).FontSize(10);
                right.Item().Height(14);
                right.Item().AlignCenter().Text(Money(entry.NetPay)).FontSize(10);
            });
            column.Item().Height(12);
            column.Item().PaddingLeft(28).Text(PayeeName(entry, employee)).Bold().FontSize(10);
            column.Item().Height(17);
            column.Item().Text($"{AmountToWords(entry.NetPay).ToUpperInvariant()} & {DecimalCents(entry.NetPay):00}/100**********")
                .Bold().FontSize(9.5f);
            column.Item().Height(17);
            column.Item().PaddingLeft(20).Column(address =>
            {
                address.Item().Text(PayeeName(entry, employee)).FontSize(8);
                if (employee is not null)
                {
                    if (!string.IsNullOrWhiteSpace(employee.Address))
                        address.Item().Text(employee.Address.ToUpperInvariant()).FontSize(8);
                    var city = CityStateZip(employee);
                    if (!string.IsNullOrWhiteSpace(city))
                        address.Item().Text(city.ToUpperInvariant()).FontSize(8);
                }
            });
        });
    }

    private static void Stub(
        IContainer container,
        string storeName,
        string storeAddress,
        PayrollRun run,
        PayrollEntry entry,
        Employee? employee,
        bool employerCopy = false)
    {
        container.PaddingHorizontal(1).PaddingVertical(2).Column(column =>
        {
            column.Item().Height(55).Row(row =>
            {
                row.RelativeItem(1.45f).PaddingLeft(2).Column(left =>
                {
                    left.Item().Text(StubEmployeeName(entry, employee)).Bold().FontSize(7.5f);
                    if (employee is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(employee.Address)) left.Item().PaddingTop(3).Text(employee.Address.ToUpperInvariant());
                        var city = CityStateZip(employee);
                        if (!string.IsNullOrWhiteSpace(city)) left.Item().PaddingTop(3).Text(city.ToUpperInvariant());
                    }
                });
                row.RelativeItem(1.35f).PaddingLeft(4).Column(middle =>
                {
                    middle.Item().Text(storeName.ToUpperInvariant()).Bold().FontSize(6.5f);
                    if (!string.IsNullOrWhiteSpace(storeAddress))
                        middle.Item().PaddingTop(3).Text(storeAddress.ToUpperInvariant()).FontSize(6.5f);
                });
                row.RelativeItem(2.2f).Element(c => CheckFacts(c, run, entry, employee));
            });

            column.Item().Height(147).Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    left.Item().Height(106).Row(tables =>
                    {
                        tables.RelativeItem(2.65f).Element(c => EarningsTable(c, entry));
                        tables.ConstantItem(5);
                        tables.RelativeItem(1.05f).Element(c => TaxesTable(c, entry));
                        tables.ConstantItem(5);
                        tables.RelativeItem(1.15f).Element(c => DeductionsTable(c, entry));
                    });
                    left.Item().PaddingTop(4).Height(37).Element(TimeOffTable);
                });
                row.ConstantItem(5);
                row.ConstantItem(128).Element(c => YearToDateTable(c, entry));
            });

            column.Item().Height(35).PaddingTop(5).Row(row =>
            {
                row.RelativeItem().Text(storeName.ToUpperInvariant()).Bold().FontSize(6);
                row.ConstantItem(120).AlignRight().Text(employerCopy ? "EMPLOYER COPY" : "EMPLOYEE COPY").FontSize(6);
            });
        });
    }

    private static void CheckFacts(
        IContainer container,
        PayrollRun run,
        PayrollEntry entry,
        Employee? employee)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(1.25f);
                columns.RelativeColumn(1.05f);
                columns.RelativeColumn(1.25f);
                columns.RelativeColumn(1.1f);
            });
            FactRow(table, "CHECK DATE:", run.PayDate.ToString("MM/dd/yyyy"), "CHECK NUMBER:", FormatCheckNumber(entry.CheckNumber));
            FactRow(table, "PERIOD START:", run.PeriodStart.ToString("MM/dd/yyyy"), "EMPLOYEE ID:", employee?.EmployeeNumber ?? entry.EmployeeId.ToString(CultureInfo.InvariantCulture));
            FactRow(table, "PERIOD END:", run.PeriodEnd.ToString("MM/dd/yyyy"), "WORKER ID:", entry.EmployeeId.ToString(CultureInfo.InvariantCulture));
            FactRow(table, "", "", "NET PAY:", Money(entry.NetPay));
        });
    }

    private static void FactRow(TableDescriptor table, string leftLabel, string leftValue, string rightLabel, string rightValue)
    {
        FactCell(table, leftLabel, true);
        FactCell(table, leftValue, false);
        FactCell(table, rightLabel, true);
        FactCell(table, rightValue, false);
    }

    private static void FactCell(TableDescriptor table, string value, bool label)
    {
        var text = table.Cell().PaddingVertical(1).Text(value);
        if (label) text.SemiBold();
    }

    private static void EarningsTable(IContainer container, PayrollEntry entry)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(2.25f);
                columns.RelativeColumn(.95f);
                columns.RelativeColumn(1.05f);
                columns.RelativeColumn(.95f);
                columns.RelativeColumn(1.25f);
            });
            TableHeader(table, "EARNINGS TYPE", "RATE", "WORKED", "TIME OFF", "AMOUNT");
            EarningsRow(table, entry.PayType == EmployeePayType.Salary ? "SALARY" : "REGULAR - HRLY",
                entry.PayType == EmployeePayType.Salary ? "" : Rate(entry.PayRate),
                Hours(entry.RegularHours), "", Money(entry.RegularPay), false);
            EarningsRow(table, "OVERTIME - HRLY",
                entry.PayType == EmployeePayType.Salary ? "" : Rate(entry.PayRate * 1.5m),
                Hours(entry.OvertimeHours), "", Money(entry.OvertimePay), true);
            EarningsRow(table, "HOLIDAY - HRLY",
                entry.PayType == EmployeePayType.Salary ? "" : Rate(entry.PayRate * 1.5m),
                Hours(entry.HolidayHours), "", Money(entry.HolidayPay), false);
            EarningsRow(table, "BONUS", "", "", "", Money(entry.BonusPay), true);
            BlankRow(table, 5, false);
            BlankRow(table, 5, true);
            EarningsRow(table, "TOTAL HOURS\n& EARNINGS", "", Hours(entry.RegularHours + entry.OvertimeHours),
                Hours(entry.HolidayHours), Money(entry.GrossPay), false, bold: true);
        });
    }

    private static void TaxesTable(IContainer container, PayrollEntry entry)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns => { columns.RelativeColumn(1.45f); columns.RelativeColumn(1); });
            TableHeader(table, "TAXES TYPE", "AMOUNT");
            CompactRow(table, "FED WTH", Money(entry.FederalWithholding), false);
            CompactRow(table, "FICA", Money(entry.SocialSecurityWithholding), true);
            CompactRow(table, "MEDFICA", Money(entry.MedicareWithholding), false);
            CompactRow(table, "STATE-IL", Money(entry.StateWithholding), true);
            BlankRow(table, 2, false);
            BlankRow(table, 2, true);
            CompactRow(table, "TOTAL TAXES", Money(TotalTaxes(entry)), false, true);
        });
    }

    private static void DeductionsTable(IContainer container, PayrollEntry entry)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns => { columns.RelativeColumn(1.55f); columns.RelativeColumn(1); });
            TableHeader(table, "DEDUCTIONS TYPE", "AMOUNT");
            CompactRow(table, "CASH ADVANCE", Money(entry.CashAdvanceDeduction), false);
            CompactRow(table, "OTHER", Money(entry.OtherDeduction), true);
            BlankRow(table, 2, false);
            BlankRow(table, 2, true);
            BlankRow(table, 2, false);
            BlankRow(table, 2, true);
            CompactRow(table, "TOTAL DEDUCTIONS", Money(entry.CashAdvanceDeduction + entry.OtherDeduction), false, true);
        });
    }

    private static void YearToDateTable(IContainer container, PayrollEntry entry)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns => { columns.RelativeColumn(1.55f); columns.RelativeColumn(1); });
            TableHeader(table, "YEAR TO DATE TYPE", "AMOUNT");
            CompactRow(table, "GROSS PAY", Money(entry.GrossPayYtd), false);
            CompactRow(table, "FED WTH", Money(entry.FederalWithholdingYtd), true);
            CompactRow(table, "FICA", Money(entry.SocialSecurityWithholdingYtd), false);
            CompactRow(table, "MEDFICA", Money(entry.MedicareWithholdingYtd), true);
            CompactRow(table, "STATE-IL", Money(entry.StateWithholdingYtd), false);
            BlankRow(table, 2, true);
            BlankRow(table, 2, false);
            BlankRow(table, 2, true);
            BlankRow(table, 2, false);
            BlankRow(table, 2, true);
        });
    }

    private static void TimeOffTable(IContainer container)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(1.1f);
                columns.RelativeColumn();
                columns.RelativeColumn();
                columns.RelativeColumn();
                columns.RelativeColumn();
                columns.RelativeColumn();
            });
            TableHeader(table, "TIME OFF TYPE", "EARNED", "USED", "AVAILABLE", "EARNED YTD", "USED YTD");
            BlankRow(table, 6, true);
        });
    }

    private static void TableHeader(TableDescriptor table, params string[] cells)
    {
        foreach (var cell in cells)
            table.Cell().Border(.5f).BorderColor(Grid).Background(Header).Padding(2)
                .Text(cell).FontSize(5.4f);
    }

    private static void EarningsRow(
        TableDescriptor table,
        string type,
        string rate,
        string worked,
        string timeOff,
        string amount,
        bool stripe,
        bool bold = false)
    {
        DataCell(table, type, stripe, bold);
        DataCell(table, rate, stripe, bold, true);
        DataCell(table, worked, stripe, bold, true);
        DataCell(table, timeOff, stripe, bold, true);
        DataCell(table, amount, stripe, bold, true);
    }

    private static void CompactRow(TableDescriptor table, string type, string amount, bool stripe, bool bold = false)
    {
        DataCell(table, type, stripe, bold);
        DataCell(table, amount, stripe, bold, true);
    }

    private static void BlankRow(TableDescriptor table, int cells, bool stripe)
    {
        for (var i = 0; i < cells; i++) DataCell(table, "", stripe, false);
    }

    private static void DataCell(TableDescriptor table, string value, bool stripe, bool bold, bool right = false)
    {
        var cell = table.Cell().BorderHorizontal(.35f).BorderColor(Grid)
            .Background(stripe ? Stripe : Colors.White).PaddingHorizontal(2).PaddingVertical(1.6f);
        var text = right ? cell.AlignRight().Text(value) : cell.Text(value);
        text.FontSize(5.9f);
        if (bold) text.Bold();
    }

    private static decimal TotalTaxes(PayrollEntry entry)
        => entry.FederalWithholding + entry.SocialSecurityWithholding + entry.MedicareWithholding + entry.StateWithholding;

    private static string PayeeName(PayrollEntry entry, Employee? employee)
        => (employee?.FullName ?? entry.EmployeeName).ToUpperInvariant();

    private static string StubEmployeeName(PayrollEntry entry, Employee? employee)
    {
        if (employee is null) return entry.EmployeeName.ToUpperInvariant();
        var given = string.Join(" ", new[] { employee.FirstName, employee.MiddleInitial }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return $"{employee.LastName}, {given}".Trim(' ', ',').ToUpperInvariant();
    }

    private static string CityStateZip(Employee employee)
        => string.Join(" ", new[]
        {
            string.IsNullOrWhiteSpace(employee.City) ? "" : employee.City.TrimEnd(',') + ",",
            employee.State,
            employee.Zip
        }.Where(x => !string.IsNullOrWhiteSpace(x)));

    private static string FormatCheckNumber(string value)
        => int.TryParse(value, out var number) ? number.ToString("00000", CultureInfo.InvariantCulture) : value;

    private static string Money(decimal value) => value.ToString("C2", CultureInfo.GetCultureInfo("en-US"));
    private static string Rate(decimal value) => value.ToString("C2", CultureInfo.GetCultureInfo("en-US"));
    private static string Hours(decimal value) => value == 0 ? "" : $"{value:0.00} hrs";

    private static int DecimalCents(decimal value)
    {
        var rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        return (int)((rounded - Math.Truncate(rounded)) * 100m);
    }

    private static string AmountToWords(decimal value)
    {
        var number = (long)Math.Truncate(Math.Round(value, 2, MidpointRounding.AwayFromZero));
        if (number == 0) return "Zero";
        string[] ones = { "", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen" };
        string[] tens = { "", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety" };
        string Part(long n)
        {
            if (n == 0) return "";
            if (n < 20) return ones[n];
            if (n < 100) return (tens[n / 10] + " " + ones[n % 10]).Trim();
            if (n < 1_000) return (ones[n / 100] + " Hundred " + Part(n % 100)).Trim();
            if (n < 1_000_000) return (Part(n / 1_000) + " Thousand " + Part(n % 1_000)).Trim();
            if (n < 1_000_000_000) return (Part(n / 1_000_000) + " Million " + Part(n % 1_000_000)).Trim();
            return (Part(n / 1_000_000_000) + " Billion " + Part(n % 1_000_000_000)).Trim();
        }
        return Part(number);
    }
}
