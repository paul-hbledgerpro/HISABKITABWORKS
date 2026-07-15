using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ManagerPaperworkSystem.Core.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ManagerPaperworkSystem.Reports.Pdf;

/// <summary>
/// Check Payouts Reports - Detail and Summary versions
/// Style: HB Brand (Black & Gold)
/// </summary>
public static class CheckPayoutsPdf
{
    #region Detail Report

    public static void Generate(string storeName, string storeAddress, DateOnly from, DateOnly to,
        IReadOnlyList<CheckPayout> entries, string outputPath)
    {
        GenerateDetail(storeName, storeAddress, from, to, entries, outputPath);
    }

    public static void GenerateDetail(string storeName, string storeAddress, DateOnly from, DateOnly to,
        IReadOnlyList<CheckPayout> entries, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path is required.", nameof(outputPath));

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var sortedEntries = (entries ?? Array.Empty<CheckPayout>()).OrderBy(x => x.Date).ToList();

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(24);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Header().Element(c =>
                    HBBrandStyle.BuildHeader(c, storeName, storeAddress, "Check Payouts - Detail Report",
                        $"Period: {from:MM/dd/yyyy} - {to:MM/dd/yyyy}"));

                page.Content().Element(c => BuildDetailTable(c, sortedEntries));

                page.Footer().Element(HBBrandStyle.BuildFooter);
            });
        });

        doc.GeneratePdf(outputPath);
    }

    private static void BuildDetailTable(IContainer container, IReadOnlyList<CheckPayout> entries)
    {
        if (entries.Count == 0)
        {
            HBBrandStyle.BuildNoDataMessage(container);
            return;
        }

        decimal totalAmount = 0;
        var clearedCount = 0;

        container.PaddingTop(8).Table(table =>
        {
            table.ColumnsDefinition(cols =>
            {
                cols.ConstantColumn(75);   // Date
                cols.ConstantColumn(80);   // Check #
                cols.RelativeColumn(1);    // Vendor
                cols.RelativeColumn(2);    // Description
                cols.ConstantColumn(80);   // Amount
                cols.ConstantColumn(55);   // Cleared
            });

            table.Header(h =>
            {
                h.Cell().Element(HBBrandStyle.Th).Text("Date");
                h.Cell().Element(HBBrandStyle.Th).Text("Check #");
                h.Cell().Element(HBBrandStyle.Th).Text("Vendor");
                h.Cell().Element(HBBrandStyle.Th).Text("Description");
                h.Cell().Element(HBBrandStyle.Th).AlignRight().Text("Amount");
                h.Cell().Element(HBBrandStyle.Th).AlignCenter().Text("Cleared");
            });

            var isEven = false;
            foreach (var e in entries)
            {
                totalAmount += e.CheckAmount;
                if (e.Cleared) clearedCount++;

                table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).Text(e.Date.ToString("MM/dd/yyyy"));
                table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).Text(e.CheckNumber ?? "");
                table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).Text(e.VendorName ?? "");
                table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).Text(e.Description ?? "");
                table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).AlignRight().Text(FmtMoney(e.CheckAmount));
                table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).AlignCenter().Text(e.Cleared ? "✓" : "");

                isEven = !isEven;
            }

            // Totals row
            table.Cell().Element(HBBrandStyle.Tfoot).Text("Totals");
            table.Cell().Element(HBBrandStyle.Tfoot).Text($"{entries.Count} checks");
            table.Cell().Element(HBBrandStyle.Tfoot).Text("");
            table.Cell().Element(HBBrandStyle.Tfoot).Text("");
            table.Cell().Element(HBBrandStyle.Tfoot).AlignRight().Text(FmtMoney(totalAmount));
            table.Cell().Element(HBBrandStyle.Tfoot).AlignCenter().Text($"{clearedCount}/{entries.Count}");
        });
    }

    #endregion

    #region Summary Report

    public static void GenerateSummary(string storeName, string storeAddress, DateOnly from, DateOnly to,
        IReadOnlyList<CheckPayout> entries, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path is required.", nameof(outputPath));

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // Ensure entries is not null
        var safeEntries = entries ?? Array.Empty<CheckPayout>();
        
        // Group by date
        var rows = safeEntries
            .GroupBy(x => x.Date)
            .Select(g => new
            {
                Date = g.Key,
                TotalAmount = g.Sum(x => x.CheckAmount),
                CheckCount = g.Count(),
                ClearedCount = g.Count(x => x.Cleared)
            })
            .OrderBy(x => x.Date)
            .ToList();

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(24);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Header().Element(c =>
                    HBBrandStyle.BuildHeader(c, storeName, storeAddress, "Check Payouts - Summary Report",
                        $"Period: {from:MM/dd/yyyy} - {to:MM/dd/yyyy}"));

                page.Content().Element(c =>
                {
                    if (rows.Count == 0)
                    {
                        HBBrandStyle.BuildNoDataMessage(c);
                        return;
                    }

                    decimal totalAmount = 0;
                    var totalChecks = 0;
                    var totalCleared = 0;

                    c.PaddingTop(8).Table(table =>
                    {
                        table.ColumnsDefinition(cols =>
                        {
                            cols.ConstantColumn(90);   // Date
                            cols.ConstantColumn(90);   // Total Amount
                            cols.ConstantColumn(70);   // # Checks
                            cols.ConstantColumn(70);   // # Cleared
                            cols.ConstantColumn(90);   // Uncleared
                        });

                        table.Header(h =>
                        {
                            h.Cell().Element(HBBrandStyle.Th).Text("Date");
                            h.Cell().Element(HBBrandStyle.Th).AlignRight().Text("Total Amount");
                            h.Cell().Element(HBBrandStyle.Th).AlignCenter().Text("# Checks");
                            h.Cell().Element(HBBrandStyle.Th).AlignCenter().Text("# Cleared");
                            h.Cell().Element(HBBrandStyle.Th).AlignRight().Text("Uncleared Amt");
                        });

                        var isEven = false;
                        foreach (var r in rows)
                        {
                            totalAmount += r.TotalAmount;
                            totalChecks += r.CheckCount;
                            totalCleared += r.ClearedCount;

                            // Calculate uncleared amount for the day
                            var dayEntries = safeEntries.Where(e => e.Date == r.Date);
                            var unclearedAmt = dayEntries.Where(e => !e.Cleared).Sum(e => e.CheckAmount);

                            table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).Text(r.Date.ToString("MM/dd/yyyy"));
                            table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).AlignRight().Text(FmtMoney(r.TotalAmount));
                            table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).AlignCenter().Text(r.CheckCount.ToString());
                            table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).AlignCenter().Text(r.ClearedCount.ToString());
                            table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).AlignRight().Text(FmtMoney(unclearedAmt));

                            isEven = !isEven;
                        }

                        var totalUncleared = safeEntries.Where(e => !e.Cleared).Sum(e => e.CheckAmount);
                        table.Cell().Element(HBBrandStyle.Tfoot).Text("Totals");
                        table.Cell().Element(HBBrandStyle.Tfoot).AlignRight().Text(FmtMoney(totalAmount));
                        table.Cell().Element(HBBrandStyle.Tfoot).AlignCenter().Text(totalChecks.ToString());
                        table.Cell().Element(HBBrandStyle.Tfoot).AlignCenter().Text(totalCleared.ToString());
                        table.Cell().Element(HBBrandStyle.Tfoot).AlignRight().Text(FmtMoney(totalUncleared));
                    });
                });

                page.Footer().Element(HBBrandStyle.BuildFooter);
            });
        });

        doc.GeneratePdf(outputPath);
    }

    #endregion

    private static string FmtMoney(decimal value)
        => value.ToString("C2", CultureInfo.CurrentCulture);
}
