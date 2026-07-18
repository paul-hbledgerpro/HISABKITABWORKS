using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ManagerPaperworkSystem.Reports.Pdf;

public sealed record BankStatementReportRow(
    DateTime Date,
    string Description,
    decimal Debit,
    decimal Credit,
    string Category,
    string Source,
    bool Matched,
    string CheckNumber);

public static class BankStatementReportPdf
{
    private const string Navy = "#071D3A";
    private const string Blue = "#2769B3";
    private const string Orange = "#F58220";
    private const string Green = "#168447";
    private const string Red = "#C83B3B";
    private const string Border = "#C9D5E2";
    private const string Muted = "#60748A";

    public static void Generate(
        string storeName,
        string storeAddress,
        DateOnly from,
        DateOnly to,
        IReadOnlyList<BankStatementReportRow> rows,
        string outputPath)
    {
        var ordered = rows.OrderBy(row => row.Date).ThenBy(row => row.Description).ToList();
        var debits = ordered.Sum(row => row.Debit);
        var credits = ordered.Sum(row => row.Credit);

        Document.Create(document =>
        {
            document.Page(page =>
            {
                page.Size(PageSizes.Letter.Landscape());
                page.Margin(22);
                page.DefaultTextStyle(style => style.FontFamily(Fonts.Arial).FontSize(8).FontColor(Navy));

                page.Header().Column(column =>
                {
                    column.Item().Row(row =>
                    {
                        row.RelativeItem().Column(left =>
                        {
                            left.Item().Text(storeName.ToUpperInvariant()).FontSize(18).Bold().FontColor(Navy);
                            if (!string.IsNullOrWhiteSpace(storeAddress))
                                left.Item().Text(storeAddress).FontSize(8).FontColor(Muted);
                        });
                        row.ConstantItem(280).AlignRight().Column(right =>
                        {
                            right.Item().AlignRight().Text("BANK STATEMENT").FontSize(18).Bold().FontColor(Orange);
                            right.Item().AlignRight().Text($"{from:M/d/yyyy} – {to:M/d/yyyy}").FontSize(9).FontColor(Muted);
                        });
                    });
                    column.Item().PaddingTop(8).Height(3).Background(Orange);
                });

                page.Content().PaddingTop(12).Column(column =>
                {
                    column.Spacing(10);
                    column.Item().Row(row =>
                    {
                        SummaryCard(row, "TOTAL CREDITS", credits, Green);
                        SummaryCard(row, "TOTAL DEBITS", debits, Red);
                        SummaryCard(row, "NET CHANGE", credits - debits, credits - debits < 0 ? Red : Blue);
                        SummaryCard(row, "TRANSACTIONS", ordered.Count.ToString(CultureInfo.InvariantCulture), Navy);
                    });

                    column.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.ConstantColumn(68);
                            columns.RelativeColumn(2.5f);
                            columns.ConstantColumn(75);
                            columns.ConstantColumn(75);
                            columns.RelativeColumn(1.2f);
                            columns.RelativeColumn(1.1f);
                            columns.ConstantColumn(58);
                            columns.ConstantColumn(58);
                        });

                        Header(table, "DATE");
                        Header(table, "DESCRIPTION");
                        Header(table, "DEBIT");
                        Header(table, "CREDIT");
                        Header(table, "CATEGORY");
                        Header(table, "SOURCE");
                        Header(table, "MATCHED");
                        Header(table, "CHECK #");

                        foreach (var entry in ordered)
                        {
                            Cell(table, entry.Date.ToString("M/d/yyyy"));
                            Cell(table, entry.Description);
                            Cell(table, entry.Debit == 0 ? "" : Money(entry.Debit), entry.Debit == 0 ? Navy : Red, true);
                            Cell(table, entry.Credit == 0 ? "" : Money(entry.Credit), entry.Credit == 0 ? Navy : Green, true);
                            Cell(table, entry.Category);
                            Cell(table, entry.Source);
                            Cell(table, entry.Matched ? "Yes" : "No");
                            Cell(table, entry.CheckNumber);
                        }

                        if (ordered.Count == 0)
                        {
                            table.Cell().ColumnSpan(8).Border(1).BorderColor(Border).Padding(18)
                                .AlignCenter().Text("No bank transactions were recorded for this period.").FontColor(Muted);
                        }
                    });
                });

                page.Footer().Row(row =>
                {
                    row.RelativeItem().Text("Generated securely by HISAB KITAB WORKS").FontSize(7).FontColor(Muted);
                    row.RelativeItem().AlignRight().Text(text =>
                    {
                        text.Span("Page ").FontColor(Muted);
                        text.CurrentPageNumber().FontColor(Navy);
                        text.Span(" of ").FontColor(Muted);
                        text.TotalPages().FontColor(Navy);
                    });
                });
            });
        }).GeneratePdf(outputPath);
    }

    private static void SummaryCard(RowDescriptor row, string label, decimal value, string color)
        => SummaryCard(row, label, Money(value), color);

    private static void SummaryCard(RowDescriptor row, string label, string value, string color)
    {
        row.RelativeItem().PaddingRight(8).Border(1).BorderColor(Border).Background(Colors.White).Padding(10).Column(column =>
        {
            column.Item().Text(label).FontSize(7).Bold().FontColor(Muted);
            column.Item().PaddingTop(3).Text(value).FontSize(14).Bold().FontColor(color);
        });
    }

    private static void Header(TableDescriptor table, string text)
        => table.Cell().Background(Navy).PaddingVertical(6).PaddingHorizontal(5)
            .Text(text).FontSize(7).Bold().FontColor(Colors.White);

    private static void Cell(TableDescriptor table, string text, string color = Navy, bool right = false)
    {
        var container = table.Cell().BorderBottom(1).BorderColor(Border).PaddingVertical(5).PaddingHorizontal(5);
        if (right)
            container = container.AlignRight();
        container.Text(text ?? "").FontSize(7.5f).FontColor(color);
    }

    private static string Money(decimal value) => value.ToString("C2", CultureInfo.GetCultureInfo("en-US"));
}
