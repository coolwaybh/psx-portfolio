namespace Psx.Api.Entities;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Gates the admin-reset-password surface (see /api/admin in Program.cs) - there's
    // no email/token-based self-service reset in this app (small trusted user base),
    // so instead the admin account can set a forgotten password directly. Only ever
    // set by the AddIsAdmin migration's data seed - no endpoint promotes a user to admin.
    public bool IsAdmin { get; set; } = false;

    public UserSettings? Settings { get; set; }
    public List<LedgerEntry> LedgerEntries { get; set; } = new();
}
