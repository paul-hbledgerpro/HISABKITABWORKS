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
/// Sales Summary by Date:
/// Groups Shift Log entries by date and totals Cash, Card, Net Sales, Tax, and Cash Drop.
/// Style: HB Brand (Black & Gold)
/// </summary>
public static class SalesSummaryByDatePdf
{
    // HB Brand Colors
    private const string BrandBlack = "#0B0B0F";
    private const string BrandGold = "#D4AF37";
    private const string BrandDarkGray = "#1a1a22";
    private const string TextWhite = "#FFFFFF";
    private const string TextMuted = "#AAAAAA";

    private sealed record Row(DateOnly Date, decimal Cash, decimal Card, decimal Net, decimal Tax, decimal Drop);

    public static void Generate(
        string storeName,
        string storeAddress,
        DateOnly from,
        DateOnly to,
        IReadOnlyList<ShiftLogEntry> entries,
        string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path is required.", nameof(outputPath));

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var rows = (entries ?? Array.Empty<ShiftLogEntry>())
            .GroupBy(x => x.Date)
            .Select(g => new Row(
                g.Key,
                g.Sum(x => x.CashTotal),
                g.Sum(x => x.CardTotal),
                g.Sum(x => x.NetSales),
                g.Sum(x => x.Tax),
                g.Sum(x => x.CashDropReceived)))
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
                    BuildHeader(c, storeName, storeAddress, from, to, "Sales Summary by Date"));

                page.Content().Element(c => BuildTable(c, rows));

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Page ").FontColor(Colors.Grey.Darken1);
                    t.CurrentPageNumber().FontColor(Colors.Grey.Darken1);
                    t.Span(" / ").FontColor(Colors.Grey.Darken1);
                    t.TotalPages().FontColor(Colors.Grey.Darken1);
                });
            });
        });

        doc.GeneratePdf(outputPath);
    }

    private static void BuildHeader(
        IContainer container,
        string storeName,
        string storeAddress,
        DateOnly from,
        DateOnly to,
        string title)
    {
        container.Background(BrandBlack).Padding(16).Column(col =>
        {
            col.Spacing(4);

            // Store name in gold
            col.Item().Text(storeName ?? "").Bold().FontSize(20).FontColor(BrandGold);

            // Store address in muted gray
            if (!string.IsNullOrWhiteSpace(storeAddress))
                col.Item().Text(storeAddress).FontSize(11).FontColor(TextMuted);

            col.Item().PaddingTop(10).Row(r =>
            {
                // Report title in white
                r.RelativeItem().Text(title).SemiBold().FontSize(14).FontColor(TextWhite);
                
                // Period in gold
                r.ConstantItem(220)
                    .AlignRight()
                    .Text($"Period: {from:MM/dd/yyyy} - {to:MM/dd/yyyy}")
                    .FontSize(11)
                    .FontColor(BrandGold);
            });
        });
    }

    private static void BuildTable(IContainer container, IReadOnlyList<Row> rows)
    {
        if (rows.Count == 0)
        {
            container
                .PaddingTop(30)
                .AlignCenter()
                .Text("No entries found for selected period.")
                .Italic()
                .FontColor(Colors.Grey.Darken1);
            return;
        }

        decimal totalCash = 0, totalCard = 0, totalNet = 0, totalTax = 0, totalDrop = 0;

        container.PaddingTop(8).Table(table =>
        {
            table.ColumnsDefinition(cols =>
            {
                cols.ConstantColumn(90);   // Date
                cols.ConstantColumn(80);   // Cash
                cols.ConstantColumn(80);   // Card
                cols.ConstantColumn(85);   // Net Sales
                cols.ConstantColumn(70);   // Tax
                cols.ConstantColumn(80);   // Cash Drop
            });

            // Header row with gold background
            table.Header(h =>
            {
                h.Cell().Element(Th).Text("Date");
                h.Cell().Element(Th).AlignRight().Text("Cash");
                h.Cell().Element(Th).AlignRight().Text("Card");
                h.Cell().Element(Th).AlignRight().Text("Net Sales");
                h.Cell().Element(Th).AlignRight().Text("Tax");
                h.Cell().Element(Th).AlignRight().Text("Cash Drop");
            });

            // Data rows with alternating backgrounds
            var isEven = false;
            foreach (var r in rows)
            {
                totalCash += r.Cash;
                totalCard += r.Card;
                totalNet += r.Net;
                totalTax += r.Tax;
                totalDrop += r.Drop;

                var bgColor = isEven ? "#FAFAFA" : "#FFFFFF";
                
                table.Cell().Element(c => Td(c, bgColor)).Text($"{r.Date:MM/dd/yyyy}");
                table.Cell().Element(c => Td(c, bgColor)).AlignRight().Text(FmtMoney(r.Cash));
                table.Cell().Element(c => Td(c, bgColor)).AlignRight().Text(FmtMoney(r.Card));
                table.Cell().Element(c => Td(c, bgColor)).AlignRight().Text(FmtMoney(r.Net));
                table.Cell().Element(c => Td(c, bgColor)).AlignRight().Text(FmtMoney(r.Tax));
                table.Cell().Element(c => Td(c, bgColor)).AlignRight().Text(FmtMoney(r.Drop));
                
                isEven = !isEven;
            }

            // Totals row with gold background and BLACK BOLD text
            table.Cell().Element(Tfoot).Text("Totals").Bold().FontColor(Colors.Black);
            table.Cell().Element(Tfoot).AlignRight().Text(FmtMoney(totalCash)).Bold().FontColor(Colors.Black);
            table.Cell().Element(Tfoot).AlignRight().Text(FmtMoney(totalCard)).Bold().FontColor(Colors.Black);
            table.Cell().Element(Tfoot).AlignRight().Text(FmtMoney(totalNet)).Bold().FontColor(Colors.Black);
            table.Cell().Element(Tfoot).AlignRight().Text(FmtMoney(totalTax)).Bold().FontColor(Colors.Black);
            table.Cell().Element(Tfoot).AlignRight().Text(FmtMoney(totalDrop)).Bold().FontColor(Colors.Black);
        });
    }

    private static string FmtMoney(decimal value)
        => value.ToString("C2", CultureInfo.CurrentCulture);

    // Header cell style - Gold background with black text
    private static IContainer Th(IContainer c)
        => c
            .DefaultTextStyle(x => x.Bold().FontColor(Colors.Black))
            .PaddingVertical(8)
            .PaddingHorizontal(6)
            .Background(BrandGold);

    // Data cell style - with alternating background
    private static IContainer Td(IContainer c, string bgColor)
        => c
            .PaddingVertical(6)
            .PaddingHorizontal(6)
            .Background(bgColor)
            .BorderBottom(1)
            .BorderColor("#DDDDDD");

    // Footer/Totals cell style - Gold background
    private static IContainer Tfoot(IContainer c)
        => c
            .PaddingVertical(8)
            .PaddingHorizontal(6)
            .Background(BrandGold)
            .BorderTop(2)
            .BorderColor(BrandBlack);
}
