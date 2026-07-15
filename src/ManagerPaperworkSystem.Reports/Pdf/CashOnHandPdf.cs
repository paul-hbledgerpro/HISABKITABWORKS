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
/// Cash On Hand Reports - Detail and Summary versions
/// Style: HB Brand (Black & Gold)
/// </summary>
public static class CashOnHandPdf
{
    #region Detail Report

    public static void Generate(string storeName, string storeAddress, DateOnly from, DateOnly to,
        IReadOnlyList<CashOnHandEntry> entries, string outputPath)
    {
        GenerateDetail(storeName, storeAddress, from, to, entries, outputPath);
    }

    public static void GenerateDetail(string storeName, string storeAddress, DateOnly from, DateOnly to,
        IReadOnlyList<CashOnHandEntry> entries, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path is required.", nameof(outputPath));

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var sortedEntries = (entries ?? Array.Empty<CashOnHandEntry>()).OrderBy(x => x.Date).ToList();

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(24);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Header().Element(c =>
                    HBBrandStyle.BuildHeader(c, storeName, storeAddress, "Cash On Hand - Detail Report",
                        $"Period: {from:MM/dd/yyyy} - {to:MM/dd/yyyy}"));

                page.Content().Element(c => BuildDetailTable(c, sortedEntries));

                page.Footer().Element(HBBrandStyle.BuildFooter);
            });
        });

        doc.GeneratePdf(outputPath);
    }

    private static void BuildDetailTable(IContainer container, IReadOnlyList<CashOnHandEntry> entries)
    {
        if (entries.Count == 0)
        {
            HBBrandStyle.BuildNoDataMessage(container);
            return;
        }

        decimal totalAdded = 0, totalPayout = 0;

        container.PaddingTop(8).Table(table =>
        {
            table.ColumnsDefinition(cols =>
            {
                cols.ConstantColumn(75);   // Date
                cols.ConstantColumn(70);   // Cash Added
                cols.ConstantColumn(70);   // Payout
                cols.RelativeColumn(1);    // Vendor
                cols.RelativeColumn(1);    // Purpose
                cols.RelativeColumn(2);    // Description
            });

            table.Header(h =>
            {
                h.Cell().Element(HBBrandStyle.Th).Text("Date");
                h.Cell().Element(HBBrandStyle.Th).AlignRight().Text("Cash Added");
                h.Cell().Element(HBBrandStyle.Th).AlignRight().Text("Payout");
                h.Cell().Element(HBBrandStyle.Th).Text("Vendor");
                h.Cell().Element(HBBrandStyle.Th).Text("Purpose");
                h.Cell().Element(HBBrandStyle.Th).Text("Description");
            });

            var isEven = false;
            foreach (var e in entries)
            {
                totalAdded += e.CashAdded;
                if (e.IsPayout) totalPayout += e.PayoutAmount;

                table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).Text(e.Date.ToString("MM/dd/yyyy"));
                table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).AlignRight().Text(FmtMoney(e.CashAdded));
                table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).AlignRight().Text(e.IsPayout ? FmtMoney(e.PayoutAmount) : "-");
                table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).Text(e.Vendor?.Name ?? "");
                table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).Text(e.Purpose?.Name ?? "");
                table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).Text(e.Description ?? "");

                isEven = !isEven;
            }

            // Totals row
            table.Cell().Element(HBBrandStyle.Tfoot).Text("Totals");
            table.Cell().Element(HBBrandStyle.Tfoot).AlignRight().Text(FmtMoney(totalAdded));
            table.Cell().Element(HBBrandStyle.Tfoot).AlignRight().Text(FmtMoney(totalPayout));
            table.Cell().Element(HBBrandStyle.Tfoot).Text("");
            table.Cell().Element(HBBrandStyle.Tfoot).Text("");
            table.Cell().Element(HBBrandStyle.Tfoot).AlignRight().Text($"Balance: {FmtMoney(totalAdded - totalPayout)}");
        });
    }

    #endregion

    #region Summary Report

    public static void GenerateSummary(string storeName, string storeAddress, DateOnly from, DateOnly to,
        IReadOnlyList<CashOnHandEntry> entries, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path is required.", nameof(outputPath));

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // Group by date
        var rows = (entries ?? Array.Empty<CashOnHandEntry>())
            .GroupBy(x => x.Date)
            .Select(g => new
            {
                Date = g.Key,
                CashAdded = g.Sum(x => x.CashAdded),
                Payouts = g.Where(x => x.IsPayout).Sum(x => x.PayoutAmount),
                EntryCount = g.Count()
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
                    HBBrandStyle.BuildHeader(c, storeName, storeAddress, "Cash On Hand - Summary Report",
                        $"Period: {from:MM/dd/yyyy} - {to:MM/dd/yyyy}"));

                page.Content().Element(c =>
                {
                    if (rows.Count == 0)
                    {
                        HBBrandStyle.BuildNoDataMessage(c);
                        return;
                    }

                    decimal totalAdded = 0, totalPayout = 0;

                    c.PaddingTop(8).Table(table =>
                    {
                        table.ColumnsDefinition(cols =>
                        {
                            cols.ConstantColumn(90);   // Date
                            cols.ConstantColumn(90);   // Cash Added
                            cols.ConstantColumn(90);   // Payouts
                            cols.ConstantColumn(90);   // Net
                            cols.ConstantColumn(60);   // Entries
                        });

                        table.Header(h =>
                        {
                            h.Cell().Element(HBBrandStyle.Th).Text("Date");
                            h.Cell().Element(HBBrandStyle.Th).AlignRight().Text("Cash Added");
                            h.Cell().Element(HBBrandStyle.Th).AlignRight().Text("Payouts");
                            h.Cell().Element(HBBrandStyle.Th).AlignRight().Text("Net");
                            h.Cell().Element(HBBrandStyle.Th).AlignCenter().Text("Entries");
                        });

                        var isEven = false;
                        foreach (var r in rows)
                        {
                            totalAdded += r.CashAdded;
                            totalPayout += r.Payouts;
                            var net = r.CashAdded - r.Payouts;

                            table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).Text(r.Date.ToString("MM/dd/yyyy"));
                            table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).AlignRight().Text(FmtMoney(r.CashAdded));
                            table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).AlignRight().Text(FmtMoney(r.Payouts));
                            table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).AlignRight().Text(FmtMoney(net));
                            table.Cell().Element(c => HBBrandStyle.Td(c, isEven)).AlignCenter().Text(r.EntryCount.ToString());

                            isEven = !isEven;
                        }

                        var totalNet = totalAdded - totalPayout;
                        table.Cell().Element(HBBrandStyle.Tfoot).Text("Totals");
                        table.Cell().Element(HBBrandStyle.Tfoot).AlignRight().Text(FmtMoney(totalAdded));
                        table.Cell().Element(HBBrandStyle.Tfoot).AlignRight().Text(FmtMoney(totalPayout));
                        table.Cell().Element(HBBrandStyle.Tfoot).AlignRight().Text(FmtMoney(totalNet));
                        table.Cell().Element(HBBrandStyle.Tfoot).AlignCenter().Text(rows.Sum(x => x.EntryCount).ToString());
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
