using AeroCompliance.ApiService.Services;
using AeroCompliance.Contracts.Dtos.Auth;
using AeroCompliance.Domain.Entities;
using AeroCompliance.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Buffers.Binary;

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
    ITokenService tokenService,
    ILogger<AuthController> logger) : ControllerBase
{
    private static readonly TimeSpan ReuseGrace = TimeSpan.FromSeconds(10);
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

        var response = IssueAndStoreTokens(user, Guid.NewGuid());
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

            // Serialize refresh/logout operations for this family before reading token state.
            var tokenId = await LockRefreshFamilyAsync(tokenHash, ct);
            var stored = tokenId is Guid id
                ? await db.RefreshTokens.Include(rt => rt.User).ThenInclude(u => u.Role)
                    .SingleAsync(rt => rt.Id == id, ct)
                : null;

            // Return the same 401 for unknown, expired, revoked, or inactive tokens.
            var now = await DatabaseNowAsync(ct);
            if (stored is null || !stored.User.IsActive)
            {
                return Unauthorized();
            }

            // An old rotated token is denied immediately. After the short window for
            // concurrent requests, its reuse revokes only this login's descendants.
            if (stored.RotatedAt is DateTimeOffset rotatedAt)
            {
                if (stored.ExpiresAt > now && now - rotatedAt >= ReuseGrace && stored.ReuseDetectedAt is null)
                {
                    await RevokeFamilyAsync(stored.FamilyId, now, ct);
                    stored.ReuseDetectedAt = now;
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    logger.LogWarning("Refresh-token reuse revoked family {FamilyId} for user {UserId}.",
                        stored.FamilyId, stored.UserId);
                }
                return Unauthorized();
            }

            if (stored.RevokedAt is not null || stored.ExpiresAt <= now) return Unauthorized();

            // Rotate: revoke the presented token, then issue a fresh pair.
            stored.RevokedAt = now;
            stored.RotatedAt = now;

            var response = IssueAndStoreTokens(stored.User, stored.FamilyId);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Ok(response);
        });
    }

    /// <summary>Revoke the presented token's login family. Unknown or already-revoked tokens return 204.</summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest request, CancellationToken ct)
    {
        var tokenHash = tokenService.HashRefreshToken(request.RefreshToken);

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync<IActionResult>(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var tokenId = await LockRefreshFamilyAsync(tokenHash, ct);
            if (tokenId is Guid id)
            {
                var familyId = await db.RefreshTokens.Where(rt => rt.Id == id)
                    .Select(rt => rt.FamilyId).SingleAsync(ct);
                await RevokeFamilyAsync(familyId, await DatabaseNowAsync(ct), ct);
                await tx.CommitAsync(ct);
            }
            return NoContent();
        });
    }

    /// <summary>Issue a token pair, persist the refresh token's hash, and shape the response.</summary>
    private TokenResponse IssueAndStoreTokens(User user, Guid familyId)
    {
        var issued = tokenService.IssueTokens(user);

        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            FamilyId = familyId,
            TokenHash = tokenService.HashRefreshToken(issued.RefreshToken),
            ExpiresAt = issued.RefreshTokenExpiresAt,
            CreatedAt = DateTimeOffset.UtcNow
        });
        return new TokenResponse(issued.AccessToken, issued.RefreshToken, issued.AccessTokenExpiresAt);
    }

    private Task<int> RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken ct) =>
        db.RefreshTokens.Where(rt => rt.FamilyId == familyId && rt.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(rt => rt.RevokedAt, now), ct);

    private async Task<DateTimeOffset> DatabaseNowAsync(CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        // clock_timestamp() is current time even after waiting for the family lock.
        command.CommandText = "SELECT clock_timestamp()";
        var value = await command.ExecuteScalarAsync(ct);
        return value switch
        {
            DateTimeOffset instant => instant,
            DateTime instant => new DateTimeOffset(DateTime.SpecifyKind(instant, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException("PostgreSQL did not return its current timestamp.")
        };
    }

    private async Task<Guid?> LockRefreshFamilyAsync(string tokenHash, CancellationToken ct)
    {
        var token = await db.RefreshTokens.AsNoTracking().Where(rt => rt.TokenHash == tokenHash)
            .Select(rt => new { rt.Id, rt.FamilyId }).SingleOrDefaultAsync(ct);
        if (token is null) return null;
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "SELECT pg_advisory_xact_lock(@family_key)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "family_key";
        parameter.Value = BinaryPrimitives.ReadInt64BigEndian(token.FamilyId.ToByteArray());
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(ct);
        return token.Id;
    }
}
