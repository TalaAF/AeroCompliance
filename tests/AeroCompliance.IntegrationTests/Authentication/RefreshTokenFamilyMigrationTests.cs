using AeroCompliance.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;

namespace AeroCompliance.IntegrationTests.Authentication;

public class RefreshTokenFamilyMigrationTests
{
    [Fact]
    public async Task Existing_sessions_remain_separate_and_keep_their_revocation_state()
    {
        await using var postgres = new PostgreSqlBuilder("postgis/postgis:16-3.4")
            .WithDatabase("session_upgrade_tests")
            .WithUsername("postgres")
            .WithPassword("isolated-tests-only-password")
            .Build();
        await postgres.StartAsync();

        var options = new DbContextOptionsBuilder<AeroComplianceDbContext>()
            .UseNpgsql(postgres.GetConnectionString(), npgsql => npgsql.UseNetTopologySuite())
            .Options;
        await using var db = new AeroComplianceDbContext(options);
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20261101081029_MultiInstanceProcessing");

        var userId = Guid.NewGuid();
        var activeId = Guid.NewGuid();
        var revokedId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users (id, full_name, email, password_hash, role_id)
            VALUES ({userId}, 'Existing user', 'existing@example.test', 'test-hash', 1)
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO refresh_tokens (id, user_id, token_hash, expires_at)
            VALUES ({activeId}, {userId}, 'active-test-hash', now() + interval '1 day')
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO refresh_tokens (id, user_id, token_hash, expires_at, revoked_at)
            VALUES ({revokedId}, {userId}, 'revoked-test-hash', now() + interval '1 day', now())
            """);

        await migrator.MigrateAsync();

        var active = await db.RefreshTokens.AsNoTracking().SingleAsync(x => x.Id == activeId);
        var revoked = await db.RefreshTokens.AsNoTracking().SingleAsync(x => x.Id == revokedId);
        Assert.Equal(activeId, active.FamilyId);
        Assert.Equal(revokedId, revoked.FamilyId);
        Assert.Null(active.RevokedAt);
        Assert.NotNull(revoked.RevokedAt);
        Assert.Null(active.RotatedAt);
        Assert.Null(revoked.RotatedAt);
    }
}
