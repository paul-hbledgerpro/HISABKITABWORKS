using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Services;
using ManagerPaperworkSystem.Core.Utils;
using ManagerPaperworkSystem.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.Data.Services;

public sealed class AuthService : IAuthService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private Func<AppDbContext>? _storeDbCreator;

    public AuthService(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Set a store-specific DbContext creator so all user operations use the correct store database.
    /// Called from MainWindow after store selection / store switch.
    /// </summary>
    public void SetStoreDbCreator(Func<AppDbContext>? creator)
    {
        _storeDbCreator = creator;
    }

    /// <summary>
    /// Creates a DbContext pointing to the currently selected store database.
    /// Falls back to the default DI factory if no store override is set.
    /// </summary>
    private AppDbContext CreateDb() => _storeDbCreator?.Invoke() ?? _factory.CreateDbContext();

    public async Task<bool> HasAnyUsersAsync(CancellationToken ct = default)
    {
        using var db = CreateDb();
        return await db.Users.AsNoTracking().AnyAsync(ct);
    }

    public async Task<UserAccount> CreateUserAsync(string firstName, string lastName, UserRole role, string username, string password, string securityQuestion, string securityAnswer, string email = "", CancellationToken ct = default)
    {
        firstName = (firstName ?? "").Trim();
        lastName = (lastName ?? "").Trim();
        username = (username ?? "").Trim();

        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username is required.", nameof(username));
        if (string.IsNullOrWhiteSpace(password) || password.Length < 4)
            throw new ArgumentException("Password must be at least 4 characters.", nameof(password));

        securityQuestion = (securityQuestion ?? "").Trim();
        if (string.IsNullOrWhiteSpace(securityQuestion))
            throw new ArgumentException("Security question is required.", nameof(securityQuestion));

        if (string.IsNullOrWhiteSpace(securityAnswer) || securityAnswer.Trim().Length < 2)
            throw new ArgumentException("Security answer is required.", nameof(securityAnswer));

        using var db = CreateDb();
        
        var normalized = username.ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Username.ToLower() == normalized, ct))
            throw new InvalidOperationException("That username already exists.");

        var (hash, salt) = PasswordHasher.HashPassword(password);
        var (aHash, aSalt) = PasswordHasher.HashPassword(securityAnswer.Trim());

        var user = new UserAccount
        {
            FirstName = firstName,
            LastName = lastName,
            Role = role,
            Username = username,
            Email = (email ?? "").Trim(),
            PasswordHashBase64 = hash,
            SaltBase64 = salt,
            SecurityQuestion = securityQuestion,
            SecurityAnswerHashBase64 = aHash,
            SecurityAnswerSaltBase64 = aSalt,
            IsActive = true,
            CreatedUtc = DateTime.UtcNow
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task<string?> GetSecurityQuestionAsync(string username, CancellationToken ct = default)
    {
        var user = await GetUserByUsernameAsync(username, ct);
        return user?.SecurityQuestion;
    }

    public async Task ResetPasswordWithSecurityAnswerAsync(string username, string securityAnswer, string newPassword, CancellationToken ct = default)
    {
        username = (username ?? "").Trim();
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username is required.", nameof(username));

        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 4)
            throw new ArgumentException("New password must be at least 4 characters.", nameof(newPassword));

        using var db = CreateDb();
        
        var normalized = username.ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == normalized, ct);
        if (user is null || !user.IsActive)
            throw new InvalidOperationException("User not found (or account disabled).");

        if (string.IsNullOrWhiteSpace(user.SecurityQuestion))
            throw new InvalidOperationException("This account does not have a security question set.");

        if (!PasswordHasher.VerifyPassword(securityAnswer ?? "", user.SecurityAnswerHashBase64, user.SecurityAnswerSaltBase64))
            throw new InvalidOperationException("Security answer is incorrect.");

        var (hash, salt) = PasswordHasher.HashPassword(newPassword);
        user.PasswordHashBase64 = hash;
        user.SaltBase64 = salt;
        user.LastChangedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<UserAccount?> AuthenticateAsync(string username, string password, CancellationToken ct = default)
    {
        username = (username ?? "").Trim();
        if (string.IsNullOrWhiteSpace(username))
            return null;

        using var db = CreateDb();
        
        var normalized = username.ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == normalized, ct);
        if (user is null || !user.IsActive)
            return null;

        if (!PasswordHasher.VerifyPassword(password ?? "", user.PasswordHashBase64, user.SaltBase64))
            return null;

        user.LastLoginUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task<UserAccount?> GetUserByUsernameAsync(string username, CancellationToken ct = default)
    {
        username = (username ?? "").Trim();
        if (string.IsNullOrWhiteSpace(username))
            return null;

        using var db = CreateDb();
        var normalized = username.ToLowerInvariant();
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username.ToLower() == normalized, ct);
    }

    public async Task<bool> VerifyAdminCredentialsAsync(string username, string password, CancellationToken ct = default)
    {
        var user = await AuthenticateAsync(username, password, ct);
        return user is not null && user.Role == UserRole.OwnerAdmin;
    }

    public async Task ChangePasswordAsync(int userId, string newPassword, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 4)
            throw new ArgumentException("New password must be at least 4 characters.", nameof(newPassword));

        using var db = CreateDb();
        
        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == userId, ct);
        if (user is null)
            throw new InvalidOperationException("User not found.");

        var (hash, salt) = PasswordHasher.HashPassword(newPassword);
        user.PasswordHashBase64 = hash;
        user.SaltBase64 = salt;
        user.LastChangedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<UserAccount>> GetUsersAsync(CancellationToken ct = default)
    {
        using var db = CreateDb();
        return await db.Users.AsNoTracking().OrderBy(u => u.Role).ThenBy(u => u.Username).ToListAsync(ct);
    }

    public async Task SetUserActiveAsync(int userId, bool isActive, CancellationToken ct = default)
    {
        using var db = CreateDb();
        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == userId, ct);
        if (user is null) return;
        user.IsActive = isActive;
        await db.SaveChangesAsync(ct);
    }
}
