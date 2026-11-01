using AeroCompliance.ApiService.Authorization;
using AeroCompliance.ApiService.Services;
using AeroCompliance.Contracts.Dtos.Common;
using AeroCompliance.Contracts.Dtos.Reports;
using AeroCompliance.Domain.Entities;
using AeroCompliance.Infrastructure.Persistence;
using AeroCompliance.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AeroCompliance.ApiService.Controllers;

/// <summary>
/// Formal violation reports. A report is a human-reviewed output: it can only be produced for a
/// confirmed violation, its PDF lives in object storage, and it is only reachable by streaming
/// through this service — storage keys, buckets and credentials never reach a client.
/// </summary>
[ApiController, Route("api/reports"), Authorize(Policy = Policies.ViewReports)]
public sealed class ReportsController(AeroComplianceDbContext db, ICurrentUserContext user, IObjectStorage storage)
    : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResponse<ReportSummaryDto>>> List(int page = 1, int pageSize = 25,
        DateTimeOffset? from = null, DateTimeOffset? to = null, string? search = null, string? generatedBy = null,
        Guid? municipalityId = null, CancellationToken ct = default)
    {
        if (!ApiSupport.IsValidPagination(page, pageSize)) return Problem(statusCode: 400, title: "Invalid pagination");
        if (from.HasValue && to.HasValue && to < from) return Problem(statusCode: 400, title: "Invalid date range");
        if (municipalityId.HasValue && !user.IsPlatformAdmin && municipalityId != user.MunicipalityId)
            return Problem(statusCode: 403, title: "Reports are limited to your own municipality.");

        IQueryable<Report> q = Visible()
            .Include(x => x.Violation).ThenInclude(v => v.CurrentStatus)
            .Include(x => x.Violation).ThenInclude(v => v.Road)
            .Include(x => x.Violation).ThenInclude(v => v.Parcel)
            .Include(x => x.Violation).ThenInclude(v => v.Detection).ThenInclude(d => d.Image!).ThenInclude(i => i.Flight).ThenInclude(f => f.Municipality)
            .Include(x => x.Violation).ThenInclude(v => v.Detection).ThenInclude(d => d.Orthomosaic!).ThenInclude(o => o.Flight).ThenInclude(f => f.Municipality)
            .Include(x => x.SignedOffByUser)
            .AsNoTracking();

        if (from.HasValue) q = q.Where(x => x.GeneratedAt >= from);
        if (to.HasValue) q = q.Where(x => x.GeneratedAt <= to);
        if (!string.IsNullOrWhiteSpace(generatedBy)) q = q.Where(x => x.GeneratedBy == generatedBy.Trim());
        if (municipalityId.HasValue) q = q.Where(x => Tenant(x.Violation) == municipalityId);

        // The report/violation references are derived from ids, so a reference search is matched
        // against the id text the reference is built from rather than a stored column.
        var s = ApiSupport.NormalizeSearch(search);
        if (s is not null)
        {
            var bare = s.Replace("ac-", "").Replace("-", "");
            q = q.Where(x => x.Id.ToString().Contains(bare) || x.ViolationId.ToString().Contains(bare) ||
                             x.Violation.ViolationType.ToLower().Contains(s));
        }

        var total = await q.CountAsync(ct);
        // Rows are materialised before mapping because the reference/tenant helpers are plain C#.
        var rows = await q.OrderByDescending(x => x.GeneratedAt)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var items = rows.Select(Summary).ToList();
        return Ok(new PagedResponse<ReportSummaryDto>(items, page, pageSize, total,
            total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize)));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ReportDetailsDto>> Get(Guid id, CancellationToken ct)
    {
        var x = await Detailed().SingleOrDefaultAsync(r => r.Id == id, ct);
        return x is null ? NotFound() : Ok(Details(x));
    }

    /// <summary>
    /// Streams the stored PDF through the API. Cross-tenant ids are indistinguishable from
    /// unknown ones (404), and the object key never appears in the response.
    /// </summary>
    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var report = await Visible().AsNoTracking()
            .Where(x => x.Id == id).Select(x => new { x.Id, x.StoragePath, x.GeneratedAt }).SingleOrDefaultAsync(ct);
        if (report is null) return NotFound();
        if (!await storage.ExistsAsync(report.StoragePath, ct))
            return Problem(statusCode: 404, title: "The report file is no longer available.");

        var stream = await storage.OpenReadAsync(report.StoragePath, ct);
        return File(stream, "application/pdf", $"{ReportReference.For(report.Id, report.GeneratedAt)}.pdf");
    }

    // --- Queries ------------------------------------------------------------------------------

    /// <summary>Tenant-scoped report set. Non-admins only ever see their own municipality's reports.</summary>
    private IQueryable<Report> Visible()
    {
        var q = db.Reports.AsQueryable();
        return user.IsPlatformAdmin ? q : q.Where(x =>
            (x.Violation.Detection.Image != null && x.Violation.Detection.Image.Flight.MunicipalityId == user.MunicipalityId) ||
            (x.Violation.Detection.Orthomosaic != null && x.Violation.Detection.Orthomosaic.Flight.MunicipalityId == user.MunicipalityId));
    }

    private IQueryable<Report> Detailed() => Visible().AsNoTracking()
        .Include(x => x.SignedOffByUser)
        .Include(x => x.Violation).ThenInclude(v => v.CurrentStatus)
        .Include(x => x.Violation).ThenInclude(v => v.ComplianceEvaluation)
        .Include(x => x.Violation).ThenInclude(v => v.Road)
        .Include(x => x.Violation).ThenInclude(v => v.Parcel)
        .Include(x => x.Violation).ThenInclude(v => v.Zone)
        .Include(x => x.Violation).ThenInclude(v => v.Block)
        .Include(x => x.Violation).ThenInclude(v => v.Detection).ThenInclude(d => d.Image!).ThenInclude(i => i.Flight).ThenInclude(f => f.Municipality)
        .Include(x => x.Violation).ThenInclude(v => v.Detection).ThenInclude(d => d.Orthomosaic!).ThenInclude(o => o.Flight).ThenInclude(f => f.Municipality)
        .Include(x => x.Violation).ThenInclude(v => v.StatusHistory).ThenInclude(h => h.Status)
        .Include(x => x.Violation).ThenInclude(v => v.StatusHistory).ThenInclude(h => h.ChangedByUser);

    // --- Mapping ------------------------------------------------------------------------------

    private static Guid? Tenant(Violation v) =>
        v.Detection.Image != null ? v.Detection.Image.Flight.MunicipalityId :
        v.Detection.Orthomosaic != null ? v.Detection.Orthomosaic.Flight.MunicipalityId : null;

    private static string MunicipalityName(Violation v) =>
        v.Detection.Image?.Flight.Municipality?.Name ??
        v.Detection.Orthomosaic?.Flight.Municipality?.Name ?? "Unknown municipality";

    private static ReportSummaryDto Summary(Report x) => new(
        x.Id,
        ReportReference.For(x.Id, x.GeneratedAt),
        ReportReference.Violation(x.ViolationId),
        Words(x.Violation.ViolationType),
        MunicipalityName(x.Violation),
        Label(x.Violation.CurrentStatus),
        x.Violation.Road?.RoadCode,
        x.Violation.Road?.Name,
        x.Violation.Parcel?.ParcelNumber,
        x.GeneratedAt,
        SourceLabel(x.GeneratedBy),
        x.SignedOffBy is not null,
        x.SignedOffByUser?.FullName,
        x.SignedOffAt,
        true);

    private static ReportDetailsDto Details(Report x)
    {
        var v = x.Violation;
        var history = v.StatusHistory.OrderBy(h => h.ChangedAt)
            .Select(h => new ReportHistoryDto(h.Status.Code, Label(h.Status), h.ChangedByUser?.FullName, h.Notes, h.ChangedAt))
            .ToList();
        // The decision entry is the most recent history row that moved the violation to its current status.
        var decision = v.StatusHistory.Where(h => h.StatusId == v.CurrentStatusId)
            .OrderByDescending(h => h.ChangedAt).FirstOrDefault();

        return new ReportDetailsDto(
            x.Id,
            ReportReference.For(x.Id, x.GeneratedAt),
            MunicipalityName(v),
            v.Id,
            ReportReference.Violation(v.Id),
            Words(v.ViolationType),
            v.CurrentStatus.Code,
            Label(v.CurrentStatus),
            Words(v.Detection.ClassLabel),
            v.Detection.Confidence,
            v.Detection.Image?.Flight.MissionName ?? v.Detection.Orthomosaic?.Flight.MissionName,
            v.Detection.Image?.OriginalFileName,
            v.Road?.RoadCode, v.Road?.Name, v.Parcel?.ParcelNumber, v.Zone?.ZoneNumber, v.Block?.BlockNumber,
            v.ComplianceEvaluation is null ? null : Words(v.ComplianceEvaluation.EvaluationType),
            v.MeasuredValue, v.RequiredValue, v.ExceedanceValue, v.ExceedanceAreaM2,
            v.ComplianceEvaluation?.Unit,
            decision?.Notes,
            decision?.ChangedByUser?.FullName,
            decision?.ChangedAt,
            history,
            v.Geom is null ? null : GeoJsonService.Write(v.Geom),
            x.GeneratedAt,
            SourceLabel(x.GeneratedBy),
            x.SignedOffBy is not null,
            x.SignedOffByUser?.FullName,
            x.SignedOffAt,
            true);
    }

    private static string Words(string value) => value.Replace('_', ' ');

    /// <summary>Seeded statuses carry an English label; fall back to the code so a row is never blank.</summary>
    internal static string Label(ViolationStatus status) => status.LabelEn ?? Words(status.Code);

    /// <summary>Turns the stored provenance code into display text without leaking the raw code.</summary>
    internal static string SourceLabel(string generatedBy) => generatedBy switch
    {
        "human_review" => "Human review",
        "demo_seed" => "Synthetic demo workflow",
        "ai_draft" => "AI draft",
        _ => Words(generatedBy)
    };
}

/// <summary>
/// Human-readable references derived deterministically from the row's identifier. Keeping these
/// derived (rather than stored) means no schema change is needed, and the same id always renders
/// the same reference across the API, the PDF and the UI.
/// </summary>
public static class ReportReference
{
    /// <summary>Renders as <c>AC-{year}-{8 hex}</c>, e.g. <c>AC-2026-02150C01</c>.</summary>
    public static string For(Guid reportId, DateTimeOffset generatedAt)
        => $"AC-{generatedAt.ToUniversalTime():yyyy}-{reportId:N}"[..16].ToUpperInvariant();

    public static string Violation(Guid violationId) => $"VIO-{violationId:N}"[..12].ToUpperInvariant();
}
