using AeroCompliance.ApiService.Authorization;
using AeroCompliance.ApiService.Services;
using AeroCompliance.Contracts.Dtos.Reports;
using AeroCompliance.Domain.Entities;
using AeroCompliance.Infrastructure.Notifications;
using AeroCompliance.Infrastructure.Persistence;
using AeroCompliance.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AeroCompliance.ApiService.Controllers;

/// <summary>
/// Report generation for a single violation. Generation is a deliberate human action on an
/// already-confirmed violation — it never runs automatically off an AI detection, and it never
/// alters the violation's review decision.
/// </summary>
[ApiController, Route("api/violations/{violationId:guid}/reports"), Authorize(Policy = Policies.GenerateReports)]
public sealed class ViolationReportsController(
    AeroComplianceDbContext db, ICurrentUserContext user, IObjectStorage storage, IAuditService audit,
    INotificationService notify, IConfiguration configuration, ILogger<ViolationReportsController> logger) : ControllerBase
{
    private const string ConfirmedCode = "confirmed_under_notification";
    private const string HumanSource = "human_review";

    /// <summary>
    /// Guards against a double-submitted generate request producing two PDFs for the same
    /// violation. This is per-process: a multi-instance deployment would need a database or
    /// distributed lock instead.
    /// </summary>
    private static readonly HashSet<Guid> InFlight = [];
    private static readonly object InFlightSync = new();

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ReportSummaryDto>>> List(Guid violationId, CancellationToken ct)
    {
        if (!await VisibleViolations().AnyAsync(x => x.Id == violationId, ct)) return NotFound();
        var rows = await db.Reports.AsNoTracking().Where(x => x.ViolationId == violationId)
            .OrderByDescending(x => x.GeneratedAt)
            .Select(x => new { x.Id, x.GeneratedAt, x.GeneratedBy, x.SignedOffBy })
            .ToListAsync(ct);
        return Ok(rows.Select(x => new
        {
            x.Id,
            Reference = ReportReference.For(x.Id, x.GeneratedAt),
            x.GeneratedAt,
            GeneratedBy = ReportsController.SourceLabel(x.GeneratedBy),
            SignedOff = x.SignedOffBy is not null
        }).ToList<object>());
    }

    [HttpPost]
    public async Task<ActionResult<ReportSummaryDto>> Generate(Guid violationId, bool regenerate = false, CancellationToken ct = default)
    {
        lock (InFlightSync)
        {
            if (!InFlight.Add(violationId))
                return Problem(statusCode: 409, title: "A report for this violation is already being generated.");
        }
        try
        {
            var violation = await LoadAsync(violationId, ct);
            if (violation is null) return NotFound();
            if (violation.CurrentStatus.Code != ConfirmedCode)
                return Problem(statusCode: 409, title: "The violation is not confirmed.",
                    detail: "A formal report can be generated after the violation is confirmed.");

            // Without an explicit regenerate flag, a repeated submission returns what already exists
            // rather than stacking up near-identical PDFs.
            if (!regenerate)
            {
                var existing = await db.Reports.AsNoTracking().Where(x => x.ViolationId == violationId)
                    .OrderByDescending(x => x.GeneratedAt).FirstOrDefaultAsync(ct);
                if (existing is not null) return Ok(await SummaryAsync(existing.Id, ct));
            }

            var municipalityId = Tenant(violation);
            if (municipalityId is null)
                return Problem(statusCode: 409, title: "The violation is not linked to a municipality.");

            var reportId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var reference = ReportReference.For(reportId, now);
            var key = $"reports/{municipalityId:N}/{violationId:N}/{reportId:N}.pdf";
            var source = configuration.GetValue<bool>("DemoMode:Enabled") &&
                Request.Headers.TryGetValue("X-AeroCompliance-Demo-Seed", out var demoSeed) &&
                demoSeed.ToString() == "1" ? "demo_seed" : HumanSource;

            var model = await BuildModelAsync(violation, reference, now, source == "demo_seed", ct);
            byte[] pdf;
            try { pdf = ViolationReportPdf.Render(model); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Rendering the PDF for violation {ViolationId} failed.", violationId);
                return Problem(statusCode: 500, title: "The report could not be produced.");
            }

            await storage.UploadBytesAsync(key, pdf, "application/pdf", ct);
            try
            {
                db.Reports.Add(new Report
                {
                    Id = reportId, ViolationId = violationId, StoragePath = key,
                    GeneratedBy = source, GeneratedAt = now
                });
                audit.Add("report.generate", "Report", reportId,
                    after: new { reference, violation = ReportReference.Violation(violationId), source, generatedAt = now },
                    municipalityId: municipalityId);

                // Only the person who asked for the report is told it is ready, and the row and the
                // notification commit together.
                if (user.UserId is Guid requester)
                {
                    var place = violation.Road?.RoadCode ?? violation.Parcel?.ParcelNumber ?? violation.Zone?.ZoneNumber;
                    var text = NotificationMessages.ReportGenerated(reference, place);
                    await notify.NotifyUserAsync(new NotificationRequest(
                        municipalityId.Value,
                        NotificationTypes.ReportGenerated,
                        NotificationSeverities.Information,
                        text.Title, text.Message,
                        SourceEventKey: reportId.ToString("N"),
                        RelatedEntityType: "Report",
                        RelatedEntityId: reportId,
                        Route: NotificationRoutes.Report(reportId)), requester, ct);
                }
                await db.SaveChangesAsync(ct);
            }
            catch
            {
                // The PDF is already in storage but the row did not commit — remove the orphan so
                // the bucket cannot accumulate objects no report record points at.
                try { await storage.DeleteAsync(key, CancellationToken.None); }
                catch (Exception cleanup) { logger.LogError(cleanup, "Orphaned report object {Key} could not be removed.", key); }
                throw;
            }

            var summary = await SummaryAsync(reportId, ct);
            return CreatedAtAction(nameof(ReportsController.Get), "Reports", new { id = reportId }, summary);
        }
        finally
        {
            lock (InFlightSync) InFlight.Remove(violationId);
        }
    }

    // --- Data ---------------------------------------------------------------------------------

    private IQueryable<Violation> VisibleViolations()
    {
        var q = db.Violations.AsQueryable();
        return user.IsPlatformAdmin ? q : q.Where(x =>
            (x.Detection.Image != null && x.Detection.Image.Flight.MunicipalityId == user.MunicipalityId) ||
            (x.Detection.Orthomosaic != null && x.Detection.Orthomosaic.Flight.MunicipalityId == user.MunicipalityId));
    }

    private Task<Violation?> LoadAsync(Guid id, CancellationToken ct) => VisibleViolations().AsNoTracking()
        .Include(x => x.CurrentStatus)
        .Include(x => x.ComplianceEvaluation)
        .Include(x => x.Road).Include(x => x.Parcel).Include(x => x.Zone).Include(x => x.Block)
        .Include(x => x.EvidenceImage)
        .Include(x => x.Detection).ThenInclude(d => d.Image!).ThenInclude(i => i.Flight).ThenInclude(f => f.Municipality)
        .Include(x => x.Detection).ThenInclude(d => d.Orthomosaic!).ThenInclude(o => o.Flight).ThenInclude(f => f.Municipality)
        .Include(x => x.StatusHistory).ThenInclude(h => h.Status)
        .Include(x => x.StatusHistory).ThenInclude(h => h.ChangedByUser)
        .SingleOrDefaultAsync(x => x.Id == id, ct);

    private static Guid? Tenant(Violation v) =>
        v.Detection.Image?.Flight.MunicipalityId ?? v.Detection.Orthomosaic?.Flight.MunicipalityId;

    private async Task<ViolationReportModel> BuildModelAsync(Violation v, string reference, DateTimeOffset now,
        bool isSyntheticDemo, CancellationToken ct)
    {
        var decision = v.StatusHistory.Where(h => h.StatusId == v.CurrentStatusId)
            .OrderByDescending(h => h.ChangedAt).FirstOrDefault();
        var centroid = v.Geom is null || v.Geom.IsEmpty ? null : v.Geom.Centroid;
        var (evidence, unavailable) = await EvidenceAsync(v, ct);

        return new ViolationReportModel(
            reference,
            v.Detection.Image?.Flight.Municipality?.Name ?? v.Detection.Orthomosaic?.Flight.Municipality?.Name ?? "Unknown municipality",
            now,
            ReportReference.Violation(v.Id),
            ReportsController.Label(v.CurrentStatus),
            v.ViolationType,
            v.Detection.ClassLabel,
            v.Detection.Confidence,
            v.Detection.Image?.Flight.MissionName ?? v.Detection.Orthomosaic?.Flight.MissionName,
            v.Detection.Image?.OriginalFileName,
            v.Road?.RoadCode, v.Road?.Name, v.Parcel?.ParcelNumber, v.Zone?.ZoneNumber, v.Block?.BlockNumber,
            centroid?.Y, centroid?.X,
            v.ComplianceEvaluation?.EvaluationType,
            v.MeasuredValue, v.RequiredValue, v.ExceedanceValue, v.ExceedanceAreaM2,
            v.ComplianceEvaluation?.Unit,
            ReportsController.Label(v.CurrentStatus),
            decision?.ChangedByUser?.FullName,
            decision?.ChangedAt,
            decision?.Notes,
            v.StatusHistory.OrderBy(h => h.ChangedAt)
                .Select(h => (ReportsController.Label(h.Status), h.ChangedByUser?.FullName, h.ChangedAt, h.Notes)).ToList(),
            evidence,
            unavailable,
            isSyntheticDemo);
    }

    /// <summary>
    /// Fetches an embeddable preview of the evidence image. Only raster formats the PDF renderer
    /// can decode are embedded; a GeoTIFF (or a missing object) yields a plain-language note
    /// instead of failing the report. The storage key is never surfaced either way.
    /// </summary>
    private async Task<(byte[]? Bytes, string? Reason)> EvidenceAsync(Violation v, CancellationToken ct)
    {
        var image = v.EvidenceImage ?? v.Detection.Image;
        if (image is null) return (null, "No evidence image is linked to this violation.");

        var type = image.ContentType?.ToLowerInvariant() ?? "";
        if (type is not ("image/jpeg" or "image/png"))
            return (null, "The source is stored as geospatial imagery, which cannot be previewed in this document. " +
                          "It remains available in AeroCompliance under the flight's images.");

        const int maxPreviewBytes = 12 * 1024 * 1024;
        if (image.ByteLength > maxPreviewBytes)
            return (null, "The evidence image is too large to embed. It remains available in AeroCompliance.");

        try
        {
            await using var source = await storage.OpenReadAsync(image.ObjectKey, ct);
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, ct);
            return (buffer.ToArray(), null);
        }
        catch (Exception ex)
        {
            // An unavailable object must degrade the report, never fail it.
            logger.LogWarning(ex, "Evidence image for violation {ViolationId} could not be read.", v.Id);
            return (null, "The evidence image could not be retrieved when this report was produced.");
        }
    }

    private async Task<ReportSummaryDto> SummaryAsync(Guid reportId, CancellationToken ct)
    {
        var x = await db.Reports.AsNoTracking()
            .Include(r => r.SignedOffByUser)
            .Include(r => r.Violation).ThenInclude(v => v.CurrentStatus)
            .Include(r => r.Violation).ThenInclude(v => v.Road)
            .Include(r => r.Violation).ThenInclude(v => v.Parcel)
            .Include(r => r.Violation).ThenInclude(v => v.Detection).ThenInclude(d => d.Image!).ThenInclude(i => i.Flight).ThenInclude(f => f.Municipality)
            .Include(r => r.Violation).ThenInclude(v => v.Detection).ThenInclude(d => d.Orthomosaic!).ThenInclude(o => o.Flight).ThenInclude(f => f.Municipality)
            .SingleAsync(r => r.Id == reportId, ct);
        return new ReportSummaryDto(x.Id, ReportReference.For(x.Id, x.GeneratedAt),
            ReportReference.Violation(x.ViolationId), x.Violation.ViolationType.Replace('_', ' '),
            x.Violation.Detection.Image?.Flight.Municipality?.Name
                ?? x.Violation.Detection.Orthomosaic?.Flight.Municipality?.Name ?? "Unknown municipality",
            ReportsController.Label(x.Violation.CurrentStatus), x.Violation.Road?.RoadCode, x.Violation.Road?.Name,
            x.Violation.Parcel?.ParcelNumber, x.GeneratedAt, ReportsController.SourceLabel(x.GeneratedBy),
            x.SignedOffBy is not null, x.SignedOffByUser?.FullName, x.SignedOffAt, true);
    }
}
