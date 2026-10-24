namespace ManagerPaperworkSystem.UI.Services;

public static class InvoiceValidation
{
    public static string? ReviewReason(ImportedInvoice invoice)
    {
        if (string.IsNullOrWhiteSpace(invoice.VendorName) || invoice.VendorName.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(invoice.InvoiceNumber) || invoice.InvoiceDate is null || invoice.Total is null or <= 0
            || invoice.Lines.Count == 0) return "Confirm supplier, invoice number, date, total and line items against the PDF.";
        if (invoice.Lines.Any(x => string.IsNullOrWhiteSpace(x.ProductName) || x.Quantity <= 0 || x.UnitCost <= 0))
            return "One or more product quantities or costs need review.";
        var subtotal = invoice.Lines.Sum(x => x.Amount ?? decimal.Round(x.Quantity * x.UnitCost, 2));
        if (Math.Abs(subtotal - invoice.Total.Value) > 0.02m)
            return $"Line items total {subtotal:N2}; invoice total {invoice.Total:N2}. Confirm taxes, shipping, discounts and missing lines.";
        return null;
    }
}
