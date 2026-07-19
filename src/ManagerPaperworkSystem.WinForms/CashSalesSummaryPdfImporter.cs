using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace ManagerPaperworkSystem.WinForms;

internal sealed record ImportedTenderLine(string TenderType, int TransactionCount, decimal Amount);
internal sealed record ImportedHourlyLine(string TimePeriod, int TransactionCount, decimal Amount);
internal sealed record ImportedDepartmentLine(
    string Department,
    decimal Quantity,
    decimal Sales,
    decimal Cost,
    decimal Profit,
    decimal ProfitPercent,
    decimal SalesPercent);

internal sealed class CashSalesSummaryImportResult
{
    public string SourceSystem { get; init; } = "AdventPOS";
    public string StoreName { get; init; } = "";
    public DateOnly? ReportFrom { get; init; }
    public DateOnly? ReportTo { get; init; }
    public string SourceFileName { get; init; } = "";
    public string SourceFileSha256 { get; init; } = "";
    public int TenderTransactionCount { get; init; }
    public decimal GrossAmountReceived { get; init; }
    public decimal GiftCardRedeemed { get; init; }
    public decimal NonRevenueReceived { get; init; }
    public decimal NonRevenueReturned { get; init; }
    public decimal NonRevenueAmount { get; init; }
    public decimal GrossSales { get; init; }
    public decimal Taxes { get; init; }
    public decimal NetSales { get; init; }
    public decimal TaxableSales { get; init; }
    public decimal NonTaxableSales { get; init; }
    public decimal RoundingOffset { get; init; }
    public decimal CashSales { get; init; }
    public decimal CardSales { get; init; }
    public int CustomerTransactionCount { get; init; }
    public decimal CustomerAverageSale { get; init; }
    public int UserLoginCount { get; init; }
    public int DeleteVoidCount { get; init; }
    public int NoSaleCount { get; init; }
    public decimal VoidDeleteAmount { get; init; }
    public decimal TotalDiscount { get; init; }
    public decimal DepartmentQuantity { get; init; }
    public decimal DepartmentSales { get; init; }
    public decimal DepartmentCost { get; init; }
    public decimal DepartmentProfit { get; init; }
    public decimal DepartmentProfitPercent { get; init; }
    public List<ImportedTenderLine> TenderLines { get; init; } = new();
    public List<ImportedHourlyLine> HourlyLines { get; init; } = new();
    public List<ImportedDepartmentLine> DepartmentLines { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
}

internal static class CashSalesSummaryPdfImporter
{
    private const double RowTolerance = 3.2;
    private const string MoneyPattern = @"\(?\$?[0-9][0-9,]*\.[0-9]{2}\)?";

    public static Task<CashSalesSummaryImportResult> ImportAsync(string filePath, CancellationToken cancellationToken = default)
        => Task.Run(() => Import(filePath), cancellationToken);

    private static CashSalesSummaryImportResult Import(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("The selected POS report could not be found.", filePath);
        if (!string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cash and Sales Summary currently supports text-based PDF reports.");

        using var document = PdfDocument.Open(filePath);
        if (document.NumberOfPages < 1)
            throw new InvalidOperationException("The selected PDF does not contain any pages.");

        var first = document.GetPage(1);
        var leftRows = ReadRows(first, 0, first.Width * 0.51);
        var rightRows = ReadRows(first, first.Width * 0.49, first.Width);
        var fullRows = ReadRows(first, 0, first.Width);
        var allText = string.Join("\n", fullRows.Select(row => row.Text));

        if (!allText.Contains("Cash and Sales Summary", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This PDF is not an AdventPOS Cash and Sales Summary report.");

        var (reportFrom, reportTo) = ParsePeriod(allText);
        var tenderLines = ParseTenderLines(leftRows);
        var hourlyLines = ParseHourlyLines(rightRows);
        var departmentLines = document.NumberOfPages >= 2
            ? ParseDepartmentLines(ReadRows(document.GetPage(2), 0, document.GetPage(2).Width))
            : new List<ImportedDepartmentLine>();

        var grossReceivedLine = FindRow(leftRows, "Gross Amt Received");
        var departmentTotal = FindRow(
            document.NumberOfPages >= 2 ? ReadRows(document.GetPage(2), 0, document.GetPage(2).Width) : [],
            "Total",
            startsWith: true);

        var warnings = new List<string>();
        var netSales = LastMoney(FindRow(leftRows, "Net Sales")?.Text);
        var departmentSales = departmentTotal is null ? departmentLines.Sum(line => line.Sales) : MoneyAt(departmentTotal.Text, 0);
        if (tenderLines.Count == 0)
            warnings.Add("No tender rows were detected.");
        if (departmentLines.Count == 0)
            warnings.Add("No department rows were detected.");
        if (departmentSales > 0 && netSales > 0 && Math.Abs(departmentSales - netSales) > 0.05m)
            warnings.Add($"Department sales ({departmentSales:C2}) do not reconcile to net sales ({netSales:C2}).");

        return new CashSalesSummaryImportResult
        {
            StoreName = DetectStoreName(first),
            ReportFrom = reportFrom,
            ReportTo = reportTo,
            SourceFileName = Path.GetFileName(filePath),
            SourceFileSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath))),
            TenderTransactionCount = FirstIntegerAfterLabel(grossReceivedLine?.Text),
            GrossAmountReceived = LastMoney(grossReceivedLine?.Text),
            GiftCardRedeemed = LastMoney(FindRow(leftRows, "Total Giftcard Redeemed")?.Text),
            NonRevenueReceived = LastMoney(FindRow(leftRows, "Total Non-Revenue Received")?.Text),
            NonRevenueReturned = LastMoney(FindRow(leftRows, "Total Non-Revenue Returned")?.Text),
            NonRevenueAmount = LastMoney(FindRow(leftRows, "Total Non-Revenue Amt")?.Text),
            GrossSales = LastMoney(FindRow(leftRows, "Gross Sale")?.Text),
            Taxes = LastMoney(FindRow(leftRows, "Taxes")?.Text),
            NetSales = netSales,
            TaxableSales = LastMoney(FindRow(leftRows, "TAXABLE Sale TOTAL")?.Text),
            NonTaxableSales = LastMoney(FindRow(leftRows, "NON - TAXABLE Sale")?.Text),
            RoundingOffset = LastMoney(FindRow(leftRows, "Rounding Offset")?.Text),
            CashSales = tenderLines.FirstOrDefault(line => line.TenderType.Equals("CASH", StringComparison.OrdinalIgnoreCase))?.Amount
                        ?? LastMoney(FindRow(rightRows, "Cash Sale")?.Text),
            CardSales = LastMoney(FindRow(leftRows, "Credit & Debit Card Total Amount")?.Text),
            CustomerTransactionCount = LastInteger(FindRow(leftRows, "Customer(Transaction) Count")?.Text),
            CustomerAverageSale = LastMoney(FindRow(leftRows, "Customer Average Sale")?.Text),
            UserLoginCount = LastInteger(FindRow(leftRows, "User Login Count")?.Text),
            DeleteVoidCount = LastInteger(FindRow(leftRows, "Delete/Void Count")?.Text),
            NoSaleCount = LastInteger(FindRow(leftRows, "No Sale Count")?.Text),
            VoidDeleteAmount = LastMoney(FindRow(leftRows, "Void/Delete Amount")?.Text),
            TotalDiscount = LastMoney(FindRow(leftRows, "Total Discount")?.Text),
            DepartmentQuantity = departmentTotal is null ? departmentLines.Sum(line => line.Quantity) : NumberAt(departmentTotal.Text, 0),
            DepartmentSales = departmentSales,
            DepartmentCost = departmentTotal is null ? departmentLines.Sum(line => line.Cost) : MoneyAt(departmentTotal.Text, 1),
            DepartmentProfit = departmentTotal is null ? departmentLines.Sum(line => line.Profit) : MoneyAt(departmentTotal.Text, 2),
            DepartmentProfitPercent = departmentTotal is null ? Percent(departmentLines.Sum(line => line.Profit), departmentSales) : PercentAt(departmentTotal.Text, 0),
            TenderLines = tenderLines,
            HourlyLines = hourlyLines,
            DepartmentLines = departmentLines,
            Warnings = warnings
        };
    }

    private static string DetectStoreName(Page page)
    {
        var rows = ReadRows(page, 0, page.Width * 0.48)
            .Where(row => row.Y > page.Height - 100)
            .OrderByDescending(row => row.Y)
            .ToList();
        return rows.Select(row => row.Text.Trim())
            .FirstOrDefault(text =>
                text.Length > 2 &&
                !text.Contains("Cash and Sales", StringComparison.OrdinalIgnoreCase) &&
                !Regex.IsMatch(text, @"^\d"))
            ?? "";
    }

    private static (DateOnly? From, DateOnly? To) ParsePeriod(string text)
    {
        var match = Regex.Match(
            text,
            @"From\s+(?<from>\d{1,2}-[A-Za-z]{3}-\d{4})\s+To\s+(?<to>\d{1,2}-[A-Za-z]{3}-\d{4})",
            RegexOptions.IgnoreCase);
        if (!match.Success)
            return (null, null);

        return (ParseDate(match.Groups["from"].Value), ParseDate(match.Groups["to"].Value));
    }

    private static DateOnly? ParseDate(string value)
    {
        if (DateTime.TryParseExact(value, "d-MMM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return DateOnly.FromDateTime(parsed);
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)
            ? DateOnly.FromDateTime(parsed)
            : null;
    }

    private static List<ImportedTenderLine> ParseTenderLines(IReadOnlyList<PositionedRow> rows)
    {
        var start = FindIndex(rows, row => row.Text.Trim().StartsWith("TENDER TYPE", StringComparison.OrdinalIgnoreCase));
        var end = FindIndex(rows, row => row.Text.Contains("Gross Amt Received", StringComparison.OrdinalIgnoreCase));
        if (start < 0 || end <= start)
            return [];

        var result = new List<ImportedTenderLine>();
        var pattern = new Regex(
            @"^(?<type>[A-Za-z][A-Za-z0-9 &/\-]*?)\s+(?<count>\d+)\s+\$?(?<amount>[0-9,]+\.[0-9]{2})$",
            RegexOptions.IgnoreCase);
        foreach (var row in rows.Skip(start + 1).Take(end - start - 1))
        {
            var match = pattern.Match(row.Text.Trim());
            if (!match.Success)
                continue;
            result.Add(new ImportedTenderLine(
                match.Groups["type"].Value.Trim(),
                ParseInt(match.Groups["count"].Value),
                ParseMoney(match.Groups["amount"].Value)));
        }
        return result;
    }

    private static List<ImportedHourlyLine> ParseHourlyLines(IReadOnlyList<PositionedRow> rows)
    {
        var result = new List<ImportedHourlyLine>();
        var pattern = new Regex(
            @"^(?<period>\d{1,2}:\d{2}\s*[AP]M\s*-\s*\d{1,2}:\d{2}\s*[AP]M)\s+(?<count>\d+)\s+\$?(?<amount>[0-9,]+\.[0-9]{2})$",
            RegexOptions.IgnoreCase);
        foreach (var row in rows)
        {
            var match = pattern.Match(row.Text.Trim());
            if (!match.Success)
                continue;
            result.Add(new ImportedHourlyLine(
                Regex.Replace(match.Groups["period"].Value, @"\s+", " ").Trim(),
                ParseInt(match.Groups["count"].Value),
                ParseMoney(match.Groups["amount"].Value)));
        }
        return result;
    }

    private static List<ImportedDepartmentLine> ParseDepartmentLines(IReadOnlyList<PositionedRow> rows)
    {
        var result = new List<ImportedDepartmentLine>();
        var pattern = new Regex(
            @"^(?<department>.+?)\s+(?<qty>-?[0-9]+(?:\.[0-9]+)?)\s+\$?(?<sales>[0-9,]+\.[0-9]{2})\s+\$?(?<cost>[0-9,]+\.[0-9]{2})\s+\$?(?<profit>-?[0-9,]+\.[0-9]{2})\s+(?<profitPercent>-?[0-9.]+)%\s+(?<salesPercent>-?[0-9.]+)%$",
            RegexOptions.IgnoreCase);
        foreach (var row in rows)
        {
            var text = row.Text.Trim();
            if (text.StartsWith("Total ", StringComparison.OrdinalIgnoreCase))
                continue;
            var match = pattern.Match(text);
            if (!match.Success)
                continue;
            result.Add(new ImportedDepartmentLine(
                match.Groups["department"].Value.Trim(),
                ParseMoney(match.Groups["qty"].Value),
                ParseMoney(match.Groups["sales"].Value),
                ParseMoney(match.Groups["cost"].Value),
                ParseMoney(match.Groups["profit"].Value),
                ParseMoney(match.Groups["profitPercent"].Value),
                ParseMoney(match.Groups["salesPercent"].Value)));
        }
        return result;
    }

    private static List<PositionedRow> ReadRows(Page page, double minX, double maxX)
    {
        var words = page.GetWords()
            .Select(word => new PositionedWord(
                (word.Text ?? "").Trim(),
                word.BoundingBox.Left,
                word.BoundingBox.Bottom,
                (word.BoundingBox.Left + word.BoundingBox.Right) / 2))
            .Where(word => word.Text.Length > 0 && word.CenterX >= minX && word.CenterX <= maxX)
            .OrderByDescending(word => word.Y)
            .ThenBy(word => word.X)
            .ToList();

        var rows = new List<List<PositionedWord>>();
        foreach (var word in words)
        {
            var row = rows.FirstOrDefault(candidate => Math.Abs(candidate[0].Y - word.Y) <= RowTolerance);
            if (row is null)
                rows.Add([word]);
            else
                row.Add(word);
        }

        return rows
            .Select(row => new PositionedRow(
                string.Join(" ", row.OrderBy(word => word.X).Select(word => word.Text)),
                row.Average(word => word.Y)))
            .OrderByDescending(row => row.Y)
            .ToList();
    }

    private static PositionedRow? FindRow(
        IReadOnlyList<PositionedRow> rows,
        string label,
        bool startsWith = false)
        => rows.FirstOrDefault(row => startsWith
            ? row.Text.Trim().StartsWith(label, StringComparison.OrdinalIgnoreCase)
            : row.Text.Contains(label, StringComparison.OrdinalIgnoreCase));

    private static int FindIndex(IReadOnlyList<PositionedRow> rows, Func<PositionedRow, bool> predicate)
    {
        for (var index = 0; index < rows.Count; index++)
        {
            if (predicate(rows[index]))
                return index;
        }
        return -1;
    }

    private static decimal LastMoney(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0m;
        var matches = Regex.Matches(text, MoneyPattern);
        return matches.Count == 0 ? 0m : ParseMoney(matches[^1].Value);
    }

    private static decimal MoneyAt(string text, int index)
    {
        var matches = Regex.Matches(text, MoneyPattern);
        return index >= 0 && index < matches.Count ? ParseMoney(matches[index].Value) : 0m;
    }

    private static decimal NumberAt(string text, int index)
    {
        var matches = Regex.Matches(text, @"(?<![$\d,.%])[-+]?[0-9]+(?:\.[0-9]+)?(?![\d,.%])");
        return index >= 0 && index < matches.Count ? ParseMoney(matches[index].Value) : 0m;
    }

    private static decimal PercentAt(string text, int index)
    {
        var matches = Regex.Matches(text, @"(?<value>-?[0-9]+(?:\.[0-9]+)?)%");
        return index >= 0 && index < matches.Count ? ParseMoney(matches[index].Groups["value"].Value) : 0m;
    }

    private static int FirstIntegerAfterLabel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;
        var colon = text.IndexOf(':');
        var search = colon >= 0 ? text[(colon + 1)..] : text;
        var match = Regex.Match(search, @"(?<![\d,.])(?<value>\d+)(?![\d,.])");
        return match.Success ? ParseInt(match.Groups["value"].Value) : 0;
    }

    private static int LastInteger(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;
        var matches = Regex.Matches(text, @"(?<![\d,.])(?<value>\d+)(?![\d,.])");
        return matches.Count == 0 ? 0 : ParseInt(matches[^1].Groups["value"].Value);
    }

    private static decimal ParseMoney(string value)
    {
        var normalized = value.Replace("$", "", StringComparison.Ordinal)
            .Replace(",", "", StringComparison.Ordinal)
            .Trim();
        var negative = normalized.StartsWith('(') && normalized.EndsWith(')');
        normalized = normalized.Trim('(', ')');
        return decimal.TryParse(normalized, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
            ? negative ? -parsed : parsed
            : 0m;
    }

    private static int ParseInt(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static decimal Percent(decimal numerator, decimal denominator)
        => denominator == 0m ? 0m : Math.Round(numerator / denominator * 100m, 4);

    private sealed record PositionedWord(string Text, double X, double Y, double CenterX);
    private sealed record PositionedRow(string Text, double Y);
}
