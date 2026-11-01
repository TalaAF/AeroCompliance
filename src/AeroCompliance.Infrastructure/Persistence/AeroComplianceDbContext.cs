using AeroCompliance.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AeroCompliance.Infrastructure.Persistence;

/// <summary>
/// EF Core context for the AeroCompliance PostGIS database. Entity shapes and geometry columns
/// (Point/Polygon/MultiPolygon, SRID 4326) are configured via <see cref="IEntityTypeConfiguration{TEntity}"/>
/// classes in the <c>Configurations</c> folder, applied in <see cref="OnModelCreating"/>.
/// </summary>
public class AeroComplianceDbContext(DbContextOptions<AeroComplianceDbContext> options)
    : DbContext(options)
{
    // RBAC
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    // Geo hierarchy
    public DbSet<Municipality> Municipalities => Set<Municipality>();
    public DbSet<Zone> Zones => Set<Zone>();
    public DbSet<Block> Blocks => Set<Block>();
    public DbSet<Parcel> Parcels => Set<Parcel>();
    public DbSet<Road> Roads => Set<Road>();
    public DbSet<ZoningRule> ZoningRules => Set<ZoningRule>();
    public DbSet<GisReferenceFeature> GisReferenceFeatures => Set<GisReferenceFeature>();

    // Flights & media
    public DbSet<Flight> Flights => Set<Flight>();
    public DbSet<FlightMission> FlightMissions => Set<FlightMission>();
    public DbSet<Image> Images => Set<Image>();
    public DbSet<Video> Videos => Set<Video>();
    public DbSet<Orthomosaic> Orthomosaics => Set<Orthomosaic>();
    public DbSet<ImageAnalysis> ImageAnalyses => Set<ImageAnalysis>();

    // AI + violations
    public DbSet<Detection> Detections => Set<Detection>();
    public DbSet<Violation> Violations => Set<Violation>();
    public DbSet<ViolationStatus> ViolationStatuses => Set<ViolationStatus>();
    public DbSet<ViolationStatusHistory> ViolationStatusHistory => Set<ViolationStatusHistory>();
    public DbSet<ComplianceEvaluation> ComplianceEvaluations => Set<ComplianceEvaluation>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<AiProcessingJob> AiProcessingJobs => Set<AiProcessingJob>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    // Reporting / notifications / audit
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<UserNotification> UserNotifications => Set<UserNotification>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<PoliceAlert> PoliceAlerts => Set<PoliceAlert>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // The PostGIS and uuid-ossp extensions back geometry columns and uuid_generate_v4() defaults.
        modelBuilder.HasPostgresExtension("postgis");
        modelBuilder.HasPostgresExtension("uuid-ossp");

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AeroComplianceDbContext).Assembly);
    }
}
