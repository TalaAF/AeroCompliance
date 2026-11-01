using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroCompliance.Contracts.Messaging;
using AeroCompliance.Contracts.Messaging.Messages;
using AeroCompliance.Domain.Entities;
using AeroCompliance.Domain.Enums;
using AeroCompliance.Infrastructure.Compliance;
using AeroCompliance.Infrastructure.Notifications;
using AeroCompliance.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace AeroCompliance.ProcessingService;

public sealed class AnalysisResultConsumer(IConnection connection, IServiceScopeFactory scopes, ILogger<AnalysisResultConsumer> logger) : BackgroundService
{
    private const string ConsumerName = "AeroCompliance.ProcessingService";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await channel.ExchangeDeclareAsync(MessagingConstants.ProcessingExchange, ExchangeType.Topic, true, false, cancellationToken: stoppingToken);
        await channel.QueueDeclareAsync(MessagingConstants.ProcessingResultsDeadLetterQueue, true, false, false, cancellationToken: stoppingToken);
        await channel.QueueBindAsync(MessagingConstants.ProcessingResultsDeadLetterQueue, MessagingConstants.ProcessingExchange, "image.analysis.dead.v1", cancellationToken: stoppingToken);
        await channel.QueueDeclareAsync(MessagingConstants.ProcessingResultsQueue, true, false, false,
            new Dictionary<string, object?> { ["x-dead-letter-exchange"] = MessagingConstants.ProcessingExchange, ["x-dead-letter-routing-key"] = "image.analysis.dead.v1" }, cancellationToken: stoppingToken);
        foreach (var key in new[] { MessagingConstants.AnalysisStartedRoutingKey, MessagingConstants.AnalysisCompletedRoutingKey, MessagingConstants.AnalysisFailedRoutingKey })
            await channel.QueueBindAsync(MessagingConstants.ProcessingResultsQueue, MessagingConstants.ProcessingExchange, key, cancellationToken: stoppingToken);
        await channel.BasicQosAsync(0, 4, false, stoppingToken);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            try { await HandleAsync(ea, stoppingToken); await channel.BasicAckAsync(ea.DeliveryTag, false, stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Failed processing AI result {MessageId}", ea.BasicProperties.MessageId); await channel.BasicNackAsync(ea.DeliveryTag, false, !ea.Redelivered, stoppingToken); }
        };
        await channel.BasicConsumeAsync(MessagingConstants.ProcessingResultsQueue, false, consumer, stoppingToken);
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var body = ea.Body.ToArray(); using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AeroComplianceDbContext>();
        var notify = scope.ServiceProvider.GetRequiredService<INotificationService>();
        using var doc = JsonDocument.Parse(body); var eventId = doc.RootElement.GetProperty("eventId").GetGuid();
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            // A failed attempt may leave added entities tracked even though its transaction rolled
            // back. Start each retry from database state before checking the inbox marker.
            db.ChangeTracker.Clear();
            if (await db.InboxMessages.AnyAsync(x => x.EventId == eventId && x.Consumer == ConsumerName, ct)) return;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var inbox = new InboxMessage { EventId = eventId, Consumer = ConsumerName, EventType = ea.RoutingKey,
                PayloadHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant(), ReceivedAt = DateTimeOffset.UtcNow };
            db.InboxMessages.Add(inbox);
            switch (ea.RoutingKey)
            {
                case MessagingConstants.AnalysisStartedRoutingKey: await Started(db, JsonSerializer.Deserialize<ImageAnalysisStarted>(body, Json)!, ct); break;
                case MessagingConstants.AnalysisCompletedRoutingKey: await Completed(db, notify, JsonSerializer.Deserialize<ImageAnalysisCompleted>(body, Json)!, ct); break;
                case MessagingConstants.AnalysisFailedRoutingKey: await Failed(db, notify, JsonSerializer.Deserialize<ImageAnalysisFailed>(body, Json)!, ct); break;
                default: throw new InvalidDataException("Unknown analysis event.");
            }
            inbox.CompletedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        });
    }

    private static async Task Started(AeroComplianceDbContext db, ImageAnalysisStarted e, CancellationToken ct)
    {
        var image = await db.Images.SingleAsync(x => x.Id == e.ImageId, ct); if (image.ProcessingRequestId != e.RequestId) return;
        image.ProcessingStatus = ImageProcessingStatus.Analyzing; image.ProcessingStartedAt = e.StartedAt;
        if (!await db.ImageAnalyses.AnyAsync(x => x.RequestId == e.RequestId, ct)) db.ImageAnalyses.Add(new ImageAnalysis { Id = Guid.NewGuid(), ImageId = e.ImageId,
            RequestId = e.RequestId, Mode = e.Mode, ModelName = e.ModelName, ModelVersion = e.ModelVersion, ConfidenceThreshold = e.ConfidenceThreshold,
            OutputCrs = "EPSG:4326", Status = "Analyzing", AttemptNumber = e.Attempt, StartedAt = e.StartedAt });
    }

    private static async Task Completed(AeroComplianceDbContext db, INotificationService notify, ImageAnalysisCompleted e, CancellationToken ct)
    {
        var image = await db.Images.Include(x => x.Flight).SingleAsync(x => x.Id == e.ImageId, ct); if (image.ProcessingRequestId != e.RequestId) return;
        var analysis = await db.ImageAnalyses.SingleOrDefaultAsync(x => x.RequestId == e.RequestId, ct) ?? new ImageAnalysis { Id = Guid.NewGuid(), ImageId = e.ImageId, RequestId = e.RequestId };
        if (db.Entry(analysis).State == EntityState.Detached) db.ImageAnalyses.Add(analysis);
        analysis.Mode=e.Mode;analysis.ModelName=e.ModelName;analysis.ModelVersion=e.ModelVersion;analysis.ConfidenceThreshold=e.ConfidenceThreshold;analysis.SourceCrs=e.SourceCrs;
        analysis.OutputCrs=e.OutputCrs;analysis.Status="EvaluatingCompliance";analysis.AttemptNumber=e.Attempt;analysis.StartedAt=e.StartedAt;analysis.CompletedAt=e.CompletedAt;analysis.DurationMs=e.DurationMs;
        // Phase 5: persist raster georeferencing the AI Worker discovered via rasterio (GeoTIFF only;
        // null on a non-raster source or a lightweight worker build without rasterio) onto the Image
        // itself — only overwrite when a new value is actually present, so a later retry that can't
        // re-derive it (e.g. rasterio missing) never clobbers a previously-known-good value.
        if (e.SourceCrs is not null) image.SourceRasterCrs = e.SourceCrs;
        if (e.RasterWidth is not null) image.RasterWidth = e.RasterWidth;
        if (e.RasterHeight is not null) image.RasterHeight = e.RasterHeight;
        if (e.AffineTransform is not null) image.AffineTransform = e.AffineTransform;
        // Phase 8: the raster's own declaration that its georeference is approximate, plus the real
        // predictions the worker deliberately kept non-geographic. Both are recorded on the
        // analysis so the run is self-describing without re-reading the raster.
        ApplyGeoreferencingPolicy(analysis, e);
        image.ProcessingStatus=ImageProcessingStatus.EvaluatingCompliance; await db.SaveChangesAsync(ct);
        foreach (var item in e.Detections)
        {
            if (await db.Detections.AnyAsync(x => x.ImageAnalysisId == analysis.Id && x.ExternalDetectionId == item.DetectionId, ct)) continue;
            var geom=GeometryReader.Read(item.Geometry); var detection=new Detection { Id=Guid.NewGuid(),ImageAnalysisId=analysis.Id,ExternalDetectionId=item.DetectionId,ImageId=image.Id,
                ClassId=item.ClassId,ClassLabel=item.ClassName,Confidence=item.Confidence,Geom=geom,ModelName=e.ModelName,ModelVersion=e.ModelVersion,CreatedAt=e.CompletedAt,
                Image=image,
                ResultSource=item.ResultSource,IsApproximate=item.IsApproximate,RequiresManualReview=item.RequiresManualReview,
                GeoreferencingConfidence=item.GeoreferencingConfidence,SourceGeoreferencingMethod=item.SourceGeoreferencingMethod,
                DistanceFromAnchorM=item.DistanceFromAnchorMetres };
            db.Detections.Add(detection); await db.SaveChangesAsync(ct); await Evaluate(db, notify, image.Flight.MunicipalityId, analysis, detection, ct);
        }
        analysis.Status="Completed";image.ProcessingStatus=ImageProcessingStatus.Completed;image.ProcessingCompletedAt=e.CompletedAt;image.LastErrorCode=null;image.LastErrorMessage=null;
        db.AuditLogs.Add(new AuditLog { Action="image.processing.completed",EntityType="Image",EntityId=image.Id,MunicipalityId=image.Flight.MunicipalityId,
            AfterState=JsonSerializer.Serialize(new{analysisId=analysis.Id,detections=e.Detections.Count,
                modelOnly=e.ModelOnlyCount,georeferencingStatus=analysis.GeoreferencingStatus}),CreatedAt=DateTimeOffset.UtcNow });
    }

    /// <summary>
    /// Records the raster's approximate-georeference declaration and the retained model-only
    /// predictions. Values are only ever written when present, so a retry that cannot re-derive
    /// them never clears what an earlier attempt established.
    /// </summary>
    private static void ApplyGeoreferencingPolicy(ImageAnalysis analysis, ImageAnalysisCompleted e)
    {
        if (e.ModelOnlyDetections is not null) analysis.ModelOnlyDetections = e.ModelOnlyDetections;
        if (e.ModelOnlyCount > 0) analysis.ModelOnlyDetectionCount = e.ModelOnlyCount;
        if (e.GeoreferencingPolicy is null) return;
        try
        {
            using var policy = JsonDocument.Parse(e.GeoreferencingPolicy);
            var root = policy.RootElement;
            analysis.GeoreferencingStatus = Text(root, "status");
            analysis.GeoreferencingMethod = Text(root, "method");
            analysis.GeoreferencingConfidence = Text(root, "confidence");
            if (root.TryGetProperty("roiRadiusMetres", out var radius) &&
                radius.ValueKind == JsonValueKind.Number) analysis.TrustedRoiRadiusM = radius.GetDouble();
        }
        catch (JsonException)
        {
            // A malformed policy must not fail an otherwise good analysis; the detections carry
            // their own provenance regardless.
            analysis.GeoreferencingStatus = "Unparseable";
        }

        static string? Text(JsonElement root, string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static async Task Failed(AeroComplianceDbContext db, INotificationService notify, ImageAnalysisFailed e, CancellationToken ct)
    {
        var image=await db.Images.Include(x=>x.Flight).SingleAsync(x=>x.Id==e.ImageId,ct);if(image.ProcessingRequestId!=e.RequestId)return;
        image.ProcessingStatus=ImageProcessingStatus.AnalysisFailed;image.ProcessingCompletedAt=e.FailedAt;image.LastErrorCode=e.ErrorCode;image.LastErrorMessage=e.Message[..Math.Min(1000,e.Message.Length)];
        var analysis=await db.ImageAnalyses.SingleOrDefaultAsync(x=>x.RequestId==e.RequestId,ct);if(analysis is not null){analysis.Status="Failed";analysis.CompletedAt=e.FailedAt;analysis.FailureCode=e.ErrorCode;analysis.FailureMessage=image.LastErrorMessage;}

        // Operational alert only: the raw error code and message stay on the image row and never
        // reach the notification text. Keyed on the processing request, so a redelivered failure
        // event does not notify anyone a second time.
        var text = NotificationMessages.ImageProcessingFailed(image.OriginalFileName, image.Flight.MissionName);
        await notify.NotifyRolesAsync(new NotificationRequest(
                image.Flight.MunicipalityId,
                NotificationTypes.ImageProcessingFailed,
                NotificationSeverities.Critical,
                text.Title, text.Message,
                SourceEventKey: e.RequestId.ToString("N"),
                RelatedEntityType: "Image",
                RelatedEntityId: image.Id,
                Route: NotificationRoutes.FlightImages(image.FlightId)),
            FailureOversightRoles, ct,
            // The operator who flew the mission is told as well, when that account is still
            // active in this municipality; otherwise they are simply skipped.
            alsoNotifyUserId: image.Flight.DroneOperatorId);
    }

    /// <summary>Roles accountable for operational processing failures.</summary>
    private static readonly string[] FailureOversightRoles = ["Admin", "MunicipalityManager"];

    /// <summary>
    /// Phase 7: the deterministic zoning-rule engine runs first. The two original heuristics
    /// (any road overlap, any spill outside the parcel) are kept only as a fallback for the
    /// concern no enforceable rule covered — otherwise a road-setback rule and the road heuristic
    /// would both raise a violation for the same physical encroachment.
    /// </summary>
    private static async Task Evaluate(AeroComplianceDbContext db, INotificationService notify, Guid municipalityId, ImageAnalysis analysis, Detection detection, CancellationToken ct)
    {
        if (!ComplianceRuleEngine.IsEvaluableClass(detection.ClassLabel)) return;

        var applied = await ComplianceRuleEngine.EvaluateAsync(db, municipalityId, detection, analysis.Id, ct, notify);
        await db.SaveChangesAsync(ct);

        var covered = applied.Select(x => x.CanonicalKey).ToHashSet();
        var roadCovered = covered.Contains(ZoningRuleCatalog.MinimumRoadSetback);
        var parcelCovered = covered.Contains(ZoningRuleCatalog.MaximumOutsideParcelArea) ||
                            covered.Contains(ZoningRuleCatalog.MaximumParcelCoverage);

        var conn=(NpgsqlConnection)db.Database.GetDbConnection();
        if (!roadCovered)
        {
            var road=await SpatialHit(conn,"SELECT id, ST_Area(ST_Intersection(ST_Transform(geom,32636),ST_Transform(@g,32636))) FROM roads WHERE municipality_id=@m AND ST_Intersects(geom,@g) ORDER BY 2 DESC LIMIT 1",municipalityId,detection.Geom,ct);
            if(road is not null){await AddViolation(db,analysis,detection,"road_encroachment",road.Value.Id,null,null,road.Value.Value,ct);return;}
        }
        if (!parcelCovered)
        {
            var parcel=await SpatialHit(conn,"SELECT p.id, ST_Area(ST_Difference(ST_Transform(@g,32636),ST_Transform(p.geom,32636))) FROM parcels p JOIN blocks b ON p.block_id=b.id JOIN zones z ON b.zone_id=z.id WHERE z.municipality_id=@m AND ST_Intersects(p.geom,@g) ORDER BY ST_Area(ST_Intersection(ST_Transform(p.geom,32636),ST_Transform(@g,32636))) DESC LIMIT 1",municipalityId,detection.Geom,ct);
            if(parcel is not null&&parcel.Value.Value>0.01m)await AddViolation(db,analysis,detection,"parcel_encroachment",null,parcel.Value.Id,null,parcel.Value.Value,ct);
        }
    }

    private static async Task<(Guid Id,decimal Value)?> SpatialHit(NpgsqlConnection conn,string sql,Guid municipalityId,NetTopologySuite.Geometries.MultiPolygon geom,CancellationToken ct){await using var cmd=new NpgsqlCommand(sql,conn);cmd.Parameters.AddWithValue("m",municipalityId);cmd.Parameters.AddWithValue("g",NpgsqlDbType.Geometry,geom);await using var reader=await cmd.ExecuteReaderAsync(ct);if(!await reader.ReadAsync(ct))return null;return(reader.GetGuid(0),reader.IsDBNull(1)?0:Convert.ToDecimal(reader.GetDouble(1)));}
    private static async Task AddViolation(AeroComplianceDbContext db,ImageAnalysis analysis,Detection detection,string type,Guid? roadId,Guid? parcelId,Guid? ruleId,decimal measured,CancellationToken ct)
    {
        if(await db.ComplianceEvaluations.AnyAsync(x=>x.DetectionId==detection.Id&&x.EvaluationType==type,ct))return;
        var evaluation=new ComplianceEvaluation{Id=Guid.NewGuid(),ImageAnalysisId=analysis.Id,DetectionId=detection.Id,ZoningRuleId=ruleId,EvaluationType=type,MeasuredValue=measured,Unit="m2",Outcome="candidate_violation",MetricCrs=32636,CreatedAt=DateTimeOffset.UtcNow,
            // The evaluation is exactly as reliable as the geometry it measured.
            IsApproximate=detection.IsApproximate,RequiresManualReview=detection.RequiresManualReview,
            GeoreferencingConfidence=detection.GeoreferencingConfidence,SourceGeoreferencingMethod=detection.SourceGeoreferencingMethod,
            ResultSource=detection.ResultSource};
        db.ComplianceEvaluations.Add(evaluation);
        // Status 1 is the un-reviewed candidate state: nothing here ever auto-confirms a violation.
        var violation=new Violation{Id=Guid.NewGuid(),ComplianceEvaluationId=evaluation.Id,DetectionId=detection.Id,ParcelId=parcelId,RoadId=roadId,RuleId=ruleId,ViolationType=type,ExceedanceAreaM2=measured,AiConfidenceScore=detection.Confidence,CurrentStatusId=1,Geom=detection.Geom,CreatedAt=DateTimeOffset.UtcNow,UpdatedAt=DateTimeOffset.UtcNow,
            IsApproximate=detection.IsApproximate,
            // An approximate geometry always needs a human, whatever the detection asked for.
            RequiresManualReview=detection.RequiresManualReview||detection.IsApproximate,
            GeoreferencingConfidence=detection.GeoreferencingConfidence,SourceGeoreferencingMethod=detection.SourceGeoreferencingMethod,
            ResultSource=detection.ResultSource,IsExperimental=detection.IsApproximate};
        db.Violations.Add(violation);
        var note=detection.IsApproximate
            ? $"Created by automated compliance evaluation from an ESTIMATED georeference ({detection.SourceGeoreferencingMethod}). Experimental candidate — requires manual review; not valid for enforcement."
            : "Created by automated compliance evaluation.";
        db.ViolationStatusHistory.Add(new ViolationStatusHistory{Id=Guid.NewGuid(),ViolationId=violation.Id,StatusId=1,Notes=note,ChangedAt=DateTimeOffset.UtcNow});
    }
}
