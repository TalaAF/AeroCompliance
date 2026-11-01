# Multi-instance processing coordination (2026-11-01)

This change is source-only. It does not migrate or restart the current installation.

## Upgrade order

Back up PostgreSQL. Apply EF migration `20261101081029_MultiInstanceProcessing`
with the normal migration runner before starting updated API or AI worker instances.
The additive migration preserves existing outbox rows. Set
`ConnectionStrings__aerocompliancedb` for every AI worker. AppHost and the checked-in
Compose configuration provide it. The worker stays unready until `ai_processing_jobs`
exists. Production API startup does not apply migrations automatically.

## Outbox

Dispatchers atomically select due rows with PostgreSQL `FOR UPDATE SKIP LOCKED` and
claim tokens. A crash leaves a two-minute lease that another dispatcher can reclaim.
Claim tokens fence late updates. Each publish is broker-confirmed and recorded separately.
Failures use exponential delays (2, 4, 8, 16 seconds, capped at 300 seconds) and stop
after five attempts. An expired fifth claim also enters the failed state. All lease and
retry timestamps use the database clock.

Inspect failed messages with:

```sql
SELECT event_id, type, routing_key, attempt_count, last_error, failed_at
FROM outbox_messages WHERE failed_at IS NOT NULL ORDER BY failed_at;
```

After fixing the cause, retry a specifically reviewed message by its event ID:

```sql
UPDATE outbox_messages
SET failed_at = NULL, attempt_count = 0, claim_token = NULL,
    claim_until = NULL, next_attempt_at = now(), last_error = NULL
WHERE event_id = '<reviewed-event-uuid>' AND published_at IS NULL
  AND failed_at IS NOT NULL;
```

The broker may accept a message just before its dispatcher crashes or loses its claim.
Reclaiming that row can publish a duplicate. Delivery remains **at least once**, not
exactly once; consumers must continue using inbox/idempotency checks.

## AI jobs

`ai_processing_jobs` is keyed by request UUID and stores a hash of the request body.
Only one worker can hold a live lease. A worker renews its lease during long inference;
after a crash another worker can reclaim it, and the old token cannot store a result.
The completed or failed result payload and routing key are committed before publication.
A redelivery after a publish or acknowledgement failure republishes that result without
rerunning inference. The heartbeat-safe consumer-thread acknowledgement and both mock
and YOLO modes remain. A failed result publication waits two seconds before requeueing,
so a broken broker route does not create a tight retry loop. Job rows and results are
retained; a retention policy needs a separate decision because late redeliveries depend
on those records.

## Reports

Generation holds a PostgreSQL transaction-scoped advisory lock for the violation through
PDF upload and report-row commit. Other API instances receive 409 during generation.
After success, the existing report is returned unless regeneration is requested.
Failed upload rolls back and can be retried. PostgreSQL releases the lock on a crash.
An upload followed by a crash before database commit can leave an orphaned PDF that the
database does not reference. An ambiguous commit retry uses the same report ID. Making
client retries of explicit regeneration unambiguous needs a separate idempotency-key
decision.

## Verification scope

Integration tests use disposable real PostGIS/PostgreSQL for competing claims, lease
expiry, retry exhaustion, AI job deduplication/replay, and concurrent report requests.
The outbox publish test also uses disposable RabbitMQ. A separate isolated local stack
ran the updated API, processing service, and worker with disposable PostGIS, RabbitMQ,
and MinIO. Mock upload, processing, candidate review, and PDF download completed.
Temporarily removing RabbitMQ's completed-result route produced a real publication
failure; after restoring it, the worker replayed the saved result with one inference
claim. Republishing the identical request also left one analysis and one candidate.
Ten concurrent report requests across two API instances produced one report row.
A separate real YOLO job with the mounted checkpoint completed with 49 model-only
predictions on a PNG with no raster georeferencing. AppHost and single-container startup
were inspected for the worker database setting but were not booted in that test.
