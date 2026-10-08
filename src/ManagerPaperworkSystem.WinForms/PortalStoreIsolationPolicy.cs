using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ManagerPaperworkSystem.WinForms;

internal static class PortalStoreIsolationPolicy
{
    public static string Normalize(string? value) => new((value ?? "")
        .Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))
        .Select(char.ToUpperInvariant).ToArray());

    // Only a complete name and city/state may omit the postal code. Never use
    // substring matching: differently named stores and cities remain distinct.
    public static bool MatchesNormalizedStore(string configuredName, string normalizedStore)
    {
        var wanted = Normalize(configuredName);
        if (wanted.Length == 0) return false;
        if (wanted == normalizedStore) return true;
        if (!Regex.IsMatch(configuredName, @"\([A-Za-z .'-]+,\s*[A-Za-z]{2}\)\s*$")) return false;
        return normalizedStore.StartsWith(wanted, StringComparison.Ordinal)
            && Regex.IsMatch(normalizedStore[wanted.Length..], @"^\d{5}(?:\d{4})?$");
    }

    public static int SelectExactStore(string configuredName, IReadOnlyList<string> names)
    {
        var wanted = Normalize(configuredName);
        var matches = Enumerable.Range(0, names.Count)
            .Where(i => wanted.Length > 0 && MatchesNormalizedStore(configuredName, Normalize(names[i]))).ToArray();
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

    public static void ValidateZReportStore(string sourceText, string portalStoreName)
    {
        // PdfPig preserves the padded receipt columns; browser innerText uses
        // newlines. Only read the receipt header immediately before its title,
        // never a store name elsewhere in the viewer or sales detail.
        var titles = Regex.Matches(sourceText ?? "", @"\bZ[\s-]*Report\b", RegexOptions.IgnoreCase);
        var verified = 0;
        foreach (Match title in titles)
        {
            var following = sourceText!.Substring(title.Index + title.Length,
                Math.Min(250, sourceText.Length - title.Index - title.Length));
            // Registers can include the portal terminal name, e.g. 1 (POS1).
            // The optional label never substitutes for the receipt store/address.
            if (!Regex.IsMatch(following, @"^\s*(?:=+\s*)?Register\s+Number\s*:\s*\d+(?:[^\S\r\n]*\([A-Za-z0-9][A-Za-z0-9 _./#-]{0,39}\))?\s*Batch\s*:",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline))
                continue;

            var header = sourceText.Substring(Math.Max(0, title.Index - 800), Math.Min(800, title.Index));
            var lines = Regex.Split(header, @"[\r\n]+|[^\S\r\n]{2,}")
                .Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
            var cityIndex = lines.Length - 1;
            if (cityIndex < 2 || !Regex.IsMatch(lines[cityIndex], @"^[A-Za-z .'-]+,\s*[A-Za-z]{2}(?:\s+\d{5}(?:-\d{4})?)?$"))
                throw UnverifiedZHeader(portalStoreName);

            // A street line begins with its building number; optional unit lines
            // follow it. Walk back past those to the immediately preceding name.
            var streetIndex = cityIndex - 1;
            while (streetIndex > 0 && !Regex.IsMatch(lines[streetIndex], @"^\d+[A-Za-z-]*\s+\S"))
                streetIndex--;
            if (streetIndex < 1)
                throw UnverifiedZHeader(portalStoreName);
            ValidateSummaryStore(lines[streetIndex - 1], portalStoreName);

            var location = Regex.Match(portalStoreName, @"\(([^()]+)\)\s*$");
            if (location.Success)
            {
                var expectedCity = Regex.Replace(location.Groups[1].Value, @"\s*-?\s*\d{5}(?:-\d{4})?\s*$", "");
                var reportedCity = Regex.Replace(lines[cityIndex], @"\s+\d{5}(?:-\d{4})?\s*$", "");
                if (Normalize(expectedCity) != Normalize(reportedCity))
                    throw new InvalidOperationException(
                        $"Store verification failed: the Z report location is '{lines[cityIndex]}', but sync is configured for '{portalStoreName}'. The report was not imported.");
            }
            verified++;
        }
        if (verified == 0)
            throw UnverifiedZHeader(portalStoreName);
    }

    private static InvalidOperationException UnverifiedZHeader(string portalStoreName) => new(
        $"Store verification failed: the Z report's store header could not be verified for '{portalStoreName}'. The report was not imported. Check POS Auto Sync Setup and the source report.");
}
