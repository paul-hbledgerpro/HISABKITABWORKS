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
/// Shift Log Reports - Detail and Summary versions
/// Style: HB Brand (Black & Gold)
/// </summary>
public static class ShiftLogPdf
{
    #region Detail Report

    public static void Generate(string storeName, string storeAddress, DateOnly from, DateOnly to,
        IReadOnlyList<ShiftLogEntry> entries, string outputPath)
    {
        GenerateDetail(storeName, storeAddress, from, to, entries, outputPath);
    }

    public static void GenerateDetail(string storeName, string storeAddress, DateOnly from, DateOnly to,
        IReadOnlyList<ShiftLogEntry> entries, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path is required.", nameof(outputPath));

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var sortedEntries = (entries ?? Array.Empty<ShiftLogEntry>()).OrderBy(x => x.Date).ThenBy(x => x.ShiftNo).ToList();

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.Letter.Landscape());
                page.Margin(20);
                page.DefaultTextStyle(x => x.FontSize(8));

                page.Header().Element(c =>
                    HBBrandStyle.BuildHeader(c, storeName, storeAddress, "Shift Log - Detail Report",
                        $"Period: {from:MM/dd/yyyy} - {to:MM/dd/yyyy}"));

                page.Content().Element(c => BuildDetailTable(c, sortedEntries));

                page.Footer().Element(HBBrandStyle.BuildFooter);
            });
        });

        doc.GeneratePdf(outputPath);
    }

    private static void BuildDetailTable(IContainer container, IReadOnlyList<ShiftLogEntry> entries)
    {
        if (entries.Count == 0)
        {
            HBBrandStyle.BuildNoDataMessage(container);
            return;
        }

        decimal totalCash = 0, totalCard = 0, totalNet = 0, totalTax = 0, totalDrop = 0, totalPayout = 0;

        container.PaddingTop(8).Table(table =>
        {
            table.ColumnsDefinition(cols =>
            {
                cols.ConstantColumn(65);   // Date
                cols.ConstantColumn(40);   // Shift #
                cols.RelativeColumn(1);    // Employee
                cols.ConstantColumn(65);   // Cash
                cols.ConstantColumn(65);   // Card
                cols.ConstantColumn(70);   // Net Sales
                cols.ConstantColumn(55);   // Tax
                cols.ConstantColumn(65);   // Cash Drop
                cols.ConstantColumn(60);   // Payout
                cols.RelativeColumn(1);    // Payout Reason
                cols.ConstantColumn(60);   // Variance
            });

            table.Header(h =>
            {
                h.Cell().Element(HBBrandStyle.Th).Text("Date");
                h.Cell().Element(HBBrandStyle.Th).Text("Shift");
                h.Cell().Element(HBBrandStyle.Th).Text("Employee");
                h.Cell().Element(HBBrandStyle.Th).AlignRight().Text("Cash");
                h.Cell().Element(HBBrandStyle.Th).AlignRight().Text("Card");
                h.Cell().Element(HBBrandStyle.Th).AlignRight().Text("Net Sales");
                h.Cell().Element(HBBrandStyle.Th).AlignRight().Text("Tax");
                h.Cell().Element(HBBrandStyle.Th).AlignRight().Text("Drop");
                h.Cell().Element(HBBrandStyle.Th).AlignRight().Text("Payout");
                h.Cell().Element(HBBrandStyle.Th).Text("Reason");
                h.Cell().Element(HBBrandStyle.Th).AlignRight().Text("Variance");
            });

            var isEven = false;
            foreach (var e in entries)
            {
                totalCash += e.CashTotal;
                totalCard += e.CardTotal;
                totalNet += e.NetSales;
                totalTax += e.Tax;
                totalDrop += e.CashDropReceived;
                totalPayout += e.RegisterPayout;

                table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).Text(e.Date.ToString("MM/dd/yy"));
                table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).Text(e.ShiftNo ?? "");
                table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).Text(e.Employee ?? "");
                table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).AlignRight().Text(FmtMoney(e.CashTotal));
                table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).AlignRight().Text(FmtMoney(e.CardTotal));
                table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).AlignRight().Text(FmtMoney(e.NetSales));
                table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).AlignRight().Text(FmtMoney(e.Tax));
                table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).AlignRight().Text(FmtMoney(e.CashDropReceived));
                table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).AlignRight().Text(FmtMoney(e.RegisterPayout));
                table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).Text(e.PayoutReason ?? "");
                table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).AlignRight().Text(FmtMoney(e.Variance));

                isEven = !isEven;
            }

            // Totals row
            table.Cell().Element(HBBrandStyle.Tfoot).Text("Totals");
            table.Cell().Element(HBBrandStyle.Tfoot).Text($"{entries.Count}");
            table.Cell().Element(HBBrandStyle.Tfoot).Text("");
            table.Cell().Element(HBBrandStyle.Tfoot).AlignRight().Text(FmtMoney(totalCash));
            table.Cell().Element(HBBrandStyle.Tfoot).AlignRight().Text(FmtMoney(totalCard));
            table.Cell().Element(HBBrandStyle.Tfoot).AlignRight().Text(FmtMoney(totalNet));
            table.Cell().Element(HBBrandStyle.Tfoot).AlignRight().Text(FmtMoney(totalTax));
            table.Cell().Element(HBBrandStyle.Tfoot).AlignRight().Text(FmtMoney(totalDrop));
            table.Cell().Element(HBBrandStyle.Tfoot).AlignRight().Text(FmtMoney(totalPayout));
            table.Cell().Element(HBBrandStyle.Tfoot).Text("");
            table.Cell().Element(HBBrandStyle.Tfoot).AlignRight().Text("");
        });
    }

    #endregion

    #region Summary Report (Same as SalesSummaryByDate)

    public static void GenerateSummary(string storeName, string storeAddress, DateOnly from, DateOnly to,
        IReadOnlyList<ShiftLogEntry> entries, string outputPath)
    {
        // Summary report groups by date - same as SalesSummaryByDatePdf
        SalesSummaryByDatePdf.Generate(storeName, storeAddress, from, to, entries, outputPath);
    }

    #endregion

    private static string FmtMoney(decimal value)
        => value.ToString("C2", CultureInfo.CurrentCulture);
}
