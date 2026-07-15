using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace ManagerPaperworkSystem.UI.Services;

public sealed record PosReportData(
    DateOnly? ReportDate,
    string? Employee,
    string? ShiftOrBatch,
    decimal CashTotal,
    decimal CardTotal,
    decimal NetSales,
    decimal TaxTotal,
    string DetectedType
);

/// <summary>
/// Imports POS reports from either XLSX or PDF.
/// Supports two layouts based on the user's sample files:
/// - End Of Shift Report
/// - Z Report
///
/// NOTE: The importer is robust by scanning for labels instead of hard-coded cells.
/// </summary>
public sealed class PosReportImportService
{
    public PosReportData Import(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".xlsx" => ImportXlsx(filePath),
            ".pdf" => ImportPdf(filePath),
            _ => throw new NotSupportedException("Only .xlsx and .pdf are supported.")
        };
    }

    private PosReportData ImportXlsx(string filePath)
    {
        using var wb = new XLWorkbook(filePath);
        var ws = wb.Worksheets.First();
        var sheetName = ws.Name ?? "";

        // Heuristics: End-of-shift has very clear headers.
        var anyCellText = SafeCellText(ws, 6, 1);
        var headerIsEndShift = sheetName.Contains("End Of Shift", StringComparison.OrdinalIgnoreCase)
                               || anyCellText.Contains("End Of Shift Report", StringComparison.OrdinalIgnoreCase);

        // Z report sample has a big header blob in A1.
        var headerA1 = SafeCellText(ws, 1, 1);
        var headerIsZ = headerA1.Contains("Z-", StringComparison.OrdinalIgnoreCase) || headerA1.Contains("Z Report", StringComparison.OrdinalIgnoreCase);

        if (headerIsEndShift) return ParseEndOfShiftFromXlsx(ws);
        if (headerIsZ) return ParseZReportFromXlsx(ws);

        // Fallback: try by presence of known labels.
        if (FindCellContaining(ws, "Shift No") is not null && FindCellContaining(ws, "Total Cash") is not null)
            return ParseEndOfShiftFromXlsx(ws);
        if (FindCellContaining(ws, "Credit/Debit") is not null && FindCellContaining(ws, "Cash") is not null)
            return ParseZReportFromXlsx(ws);

        throw new Exception("Could not identify POS report type from XLSX.");
    }

    private PosReportData ImportPdf(string filePath)
    {
        var text = PdfTextExtractor.Extract(filePath);
        if (text.IndexOf("End Of Shift Report", StringComparison.OrdinalIgnoreCase) >= 0)
            return ParseEndOfShiftFromText(text);
        if (text.IndexOf("Z-", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Z Report", StringComparison.OrdinalIgnoreCase) >= 0)
            return ParseZReportFromText(text);

        // Fallback by labels
        if (text.IndexOf("Shift No", StringComparison.OrdinalIgnoreCase) >= 0 && text.IndexOf("Total Cash", StringComparison.OrdinalIgnoreCase) >= 0)
            return ParseEndOfShiftFromText(text);
        if (text.IndexOf("Credit/Debit", StringComparison.OrdinalIgnoreCase) >= 0 && text.IndexOf("Net Sales", StringComparison.OrdinalIgnoreCase) >= 0)
            return ParseZReportFromText(text);

        throw new Exception("Could not identify POS report type from PDF.");
    }

    // -------------------- XLSX PARSERS --------------------

    private static PosReportData ParseZReportFromXlsx(IXLWorksheet ws)
    {
        var hdr1 = SafeCellText(ws, 1, 1);
        var hdr2 = SafeCellText(ws, 2, 1);
        var header = (hdr1 + "\n" + hdr2).Replace("\r", "");

        var batch = RegexMatch1(header, @"Batch:\s*([0-9]+)") ?? "";
        
        // Employee: extract just the username after "User:" - stop at whitespace or newline
        var employee = RegexMatch1(header, @"User:\s*([A-Za-z0-9_]+)")?.Trim();
        
        var startDate = ParseDateOnly(RegexMatch1(header, @"Start Date:\s*([0-9]{1,2}/[0-9]{1,2}/[0-9]{2,4})")) ??
                        ParseDateOnlyFromHuman(RegexMatch0(header, @"Start\s*(?:Day|Date)\s*:\s*([^\n\r]+)"));
        var endDate = ParseDateOnly(RegexMatch1(header, @"End Date:\s*([0-9]{1,2}/[0-9]{1,2}/[0-9]{2,4})"));

        var netSales = MoneyOrZero(RegexMatch1(header, @"Net Sales:\s*\$?\s*([0-9,]+\.[0-9]{2})"));

        // Tax: look for "T=Sales Tax (8.5000): 188.10" pattern first
        var tax = 0m;
        var mTax = Regex.Match(header, @"T=Sales Tax\s*\([^)]+\):\s*([0-9,]+\.[0-9]{2})", RegexOptions.IgnoreCase);
        if (mTax.Success)
        {
            tax = MoneyOrZero(mTax.Groups[1].Value);
        }
        else
        {
            // Fallback: "State Sales Tax (10.500 54.12" -> capture second number
            mTax = Regex.Match(header.Replace("\n", " "), @"State Sales Tax\s*\(\s*([0-9.]+)\s+([0-9.]+)", RegexOptions.IgnoreCase);
            if (mTax.Success) tax = MoneyOrZero(mTax.Groups[2].Value);
        }

        // Cash total: Look for "CASH (" pattern specifically (under AMOUNT RECEIVED BY TENDER)
        // NOT Opening Balance
        var cashTotal = 0m;
        var cashCell = FindCellContainingPattern(ws, @"CASH\s*\(\s*\d+\s*\)");
        if (cashCell != null)
        {
            cashTotal = FindMoneyOnRow(ws, "CASH") ?? 0m;
        }
        else
        {
            // Fallback: try "Cash Sales (+):" 
            cashTotal = FindMoneyOnRow(ws, "Cash Sales") ?? 0m;
        }
        
        var cardTotal = FindMoneyOnRow(ws, "Credit/Debit") ?? 0m;

        return new PosReportData(
            startDate ?? endDate,
            employee,
            string.IsNullOrWhiteSpace(batch) ? null : batch,
            cashTotal,
            cardTotal,
            netSales,
            tax,
            "Z Report"
        );
    }

    private static PosReportData ParseEndOfShiftFromXlsx(IXLWorksheet ws)
    {
        var shiftNo = RegexMatch1(FindTextContaining(ws, "Shift No"), @"Shift\s*No\.?\s*:\s*([A-Za-z0-9\-]+)");
        var emp = RegexMatch1(FindTextContaining(ws, "Employee Name"), @"Employee\s*Name\s*:\s*(.+)")?.Trim();

        // Date: always prefer shift start date ("FROM:") before the end date ("TO:").
        var toText = FindTextContaining(ws, "TO:");
        var fromText = FindTextContaining(ws, "FROM:");
        var date = ParseDateOnlyFromHuman(fromText) ?? ParseDateOnlyFromHuman(toText);

        var cashTotal = FindMoneyInSameRow(ws, "Total Cash") ?? FindMoneyInSameRow(ws, "CASH") ?? 0m;
        var cardTotal = FindMoneyInSameRow(ws, "Total Card") ?? FindMoneyInSameRow(ws, "CARD") ?? 0m;
        var netSales = FindMoneyInSameRow(ws, "Net Revenue") ?? FindMoneyInSameRow(ws, "Taxable") ?? 0m;
        var tax = FindMoneyInSameRow(ws, "Total Tax Amount") ?? 0m;

        return new PosReportData(
            date,
            emp,
            shiftNo,
            cashTotal,
            cardTotal,
            netSales,
            tax,
            "End Of Shift Report"
        );
    }

    // -------------------- TEXT (PDF) PARSERS --------------------

    private static PosReportData ParseZReportFromText(string text)
    {
        var batch = RegexMatch1(text, @"Batch:\s*([0-9]+)");
        
        // Employee: extract just the username after "User:" 
        // The PDF text often concatenates "User: HARINI" with "Start Day:" as "HARINIStart"
        // We need to split at known keywords like "Start", "Day", weekday names, etc.
        string? employee = null;
        var userMatch = Regex.Match(text, @"User:\s*([A-Za-z0-9_]+)", RegexOptions.IgnoreCase);
        if (userMatch.Success)
        {
            var rawName = userMatch.Groups[1].Value.Trim();
            
            // Check if name contains "Start" concatenated (like "HARINIStart")
            // Split at common keywords that might be concatenated
            var splitKeywords = new[] { "Start", "Day", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday", "End", "Date", "Batch", "From", "To" };
            foreach (var keyword in splitKeywords)
            {
                var idx = rawName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
                if (idx > 0) // Must be after the first character
                {
                    rawName = rawName.Substring(0, idx);
                    break;
                }
            }
            employee = rawName.Trim();
        }
        
        var startDate = ParseDateOnly(RegexMatch1(text, @"Start Date:\s*([0-9]{1,2}/[0-9]{1,2}/[0-9]{2,4})")) ??
                        ParseDateOnlyFromHuman(RegexMatch0(text, @"Start\s*(?:Day|Date)\s*:\s*([^\n\r]+)"));
        var endDate = ParseDateOnly(RegexMatch1(text, @"End Date:\s*([0-9]{1,2}/[0-9]{1,2}/[0-9]{2,4})"));
        var netSales = MoneyOrZero(RegexMatch1(text, @"Net Sales:\s*\$?\s*([0-9,]+\.[0-9]{2})"));

        // Tax: look for pattern like "T=Sales Tax (8.5000): 188.10" or "State Sales Tax"
        var tax = 0m;
        var mTax = Regex.Match(text, @"T=Sales Tax\s*\([^)]+\):\s*([0-9,]+\.[0-9]{2})", RegexOptions.IgnoreCase);
        if (mTax.Success)
        {
            tax = MoneyOrZero(mTax.Groups[1].Value);
        }
        else
        {
            mTax = Regex.Match(text.Replace("\n", " "), @"State Sales Tax\s*\(\s*([0-9.]+)\s+([0-9.]+)", RegexOptions.IgnoreCase);
            if (mTax.Success) tax = MoneyOrZero(mTax.Groups[2].Value);
        }

        // Cash total: Look specifically for "CASH (" pattern under "AMOUNT RECEIVED BY TENDER" section
        // Format is like "CASH (23) 632.11" - we want 632.11, NOT the Opening Balance
        var cashTotal = 0m;
        var cashMatch = Regex.Match(text, @"CASH\s*\(\s*\d+\s*\)\s*([0-9,]+\.[0-9]{2})", RegexOptions.IgnoreCase);
        if (cashMatch.Success)
        {
            cashTotal = MoneyOrZero(cashMatch.Groups[1].Value);
        }
        else
        {
            // Fallback: try "Cash Sales (+):" pattern
            var cashSalesMatch = Regex.Match(text, @"Cash Sales\s*\(\+\):\s*([0-9,]+\.[0-9]{2})", RegexOptions.IgnoreCase);
            if (cashSalesMatch.Success)
            {
                cashTotal = MoneyOrZero(cashSalesMatch.Groups[1].Value);
            }
        }
        
        // Card total: use Credit/Debit total
        var cardTotal = MoneyOrZero(RegexMatch1(text, @"Credit/Debit:?\s*\$?\s*([0-9,]+\.[0-9]{2})"));

        return new PosReportData(startDate ?? endDate, employee, batch, cashTotal, cardTotal, netSales, tax, "Z Report");
    }

    private static PosReportData ParseEndOfShiftFromText(string text)
    {
        var shiftNo = RegexMatch1(text, @"Shift\s*No\.?\s*:\s*([A-Za-z0-9\-]+)");
        var emp = RegexMatch1(text, @"Employee\s*Name\s*:\s*(.+)")?.Trim();

        // Date: always prefer shift start date (FROM) before shift end date (TO).
        var date = ParseDateOnlyFromHuman(RegexMatch0(text, @"FROM:\s*([^\n\r]+)")) ??
                   ParseDateOnlyFromHuman(RegexMatch0(text, @"TO:\s*([^\n\r]+)"));

        var cashTotal = MoneyOrZero(RegexMatch1(text, @"Total\s*Cash\s*\$?\s*([0-9,]+\.[0-9]{2})"));
        var cardTotal = MoneyOrZero(RegexMatch1(text, @"Total\s*Card\s*\$?\s*([0-9,]+\.[0-9]{2})"));
        var netSales = MoneyOrZero(RegexMatch1(text, @"Net\s*Revenue\s*\$?\s*([0-9,]+\.[0-9]{2})"));
        var tax = MoneyOrZero(RegexMatch1(text, @"Total\s*Tax\s*Amount.*?\$?\s*([0-9,]+\.[0-9]{2})"));

        return new PosReportData(date, emp, shiftNo, cashTotal, cardTotal, netSales, tax, "End Of Shift Report");
    }

    // -------------------- HELPERS --------------------

    private static string SafeCellText(IXLWorksheet ws, int row, int col)
    {
        try { return ws.Cell(row, col).GetString() ?? ""; }
        catch { return ""; }
    }

    private static string FindTextContaining(IXLWorksheet ws, string token)
    {
        var cell = FindCellContaining(ws, token);
        return cell?.GetString() ?? "";
    }

    private static IXLCell? FindCellContaining(IXLWorksheet ws, string token)
    {
        foreach (var c in ws.CellsUsed())
        {
            var s = c.GetString();
            if (string.IsNullOrWhiteSpace(s)) continue;
            if (s.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return c;
        }
        return null;
    }

    private static IXLCell? FindCellContainingPattern(IXLWorksheet ws, string pattern)
    {
        foreach (var c in ws.CellsUsed())
        {
            var s = c.GetString();
            if (string.IsNullOrWhiteSpace(s)) continue;
            if (Regex.IsMatch(s, pattern, RegexOptions.IgnoreCase)) return c;
        }
        return null;
    }

    private static decimal? FindMoneyInSameRow(IXLWorksheet ws, string labelToken)
    {
        var labelCell = FindCellContaining(ws, labelToken);
        if (labelCell is null) return null;

        var row = labelCell.Address.RowNumber;
        var startCol = labelCell.Address.ColumnNumber;
        for (var c = startCol; c <= startCol + 10; c++)
        {
            var v = ws.Cell(row, c).Value;
            if (TryMoney(v, out var money)) return money;
        }
        return null;
    }

    private static decimal? FindMoneyOnRow(IXLWorksheet ws, string labelToken)
    {
        var labelCell = FindCellContaining(ws, labelToken);
        if (labelCell is null) return null;
        var row = labelCell.Address.RowNumber;
        var startCol = labelCell.Address.ColumnNumber;
        for (var c = startCol; c <= startCol + 10; c++)
        {
            var v = ws.Cell(row, c).Value;
            if (TryMoney(v, out var money)) return money;
        }
        return null;
    }

    private static bool TryMoney(XLCellValue v, out decimal money)
    {
        money = 0m;
        if (v.IsNumber)
        {
            money = Convert.ToDecimal(v.GetNumber(), CultureInfo.InvariantCulture);
            return true;
        }
        if (!v.IsText) return false;

        var s = v.GetText();
        return TryMoney(s, out money);
    }

    private static bool TryMoney(string? s, out decimal money)
    {
        money = 0m;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Replace("$", "").Replace(",", "").Trim();
        return decimal.TryParse(s, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out money)
               || decimal.TryParse(s, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.CurrentCulture, out money);
    }

    private static decimal MoneyOrZero(string? s)
        => TryMoney(s, out var d) ? d : 0m;

    private static string? RegexMatch1(string input, string pattern)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var m = Regex.Match(input, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? RegexMatch0(string input, string pattern)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var m = Regex.Match(input, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static DateOnly? ParseDateOnly(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out var dt))
            return DateOnly.FromDateTime(dt);
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            return DateOnly.FromDateTime(dt);
        return null;
    }

    private static DateOnly? ParseDateOnlyFromHuman(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        // Examples: "TO:  Dec 24, 2025 11:59 PM" or "FROM:  Dec 24, 2025 12:00 AM"
        var m = Regex.Match(s, @"([A-Za-z]{3,9})\s+([0-9]{1,2}),\s*([0-9]{4})", RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            // fallback
            return ParseDateOnly(s);
        }
        var datePart = $"{m.Groups[1].Value} {m.Groups[2].Value}, {m.Groups[3].Value}";
        return ParseDateOnly(datePart);
    }
}

internal static class PdfTextExtractor
{
    public static string Extract(string filePath)
    {
        // UglyToad.PdfPig reads text for text-based PDFs.
        // If the PDF is a scanned image, it will contain little/no text.
        using var doc = UglyToad.PdfPig.PdfDocument.Open(filePath);
        var sb = new System.Text.StringBuilder();
        foreach (var page in doc.GetPages())
        {
            sb.AppendLine(page.Text);
        }
        return sb.ToString();
    }
}
