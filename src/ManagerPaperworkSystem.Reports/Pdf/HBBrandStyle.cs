using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ManagerPaperworkSystem.Reports.Pdf;

/// <summary>
/// Shared HB Brand styling for all PDF reports.
/// Colors: Black (#0B0B0F) and Gold (#D4AF37)
/// </summary>
public static class HBBrandStyle
{
    // HB Brand Colors
    public const string BrandBlack = "#0B0B0F";
    public const string BrandGold = "#D4AF37";
    public const string BrandDarkGray = "#1a1a22";
    public const string TextWhite = "#FFFFFF";
    public const string TextMuted = "#AAAAAA";
    public const string RowEven = "#FAFAFA";
    public const string RowOdd = "#FFFFFF";
    public const string BorderLight = "#DDDDDD";
    
    // Profit/Loss colors
    public const string ProfitGreen = "#059669";
    public const string LossRed = "#DC2626";

    /// <summary>
    /// Builds the standard HB Brand header for reports
    /// </summary>
    public static void BuildHeader(
        IContainer container,
        string storeName,
        string storeAddress,
        string title,
        string periodText)
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
                r.ConstantItem(240)
                    .AlignRight()
                    .Text(periodText)
                    .FontSize(11)
                    .FontColor(BrandGold);
            });
        });
    }

    /// <summary>
    /// Header cell style - Gold background with black bold text
    /// </summary>
    public static IContainer Th(IContainer c)
        => c
            .DefaultTextStyle(x => x.Bold().FontColor(Colors.Black))
            .PaddingVertical(8)
            .PaddingHorizontal(6)
            .Background(BrandGold);

    /// <summary>
    /// Data cell style with alternating background
    /// </summary>
    public static IContainer Td(IContainer c, bool isEven)
        => c
            .PaddingVertical(6)
            .PaddingHorizontal(6)
            .Background(isEven ? RowEven : RowOdd)
            .BorderBottom(1)
            .BorderColor(BorderLight);

    /// <summary>
    /// Data cell style - default white background
    /// </summary>
    public static IContainer Td(IContainer c)
        => Td(c, false);

    /// <summary>
    /// Footer/Totals cell style - Gold background with black bold text
    /// </summary>
    public static IContainer Tfoot(IContainer c)
        => c
            .DefaultTextStyle(x => x.Bold().FontColor(Colors.Black))
            .PaddingVertical(8)
            .PaddingHorizontal(6)
            .Background(BrandGold)
            .BorderTop(2)
            .BorderColor(BrandBlack);

    /// <summary>
    /// Standard page footer with page numbers
    /// </summary>
    public static void BuildFooter(IContainer container)
    {
        container.AlignCenter().Text(t =>
        {
            t.Span("Page ").FontColor(Colors.Grey.Darken1);
            t.CurrentPageNumber().FontColor(Colors.Grey.Darken1);
            t.Span(" / ").FontColor(Colors.Grey.Darken1);
            t.TotalPages().FontColor(Colors.Grey.Darken1);
        });
    }

    /// <summary>
    /// No data message
    /// </summary>
    public static void BuildNoDataMessage(IContainer container, string message = "No entries found for selected period.")
    {
        container
            .PaddingTop(30)
            .AlignCenter()
            .Text(message)
            .Italic()
            .FontColor(Colors.Grey.Darken1);
    }
}
