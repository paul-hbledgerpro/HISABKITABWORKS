using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace ManagerPaperworkSystem.UI.Services;

public sealed class CheckPrintRequest
{
    public DateTime Date { get; init; } = DateTime.Today;
    public string PayeeName { get; init; } = "";
    public decimal Amount { get; init; }
    public string? Address { get; init; }
    public string? Memo { get; init; }
    public string? Reference { get; init; }
}

public sealed class CheckPrintService
{
    private const string TemplateRelativePath = "Assets\\CheckPrintTemplate.xlsm";

    public void PrintCheck(CheckPrintRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.PayeeName))
            throw new InvalidOperationException("Payee name is required.");
        if (request.Amount <= 0)
            throw new InvalidOperationException("Amount must be greater than 0.");

        var templatePath = Path.Combine(AppContext.BaseDirectory, TemplateRelativePath);
        if (!File.Exists(templatePath))
            throw new FileNotFoundException("Check print template not found.", templatePath);

        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"HisabKitab_Check_{DateTime.Now:yyyyMMdd_HHmmssfff}.xlsm");

        File.Copy(templatePath, tempPath, overwrite: true);

        var excelType = Type.GetTypeFromProgID("Excel.Application");
        if (excelType is null)
            throw new InvalidOperationException(
                "Microsoft Excel is required to print checks using the Excel template. Please install Excel and try again.");

        dynamic? excel = null;
        try
        {
			excel = Activator.CreateInstance(excelType);
			if (excel is null)
			{
				throw new InvalidOperationException(
					"Microsoft Excel could not be started. Please ensure Excel is installed and try again.");
			}
			excel.Visible = false;
            excel.DisplayAlerts = false;

            dynamic wb = excel.Workbooks.Open(tempPath, ReadOnly: false);
            dynamic ws = wb.Worksheets["Sheet1"]; // template default

            // Input area (top)
            ws.Range["B2"].Value = request.PayeeName;
            ws.Range["B3"].Value = request.Amount.ToString("0.00", CultureInfo.InvariantCulture);
            ws.Range["B4"].Value = request.Memo ?? "";
            ws.Range["B6"].Value = request.Reference ?? "";
            ws.Range["B7"].Value = request.Address ?? "";

            // Printed check area (main)
            ws.Range["J9"].Value = request.Date.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
            ws.Range["B11"].Value = request.PayeeName;
            ws.Range["J11"].Value = request.Amount.ToString("0.00", CultureInfo.InvariantCulture);
            ws.Range["B12"].Value = request.Address ?? "";
            ws.Range["A13"].Value = AmountToWords(request.Amount);
            ws.Range["B17"].Value = request.Memo ?? "";

            ws.PrintOut();

            wb.Close(SaveChanges: true);
        }
        finally
        {
            try { excel?.Quit(); } catch { /* ignore */ }
            if (excel is not null)
            {
                try { Marshal.FinalReleaseComObject(excel); } catch { /* ignore */ }
            }
        }
    }

    private static string AmountToWords(decimal amount)
    {
        var dollars = (long)Math.Floor(amount);
        var cents = (int)Math.Round((amount - dollars) * 100m, 0, MidpointRounding.AwayFromZero);

        var dollarsWords = dollars == 0 ? "Zero" : NumberToWords(dollars);
        return $"{dollarsWords} and {cents:00}/100";
    }

    private static string NumberToWords(long number)
    {
        if (number == 0) return "Zero";
        if (number < 0) return "Minus " + NumberToWords(Math.Abs(number));

        string[] units =
        {
            "Zero","One","Two","Three","Four","Five","Six","Seven","Eight","Nine","Ten",
            "Eleven","Twelve","Thirteen","Fourteen","Fifteen","Sixteen","Seventeen","Eighteen","Nineteen"
        };

        string[] tens =
        {
            "Zero","Ten","Twenty","Thirty","Forty","Fifty","Sixty","Seventy","Eighty","Ninety"
        };

        string words = "";

        if ((number / 1_000_000_000) > 0)
        {
            words += NumberToWords(number / 1_000_000_000) + " Billion ";
            number %= 1_000_000_000;
        }

        if ((number / 1_000_000) > 0)
        {
            words += NumberToWords(number / 1_000_000) + " Million ";
            number %= 1_000_000;
        }

        if ((number / 1000) > 0)
        {
            words += NumberToWords(number / 1000) + " Thousand ";
            number %= 1000;
        }

        if ((number / 100) > 0)
        {
            words += NumberToWords(number / 100) + " Hundred ";
            number %= 100;
        }

        if (number > 0)
        {
            if (words != "") words += "";

            if (number < 20)
            {
                words += units[number];
            }
            else
            {
                words += tens[number / 10];
                if ((number % 10) > 0)
                    words += " " + units[number % 10];
            }
        }

        return words.Trim();
    }
}
