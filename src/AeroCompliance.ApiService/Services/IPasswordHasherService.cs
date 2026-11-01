using AeroCompliance.Domain.Entities;

namespace AeroCompliance.ApiService.Services;

/// <summary>
/// Hashes and verifies user passwords. Thin wrapper over ASP.NET Core Identity's
/// <see cref="Microsoft.AspNetCore.Identity.PasswordHasher{TUser}"/> so callers don't take a
/// direct dependency on Identity.
/// </summary>
public interface IPasswordHasherService
{
    /// <summary>Produce a salted hash for the given plaintext password.</summary>
    string Hash(User user, string password);

    /// <summary>Verify a plaintext password against a stored hash.</summary>
    bool Verify(User user, string passwordHash, string providedPassword);

    /// <summary>
    /// Perform the same password-hash work as a real verification when no account was found.
    /// This keeps the login failure path from becoming a useful account-enumeration timing signal.
    /// </summary>
    void VerifyDummy(string providedPassword);
}
