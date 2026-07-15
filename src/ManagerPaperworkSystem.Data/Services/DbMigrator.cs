using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.Data.Services;

/// <summary>
/// Database migrator for SQL Server - ensures default data exists.
/// Schema is created via SQL script.
/// </summary>
public static class DbMigrator
{
    public static async Task EnsureDefaultStoreAsync(AppDbContext db, CancellationToken ct = default)
    {
        var hasStore = await db.Stores.AsNoTracking().AnyAsync(ct);
        if (!hasStore)
        {
            var s = await db.Settings.FirstOrDefaultAsync(ct) ?? new AppSettings();
            var name = string.IsNullOrWhiteSpace(s.StoreName) ? "Default Store" : s.StoreName.Trim();
            var address = s.StoreAddress?.Trim() ?? "";

            db.Stores.Add(new Store
            {
                Name = name,
                Address = address,
                IsActive = true,
                CreatedUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }

        var settings = await db.Settings.FirstOrDefaultAsync(ct);
        if (settings is null) return;

        var firstStoreId = await db.Stores.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Id).FirstOrDefaultAsync(ct);
        if (firstStoreId > 0)
        {
            if (settings.DefaultStoreId <= 0) settings.DefaultStoreId = firstStoreId;
            if (settings.LastStoreId <= 0) settings.LastStoreId = firstStoreId;
            await db.SaveChangesAsync(ct);
        }
    }
}
