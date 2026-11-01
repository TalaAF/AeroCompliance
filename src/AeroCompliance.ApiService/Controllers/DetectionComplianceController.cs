using AeroCompliance.ApiService.Authorization;
using AeroCompliance.ApiService.Services;
using AeroCompliance.Contracts.Dtos.Compliance;
using AeroCompliance.Domain.Entities;
using AeroCompliance.Infrastructure.Compliance;
using AeroCompliance.Infrastructure.Notifications;
using AeroCompliance.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AeroCompliance.ApiService.Controllers;

/// <summary>
/// Compliance results for a single AI detection, and the manual re-evaluation action.
/// </summary>
/// <remarks>
/// Re-evaluation re-measures the detection against the currently enforceable rules. It is
/// idempotent — an evaluation is updated in place rather than duplicated — and it never touches a
/// violation a human has already reviewed.
/// </remarks>
[ApiController, Route("api/detections/{detectionId:guid}"), Authorize]
public sealed class DetectionComplianceController(
    AeroComplianceDbContext db, ICurrentUserContext user, IAuditService audit,
    INotificationService notify, ILogger<DetectionComplianceController> logger) : ControllerBase
{
    [HttpGet("compliance-evaluations")]
    public async Task<ActionResult<IReadOnlyList<ComplianceEvaluationDto>>> List(Guid detectionId, CancellationToken ct)
    {
        var detection = await VisibleDetections().AsNoTracking().SingleOrDefaultAsync(x => x.Id == detectionId, ct);
        if (detection is null) return NotFound();
        return Ok(await ReadAsync(detectionId, ct));
    }

    /// <summary>
    /// Re-runs the rule engine for this detection. Uses the GIS-management policy: deciding that a
    /// detection should be re-measured against zoning rules is the same authority as maintaining
    /// those rules.
    /// </summary>
    [HttpPost("evaluate-compliance"), Authorize(Policy = Policies.ManageGis)]
    public async Task<ActionResult<ReEvaluationResultDto>> Evaluate(Guid detectionId, CancellationToken ct)
    {
        var detection = await VisibleDetections()
            .Include(x => x.Image!).ThenInclude(i => i.Flight)
            .Include(x => x.Orthomosaic!).ThenInclude(o => o.Flight)
            .SingleOrDefaultAsync(x => x.Id == detectionId, ct);
        if (detection is null) return NotFound();

        var municipalityId = detection.Image?.Flight.MunicipalityId ?? detection.Orthomosaic?.Flight.MunicipalityId;
        if (municipalityId is null)
            return Problem(statusCode: 409, title: "This detection is not linked to a municipality.");

        if (!ComplianceRuleEngine.IsEvaluableClass(detection.ClassLabel))
            return Problem(statusCode: 409, title: "This detection cannot be assessed.",
                detail: $"Compliance rules apply to detected buildings; this detection is classified as " +
                        $"'{detection.ClassLabel}'.");

        IReadOnlyList<RuleEvaluationResult> applied;
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            applied = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var currentDetection = await VisibleDetections()
                    .Include(x => x.Image!).ThenInclude(i => i.Flight)
                    .Include(x => x.Orthomosaic!).ThenInclude(o => o.Flight)
                    .SingleAsync(x => x.Id == detectionId, ct);
                var outcome = await ComplianceRuleEngine.EvaluateAsync(db, municipalityId.Value, currentDetection, currentDetection.ImageAnalysisId, ct, notify);
                var passedCount = outcome.Count(x => x.Outcome == ComplianceOutcomes.Pass);
                var failedCount = outcome.Count(x => x.Outcome == ComplianceOutcomes.Fail);
                audit.Add("detection.compliance.reevaluate", "Detection", detectionId,
                    after: new
                    {
                        rulesEvaluated = outcome.Count, passed = passedCount, failed = failedCount,
                        notAssessed = outcome.Count - passedCount - failedCount,
                        violationsCreated = outcome.Count(x => x.CreatedViolation),
                        rules = outcome.Select(x => new { x.RuleLabel, x.Outcome, x.MeasuredValue, x.RequiredValue, x.Unit })
                    },
                    municipalityId: municipalityId);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return outcome;
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Re-evaluating compliance for detection {DetectionId} failed.", detectionId);
            return Problem(statusCode: 500, title: "The compliance check could not be completed.");
        }

        var passed = applied.Count(x => x.Outcome == ComplianceOutcomes.Pass);
        var failed = applied.Count(x => x.Outcome == ComplianceOutcomes.Fail);
        var notAssessed = applied.Count - passed - failed;
        var created = applied.Count(x => x.CreatedViolation);

        return Ok(new ReEvaluationResultDto(detectionId, applied.Count, passed, failed, notAssessed, created,
            Summarise(applied.Count, passed, failed, notAssessed, created), await ReadAsync(detectionId, ct)));
    }

    private static string Summarise(int total, int passed, int failed, int notAssessed, int created)
    {
        if (total == 0)
            return "No enforceable zoning rule currently applies to this detection. " +
                   "A rule must be supported, active, validated, and cover this detection's zone before it is checked.";
        var parts = new List<string>();
        if (passed > 0) parts.Add($"{passed} rule{(passed == 1 ? "" : "s")} passed");
        if (failed > 0) parts.Add($"{failed} rule{(failed == 1 ? "" : "s")} failed");
        if (notAssessed > 0) parts.Add($"{notAssessed} could not be assessed");
        var summary = $"Checked {total} rule{(total == 1 ? "" : "s")}: {string.Join(", ", parts)}.";
        return created > 0
            ? $"{summary} {created} candidate violation{(created == 1 ? " was" : "s were")} raised for review."
            : failed > 0
                ? $"{summary} A candidate violation already exists for the failed rule."
                : summary;
    }

    // --- Reading -----------------------------------------------------------------------------

    /// <summary>
    /// Loads the detection's evaluations with every human-readable name resolved server-side, so
    /// the frontend never has to look up a rule label or a zone name from an id.
    /// </summary>
    private async Task<IReadOnlyList<ComplianceEvaluationDto>> ReadAsync(Guid detectionId, CancellationToken ct)
    {
        var rows = await db.ComplianceEvaluations.AsNoTracking()
            .Where(x => x.DetectionId == detectionId)
            .Include(x => x.ZoningRule).ThenInclude(r => r!.Zone)
            .Include(x => x.Violation).ThenInclude(v => v!.Parcel)
            .Include(x => x.Violation).ThenInclude(v => v!.Road)
            .Include(x => x.Violation).ThenInclude(v => v!.Zone)
            .Include(x => x.Violation).ThenInclude(v => v!.Block)
            .OrderBy(x => x.EvaluationType)
            .ToListAsync(ct);

        return rows.Select(x =>
        {
            var definition = ZoningRuleCatalog.Find(x.ZoningRule?.RuleKey ?? x.EvaluationType);
            var label = definition?.Label ?? Humanize(x.EvaluationType);
            var unit = x.Unit ?? definition?.Unit ?? "";
            var v = x.Violation;
            return new ComplianceEvaluationDto(
                x.Id, x.DetectionId, x.EvaluationType, label, definition?.Description,
                x.ZoningRuleId,
                x.ZoningRule is null ? null : ZoningRuleCatalog.EnforcementState(x.ZoningRule),
                v?.Zone?.ZoneNumber ?? x.ZoningRule?.Zone?.ZoneNumber,
                v?.Zone?.Name ?? x.ZoningRule?.Zone?.Name,
                v?.Block?.BlockNumber,
                v?.Parcel?.ParcelNumber,
                v?.Road?.RoadCode,
                v?.Road?.Name,
                x.MeasuredValue, Display(definition, x.MeasuredValue, unit),
                x.RequiredValue, Display(definition, x.RequiredValue, unit),
                unit,
                x.Outcome, ComplianceOutcomes.Label(x.Outcome),
                Explanation(x, definition, unit),
                x.MetricCrs, v?.Id, x.CreatedAt);
        }).ToList();
    }

    private static string? Display(ZoningRuleDefinition? definition, decimal? value, string unit)
    {
        if (value is null) return null;
        if (definition is not null) return definition.Format(value.Value);
        return unit switch
        {
            "m" => $"{value:0.##} m",
            "m2" => $"{value:0.##} m²",
            "percent" => $"{value:0.##}%",
            _ => $"{value:0.##}".Trim()
        };
    }

    /// <summary>
    /// Rebuilds the sentence from stored values. Pre-Phase-7 heuristic rows have no rule and no
    /// required value, so they get a description of what was actually recorded instead.
    /// </summary>
    private static string Explanation(ComplianceEvaluation x, ZoningRuleDefinition? definition, string unit)
    {
        if (x.Outcome == ComplianceOutcomes.NotApplicable)
            return "Not applicable: the spatial information this rule needs was not available for this detection.";
        if (x.Outcome == ComplianceOutcomes.InsufficientData)
            return "Cannot be assessed: the measurement could not be completed for this detection.";
        if (definition is not null && x.MeasuredValue is decimal m && x.RequiredValue is decimal r)
            return definition.Explain(m, r, null);
        var measured = Display(definition, x.MeasuredValue, unit);
        return x.Outcome is ComplianceOutcomes.Fail or "candidate_violation"
            ? $"Does not comply. Recorded encroachment: {measured ?? "not recorded"}."
            : $"Recorded measurement: {measured ?? "not recorded"}.";
    }

    private static string Humanize(string value)
    {
        var text = value.Replace('_', ' ');
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private IQueryable<Detection> VisibleDetections() => user.IsPlatformAdmin
        ? db.Detections
        : db.Detections.Where(x =>
            (x.Image != null && x.Image.Flight.MunicipalityId == user.MunicipalityId) ||
            (x.Orthomosaic != null && x.Orthomosaic.Flight.MunicipalityId == user.MunicipalityId));
}

/// <summary>
/// The closed catalogue of executable rule types, so the frontend's guided form and the backend
/// validator are driven by exactly the same list.
/// </summary>
[ApiController, Route("api/zoning-rules/types"), Authorize]
public sealed class ZoningRuleTypesController : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<RuleTypeDto>> List() => Ok(ZoningRuleCatalog.All.Select(d => new RuleTypeDto(
        d.CanonicalKey, d.Label, d.Description, d.Unit, d.UnitLabel, d.MinimumValue, d.MaximumValue,
        d.Comparison == Comparison.MustBeAtLeast ? "Must be at least" : "Must not exceed",
        d.CalculationSummary,
        d.RequiredContext switch
        {
            SpatialContext.Road => "Needs a mapped road nearby",
            SpatialContext.Parcel => "Needs the detection to fall inside a mapped parcel",
            SpatialContext.Road | SpatialContext.Parcel => "Needs a mapped road and parcel",
            _ => "No additional map data required"
        },
        d.CanonicalKey switch
        {
            ZoningRuleCatalog.MinimumRoadSetback => "3",
            ZoningRuleCatalog.MaximumBuildingArea => "250",
            ZoningRuleCatalog.MaximumParcelCoverage => "60",
            _ => "0"
        })).ToList());
}
