using AeroCompliance.Domain.Entities;
using AeroCompliance.Domain.Enums;
using AeroCompliance.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace AeroCompliance.ApiService.Services;

/// <summary>
/// Seeds a tiny, explicitly synthetic dataset for local demonstration deployments. It is opt-in
/// through DemoSeed:Enabled, idempotent, and contains no municipality source data or personal data.
/// </summary>
public sealed class DemoDataSeeder(AeroComplianceDbContext db, ILogger<DemoDataSeeder> logger)
{
    public static readonly Guid ZoneId = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    public static readonly Guid BlockId = Guid.Parse("d0000000-0000-0000-0000-000000000002");
    public static readonly Guid ParcelId = Guid.Parse("d0000000-0000-0000-0000-000000000003");
    public static readonly Guid RoadId = Guid.Parse("d0000000-0000-0000-0000-000000000004");
    public static readonly Guid FlightId = Guid.Parse("d0000000-0000-0000-0000-000000000005");

    public async Task SeedAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var area = Rectangle(35.1994, 31.8994, 35.2006, 31.9006);
        var roadArea = Rectangle(35.19996, 31.8994, 35.20004, 31.9006);

        if (!await db.Zones.AnyAsync(x => x.Id == ZoneId, ct))
            db.Zones.Add(new Zone
            {
                Id = ZoneId, MunicipalityId = SeedData.PilotMunicipalityId,
                ZoneNumber = "DEMO-Z01", Name = "Synthetic demonstration zone",
                ZoneType = "synthetic_demo", Geom = area,
                SourceDocument = "Local demonstration data - not an official municipal regulation",
                CreatedAt = now
            });

        if (!await db.Blocks.AnyAsync(x => x.Id == BlockId, ct))
            db.Blocks.Add(new Block
            {
                Id = BlockId, ZoneId = ZoneId, BlockNumber = "DEMO-B01", Geom = area, CreatedAt = now
            });

        if (!await db.Parcels.AnyAsync(x => x.Id == ParcelId, ct))
            db.Parcels.Add(new Parcel
            {
                Id = ParcelId, BlockId = BlockId, ParcelNumber = "DEMO-P01",
                OwnerName = "Synthetic fixture - no person", ZoneType = "synthetic_demo",
                AreaM2 = 12_000, Geom = area, Source = "offline_synthetic_demo", CreatedAt = now
            });

        if (!await db.Roads.AnyAsync(x => x.Id == RoadId, ct))
            db.Roads.Add(new Road
            {
                Id = RoadId, MunicipalityId = SeedData.PilotMunicipalityId,
                RoadCode = "DEMO-R01", Name = "Synthetic demonstration road",
                RoadType = "synthetic_demo", WidthM = 8, IsSensitive = true,
                Geom = roadArea, CreatedAt = now
            });

        var operatorId = await db.Users.Where(x => x.RoleId == (int)RoleName.DroneOperator)
            .Select(x => x.Id).FirstAsync(ct);
        if (!await db.Flights.AnyAsync(x => x.Id == FlightId, ct))
            db.Flights.Add(new Flight
            {
                Id = FlightId, MunicipalityId = SeedData.PilotMunicipalityId,
                DroneOperatorId = operatorId, MissionName = "SYNTHETIC DEMO - Follow-up site visit",
                PlannedArea = area, Status = FlightStatus.Planned, ScheduledAt = now.AddDays(2),
                QualityFlag = true, CreatedAt = now
            });

        var written = await db.SaveChangesAsync(ct);
        if (written > 0)
            logger.LogWarning("Seeded {Count} synthetic local demonstration record(s).", written);
    }

    private static MultiPolygon Rectangle(double minX, double minY, double maxX, double maxY)
    {
        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        var ring = factory.CreateLinearRing([
            new Coordinate(minX, minY), new Coordinate(maxX, minY),
            new Coordinate(maxX, maxY), new Coordinate(minX, maxY),
            new Coordinate(minX, minY)
        ]);
        return factory.CreateMultiPolygon([factory.CreatePolygon(ring)]);
    }
}
