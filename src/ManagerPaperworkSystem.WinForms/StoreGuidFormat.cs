namespace ManagerPaperworkSystem.WinForms;

internal static class StoreGuidFormat
{
    public static string Create(string state, string storeName, string businessType, string zip)
    {
        var stateCode = Compact(state);
        var nameCode = Compact(storeName);
        var typeCode = Compact(businessType);
        var zipCode = new string((zip ?? "").Where(char.IsDigit).ToArray());
        if (stateCode.Length != 2 || nameCode.Length == 0 || typeCode.Length < 2 || zipCode.Length != 5)
            return "";
        return $"{stateCode}_{nameCode}_{typeCode}_{zipCode}";
    }

    public static bool IsValid(string value)
    {
        var normalized = value ?? "";
        var parts = normalized.Split('_');
        return parts.Length == 4 && parts[0].Length == 2 && parts[1].Length > 0 &&
               parts[2].Length >= 2 && parts[3].Length == 5 &&
               parts.All(part => part.All(char.IsLetterOrDigit)) &&
               string.Equals(normalized, normalized.ToUpperInvariant(), StringComparison.Ordinal);
    }

    private static string Compact(string? value)
        => new((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
