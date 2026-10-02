using System.Globalization;
using System.Text;

namespace ManagerPaperworkSystem.WinForms;

internal static class PortalStoreIsolationPolicy
{
    public static string Normalize(string? value) => new((value ?? "")
        .Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))
        .Select(char.ToUpperInvariant).ToArray());

    public static int SelectExactStore(string configuredName, IReadOnlyList<string> names)
    {
        var wanted = Normalize(configuredName);
        var matches = Enumerable.Range(0, names.Count)
            .Where(i => wanted.Length > 0 && Normalize(names[i]) == wanted).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException(
                $"Store verification failed: '{configuredName}' must match exactly one AdventPOS store. " +
                "Open POS Auto Sync Setup and copy the full name from the portal store list. No reports were imported.");
        return matches[0];
    }

    public static void ValidateSummaryStore(string reportedName, string portalStoreName)
    {
        var actual = Normalize(reportedName);
        var expected = Normalize(portalStoreName);
        // AdventPOS summary headers omit the parenthesized city/state shown in
        // the store picker. Only allow that precise omission after fresh login
        // and an exact store selection; never allow arbitrary substring matches.
        var suffix = portalStoreName.LastIndexOf('(');
        var headerName = suffix > 0 && portalStoreName.TrimEnd().EndsWith(')')
            ? Normalize(portalStoreName[..suffix]) : expected;
        if (actual.Length == 0 || expected.Length == 0 ||
            (actual != expected && actual != headerName))
            throw new InvalidOperationException(
                $"Store verification failed: the report says '{reportedName}', but sync is configured for '{portalStoreName}'. " +
                "The report was not imported. Check POS Auto Sync Setup.");
    }

    public static bool DatabaseMatches(string? configured, string? licensed) =>
        string.IsNullOrWhiteSpace(configured) ||
        string.Equals(configured.Trim(), licensed?.Trim(), StringComparison.OrdinalIgnoreCase);
}
