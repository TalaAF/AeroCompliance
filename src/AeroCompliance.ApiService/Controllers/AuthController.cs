using AeroCompliance.ApiService.Services;
using AeroCompliance.Contracts.Dtos.Auth;
using AeroCompliance.Domain.Entities;
using AeroCompliance.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AeroCompliance.ApiService.Controllers;

/// <summary>
/// Authentication endpoints (login / refresh / logout). All endpoints are anonymous — they are the
/// entry point for obtaining tokens. Refresh tokens are opaque, stored hashed, and rotated on refresh.
/// </summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public class AuthController(
    AeroComplianceDbContext db,
    IPasswordHasherService passwordHasher,
    ITokenService tokenService) : ControllerBase
{
    /// <summary>Exchange email + password for an access + refresh token pair.</summary>
    [HttpPost("login")]
    [EnableRateLimiting("login")]
    [ProducesResponseType(typeof(TokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        // Load the user with its role (needed for the role claim).
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users
            .Include(u => u.Role)
            .SingleOrDefaultAsync(u => u.Email == email, ct);

        // Same 401 whether the email is unknown, the password is wrong, or the account is inactive —
        // never disclose which. Always run exactly one password verification, including when the
        // account does not exist or is inactive, to reduce account-enumeration timing signals.
        bool passwordValid;
        if (user is null)
        {
            passwordHasher.VerifyDummy(request.Password);
            passwordValid = false;
        }
        else
        {
            passwordValid = passwordHasher.Verify(user, user.PasswordHash, request.Password);
        }

        if (user is null || !user.IsActive || !passwordValid)
        {
            return Unauthorized();
        }

        var response = IssueAndStoreTokens(user);
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Ok(response);
    }

    /// <summary>Exchange a valid refresh token for a new access + refresh pair (rotation).</summary>
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(TokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequest request, CancellationToken ct)
    {
        var tokenHash = tokenService.HashRefreshToken(request.RefreshToken);
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync<IActionResult>(async () =>
        {
            // A retry must not inherit tracked token/user state from the failed attempt.
            db.ChangeTracker.Clear();

            await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Lock the presented token row before testing/revoking it. Without this, two concurrent
        // requests can both observe IsActive and each mint a valid successor token.
            var tokenId = await LockRefreshTokenAsync(tokenHash, ct);
            var stored = tokenId is Guid id
                ? await db.RefreshTokens.Include(rt => rt.User).ThenInclude(u => u.Role)
                    .SingleAsync(rt => rt.Id == id, ct)
                : null;

        // Reject unknown, expired, or already-revoked tokens (and inactive users) — all as a flat 401.
            if (stored is null || !stored.IsActive || !stored.User.IsActive)
            {
                return Unauthorized();
            }

        // Rotate: revoke the presented token, then issue a fresh pair.
            stored.RevokedAt = DateTimeOffset.UtcNow;

            var response = IssueAndStoreTokens(stored.User);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Ok(response);
        });
    }

    /// <summary>Revoke a refresh token. Idempotent — an unknown/already-revoked token still returns 204.</summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest request, CancellationToken ct)
    {
        var tokenHash = tokenService.HashRefreshToken(request.RefreshToken);

        var stored = await db.RefreshTokens
            .SingleOrDefaultAsync(rt => rt.TokenHash == tokenHash, ct);

        if (stored is { RevokedAt: null })
        {
            stored.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return NoContent();
    }

    /// <summary>Issue a token pair, persist the refresh token's hash, and shape the response.</summary>
    private TokenResponse IssueAndStoreTokens(User user)
    {
        var issued = tokenService.IssueTokens(user);

        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = tokenService.HashRefreshToken(issued.RefreshToken),
            ExpiresAt = issued.RefreshTokenExpiresAt,
            CreatedAt = DateTimeOffset.UtcNow
        });
        return new TokenResponse(issued.AccessToken, issued.RefreshToken, issued.AccessTokenExpiresAt);
    }

    private async Task<Guid?> LockRefreshTokenAsync(string tokenHash, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "SELECT id FROM refresh_tokens WHERE token_hash = @token_hash FOR UPDATE";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "token_hash";
        parameter.Value = tokenHash;
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync(ct);
        return result is Guid id ? id : null;
    }
}
