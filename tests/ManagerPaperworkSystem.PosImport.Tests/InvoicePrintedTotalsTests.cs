using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.UI.Services;
using Xunit;

namespace ManagerPaperworkSystem.PosImport.Tests;

public sealed class InvoicePrintedTotalsTests
{
    private static InvoiceImportResult Invoice(string vendor, string footer, decimal lines, decimal total) => new()
    {
        Success = true, RawText = footer,
        Invoices = [new ImportedInvoice { VendorName = vendor, InvoiceNumber = "TEST1", InvoiceDate = new(2026,10,1), Total = total,
            Lines = [new PurchaseInvoiceLine { ProductName = "Fixture product", Quantity = 1, UnitCost = lines, Amount = lines }] }]
    };
    [Fact] public void AkTaxDiscountAndShippingReconcileWithoutChangingProductCost()
    {
        var result = Invoice("AK Wholesale Inc", "You Saved $20.00 Sub-Total $100.00\nCash Check Credit Card Wire-In Discount ($5.00)\nPromo(D/C) $0.00\nHazmat $0.00\nS & H $3.00\nIL TAX $15.00\nTotal $113.00\nPayment $113.00\nBalance $0.00", 100, 113);
        InvoicePrintedTotals.Apply(result);
        Assert.Null(InvoiceValidation.ReviewReason(result.Invoices[0]));
        Assert.Equal(100, result.Invoices[0].Lines[0].UnitCost); Assert.Equal(3, result.Invoices[0].Adjustments.Count);
    }
    [Fact] public void MissingLineIsNotHiddenByARecognizedTax()
    {
        var result = Invoice("AK Wholesale Inc", "Sub-Total $100.00\nIL TAX $15.00\nTotal $115.00", 90, 115);
        InvoicePrintedTotals.Apply(result); Assert.NotNull(InvoiceValidation.ReviewReason(result.Invoices[0]));
    }
    [Fact] public void UnknownDifferenceCannotBeInventedAsTax()
    {
        var result = Invoice("AK Wholesale Inc", "Sub-Total $100.00\nTotal $115.00", 100, 115);
        InvoicePrintedTotals.Apply(result); Assert.Empty(result.Invoices[0].Adjustments);
        Assert.NotNull(InvoiceValidation.ReviewReason(result.Invoices[0]));
    }
    [Theory]
    [InlineData("SKYGATE WHOLESALE", "Sub-Total $100.00\nIL TAX $15.00\nTotal $115.00", 115)]
    [InlineData("1 OAK WHOLESALE", "Subtotal $100.00\nShipping & Handling $5.00\nFixed Product Tax $15.00\nTax $0.00\nGrand Total (Incl.Tax) $120.00", 120)]
    [InlineData("DemandVape", "SubTotal 100.00\nFREIGHT 5.00\nILLINOIS 45% Tax 15.00", 120)]
    public void OtherKnownSupplierChargesAreVerified(string vendor, string footer, decimal total)
    {
        var result = Invoice(vendor, footer, 100, total); InvoicePrintedTotals.Apply(result);
        Assert.Null(InvoiceValidation.ReviewReason(result.Invoices[0])); Assert.Empty(result.Warnings);
    }
    [Fact] public void ConflictingPrintedTaxesRequireReview()
    {
        var result = Invoice("AK Wholesale Inc", "Sub-Total $100.00\nIL TAX $15.00\nIL TAX $30.00", 100, 115);
        InvoicePrintedTotals.Apply(result); Assert.NotEmpty(result.Warnings);
    }
}
