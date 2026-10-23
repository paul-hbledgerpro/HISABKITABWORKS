using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.Data.Services;

/// <summary>Store-local reusable names, staged inside the entry's existing transaction.</summary>
public static class PayoutChoices
{
    public static string Clean(string name)
    {
        var value = (name ?? "").Trim();
        if (value.Length > 200) throw new ArgumentException("Payee and purpose names must be 200 characters or fewer.");
        return value;
    }
    public static async Task<Vendor?> VendorAsync(AppDbContext db, int storeId, string name, CancellationToken ct = default)
    {
        name = Clean(name);
        if (name.Length == 0) return null;
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Save lookup names inside the entry transaction.");
        var rows = await db.Vendors.Where(x => x.StoreId == storeId).OrderBy(x => x.Id).ToListAsync(ct);
        var row = rows.Concat(db.Vendors.Local.Where(x => x.StoreId == storeId)).FirstOrDefault(x => string.Equals(x.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
        if (row is not null) return row;
        row = new Vendor { StoreId = storeId, Name = name }; db.Vendors.Add(row); return row;
    }
    public static async Task<Purpose?> PurposeAsync(AppDbContext db, int storeId, string name, CancellationToken ct = default)
    {
        name = Clean(name);
        if (name.Length == 0) return null;
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Save lookup names inside the entry transaction.");
        var rows = await db.Purposes.Where(x => x.StoreId == storeId).OrderBy(x => x.Id).ToListAsync(ct);
        var row = rows.Concat(db.Purposes.Local.Where(x => x.StoreId == storeId)).FirstOrDefault(x => string.Equals(x.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
        if (row is not null) return row;
        row = new Purpose { StoreId = storeId, Name = name }; db.Purposes.Add(row); return row;
    }
}
