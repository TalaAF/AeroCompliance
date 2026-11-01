using AeroCompliance.ApiService.Services;
using AeroCompliance.Domain.Entities;
using AeroCompliance.Domain.Enums;
using AeroCompliance.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using AeroCompliance.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetTopologySuite.Geometries;
using Testcontainers.PostgreSql;

namespace AeroCompliance.IntegrationTests.Infrastructure;

public sealed class ApiTestFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgis/postgis:16-3.4")
        .WithDatabase("aerocompliance_tests")
        .WithUsername("postgres")
        .WithPassword("isolated-tests-only-password")
        .Build();

    private AeroComplianceApiFactory? _factory;

    public IServiceProvider Services => Factory.Services;

    public HttpClient CreateClient()
        => Factory.CreateClient(new() { AllowAutoRedirect = false });

    public WebApplicationFactory<Program> CreateFactoryWithLoginLimit(int permitLimit)
        => Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Auth:LoginRateLimit:PermitLimit", permitLimit.ToString()));

    public WebApplicationFactory<Program> CreateProxyTestFactory(int permitLimit, string trustedProxy)
        => Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:LoginRateLimit:PermitLimit", permitLimit.ToString());
            builder.UseSetting("ReverseProxy:KnownProxies:0", trustedProxy);
            builder.ConfigureTestServices(services => services.AddTransient<IStartupFilter, TestRemoteIpStartupFilter>());
        });

    public WebApplicationFactory<Program> CreateReportFactory(IObjectStorage storage)
        => Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IObjectStorage>();
            services.AddSingleton(storage);
        }));

    public async Task<int> CountReportsAsync(Guid violationId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroComplianceDbContext>();
        return await db.Reports.CountAsync(value => value.ViolationId == violationId);
    }

    public async Task<System.Net.IPAddress?> FindAuditIpAsync(Guid entityId, string action)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroComplianceDbContext>();
        return await db.AuditLogs.AsNoTracking()
            .Where(value => value.EntityId == entityId && value.Action == action)
            .OrderByDescending(value => value.CreatedAt)
            .Select(value => value.IpAddress)
            .FirstOrDefaultAsync();
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _postgres.StartAsync();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Integration tests require a working Docker engine for an isolated PostGIS container. " +
                "No developer database is used.", exception);
        }

        _factory = new AeroComplianceApiFactory(_postgres.GetConnectionString());

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroComplianceDbContext>();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    public async Task<User> CreateUserAsync(
        RoleName role,
        bool isActive = true,
        Guid? id = null,
        string? email = null,
        Guid? municipalityId = null)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroComplianceDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();

        var user = new User
        {
            Id = id ?? Guid.NewGuid(),
            FullName = $"{role} Integration User",
            Email = email ?? $"{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}@example.test",
            RoleId = (int)role,
            MunicipalityId = role == RoleName.Admin ? null : municipalityId ?? SeedData.PilotMunicipalityId,
            IsActive = isActive,
            CreatedAt = DateTimeOffset.UtcNow
        };
        user.PasswordHash = hasher.Hash(user, TestPassword);

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public async Task<Guid> CreateMunicipalityAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroComplianceDbContext>();
        var municipality = new Municipality
        {
            Id = Guid.NewGuid(), Name = $"Other municipality {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Municipalities.Add(municipality);
        await db.SaveChangesAsync();
        return municipality.Id;
    }

    public async Task SetActiveAsync(Guid userId, bool active)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroComplianceDbContext>();
        var user = await db.Users.SingleAsync(value => value.Id == userId);
        user.IsActive = active;
        await db.SaveChangesAsync();
    }

    public async Task DeleteUserAsync(Guid userId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroComplianceDbContext>();
        var user = await db.Users.SingleAsync(value => value.Id == userId);
        db.Users.Remove(user);
        await db.SaveChangesAsync();
    }

    public async Task<RefreshToken?> FindRefreshTokenAsync(string plaintextToken)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroComplianceDbContext>();
        var tokenService = scope.ServiceProvider.GetRequiredService<ITokenService>();
        var hash = tokenService.HashRefreshToken(plaintextToken);
        return await db.RefreshTokens.AsNoTracking().SingleOrDefaultAsync(value => value.TokenHash == hash);
    }

    public async Task AddRefreshTokenAsync(User user, string plaintextToken, DateTimeOffset expiresAt, bool revoked)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroComplianceDbContext>();
        var tokenService = scope.ServiceProvider.GetRequiredService<ITokenService>();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = tokenService.HashRefreshToken(plaintextToken),
            ExpiresAt = expiresAt,
            RevokedAt = revoked ? DateTimeOffset.UtcNow : null,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    public async Task<Guid> CreateReviewViolationAsync(Guid droneOperatorId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroComplianceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var geometryFactory = new GeometryFactory(new PrecisionModel(), 4326);
        var polygon = geometryFactory.CreatePolygon([
            new Coordinate(35.20, 31.90),
            new Coordinate(35.21, 31.90),
            new Coordinate(35.21, 31.91),
            new Coordinate(35.20, 31.91),
            new Coordinate(35.20, 31.90)
        ]);
        var geometry = geometryFactory.CreateMultiPolygon([polygon]);
        var flight = new Flight
        {
            Id = Guid.NewGuid(), MunicipalityId = SeedData.PilotMunicipalityId,
            DroneOperatorId = droneOperatorId, MissionName = "Concurrent review test", CreatedAt = now
        };
        var image = new Image
        {
            Id = Guid.NewGuid(), FlightId = flight.Id, StoragePath = "tests/review.tif",
            Bucket = "tests", ObjectKey = "tests/review.tif", OriginalFileName = "review.tif",
            ContentType = "image/tiff", ByteLength = 4, Sha256Hash = new string('0', 64),
            CapturedAt = now, CreatedAt = now
        };
        var detection = new Detection
        {
            Id = Guid.NewGuid(), ImageId = image.Id, ClassLabel = "building", Confidence = 0.9m,
            Geom = geometry, ModelName = "integration-test", ModelVersion = "1", CreatedAt = now
        };
        var violation = new Violation
        {
            Id = Guid.NewGuid(), DetectionId = detection.Id, ViolationType = "test_violation",
            AiConfidenceScore = detection.Confidence, CurrentStatusId = 1, Geom = geometry,
            CreatedAt = now, UpdatedAt = now
        };
        db.AddRange(flight, image, detection, violation);
        await db.SaveChangesAsync();
        return violation.Id;
    }

    public async Task<int> CountReviewDecisionsAsync(Guid violationId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroComplianceDbContext>();
        return await db.ViolationStatusHistory.CountAsync(value =>
            value.ViolationId == violationId && (value.StatusId == 2 || value.StatusId == 7));
    }

    public const string TestPassword = "ValidPassword123!";

    private AeroComplianceApiFactory Factory
        => _factory ?? throw new InvalidOperationException("The integration fixture has not started.");
}
