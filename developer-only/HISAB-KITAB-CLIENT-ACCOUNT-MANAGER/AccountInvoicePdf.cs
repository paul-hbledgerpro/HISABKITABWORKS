using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace HisabKitabWorks.ClientAccountManager.WinForms;

internal static class AccountInvoicePdf
{
    private const string Navy = "#0A3059";
    private const string Blue = "#1F5BA6";
    private const string Orange = "#F77F19";
    private const string PaleBlue = "#F1F6FC";
    private const string Ink = "#182D48";
    private const string Muted = "#586D89";
    private const string Green = "#118E4C";

    public static void Generate(InvoiceDocumentData data, string logoPath, string outputPath)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var logo = File.Exists(logoPath) ? File.ReadAllBytes(logoPath) : null;
        var dividerPath = Path.Combine(Path.GetDirectoryName(logoPath) ?? "", "InvoiceDivider.svg");
        var dividerSvg = File.Exists(dividerPath) ? File.ReadAllText(dividerPath) : "";
        Document.Create(document =>
        {
            document.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(0);
                page.DefaultTextStyle(x => x.FontFamily("Arial").FontSize(9).FontColor(Ink));
                page.Content().Row(row =>
                {
                    row.ConstantItem(175).Background(Navy).Element(c => Sidebar(c, data, logo, dividerSvg));
                    row.ConstantItem(3).Background(Orange);
                    row.RelativeItem().Background(Colors.White).Padding(30).Element(c => InvoiceBody(c, data));
                });
            });
        }).GeneratePdf(outputPath);
    }

    private static void Sidebar(IContainer container, InvoiceDocumentData data, byte[]? logo, string dividerSvg)
    {
        container.Layers(layers =>
        {
            if (!string.IsNullOrWhiteSpace(dividerSvg))
                layers.Layer().Svg(dividerSvg);
            layers.PrimaryLayer().Padding(22).Column(column =>
            {
                if (logo is not null)
                    column.Item().Height(95).AlignCenter().Image(logo).FitArea();
                column.Item().PaddingTop(10).AlignCenter().Text("HISAB KITAB WORKS").Bold().FontSize(15).FontColor(Colors.White);
                column.Item().AlignCenter().Text("SOFTWARE SUBSCRIPTIONS").FontSize(8).FontColor(Orange).LetterSpacing(.8f);
                column.Item().PaddingVertical(22).LineHorizontal(1).LineColor("#7FA3CC");

                SideHeading(column, "CLIENT ACCOUNT");
                SideValue(column, data.Account.BusinessName);
                SideValue(column, data.Account.OwnerName);
                if (!string.IsNullOrWhiteSpace(data.Account.Phone)) SideValue(column, data.Account.Phone);
                if (!string.IsNullOrWhiteSpace(data.Account.Email)) SideValue(column, data.Account.Email);

                column.Item().PaddingTop(24);
                SideHeading(column, "SUBSCRIPTION");
                SideValue(column, data.Account.StoreGuid);
                SideValue(column, $"{data.Account.MaxDevices} paid PC seat(s)");
                SideValue(column, $"{data.Account.MaxBusinesses} business slot(s)");

                column.Item().PaddingTop(24);
                SideHeading(column, "PAYMENT STATUS");
                column.Item().PaddingTop(7).Background(data.Invoice.Status == "Paid" ? Green : Orange).PaddingVertical(7)
                    .AlignCenter().Text(data.Invoice.Status.ToUpperInvariant()).Bold().FontColor(Colors.White);

                column.Item().PaddingTop(24);
                SideHeading(column, "PAYMENT METHODS");
                SideValue(column, "Check");
                SideValue(column, "Zelle");
                SideValue(column, "ACH / Bank Transfer");

                column.Item().ExtendVertical().AlignBottom().Column(bottom =>
                {
                    bottom.Item().LineHorizontal(1).LineColor("#7FA3CC");
                    bottom.Item().PaddingTop(12).Text("Prepared by").FontSize(8).FontColor("#BDD1E8");
                    bottom.Item().Text("HISAB KITAB WORKS").Bold().FontColor(Colors.White);
                });
            });
        });
    }

    private static void InvoiceBody(IContainer container, InvoiceDocumentData data)
    {
        container.Column(column =>
        {
            column.Item().AlignRight().Text("INVOICE").Bold().FontSize(28).FontColor(Navy);
            column.Item().PaddingTop(4).AlignRight().Width(230).LineHorizontal(2).LineColor(Navy);
            column.Item().PaddingTop(12).AlignRight().Width(275).Table(table =>
            {
                table.ColumnsDefinition(columns => { columns.RelativeColumn(); columns.RelativeColumn(1.25f); });
                MetaRow(table, "INVOICE #", data.Invoice.InvoiceNumber, true);
                MetaRow(table, "INVOICE DATE", data.Invoice.InvoiceDate.ToString("MM/dd/yyyy"));
                MetaRow(table, "DUE DATE", data.Invoice.DueDate.ToString("MM/dd/yyyy"));
                MetaRow(table, "STATUS", data.Invoice.Status.ToUpperInvariant(), true);
            });

            column.Item().PaddingTop(14).Row(row =>
            {
                row.RelativeItem().Background(PaleBlue).Border(1).BorderColor("#CBD9E9").Padding(11).Column(card =>
                {
                    card.Item().Text("BILL TO").Bold().FontSize(11).FontColor(Navy);
                    card.Item().PaddingTop(14).Text(data.Account.BusinessName.ToUpperInvariant()).Bold().FontSize(13);
                    if (!string.IsNullOrWhiteSpace(data.Account.StoreAddress))
                        card.Item().PaddingTop(5).Text(data.Account.StoreAddress);
                    if (!string.IsNullOrWhiteSpace(data.Account.Phone)) card.Item().Text(data.Account.Phone).FontColor(Muted);
                    if (!string.IsNullOrWhiteSpace(data.Account.Email)) card.Item().Text(data.Account.Email).FontColor(Muted);
                });
                row.ConstantItem(14);
                row.RelativeItem().Background("#FFF8EF").Border(1).BorderColor("#F4D3AD").Padding(11).Column(card =>
                {
                    card.Item().Text("BILLING PERIOD").Bold().FontSize(11).FontColor(Orange);
                    card.Item().PaddingTop(14).Text($"{data.Invoice.PeriodStart:MM/dd/yyyy} - {data.Invoice.PeriodEnd:MM/dd/yyyy}").Bold().FontSize(11);
                    card.Item().PaddingTop(6).Text("Monthly software subscription");
                    card.Item().Text(data.Account.EnabledServices.Replace(",", "  •  ")).FontColor(Muted);
                });
            });

            column.Item().PaddingTop(14).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.ConstantColumn(34);
                    columns.RelativeColumn(.85f);
                    columns.RelativeColumn(1.8f);
                    columns.ConstantColumn(78);
                });
                TableHeader(table, "QTY");
                TableHeader(table, "SERVICE");
                TableHeader(table, "DESCRIPTION");
                TableHeader(table, "AMOUNT", true);
                foreach (var item in data.Items)
                {
                    BodyCell(table, "1");
                    BodyCell(table, item.ServiceName, bold: true);
                    BodyCell(table, item.Description);
                    BodyCell(table, item.Amount.ToString("C2"), right: true, bold: true);
                }
            });

            column.Item().PaddingTop(12).AlignRight().Width(245).Background(PaleBlue).Border(1).BorderColor("#CBD9E9").Padding(10).Column(totals =>
            {
                TotalRow(totals, "SUBTOTAL", data.Invoice.Subtotal);
                TotalRow(totals, "PAYMENTS", -data.Invoice.AmountPaid);
                totals.Item().PaddingTop(7).Background(Navy).Padding(9).Row(row =>
                {
                    row.RelativeItem().Text("BALANCE DUE").Bold().FontSize(12).FontColor(Colors.White);
                    row.ConstantItem(92).AlignRight().Text(data.Invoice.BalanceDue.ToString("C2")).Bold().FontSize(13).FontColor(Colors.White);
                });
            });

            column.Item().PaddingTop(12).Border(1).BorderColor(Green).Padding(10).Row(row =>
            {
                row.ConstantItem(42).Height(42).Background(Green).AlignCenter().AlignMiddle().Text("$").Bold().FontSize(19).FontColor(Colors.White);
                row.ConstantItem(11);
                row.RelativeItem().Column(info =>
                {
                    info.Item().Text("PAYMENT INFORMATION").Bold().FontSize(12).FontColor(Navy);
                    info.Item().PaddingTop(4).Text("Please reference the invoice number with your payment. Payments recorded by the developer appear on the next exported copy.");
                });
            });

            column.Item().PaddingTop(12).Background(PaleBlue).Padding(10).Column(terms =>
            {
                terms.Item().Text("TERMS & CONDITIONS").Bold().FontSize(11).FontColor(Navy);
                terms.Item().PaddingTop(4).Text("Payment is due by the date shown above. Subscription services and licensed access are governed by the active client agreement.").FontSize(8);
            });

            column.Item().PaddingTop(12).Column(footer =>
            {
                footer.Item().LineHorizontal(1).LineColor("#CBD9E9");
                footer.Item().PaddingTop(9).AlignCenter().Text("Thank you for your business!").Bold().FontSize(15).FontColor(Navy);
                footer.Item().PaddingTop(3).AlignCenter().Text($"Generated by HISAB KITAB WORKS  •  {DateTime.Now:MM/dd/yyyy h:mm tt}").FontSize(7).FontColor(Muted);
            });
        });
    }

    private static void SideHeading(ColumnDescriptor column, string text) =>
        column.Item().Text(text).Bold().FontSize(9).FontColor(Orange);

    private static void SideValue(ColumnDescriptor column, string text) =>
        column.Item().PaddingTop(5).Text(text).FontSize(8).FontColor(Colors.White);

    private static void MetaRow(TableDescriptor table, string label, string value, bool bold = false)
    {
        table.Cell().PaddingVertical(3).Text(label).Bold().FontColor(Muted);
        var text = table.Cell().PaddingVertical(3).AlignRight().Text(value);
        if (bold) text.Bold();
    }

    private static void TableHeader(TableDescriptor table, string value, bool right = false)
    {
        var cell = table.Cell().Background(Navy).PaddingVertical(8).PaddingHorizontal(6);
        var aligned = right ? cell.AlignRight() : cell;
        aligned.Text(value).Bold().FontColor(Colors.White);
    }

    private static void BodyCell(TableDescriptor table, string value, bool right = false, bool bold = false)
    {
        var cell = table.Cell().BorderBottom(1).BorderColor("#D5DFEB").PaddingVertical(7).PaddingHorizontal(6);
        var aligned = right ? cell.AlignRight() : cell;
        var text = aligned.Text(value);
        if (bold) text.Bold();
    }

    private static void TotalRow(ColumnDescriptor column, string label, decimal amount) =>
        column.Item().PaddingVertical(4).Row(row =>
        {
            row.RelativeItem().Text(label).Bold();
            row.ConstantItem(95).AlignRight().Text(amount.ToString("C2")).Bold();
        });
}
