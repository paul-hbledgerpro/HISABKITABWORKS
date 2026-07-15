using ManagerPaperworkSystem.Core.Models;

namespace ManagerPaperworkSystem.UI.Services;

public sealed class SessionState
{
    public int UserId { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Manager;

    /// <summary>Store selected at login (or last switched to).</summary>
    public int LastStoreId { get; set; }

    /// <summary>Store name for display.</summary>
    public string StoreName { get; set; } = "";

    public bool IsAdmin => Role == UserRole.OwnerAdmin;

    public void Clear()
    {
        UserId = 0;
        Username = "";
        DisplayName = "";
        Role = UserRole.Manager;
        LastStoreId = 0;
        StoreName = "";
    }
}
