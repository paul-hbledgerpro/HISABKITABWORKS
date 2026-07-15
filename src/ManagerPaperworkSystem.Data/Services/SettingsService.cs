using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Services;
using ManagerPaperworkSystem.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.Data.Services;

public sealed class SettingsService : ISettingsService
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SettingsService(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var s = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        return s is not null && !string.IsNullOrWhiteSpace(s.StoreName);
    }

    public async Task<AppSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var s = await db.Settings.FirstOrDefaultAsync(ct);
        if (s is null)
        {
            s = new AppSettings();
            db.Settings.Add(s);
            await db.SaveChangesAsync(ct);
        }
        return s;
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        
        if (settings.Id == 0)
        {
            db.Settings.Add(settings);
        }
        else
        {
            var existing = await db.Settings.FindAsync(new object[] { settings.Id }, ct);
            if (existing != null)
            {
                existing.StoreName = settings.StoreName;
                existing.StoreAddress = settings.StoreAddress;
                existing.DefaultReportType = settings.DefaultReportType;
                existing.ScreenMode = settings.ScreenMode;
                existing.DefaultStoreId = settings.DefaultStoreId;
                existing.LastStoreId = settings.LastStoreId;
            }
            else
            {
                db.Settings.Attach(settings);
                db.Entry(settings).State = EntityState.Modified;
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
