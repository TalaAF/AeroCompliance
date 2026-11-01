using AeroCompliance.Domain.Entities;
using AeroCompliance.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroCompliance.ApiService.Services;

/// <summary>Atomic PostgreSQL claims; a lease is only an ownership hint, not exactly-once delivery.</summary>
public sealed class OutboxClaimStore(AeroComplianceDbContext db)
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    public async Task<(Guid Token, IReadOnlyList<OutboxMessage> Messages)> ClaimAsync(int batchSize, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE outbox_messages SET failed_at = now(), claim_token = NULL,
                claim_until = NULL, last_error = 'Claim expired after retry limit'
            WHERE published_at IS NULL AND failed_at IS NULL
              AND attempt_count >= {MaxAttempts}
              AND (claim_until IS NULL OR claim_until <= now())
            """, ct);

        var token = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH due AS (
                SELECT event_id FROM outbox_messages
                WHERE published_at IS NULL AND failed_at IS NULL
                  AND next_attempt_at <= now()
                  AND (claim_until IS NULL OR claim_until <= now())
                  AND attempt_count < {MaxAttempts}
                ORDER BY created_at, event_id
                FOR UPDATE SKIP LOCKED
                LIMIT {batchSize}
            )
            UPDATE outbox_messages AS message
            SET claim_token = {token}, claim_until = now() + {Lease},
                attempt_count = message.attempt_count + 1
            FROM due WHERE message.event_id = due.event_id
            """, ct);

        var messages = await db.OutboxMessages.AsNoTracking()
            .Where(x => x.ClaimToken == token).OrderBy(x => x.CreatedAt).ToListAsync(ct);
        return (token, messages);
    }

    public Task<int> PublishedAsync(Guid eventId, Guid token, CancellationToken ct)
    {
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE outbox_messages SET published_at = now(), claim_token = NULL,
                claim_until = NULL, last_error = NULL
            WHERE event_id = {eventId} AND claim_token = {token} AND published_at IS NULL
            """, ct);
    }

    public Task<int> FailedAsync(OutboxMessage message, Guid token, string error, CancellationToken ct)
    {
        var exhausted = message.AttemptCount >= MaxAttempts;
        var delaySeconds = Math.Min(300, (int)Math.Pow(2, message.AttemptCount));
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE outbox_messages SET claim_token = NULL, claim_until = NULL,
                next_attempt_at = now() + ({delaySeconds} * interval '1 second'),
                failed_at = CASE WHEN {exhausted} THEN now() ELSE NULL END,
                last_error = {error}
            WHERE event_id = {message.EventId} AND claim_token = {token} AND published_at IS NULL
            """, ct);
    }
}
