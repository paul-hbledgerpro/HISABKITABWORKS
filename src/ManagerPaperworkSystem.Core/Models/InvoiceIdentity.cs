using System.Text.RegularExpressions;

namespace ManagerPaperworkSystem.Core.Models;

public static class InvoiceIdentity
{
    public static string Key(string? vendor, string? number) => Normalize(vendor) + "|" + Normalize(number);
    private static string Normalize(string? value) => new((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    // Compare like packaging only. Unknown packaging keeps the full description; never
    // infer a carton-to-unit conversion from an ambiguous invoice description.
    public static string ProductKey(string? code, string? description)
    {
        var name = Regex.Replace((description ?? "").Trim().ToUpperInvariant(), @"\s+", " ");
        var item = Regex.Replace((code ?? "").Trim().ToUpperInvariant(), @"\s+", "");
        var pack = Regex.Match(name, @"\b\d+\s*(?:PACK|PK|CT|COUNT|PCS|PIECES)\b|\b\d+\s*[X×]\s*\d+(?:\.\d+)?\s*(?:ML|OZ|G)\b").Value;
        var key = item.Length is 8 or 12 or 13 or 14 && item.All(char.IsDigit) && pack.Length > 0 ? $"PACK:{item}|{pack.Replace(" ", "")}" : item.Length > 0 ? $"{item}|{name}" : name;
        return key.Length <= 260 ? key : key[..260];
    }
}

public sealed class DuplicateInvoiceException : InvalidOperationException
{
    public bool Conflicting { get; }
    public DuplicateInvoiceException(bool conflicting) : base(conflicting
        ? "This supplier and invoice number already exist with different values. Review both invoices before making changes."
        : "Already Imported: this supplier and invoice number are already recorded for this store.") => Conflicting = conflicting;
}
