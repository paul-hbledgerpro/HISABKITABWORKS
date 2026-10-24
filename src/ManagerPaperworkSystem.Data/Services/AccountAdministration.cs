using System.Data;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Utils;
using ManagerPaperworkSystem.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.Data.Services;

public static class AccountAdministration
{
    public static async Task RequireAdminAsync(AppDbContext db, int actorId, CancellationToken ct = default)
    {
        if (!await db.Users.AnyAsync(x => x.Id == actorId && x.IsActive && x.Role == UserRole.OwnerAdmin, ct))
            throw new UnauthorizedAccessException("An active Owner/Admin account is required.");
    }
    public static async Task ChangeAsync(AppDbContext db, int actorId, int userId, string action,
        string first = "", string last = "", string email = "", UserRole role = UserRole.Manager,
        int[]? allowedStores = null, string password = "", CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await RequireAdminAsync(db, actorId, ct);
        var user = await db.Users.SingleAsync(x => x.Id == userId, ct);
        var actor = await db.Users.SingleAsync(x => x.Id == actorId, ct);
        var removesAdmin = action is "Delete" || action == "Deactivate" || action == "Edit" && role != UserRole.OwnerAdmin;
        if (user.IsActive && user.Role == UserRole.OwnerAdmin && removesAdmin && !await db.Users.AnyAsync(x => x.Id != userId && x.IsActive && x.Role == UserRole.OwnerAdmin, ct))
            throw new InvalidOperationException("Keep at least one active Owner/Admin account.");
        if (userId == actorId && (removesAdmin || action == "Edit" && allowedStores is not null))
            throw new InvalidOperationException("Use another administrator to remove or restrict your own access.");
        switch (action)
        {
            case "Edit":
                if (string.IsNullOrWhiteSpace(first) || first.Trim().Length > 80 || last.Trim().Length > 80 || email.Trim().Length > 200 || !Enum.IsDefined(role))
                    throw new InvalidOperationException("Enter a valid name, email and role.");
                if (email.Trim().Length > 0 && !System.Net.Mail.MailAddress.TryCreate(email.Trim(), out _)) throw new InvalidOperationException("Enter a valid email address.");
                if (allowedStores is { Length: 0 }) throw new InvalidOperationException("Select at least one permitted store.");
                if (allowedStores is not null && await db.Stores.CountAsync(x => allowedStores.Contains(x.Id) && x.IsActive, ct) != allowedStores.Distinct().Count())
                    throw new InvalidOperationException("A selected store is no longer available.");
                user.FirstName = first.Trim(); user.LastName = last.Trim(); user.Email = email.Trim(); user.Role = role;
                user.AllowedStoreIds = allowedStores is null ? "" : string.Join(',', allowedStores.Distinct().Order());
                if (user.AllowedStoreIds.Length > 2000) throw new InvalidOperationException("Too many individual store permissions. Use all configured stores or reduce the selection.");
                break;
            case "Deactivate": user.IsActive = false; break;
            case "Reactivate": user.IsActive = true; break;
            case "Reset Password":
                if (password.Length < 8) throw new InvalidOperationException("Use a password of at least 8 characters.");
                (user.PasswordHashBase64, user.SaltBase64) = PasswordHasher.HashPassword(password); break;
            case "Delete":
                // A prior login may have written in another connected database. Retain that identity.
                if (user.LastLoginUtc is not null || await HasHistoryAsync(db, userId, ct))
                    throw new InvalidOperationException("This account has login or transaction history. Deactivate it to preserve accountability.");
                db.Users.Remove(user); break;
            default: throw new ArgumentException("Unknown account action.");
        }
        user.LastChangedUtc = DateTime.UtcNow;
        db.ActivityLogs.Add(new ActivityLogEntry { UserId = actor.Id, UserName = actor.DisplayName, UserRole = actor.Role.ToString(),
            Section = "User Accounts", Action = action, EntityType = nameof(UserAccount), EntityId = user.Id,
            Description = $"{action}: {user.Username}. Role: {user.Role}. Stores: {(user.AllowedStoreIds.Length == 0 ? "All configured stores" : user.AllowedStoreIds)}" });
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    }
    private static async Task<bool> HasHistoryAsync(AppDbContext db, int id, CancellationToken ct) =>
        await db.ActivityLogs.AnyAsync(x => x.UserId == id, ct) || await db.ShiftLogs.AnyAsync(x => x.CreatedByUserId == id, ct)
        || await db.CashOnHand.AnyAsync(x => x.CreatedByUserId == id, ct) || await db.CheckPayouts.AnyAsync(x => x.CreatedByUserId == id, ct)
        || await db.PurchaseInvoices.AnyAsync(x => x.CreatedByUserId == id, ct) || await db.PendingShiftDrops.AnyAsync(x => x.UserId == id, ct);
}
