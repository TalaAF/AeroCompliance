namespace AeroCompliance.Domain.Entities;

public class OutboxMessage
{
    public Guid EventId { get; set; }
    public string Type { get; set; } = null!;
    public string RoutingKey { get; set; } = null!;
    public int SchemaVersion { get; set; }
    public string Payload { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public Guid? ClaimToken { get; set; }
    public DateTimeOffset? ClaimUntil { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
}

/// <summary>Durable AI result and lease for one stable processing request.</summary>
public class AiProcessingJob
{
    public Guid RequestId { get; set; }
    public string RequestHash { get; set; } = null!;
    public string State { get; set; } = null!;
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? ResultRoutingKey { get; set; }
    public string? ResultPayload { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class InboxMessage
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public string Consumer { get; set; } = null!;
    public string EventType { get; set; } = null!;
    public string? PayloadHash { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
