using ManagerPaperworkSystem.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.WinForms;

/// <summary>
/// Resolves the StoreId that owns the operational rows inside one physical
/// store database. Restored HB Ledger Pro databases can retain their original
/// store row while licensing adds a newer directory row with the current
/// business name. In that case the populated legacy row remains the data owner.
/// </summary>
internal static class StoreDataIdentityResolver
{
    public static async Task<int> ResolveAsync(
        AppDbContext db,
        string businessName,
        CancellationToken cancellationToken = default)
    {
        var stores = await db.Stores
            .AsNoTracking()
            .OrderBy(store => store.Id)
            .ToListAsync(cancellationToken);
        if (stores.Count == 0)
        {
            throw new InvalidOperationException(
                $"The database for '{businessName}' does not contain a store record.");
        }

        var rowCounts = stores.ToDictionary(store => store.Id, _ => 0L);
        void AddCounts(IReadOnlyDictionary<int, int> counts)
        {
            foreach (var count in counts)
            {
                if (rowCounts.ContainsKey(count.Key))
                    rowCounts[count.Key] += count.Value;
            }
        }

        AddCounts(await db.ShiftLogs.AsNoTracking()
            .GroupBy(row => row.StoreId)
            .ToDictionaryAsync(group => group.Key, group => group.Count(), cancellationToken));
        AddCounts(await db.CashOnHand.AsNoTracking()
            .GroupBy(row => row.StoreId)
            .ToDictionaryAsync(group => group.Key, group => group.Count(), cancellationToken));
        AddCounts(await db.CheckPayouts.AsNoTracking()
            .GroupBy(row => row.StoreId)
            .ToDictionaryAsync(group => group.Key, group => group.Count(), cancellationToken));
        AddCounts(await db.PosSalesSummaries.AsNoTracking()
            .GroupBy(row => row.StoreId)
            .ToDictionaryAsync(group => group.Key, group => group.Count(), cancellationToken));
        AddCounts(await db.PurchaseInvoices.AsNoTracking()
            .GroupBy(row => row.StoreId)
            .ToDictionaryAsync(group => group.Key, group => group.Count(), cancellationToken));
        AddCounts(await db.ProductCosts.AsNoTracking()
            .GroupBy(row => row.StoreId)
            .ToDictionaryAsync(group => group.Key, group => group.Count(), cancellationToken));
        AddCounts(await db.PriceAlerts.AsNoTracking()
            .GroupBy(row => row.StoreId)
            .ToDictionaryAsync(group => group.Key, group => group.Count(), cancellationToken));
        AddCounts(await db.Employees.AsNoTracking()
            .GroupBy(row => row.StoreId)
            .ToDictionaryAsync(group => group.Key, group => group.Count(), cancellationToken));
        AddCounts(await db.ScheduleShifts.AsNoTracking()
            .GroupBy(row => row.StoreId)
            .ToDictionaryAsync(group => group.Key, group => group.Count(), cancellationToken));

        var nameMatch = stores.FirstOrDefault(store =>
            NamesMatch(store.Name, businessName));
        if (nameMatch is not null && rowCounts[nameMatch.Id] > 0)
            return nameMatch.Id;

        var populatedStoreIds = rowCounts
            .Where(item => item.Value > 0)
            .Select(item => item.Key)
            .ToList();
        if (populatedStoreIds.Count == 1)
            return populatedStoreIds[0];

        return nameMatch?.Id
               ?? stores.FirstOrDefault(store => store.IsActive)?.Id
               ?? stores[0].Id;
    }

    private static bool NamesMatch(string? left, string? right)
    {
        static string Normalize(string? value) =>
            new((value ?? "")
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());

        var first = Normalize(left);
        var second = Normalize(right);
        return first.Length > 0 && first == second;
    }
}
