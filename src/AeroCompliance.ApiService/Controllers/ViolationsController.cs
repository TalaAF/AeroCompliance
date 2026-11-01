using AeroCompliance.ApiService.Authorization; using AeroCompliance.ApiService.Services; using AeroCompliance.Contracts.Dtos.Common; using AeroCompliance.Contracts.Dtos.Operations; using AeroCompliance.Domain.Entities; using AeroCompliance.Infrastructure.Notifications; using AeroCompliance.Infrastructure.Persistence; using Microsoft.AspNetCore.Authorization; using Microsoft.AspNetCore.Mvc; using Microsoft.EntityFrameworkCore;
namespace AeroCompliance.ApiService.Controllers;
[ApiController,Route("api/violations"),Authorize] public sealed class ViolationsController(AeroComplianceDbContext db,ICurrentUserContext user,IAuditService audit,INotificationService notify):ControllerBase
{
 [HttpGet] public async Task<ActionResult<PagedResponse<ViolationSummaryDto>>> List(int page=1,int pageSize=25,string? status=null,Guid? flightId=null,decimal? minConfidence=null,decimal? maxConfidence=null,DateTimeOffset? from=null,DateTimeOffset? to=null,string? search=null,CancellationToken ct=default){if(!ApiSupport.IsValidPagination(page,pageSize))return Problem(statusCode:400,title:"Invalid pagination");if(minConfidence is<0 or>1||maxConfidence is<0 or>1||minConfidence>maxConfidence)return Problem(statusCode:400,title:"Invalid confidence range");var q=Visible().AsNoTracking();if(!string.IsNullOrWhiteSpace(status))q=q.Where(x=>x.CurrentStatus.Code==status.Trim());if(flightId.HasValue)q=q.Where(x=>(x.Detection.Image!=null&&x.Detection.Image!.FlightId==flightId)||(x.Detection.Orthomosaic!=null&&x.Detection.Orthomosaic!.FlightId==flightId));if(minConfidence.HasValue)q=q.Where(x=>x.AiConfidenceScore>=minConfidence);if(maxConfidence.HasValue)q=q.Where(x=>x.AiConfidenceScore<=maxConfidence);if(from.HasValue)q=q.Where(x=>x.CreatedAt>=from);if(to.HasValue)q=q.Where(x=>x.CreatedAt<=to);// Phase 8 reviewer queue: a search term also matches the human references an officer
 // actually knows - road, parcel, zone, block, and the flight the case came from.
 var s=ApiSupport.NormalizeSearch(search);if(s is not null)q=q.Where(x=>x.ViolationType.ToLower().Contains(s)||x.Id.ToString().Contains(s)||(x.Road!=null&&(x.Road.RoadCode.ToLower().Contains(s)||(x.Road.Name!=null&&x.Road.Name.ToLower().Contains(s))))||(x.Parcel!=null&&x.Parcel.ParcelNumber.ToLower().Contains(s))||(x.Zone!=null&&x.Zone.ZoneNumber.ToLower().Contains(s))||(x.Block!=null&&x.Block.BlockNumber.ToLower().Contains(s))||(x.Detection.Image!=null&&x.Detection.Image.Flight.MissionName!=null&&x.Detection.Image.Flight.MissionName.ToLower().Contains(s)));return Ok(await q.OrderByDescending(x=>x.CreatedAt).Select(SummaryExpr()).ToPagedAsync(page,pageSize,ct));}
 [HttpGet("{id:guid}")] public async Task<ActionResult<ViolationDetailsDto>> Get(Guid id,CancellationToken ct){var x=await Details(id,ct);return x is null?NotFound():Ok(x);}
 [HttpPost("{id:guid}/confirm"),Authorize(Policy=Policies.ReviewViolations)] public Task<ActionResult<ViolationDetailsDto>> Confirm(Guid id,ViolationDecisionRequest r,CancellationToken ct)=>Decide(id,r.Notes,"confirmed_under_notification","violation.confirm",ct);
 [HttpPost("{id:guid}/dismiss"),Authorize(Policy=Policies.ReviewViolations)] public Task<ActionResult<ViolationDetailsDto>> Dismiss(Guid id,ViolationDecisionRequest r,CancellationToken ct)=>Decide(id,r.Notes,"dismissed","violation.dismiss",ct);
 private async Task<ActionResult<ViolationDetailsDto>> Decide(Guid id,string notes,string targetCode,string action,CancellationToken ct){var strategy=db.Database.CreateExecutionStrategy();return await strategy.ExecuteAsync<ActionResult<ViolationDetailsDto>>(async()=>{db.ChangeTracker.Clear();await using var tx=await db.Database.BeginTransactionAsync(ct);var x=await Visible().AsNoTracking().Include(x=>x.CurrentStatus).Include(x=>x.Detection).ThenInclude(x=>x.Image).ThenInclude(x=>x!.Flight).Include(x=>x.Detection).ThenInclude(x=>x.Orthomosaic).ThenInclude(x=>x!.Flight).SingleOrDefaultAsync(x=>x.Id==id,ct);if(x is null)return NotFound();if(x.CurrentStatus.Code!="under_review")return Problem(statusCode:409,title:"Invalid violation transition",detail:$"A violation in status '{x.CurrentStatus.Code}' cannot be reviewed again.");var target=await db.ViolationStatuses.SingleAsync(s=>s.Code==targetCode,ct);var before=x.CurrentStatus.Code;var now=DateTimeOffset.UtcNow;
  // Claim the transition atomically. Two reviewers may both have loaded "under_review", but only
  // one conditional UPDATE can commit a decision and its history/audit/notifications.
  var changed=await db.Violations.Where(v=>v.Id==id&&v.CurrentStatusId==x.CurrentStatusId).ExecuteUpdateAsync(s=>s.SetProperty(v=>v.CurrentStatusId,target.Id).SetProperty(v=>v.UpdatedAt,now),ct);if(changed==0)return Problem(statusCode:409,title:"Invalid violation transition",detail:"Another reviewer already decided this violation.");db.ViolationStatusHistory.Add(new ViolationStatusHistory{Id=Guid.NewGuid(),ViolationId=id,StatusId=target.Id,ChangedBy=user.UserId,Notes=notes.Trim(),ChangedAt=now});var tenant=TenantId(x);audit.Add(action,"Violation",id,new{Status=before},new{Status=targetCode,Notes=notes.Trim()},tenant);
  // The outcome notification is staged into this same SaveChanges, so recipients are only ever
  // told about a decision that actually committed.
  if(tenant is Guid municipality){
   var confirmed=targetCode=="confirmed_under_notification";
   var reviewer=user.UserId is Guid actorId?await db.Users.AsNoTracking().Where(u=>u.Id==actorId).Select(u=>u.FullName).SingleOrDefaultAsync(ct):null;
   var place=await db.Violations.AsNoTracking().Where(v=>v.Id==id)
    .Select(v=>v.Road!=null?v.Road.RoadCode:v.Parcel!=null?v.Parcel.ParcelNumber:v.Zone!=null?v.Zone.ZoneNumber:null).SingleOrDefaultAsync(ct);
   var text=NotificationMessages.ViolationDecided(confirmed,NotificationMessages.ViolationReference(id,x.CreatedAt),reviewer,place);
   await notify.NotifyRolesAsync(new NotificationRequest(municipality,
     confirmed?NotificationTypes.ViolationConfirmed:NotificationTypes.ViolationDismissed,
     NotificationSeverities.Information,text.Title,text.Message,
     SourceEventKey:id.ToString("N"),RelatedEntityType:"Violation",RelatedEntityId:id,
     Route:NotificationRoutes.Violation(id)),
    OutcomeRecipientRoles,ct,
    // The decision-maker does not need telling about their own action.
    excludeUserId:user.UserId);
  }
  await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Ok(await Details(id,ct));});}
 /// <summary>Who is told a violation was confirmed or dismissed. The flight's DroneOperator is
 /// excluded: the current authorization model does not grant that role violation visibility.</summary>
 private static readonly string[] OutcomeRecipientRoles=["MunicipalityManager","Inspector","Reviewer"];
 private IQueryable<Violation> Visible(){var q=db.Violations.AsQueryable();return user.IsPlatformAdmin?q:q.Where(x=>(x.Detection.Image!=null&&x.Detection.Image.Flight.MunicipalityId==user.MunicipalityId)||(x.Detection.Orthomosaic!=null&&x.Detection.Orthomosaic.Flight.MunicipalityId==user.MunicipalityId));}
 private static Guid? TenantId(Violation x)=>x.Detection.Image?.Flight?.MunicipalityId??x.Detection.Orthomosaic?.Flight?.MunicipalityId;
 // Re-reads through the same tenant-scoped, name-projecting query used by GET, so a confirm/dismiss
 // response always carries the related Road/Parcel/Zone/Block/Rule/Flight/image names.
 private async Task<ViolationDetailsDto?> Details(Guid id,CancellationToken ct){var x=await Visible().AsNoTracking().Include(x=>x.CurrentStatus).Include(x=>x.Detection).ThenInclude(x=>x.Image).ThenInclude(x=>x!.Flight).Include(x=>x.Detection).ThenInclude(x=>x.Orthomosaic).ThenInclude(x=>x!.Flight).Include(x=>x.EvidenceImage).Include(x=>x.Parcel).Include(x=>x.Road).Include(x=>x.Zone).Include(x=>x.Block).Include(x=>x.Rule).Include(x=>x.StatusHistory).ThenInclude(x=>x.Status).SingleOrDefaultAsync(x=>x.Id==id,ct);return x is null?null:Map(x);}
 private static ViolationDetailsDto Map(Violation x)=>new(x.Id,x.ViolationType,x.CurrentStatus.Code,x.AiConfidenceScore,x.MeasuredValue,x.RequiredValue,x.ExceedanceValue,x.ExceedanceAreaM2,x.IsZeroState,
  x.DetectionId,x.Detection.ClassLabel,x.Detection.Confidence,
  x.Detection.Image?.FlightId??x.Detection.Orthomosaic?.FlightId,
  x.Detection.Image?.Flight.MissionName??x.Detection.Orthomosaic?.Flight.MissionName,
  x.Detection.Image?.OriginalFileName,
  x.ParcelId,x.Parcel?.ParcelNumber,x.RoadId,x.Road?.RoadCode,x.ZoneId,x.Zone?.ZoneNumber,x.BlockId,x.Block?.BlockNumber,x.RuleId,x.Rule?.RuleKey,
  x.EvidenceImageId,x.EvidenceImage?.StoragePath,
  x.StatusHistory.OrderBy(h=>h.ChangedAt).Select(h=>new ViolationHistoryDto(h.Status.Code,h.ChangedBy,h.Notes,h.ChangedAt)).ToList(),
  x.CreatedAt,x.UpdatedAt,
  // Provenance travels with the case so a reviewer sees how the geometry was obtained.
  x.ResultSource,x.IsApproximate,x.IsExperimental,x.RequiresManualReview,
  x.GeoreferencingConfidence,x.SourceGeoreferencingMethod,x.Detection.ClassId);
 private static System.Linq.Expressions.Expression<Func<Violation,ViolationSummaryDto>> SummaryExpr()=>x=>new(x.Id,x.ViolationType,x.CurrentStatus.Code,x.AiConfidenceScore,
  x.Detection.Image!=null?x.Detection.Image.FlightId:x.Detection.Orthomosaic!=null?x.Detection.Orthomosaic.FlightId:null,
  x.Detection.Image!=null?x.Detection.Image.Flight.MissionName:x.Detection.Orthomosaic!=null?x.Detection.Orthomosaic.Flight.MissionName:null,
  x.CreatedAt,x.UpdatedAt,
  x.ResultSource,x.IsApproximate,x.IsExperimental,x.RequiresManualReview);
}
