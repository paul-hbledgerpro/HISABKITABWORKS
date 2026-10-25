using System.Globalization;
using System.Text.RegularExpressions;

namespace ManagerPaperworkSystem.UI.Services;

public sealed record InvoiceAdjustment(string Label, decimal Amount);

// Only printed, labelled amounts from known supplier footers may reconcile a total.
// Never invent an adjustment from the difference between lines and the invoice total.
public static class InvoicePrintedTotals
{
    public static void Apply(InvoiceImportResult result)
    {
        if (!result.Success || result.Invoices.Count != 1) return;
        var invoice = result.Invoices[0];
        var vendor = invoice.VendorName.ToUpperInvariant();
        var ak = Regex.IsMatch(vendor, @"\bAK\s+WHOLESALE\b"); var sky = vendor.Contains("SKYGATE");
        var oak = vendor.Contains("1 OAK") || vendor.Contains("1OAK"); var demand = vendor.Contains("DEMANDVAPE");
        if (!ak && !sky && !oak && !demand) return;
        const string money = @"\s*[:]?\s*(?<open>\()?\s*\$?\s*(?<amount>-?[0-9][0-9,]*\.[0-9]{2})\s*\)?\s*$";
        var subtotalMatches = Regex.Matches(result.RawText, @"(?im)\bSub[- ]?Total" + money).Cast<Match>().ToList();
        if (subtotalMatches.Count == 0) return;
        var subtotals = subtotalMatches.Select(Parse).Distinct().ToList();
        if (subtotals.Count != 1) { result.Warnings.Add("Conflicting printed subtotals need review."); return; }
        invoice.PrintedSubtotal = subtotals[0];
        var footer = result.RawText[subtotalMatches.Last().Index..];
        var labels = ak || sky
            ? new[] { ("Discount", @"\bDiscount", true), ("Promo discount", @"^Promo\(D/C\)", true),
                ("Hazmat", @"^Hazmat", false), ("Shipping", @"\bS\s*&\s*H", false),
                ("IL tax", @"^IL TAX", false), ("CT tax", @"\bCT Tax", false) }
            : oak ? new[] { ("Shipping", @"^Shipping\s*&\s*Handling", false), ("Fixed product tax", @"^Fixed Product Tax", false),
                ("Tax", @"^Tax", false), ("Discount", @"^Discount", true) }
            : new[] { ("Freight", @"^FREIGHT", false), ("Illinois tax", @"^ILLINOIS 45% Tax", false),
                ("ML tax", @"\bML TAX", false) };
        foreach (var (label, pattern, discount) in labels)
        {
            var matches = Regex.Matches(footer, "(?im)" + pattern + money).Cast<Match>().ToList();
            var amounts = matches.Select(Parse).Distinct().ToList();
            if (amounts.Count > 1) { result.Warnings.Add($"Conflicting {label} amounts need review."); continue; }
            if (amounts.Count == 1 && amounts[0] != 0)
                invoice.Adjustments.Add(new InvoiceAdjustment(label, discount ? -Math.Abs(amounts[0]) : amounts[0]));
        }
    }
    private static decimal Parse(Match m)
    {
        var value = decimal.Parse(m.Groups["amount"].Value, NumberStyles.Number, CultureInfo.InvariantCulture);
        return m.Groups["open"].Success ? -Math.Abs(value) : value;
    }
}
