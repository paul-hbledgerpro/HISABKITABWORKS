using System.Data;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.Data.Services;

public static class CashEntryService
{
    /// <summary>Save a new cash movement and its store-local lookups, including activity history.</summary>
    public static async Task SaveAsync(AppDbContext db, CashOnHandEntry entry, string vendorName,
        CancellationToken cancellationToken = default)
    {
        vendorName = vendorName.Trim();
        if (vendorName.Length > 200)
            throw new ArgumentException("Vendor names must be 200 characters or fewer.", nameof(vendorName));

        // Keep lookup resolution, cash and the context's supplemental audit save atomic.
        // Serializable also prevents two simultaneous payouts creating the same lookup.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        entry.Vendor = null;
        entry.VendorId = null;
        entry.Purpose = null;
        if (entry.IsPayout)
        {
            if (vendorName.Length > 0)
            {
                var vendors = await db.Vendors.Where(x => x.StoreId == entry.StoreId)
                    .OrderBy(x => x.Id).ToListAsync(cancellationToken);
                entry.Vendor = vendors.FirstOrDefault(x =>
                    string.Equals(x.Name.Trim(), vendorName, StringComparison.OrdinalIgnoreCase))
                    ?? new Vendor { StoreId = entry.StoreId, Name = vendorName };
            }

            if (entry.PurposeId is int purposeId)
            {
                entry.Purpose = await db.Purposes.FirstOrDefaultAsync(
                    x => x.Id == purposeId && x.StoreId == entry.StoreId, cancellationToken)
                    ?? throw new InvalidOperationException("The selected purpose is no longer available for this store. Reopen Record Payout to refresh the list.");
            }
            else
            {
                var purposes = await db.Purposes.Where(x => x.StoreId == entry.StoreId)
                    .OrderBy(x => x.Id).ToListAsync(cancellationToken);
                entry.Purpose = purposes.FirstOrDefault(x =>
                    string.Equals(x.Name.Trim(), "Payout", StringComparison.OrdinalIgnoreCase))
                    ?? new Purpose { StoreId = entry.StoreId, Name = "Payout" };
            }
        }
        else
        {
            entry.PurposeId = null;
        }

        db.CashOnHand.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
