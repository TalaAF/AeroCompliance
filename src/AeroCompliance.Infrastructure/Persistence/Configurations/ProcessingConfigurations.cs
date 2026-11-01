using AeroCompliance.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AeroCompliance.Infrastructure.Persistence.Configurations;

public sealed class ImageAnalysisConfiguration : IEntityTypeConfiguration<ImageAnalysis>
{
    public void Configure(EntityTypeBuilder<ImageAnalysis> b)
    {
        b.ToTable("image_analyses"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id"); b.Property(x => x.ImageId).HasColumnName("image_id");
        b.Property(x => x.RequestId).HasColumnName("request_id"); b.Property(x => x.Mode).HasColumnName("mode").HasMaxLength(20);
        b.Property(x => x.ModelName).HasColumnName("model_name").HasMaxLength(100); b.Property(x => x.ModelVersion).HasColumnName("model_version").HasMaxLength(50);
        b.Property(x => x.ConfidenceThreshold).HasColumnName("confidence_threshold").HasColumnType("numeric(5,4)");
        b.Property(x => x.SourceCrs).HasColumnName("source_crs").HasMaxLength(100); b.Property(x => x.OutputCrs).HasColumnName("output_crs").HasMaxLength(30);
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(40); b.Property(x => x.AttemptNumber).HasColumnName("attempt_number");
        b.Property(x => x.StartedAt).HasColumnName("started_at"); b.Property(x => x.CompletedAt).HasColumnName("completed_at");
        b.Property(x => x.DurationMs).HasColumnName("duration_ms"); b.Property(x => x.FailureCode).HasColumnName("failure_code").HasMaxLength(100);
        b.Property(x => x.FailureMessage).HasColumnName("failure_message").HasMaxLength(1000);
        // Phase 8: approximate-georeference declaration + retained non-geographic predictions.
        b.Property(x => x.GeoreferencingStatus).HasColumnName("georeferencing_status").HasMaxLength(30);
        b.Property(x => x.GeoreferencingMethod).HasColumnName("georeferencing_method").HasMaxLength(100);
        b.Property(x => x.GeoreferencingConfidence).HasColumnName("georeferencing_confidence").HasMaxLength(30);
        b.Property(x => x.TrustedRoiRadiusM).HasColumnName("trusted_roi_radius_m");
        b.Property(x => x.ModelOnlyDetections).HasColumnName("model_only_detections").HasColumnType("jsonb");
        b.Property(x => x.ModelOnlyDetectionCount).HasColumnName("model_only_detection_count").HasDefaultValue(0);
        b.HasIndex(x => x.RequestId).IsUnique(); b.HasIndex(x => x.ImageId);
        b.HasOne(x => x.Image).WithMany(x => x.Analyses).HasForeignKey(x => x.ImageId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ToTable("outbox_messages"); b.HasKey(x => x.EventId); b.Property(x => x.EventId).HasColumnName("event_id");
        b.Property(x => x.Type).HasColumnName("type").HasMaxLength(100); b.Property(x => x.RoutingKey).HasColumnName("routing_key").HasMaxLength(150);
        b.Property(x => x.SchemaVersion).HasColumnName("schema_version"); b.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
        b.Property(x => x.CreatedAt).HasColumnName("created_at"); b.Property(x => x.PublishedAt).HasColumnName("published_at");
        b.Property(x => x.AttemptCount).HasColumnName("attempt_count"); b.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(1000);
        b.Property(x => x.ClaimToken).HasColumnName("claim_token"); b.Property(x => x.ClaimUntil).HasColumnName("claim_until");
        b.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at").HasDefaultValueSql("now()");
        b.Property(x => x.FailedAt).HasColumnName("failed_at");
        b.HasIndex(x => x.PublishedAt);
        b.HasIndex(x => new { x.FailedAt, x.NextAttemptAt, x.ClaimUntil });
    }
}

public sealed class AiProcessingJobConfiguration : IEntityTypeConfiguration<AiProcessingJob>
{
    public void Configure(EntityTypeBuilder<AiProcessingJob> b)
    {
        b.ToTable("ai_processing_jobs", table =>
            table.HasCheckConstraint("CK_ai_processing_jobs_state", "state IN ('processing', 'result_ready')"));
        b.HasKey(x => x.RequestId);
        b.Property(x => x.RequestId).HasColumnName("request_id");
        b.Property(x => x.RequestHash).HasColumnName("request_hash").HasMaxLength(64).IsRequired();
        b.Property(x => x.State).HasColumnName("state").HasMaxLength(20).IsRequired();
        b.Property(x => x.LeaseToken).HasColumnName("lease_token");
        b.Property(x => x.LeaseUntil).HasColumnName("lease_until");
        b.Property(x => x.ResultRoutingKey).HasColumnName("result_routing_key").HasMaxLength(150);
        b.Property(x => x.ResultPayload).HasColumnName("result_payload").HasColumnType("jsonb");
        b.Property(x => x.AttemptCount).HasColumnName("attempt_count");
        b.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        b.HasIndex(x => new { x.State, x.LeaseUntil });
    }
}

public sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> b)
    {
        b.ToTable("inbox_messages"); b.HasKey(x => x.Id); b.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        b.Property(x => x.EventId).HasColumnName("event_id"); b.Property(x => x.Consumer).HasColumnName("consumer").HasMaxLength(100);
        b.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(100); b.Property(x => x.PayloadHash).HasColumnName("payload_hash").HasMaxLength(64);
        b.Property(x => x.ReceivedAt).HasColumnName("received_at"); b.Property(x => x.CompletedAt).HasColumnName("completed_at");
        b.HasIndex(x => new { x.EventId, x.Consumer }).IsUnique();
    }
}

public sealed class ComplianceEvaluationConfiguration : IEntityTypeConfiguration<ComplianceEvaluation>
{
    public void Configure(EntityTypeBuilder<ComplianceEvaluation> b)
    {
        b.ToTable("compliance_evaluations"); b.HasKey(x => x.Id); b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.ImageAnalysisId).HasColumnName("image_analysis_id"); b.Property(x => x.DetectionId).HasColumnName("detection_id");
        b.Property(x => x.ZoningRuleId).HasColumnName("zoning_rule_id"); b.Property(x => x.EvaluationType).HasColumnName("evaluation_type").HasMaxLength(60);
        b.Property(x => x.MeasuredValue).HasColumnName("measured_value").HasColumnType("numeric(14,3)"); b.Property(x => x.RequiredValue).HasColumnName("required_value").HasColumnType("numeric(14,3)");
        b.Property(x => x.Unit).HasColumnName("unit").HasMaxLength(20); b.Property(x => x.Outcome).HasColumnName("outcome").HasMaxLength(30);
        b.Property(x => x.MetricCrs).HasColumnName("metric_crs"); b.Property(x => x.CreatedAt).HasColumnName("created_at");
        // Phase 8: reliability inherited from the evaluated detection.
        b.Property(x => x.IsApproximate).HasColumnName("is_approximate").HasDefaultValue(false);
        b.Property(x => x.RequiresManualReview).HasColumnName("requires_manual_review").HasDefaultValue(false);
        b.Property(x => x.GeoreferencingConfidence).HasColumnName("georeferencing_confidence").HasMaxLength(30);
        b.Property(x => x.SourceGeoreferencingMethod).HasColumnName("source_georeferencing_method").HasMaxLength(100);
        b.Property(x => x.ResultSource).HasColumnName("result_source").HasMaxLength(50);
        b.HasIndex(x => new { x.DetectionId, x.EvaluationType }).IsUnique();
        b.HasOne(x => x.ImageAnalysis).WithMany().HasForeignKey(x => x.ImageAnalysisId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Detection).WithMany(x => x.ComplianceEvaluations).HasForeignKey(x => x.DetectionId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.ZoningRule).WithMany().HasForeignKey(x => x.ZoningRuleId).OnDelete(DeleteBehavior.Restrict);
    }
}
