namespace Psx.Api.Services;

public static class PasswordHasher
{
    // A real BCrypt hash of a value nobody will ever type, used to pay the same verify cost
    // for a username that doesn't exist as for one that does - otherwise the missing-user path
    // returns near-instantly while a real-but-wrong-password check takes ~100ms, letting an
    // attacker enumerate valid usernames purely by timing the login response.
    public static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString());

    public static string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password);

    public static bool Verify(string password, string? stored) =>
        !string.IsNullOrEmpty(stored) && BCrypt.Net.BCrypt.Verify(password, stored);
}
