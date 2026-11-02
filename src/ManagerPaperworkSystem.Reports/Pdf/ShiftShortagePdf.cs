using System.Globalization;
using ManagerPaperworkSystem.Core.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ManagerPaperworkSystem.Reports.Pdf;

public static class ShiftShortagePdf
{
    public static byte[] Generate(ShiftShortageReport report)
    {
        string Money(decimal value) => value.ToString("C2", CultureInfo.GetCultureInfo("en-US"));
        return Document.Create(document => document.Page(page => {
            page.Size(PageSizes.Letter); page.Margin(36); page.DefaultTextStyle(s => s.FontSize(11));
            page.Header().Column(column => {
                column.Item().Text("HISAB KITAB WORKS").Bold().FontSize(14).FontColor("#173B5E");
                column.Item().PaddingTop(8).Text(report.StoreName).Bold().FontSize(18);
                column.Item().Text($"Shift / Batch {report.Batch}  |  {report.Date:MM/dd/yyyy}");
            });
            page.Content().PaddingTop(20).Column(column => {
                column.Spacing(12);
                column.Item().Background("#FEF2F2").Padding(16).Column(box => {
                    box.Item().Text("CASH SHORTAGE").Bold().FontColor("#B91C1C");
                    box.Item().Text("SHORT " + Money(report.Shortage)).Bold().FontSize(28).FontColor("#B91C1C");
                    box.Item().Text("Owner's alert limit: " + Money(report.Threshold)).FontSize(10);
                });
                column.Item().Table(table => {
                    table.ColumnsDefinition(columns => { columns.RelativeColumn(); columns.RelativeColumn(2); });
                    void Row(string label, string value) {
                        table.Cell().BorderBottom(0.5f).BorderColor("#D1D5DB").PaddingVertical(7).Text(label).SemiBold();
                        table.Cell().BorderBottom(0.5f).BorderColor("#D1D5DB").PaddingVertical(7).Text(value);
                    }
                    Row("Employee on report", report.Employee);
                    Row("Cash expected", Money(report.Cash)); Row("Card", Money(report.Card));
                    Row("Gross sales", Money(report.Cash + report.Card + report.Tax));
                    Row("Net sales", Money(report.Net)); Row("Tax", Money(report.Tax));
                    Row("Cash drop entered", Money(report.Drop)); Row("Register payout", Money(report.Payout));
                    Row("Paid to / vendor", report.Payee.Length == 0 ? "—" : report.Payee);
                    Row("Purpose / reason", report.Reason.Length == 0 ? "—" : report.Reason);
                    Row("Entered by", report.EnteredBy);
                    Row("Recorded (UTC)", report.EnteredUtc.ToString("yyyy-MM-dd HH:mm:ss"));
                    if (report.IsCorrection) Row("Entry type", "Correction");
                });
                column.Item().Text("Shortage = expected cash − (cash drop + register payout). This report records the amounts at the time of entry; later corrections may change the balance.").FontSize(9).FontColor("#4B5563");
            });
            page.Footer().AlignCenter().Text("Automated shift shortage notification • HISAB KITAB WORKS").FontSize(9).FontColor("#4B5563");
        })).GeneratePdf();
    }
}
