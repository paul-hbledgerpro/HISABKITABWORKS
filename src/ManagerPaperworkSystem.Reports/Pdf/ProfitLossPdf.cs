using System;
using System.Globalization;
using System.IO;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ManagerPaperworkSystem.Reports.Pdf;

/// <summary>
/// Profit & Loss Statement Report
/// Style 2: HB Brand with Cards
/// </summary>
public static class ProfitLossPdf
{
    public static void Generate(
        string storeName,
        string storeAddress,
        DateOnly from,
        DateOnly to,
        decimal grossSales,
        decimal salesTax,
        decimal purchases,
        decimal cashPayouts,
        decimal checkPayouts,
        string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path is required.", nameof(outputPath));

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var totalRevenue = grossSales + salesTax;
        var totalExpenses = purchases + cashPayouts + checkPayouts;
        var netProfitLoss = totalRevenue - totalExpenses;
        var isProfit = netProfitLoss >= 0;

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(30);
                page.DefaultTextStyle(x => x.FontSize(10));

                // Header
                page.Header().Element(c => BuildHeader(c, storeName, storeAddress, from, to));

                // Content
                page.Content().Element(c => BuildContent(c, grossSales, salesTax, totalRevenue,
                    purchases, cashPayouts, checkPayouts, totalExpenses, netProfitLoss, isProfit));

                // Footer
                page.Footer().Element(c => BuildFooter(c));
            });
        });

        doc.GeneratePdf(outputPath);
    }

    private static void BuildHeader(IContainer container, string storeName, string storeAddress, DateOnly from, DateOnly to)
    {
        container.Background(HBBrandStyle.BrandBlack).Padding(20).Column(col =>
        {
            col.Spacing(4);

            // Store name in gold
            col.Item().Text(storeName ?? "").Bold().FontSize(22).FontColor(HBBrandStyle.BrandGold);

            // Store address
            if (!string.IsNullOrWhiteSpace(storeAddress))
                col.Item().Text(storeAddress).FontSize(11).FontColor(HBBrandStyle.TextMuted);

            // Report title
            col.Item().PaddingTop(8).Text("PROFIT & LOSS STATEMENT")
                .FontSize(13).FontColor(HBBrandStyle.TextWhite)
                .LetterSpacing(0.1f);

            // Period
            col.Item().PaddingTop(4).Text($"{from:MMMM d, yyyy} - {to:MMMM d, yyyy}")
                .FontSize(11).FontColor(HBBrandStyle.BrandGold);
        });
    }

    private static void BuildContent(IContainer container, 
        decimal grossSales, decimal salesTax, decimal totalRevenue,
        decimal purchases, decimal cashPayouts, decimal checkPayouts, decimal totalExpenses,
        decimal netProfitLoss, bool isProfit)
    {
        container.PaddingTop(15).Column(col =>
        {
            col.Spacing(15);

            // Revenue Section
            col.Item().Element(c => BuildSection(c, "REVENUE", new[]
            {
                ("Gross Sales (Net Sales)", grossSales),
                ("Sales Tax Collected", salesTax)
            }, ("Total Revenue", totalRevenue)));

            // Expenses Section
            col.Item().Element(c => BuildSection(c, "COST OF GOODS & EXPENSES", new[]
            {
                ("Purchases / Inventory Cost", -purchases),
                ("Cash Payouts", -cashPayouts),
                ("Check Payouts", -checkPayouts)
            }, ("Total Expenses", -totalExpenses)));

            // Net Profit/Loss
            col.Item().Element(c => BuildNetResult(c, netProfitLoss, isProfit));
        });
    }

    private static void BuildSection(IContainer container, string title, 
        (string label, decimal amount)[] items, (string label, decimal amount) subtotal)
    {
        container
            .Background("#F9F9F9")
            .Border(1)
            .BorderColor("#E5E5E5")
            .Column(col =>
            {
                // Section title
                col.Item()
                    .Background(HBBrandStyle.BrandGold)
                    .Padding(10)
                    .Text(title)
                    .Bold()
                    .FontSize(11)
                    .FontColor(Colors.Black);

                // Items
                col.Item().Padding(15).Column(itemsCol =>
                {
                    foreach (var (label, amount) in items)
                    {
                        itemsCol.Item().Row(r =>
                        {
                            r.RelativeItem().PaddingLeft(10).Text(label).FontSize(10);
                            r.ConstantItem(100).AlignRight().Text(FmtMoney(amount)).FontSize(10);
                        });
                        itemsCol.Item().PaddingVertical(4);
                    }

                    // Subtotal
                    itemsCol.Item()
                        .Background("#F0F0F0")
                        .Padding(8)
                        .Row(r =>
                        {
                            r.RelativeItem().Text(subtotal.label).Bold().FontSize(10);
                            r.ConstantItem(100).AlignRight().Text(FmtMoney(subtotal.amount)).Bold().FontSize(10);
                        });
                });
            });
    }

    private static void BuildNetResult(IContainer container, decimal netProfitLoss, bool isProfit)
    {
        var resultColor = isProfit ? HBBrandStyle.ProfitGreen : HBBrandStyle.LossRed;
        var resultText = isProfit ? "NET PROFIT" : "NET LOSS";

        container
            .Background(HBBrandStyle.BrandGold)
            .Padding(15)
            .Row(r =>
            {
                r.RelativeItem().Column(c =>
                {
                    c.Item().Text("NET PROFIT / (LOSS)").Bold().FontSize(12).FontColor(Colors.Black);
                });
                r.ConstantItem(150).AlignRight().Column(c =>
                {
                    c.Item().Text(FmtMoney(netProfitLoss))
                        .Bold()
                        .FontSize(18)
                        .FontColor(resultColor);
                });
            });
    }

    private static void BuildFooter(IContainer container)
    {
        container.Column(col =>
        {
            col.Item().PaddingTop(10).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
            col.Item().PaddingTop(8).Row(r =>
            {
                r.RelativeItem().Text($"Generated on {DateTime.Now:MM/dd/yyyy hh:mm tt}")
                    .FontSize(9).FontColor(Colors.Grey.Darken1);
                r.ConstantItem(100).AlignRight().Text(t =>
                {
                    t.Span("Page ").FontSize(9).FontColor(Colors.Grey.Darken1);
                    t.CurrentPageNumber().FontSize(9).FontColor(Colors.Grey.Darken1);
                    t.Span(" / ").FontSize(9).FontColor(Colors.Grey.Darken1);
                    t.TotalPages().FontSize(9).FontColor(Colors.Grey.Darken1);
                });
            });
        });
    }

    private static string FmtMoney(decimal value)
    {
        if (value < 0)
            return $"({Math.Abs(value):C2})";
        return value.ToString("C2", CultureInfo.CurrentCulture);
    }
}
