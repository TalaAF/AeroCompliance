using System.Security.Cryptography;
using AeroCompliance.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace AeroCompliance.ApiService.Services;

/// <summary>
/// <see cref="IPasswordHasherService"/> backed by <see cref="PasswordHasher{TUser}"/>. Uses the
/// PBKDF2-based algorithm ASP.NET Core Identity ships by default; each <see cref="Hash"/> call
/// produces a distinct salted result.
/// </summary>
public sealed class PasswordHasherService : IPasswordHasherService
{
    private readonly PasswordHasher<User> _hasher;
    private readonly User _dummyUser = new() { Id = Guid.Empty, Email = "login-timing@invalid" };
    private readonly string _dummyHash;

    public PasswordHasherService(PasswordHasher<User> hasher)
    {
        _hasher = hasher;
        _dummyHash = hasher.HashPassword(
            _dummyUser,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    public string Hash(User user, string password)
        => _hasher.HashPassword(user, password);

    public bool Verify(User user, string passwordHash, string providedPassword)
    {
        var result = _hasher.VerifyHashedPassword(user, passwordHash, providedPassword);
        // Treat a "needs rehash" result as valid — the password was correct, just hashed with older params.
        return result is PasswordVerificationResult.Success
            or PasswordVerificationResult.SuccessRehashNeeded;
    }

    public void VerifyDummy(string providedPassword)
        => _ = _hasher.VerifyHashedPassword(_dummyUser, _dummyHash, providedPassword);
}
