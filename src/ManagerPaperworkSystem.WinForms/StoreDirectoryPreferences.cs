using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ManagerPaperworkSystem.WinForms;

internal sealed class StoreDirectoryPreferencesDocument
{
    public int Version { get; set; } = 1;
    public string DefaultStoreKey { get; set; } = "";
    public List<string> OrderedStoreKeys { get; set; } = [];
    public List<string> DisconnectedStoreKeys { get; set; } = [];
}

internal static class StoreDirectoryPreferencesStore
{
    private static readonly object Gate = new();
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("HISAB-KITAB-WORKS-STORE-DIRECTORY-PREFERENCES-V1");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static string ProtectedPath =>
        Path.Combine(AppBootstrap.AppDataPath, "store-directory-preferences.protected");

    public static IReadOnlyList<LicensedBusinessConnection> GetOrderedBusinesses(
        IReadOnlyList<LicensedBusinessConnection> businesses,
        bool includeDisconnected = false)
    {
        lock (Gate)
        {
            var document = LoadAndNormalize(businesses);
            var order = document.OrderedStoreKeys
                .Select((key, index) => new { key, index })
                .ToDictionary(item => item.key, item => item.index, StringComparer.OrdinalIgnoreCase);
            var disconnected = document.DisconnectedStoreKeys
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return businesses
                .Where(business => includeDisconnected || !disconnected.Contains(Key(business)))
                .OrderBy(business => order.GetValueOrDefault(Key(business), int.MaxValue))
                .ThenByDescending(business => business.IsPrimary)
                .ThenBy(business => business.BusinessName)
                .ToList();
        }
    }

    public static LicensedBusinessConnection? GetDefaultBusiness(
        IReadOnlyList<LicensedBusinessConnection> businesses)
    {
        lock (Gate)
        {
            var document = LoadAndNormalize(businesses);
            var connected = GetConnectedKeys(document);
            return businesses.FirstOrDefault(business =>
                       connected.Contains(Key(business)) &&
                       string.Equals(
                           Key(business),
                           document.DefaultStoreKey,
                           StringComparison.OrdinalIgnoreCase))
                   ?? GetOrderedBusinesses(businesses).FirstOrDefault();
        }
    }

    public static bool IsDefault(
        LicensedBusinessConnection business,
        IReadOnlyList<LicensedBusinessConnection> businesses)
    {
        lock (Gate)
        {
            var document = LoadAndNormalize(businesses);
            return string.Equals(
                document.DefaultStoreKey,
                Key(business),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    public static bool IsConnected(
        LicensedBusinessConnection business,
        IReadOnlyList<LicensedBusinessConnection> businesses)
    {
        lock (Gate)
        {
            var document = LoadAndNormalize(businesses);
            return !document.DisconnectedStoreKeys.Contains(
                Key(business),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public static bool IsConnected(string? storeGuid, string? databaseName)
    {
        var businesses = LicensedBusinessService.Load();
        var business = businesses.FirstOrDefault(item =>
            (!string.IsNullOrWhiteSpace(databaseName) &&
             string.Equals(item.DatabaseName, databaseName, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(storeGuid) &&
             string.Equals(item.StoreGuid, storeGuid, StringComparison.OrdinalIgnoreCase)));
        return business is not null && IsConnected(business, businesses);
    }

    public static void SetDefault(
        LicensedBusinessConnection business,
        IReadOnlyList<LicensedBusinessConnection> businesses)
    {
        lock (Gate)
        {
            var document = LoadAndNormalize(businesses);
            var key = Key(business);
            if (document.DisconnectedStoreKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Reconnect this store before making it the default.");
            document.DefaultStoreKey = key;
            Save(document);
        }
    }

    public static void Move(
        LicensedBusinessConnection business,
        IReadOnlyList<LicensedBusinessConnection> businesses,
        int direction)
    {
        lock (Gate)
        {
            var document = LoadAndNormalize(businesses);
            var key = Key(business);
            var current = document.OrderedStoreKeys.FindIndex(item =>
                string.Equals(item, key, StringComparison.OrdinalIgnoreCase));
            var target = current + Math.Sign(direction);
            if (current < 0 || target < 0 || target >= document.OrderedStoreKeys.Count)
                return;
            (document.OrderedStoreKeys[current], document.OrderedStoreKeys[target]) =
                (document.OrderedStoreKeys[target], document.OrderedStoreKeys[current]);
            Save(document);
        }
    }

    public static void SetConnected(
        LicensedBusinessConnection business,
        IReadOnlyList<LicensedBusinessConnection> businesses,
        bool connected)
    {
        lock (Gate)
        {
            var document = LoadAndNormalize(businesses);
            var key = Key(business);
            if (!connected && business.IsPrimary)
                throw new InvalidOperationException(
                    "The primary login business cannot be disconnected from this PC.");

            if (connected)
            {
                document.DisconnectedStoreKeys.RemoveAll(item =>
                    string.Equals(item, key, StringComparison.OrdinalIgnoreCase));
            }
            else if (!document.DisconnectedStoreKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                var connectedCount = businesses.Count(item =>
                    !document.DisconnectedStoreKeys.Contains(
                        Key(item),
                        StringComparer.OrdinalIgnoreCase));
                if (connectedCount <= 1)
                    throw new InvalidOperationException(
                        "At least one licensed store must remain connected.");
                document.DisconnectedStoreKeys.Add(key);
            }

            Normalize(document, businesses);
            Save(document);
        }
    }

    public static string Key(LicensedBusinessConnection business)
    {
        var identity = !string.IsNullOrWhiteSpace(business.StoreGuid)
            ? business.StoreGuid
            : !string.IsNullOrWhiteSpace(business.DatabaseName)
                ? business.DatabaseName
                : business.BusinessName;
        return NormalizeKey(identity);
    }

    public static int OrderOf(
        string? businessName,
        IReadOnlyList<LicensedBusinessConnection> businesses)
    {
        var ordered = GetOrderedBusinesses(businesses);
        for (var index = 0; index < ordered.Count; index++)
        {
            if (NamesMatch(ordered[index].BusinessName, businessName))
                return index;
        }
        return int.MaxValue;
    }

    public static bool NamesMatch(string? left, string? right)
    {
        static string NormalizeName(string? value) =>
            new((value ?? "").Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());
        var first = NormalizeName(left);
        var second = NormalizeName(right);
        return first.Length > 0 && first == second;
    }

    private static StoreDirectoryPreferencesDocument LoadAndNormalize(
        IReadOnlyList<LicensedBusinessConnection> businesses)
    {
        var document = Load();
        if (Normalize(document, businesses))
            Save(document);
        return document;
    }

    private static StoreDirectoryPreferencesDocument Load()
    {
        try
        {
            if (!File.Exists(ProtectedPath))
                return new StoreDirectoryPreferencesDocument();
            var protectedBytes = File.ReadAllBytes(ProtectedPath);
            var clear = ProtectedData.Unprotect(
                protectedBytes,
                Entropy,
                DataProtectionScope.LocalMachine);
            try
            {
                return JsonSerializer.Deserialize<StoreDirectoryPreferencesDocument>(
                           clear,
                           JsonOptions)
                       ?? new StoreDirectoryPreferencesDocument();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
        catch
        {
            return new StoreDirectoryPreferencesDocument();
        }
    }

    private static bool Normalize(
        StoreDirectoryPreferencesDocument document,
        IReadOnlyList<LicensedBusinessConnection> businesses)
    {
        var changed = false;
        var validKeys = businesses
            .Select(Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var initialOrder = businesses
            .OrderByDescending(item => item.IsPrimary)
            .ThenBy(item => item.BusinessName)
            .Select(Key)
            .ToList();

        var normalizedOrder = document.OrderedStoreKeys
            .Where(validKeys.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Concat(initialOrder.Where(key =>
                !document.OrderedStoreKeys.Contains(key, StringComparer.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!document.OrderedStoreKeys.SequenceEqual(
                normalizedOrder,
                StringComparer.OrdinalIgnoreCase))
        {
            document.OrderedStoreKeys = normalizedOrder;
            changed = true;
        }

        var disconnected = document.DisconnectedStoreKeys
            .Where(validKeys.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var primaryKey in businesses.Where(item => item.IsPrimary).Select(Key))
        {
            disconnected.RemoveAll(item =>
                string.Equals(item, primaryKey, StringComparison.OrdinalIgnoreCase));
        }
        if (!document.DisconnectedStoreKeys.SequenceEqual(
                disconnected,
                StringComparer.OrdinalIgnoreCase))
        {
            document.DisconnectedStoreKeys = disconnected;
            changed = true;
        }

        var connectedKeys = GetConnectedKeys(document);
        if (connectedKeys.Count == 0 && initialOrder.Count > 0)
        {
            document.DisconnectedStoreKeys.RemoveAll(item =>
                string.Equals(item, initialOrder[0], StringComparison.OrdinalIgnoreCase));
            connectedKeys = GetConnectedKeys(document);
            changed = true;
        }

        if (!connectedKeys.Contains(document.DefaultStoreKey))
        {
            var primary = businesses.FirstOrDefault(item =>
                item.IsPrimary && connectedKeys.Contains(Key(item)));
            document.DefaultStoreKey = primary is not null
                ? Key(primary)
                : normalizedOrder.FirstOrDefault(connectedKeys.Contains) ?? "";
            changed = true;
        }

        return changed;
    }

    private static HashSet<string> GetConnectedKeys(
        StoreDirectoryPreferencesDocument document)
    {
        var disconnected = document.DisconnectedStoreKeys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return document.OrderedStoreKeys
            .Where(key => !disconnected.Contains(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void Save(StoreDirectoryPreferencesDocument document)
    {
        Directory.CreateDirectory(AppBootstrap.AppDataPath);
        var clear = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        try
        {
            var protectedBytes = ProtectedData.Protect(
                clear,
                Entropy,
                DataProtectionScope.LocalMachine);
            var temporaryPath = ProtectedPath + ".new";
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, ProtectedPath, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private static string NormalizeKey(string? value) =>
        new((value ?? "").Trim()
            .Where(character => char.IsLetterOrDigit(character) || character is '_' or '-')
            .Select(char.ToUpperInvariant)
            .ToArray());
}
