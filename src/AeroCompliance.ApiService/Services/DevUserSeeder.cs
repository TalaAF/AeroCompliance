using AeroCompliance.Domain.Entities;
using AeroCompliance.Domain.Enums;
using AeroCompliance.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroCompliance.ApiService.Services;

/// <summary>
/// Seeds one dev test user per role (under the pilot municipality) after migrations. Idempotent:
/// checks by email and only inserts missing users, so it is safe to run on every startup.
///
/// DEV-ONLY: every seeded user shares the password below. Never enable this seeding in production,
/// and never ship these credentials.
/// </summary>
public sealed class DevUserSeeder(
    AeroComplianceDbContext db,
    IPasswordHasherService passwordHasher,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<DevUserSeeder> logger)
{
    /// <summary>DEV-ONLY shared password for all seeded users.</summary>
    private const string DevPassword = "ChangeMe123!";

    private const string EmailDomain = "aerocompliance.test";

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var configuredPassword = configuration["DemoSeed:Password"]?.Trim();
        var password = string.IsNullOrWhiteSpace(configuredPassword)
            ? environment.IsDevelopment()
                ? DevPassword
                : throw new InvalidOperationException(
                    "DemoSeed:Password is required when demo seeding is enabled outside Development.")
            : configuredPassword;
        if (password.Length < 12)
            throw new InvalidOperationException("DemoSeed:Password must contain at least 12 characters.");

        var created = 0;

        foreach (var role in Enum.GetValues<RoleName>())
        {
            // Email pattern {role}@aerocompliance.test, lowercased (e.g. admin@..., municipalitymanager@...).
            var email = $"{role.ToString().ToLowerInvariant()}@{EmailDomain}";

            if (await db.Users.AnyAsync(u => u.Email == email, cancellationToken))
            {
                continue;
            }

            var user = new User
            {
                Id = Guid.NewGuid(),
                FullName = $"{role} (seed)",
                Email = email,
                RoleId = (int)role,
                // Admin is platform-wide (no municipality); everyone else sits under the pilot municipality.
                MunicipalityId = role == RoleName.Admin ? null : SeedData.PilotMunicipalityId,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow
            };

            // Hash live (PasswordHasher salts per call, so this can't be precomputed in a migration).
            user.PasswordHash = passwordHasher.Hash(user, password);

            db.Users.Add(user);
            created++;
        }

        if (created > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning(
                "Seeded {Count} local demo user(s). Do not use these accounts in production.",
                created);
        }
    }
}
