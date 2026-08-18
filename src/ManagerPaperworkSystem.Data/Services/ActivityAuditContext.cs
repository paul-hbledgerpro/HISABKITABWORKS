using System.Threading;

namespace ManagerPaperworkSystem.Data.Services;

public sealed record ActivityAuditActor(
    int UserId,
    string UserName,
    string UserRole,
    bool IsSystem);

/// <summary>
/// Supplies the current actor to AppDbContext without coupling the data layer
/// to WinForms session types. AsyncLocal keeps background/system scopes from
/// leaking into unrelated operations.
/// </summary>
public static class ActivityAuditContext
{
    private static readonly AsyncLocal<ActivityAuditActor?> AmbientActor = new();

    public static ActivityAuditActor? Current => AmbientActor.Value;

    public static void SetUser(int userId, string? userName, string? userRole)
    {
        AmbientActor.Value = new ActivityAuditActor(
            userId,
            string.IsNullOrWhiteSpace(userName) ? "Signed-in user" : userName.Trim(),
            userRole?.Trim() ?? "",
            false);
    }

    public static IDisposable BeginSystem(string name)
    {
        var prior = AmbientActor.Value;
        AmbientActor.Value = new ActivityAuditActor(0, name.Trim(), "System", true);
        return new RestoreScope(prior);
    }

    public static void Clear() => AmbientActor.Value = null;

    private sealed class RestoreScope(ActivityAuditActor? prior) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            AmbientActor.Value = prior;
        }
    }
}
