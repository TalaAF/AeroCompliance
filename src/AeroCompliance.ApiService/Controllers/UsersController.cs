using AeroCompliance.ApiService.Authorization;
using AeroCompliance.ApiService.Services;
using AeroCompliance.Contracts.Dtos.Common;
using AeroCompliance.Contracts.Dtos.Users;
using AeroCompliance.Domain.Entities;
using AeroCompliance.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AeroCompliance.ApiService.Controllers;

[ApiController, Route("api/users"), Authorize]
public sealed class UsersController(AeroComplianceDbContext db, ICurrentUserContext currentUser, IPasswordHasherService passwordHasher, IAuditService audit) : ControllerBase
{
    [HttpGet, Authorize(Policy = Policies.ManageUsers)]
    public async Task<ActionResult<PagedResponse<UserDto>>> GetAll(int page = 1, int pageSize = 25, string? search = null, string? role = null, bool? isActive = null, Guid? municipalityId = null, CancellationToken ct = default)
    {
        if (!ApiSupport.IsValidPagination(page, pageSize)) return BadPagination();
        var query = VisibleUsers().AsNoTracking();
        var normalized = ApiSupport.NormalizeSearch(search);
        if (normalized is not null) query = query.Where(x => x.FullName.ToLower().Contains(normalized) || x.Email.ToLower().Contains(normalized));
        if (!string.IsNullOrWhiteSpace(role)) query = query.Where(x => x.Role.Name == role.Trim());
        if (isActive.HasValue) query = query.Where(x => x.IsActive == isActive);
        if (currentUser.IsPlatformAdmin && municipalityId.HasValue) query = query.Where(x => x.MunicipalityId == municipalityId);
        return Ok(await query.OrderBy(x => x.FullName).Select(ToDto()).ToPagedAsync(page, pageSize, ct));
    }

    [HttpGet("{id:guid}"), Authorize(Policy = Policies.ManageUsers)]
    public async Task<ActionResult<UserDto>> GetById(Guid id, CancellationToken ct)
    {
        var item = await VisibleUsers().AsNoTracking().Where(x => x.Id == id).Select(ToDto()).SingleOrDefaultAsync(ct);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpPost, Authorize(Policy = Policies.ManageUsers)]
    public async Task<ActionResult<UserDto>> Create(CreateUserRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(x => x.Email == email, ct)) return ConflictProblem("A user with this email already exists.");
        var role = await db.Roles.SingleOrDefaultAsync(x => x.Name == request.Role.Trim(), ct);
        if (role is null || !Roles.All.Contains(role.Name)) return Problem(statusCode: 400, title: "Invalid role");
        if (!TryResolveAssignment(role.Name, request.MunicipalityId, out var municipalityId, out var error)) return Problem(statusCode: 403, title: "Forbidden assignment", detail: error);
        if (municipalityId.HasValue && !await db.Municipalities.AnyAsync(x => x.Id == municipalityId, ct)) return Problem(statusCode: 400, title: "Invalid municipality");
        var entity = new User { Id = Guid.NewGuid(), FullName = request.FullName.Trim(), Email = email, RoleId = role.Id, MunicipalityId = municipalityId, IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
        entity.PasswordHash = passwordHasher.Hash(entity, request.InitialPassword);
        db.Users.Add(entity);
        audit.Add("user.create", "User", entity.Id, after: new { entity.FullName, entity.Email, Role = role.Name, entity.MunicipalityId, entity.IsActive }, municipalityId: entity.MunicipalityId);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(GetById), new { id = entity.Id }, await ReadDto(entity.Id, ct));
    }

    [HttpPut("{id:guid}"), Authorize(Policy = Policies.ManageUsers)]
    public async Task<ActionResult<UserDto>> Update(Guid id, UpdateUserRequest request, CancellationToken ct)
    {
        var entity = await VisibleUsers().Include(x => x.Role).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null || (!currentUser.IsPlatformAdmin && entity.Role.Name == Roles.Admin)) return NotFound();
        var email = request.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(x => x.Id != id && x.Email == email, ct)) return ConflictProblem("A user with this email already exists.");
        var role = await db.Roles.SingleOrDefaultAsync(x => x.Name == request.Role.Trim(), ct);
        if (role is null || !Roles.All.Contains(role.Name)) return Problem(statusCode: 400, title: "Invalid role");
        if (!TryResolveAssignment(role.Name, request.MunicipalityId, out var municipalityId, out var error)) return Problem(statusCode: 403, title: "Forbidden assignment", detail: error);
        if (municipalityId.HasValue && !await db.Municipalities.AnyAsync(x => x.Id == municipalityId, ct)) return Problem(statusCode: 400, title: "Invalid municipality");
        var before = new { entity.FullName, entity.Email, Role = entity.Role.Name, entity.MunicipalityId, entity.IsActive };
        entity.FullName = request.FullName.Trim(); entity.Email = email; entity.RoleId = role.Id; entity.MunicipalityId = municipalityId;
        audit.Add("user.update", "User", id, before, new { entity.FullName, entity.Email, Role = role.Name, entity.MunicipalityId, entity.IsActive }, entity.MunicipalityId);
        await db.SaveChangesAsync(ct);
        return Ok(await ReadDto(id, ct));
    }

    [HttpPost("{id:guid}/activate"), Authorize(Policy = Policies.ManageUsers)]
    public Task<IActionResult> Activate(Guid id, UserStateChangeRequest request, CancellationToken ct) => ChangeState(id, true, request.Reason, ct);
    [HttpPost("{id:guid}/deactivate"), Authorize(Policy = Policies.ManageUsers)]
    public Task<IActionResult> Deactivate(Guid id, UserStateChangeRequest request, CancellationToken ct) => ChangeState(id, false, request.Reason, ct);

    [HttpGet("me")]
    public async Task<ActionResult<CurrentUserDto>> Me(CancellationToken ct)
    {
        if (currentUser.UserId is not Guid userId) return Unauthorized();
        var profile = await db.Users.AsNoTracking().Where(x => x.Id == userId).Select(x => new CurrentUserDto(x.Id, x.FullName, x.Email, new CurrentUserRoleDto(x.Role.Id, x.Role.Name), x.Municipality == null ? null : new CurrentUserMunicipalityDto(x.Municipality.Id, x.Municipality.Name), x.IsActive)).SingleOrDefaultAsync(ct);
        if (profile is null) return NotFound();
        return profile.IsActive ? Ok(profile) : Forbid();
    }

    private async Task<IActionResult> ChangeState(Guid id, bool active, string? reason, CancellationToken ct)
    {
        var entity = await VisibleUsers().Include(x => x.Role).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null || (!currentUser.IsPlatformAdmin && entity.Role.Name == Roles.Admin)) return NotFound();
        if (!active && currentUser.UserId == id) return ConflictProblem("You cannot deactivate your own account.");
        var before = new { entity.IsActive }; entity.IsActive = active;
        if (!active) { var now = DateTimeOffset.UtcNow; await db.RefreshTokens.Where(x => x.UserId == id && x.RevokedAt == null && x.ExpiresAt > now).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct); }
        audit.Add(active ? "user.activate" : "user.deactivate", "User", id, before, new { entity.IsActive, Reason = reason?.Trim() }, entity.MunicipalityId);
        await db.SaveChangesAsync(ct); return NoContent();
    }

    private IQueryable<User> VisibleUsers() => currentUser.IsPlatformAdmin ? db.Users : db.Users.Where(x => x.MunicipalityId == currentUser.MunicipalityId && x.Role.Name != Roles.Admin);
    private bool TryResolveAssignment(string role, Guid? requestedMunicipality, out Guid? municipalityId, out string error)
    {
        error = string.Empty;
        if (!currentUser.IsPlatformAdmin) { municipalityId = currentUser.MunicipalityId; if (municipalityId is null || role == Roles.Admin) { error = "Municipality managers cannot manage platform administrators or cross-tenant users."; return false; } return true; }
        municipalityId = role == Roles.Admin ? null : requestedMunicipality;
        if (role != Roles.Admin && municipalityId is null) { error = "A municipality is required for non-admin users."; return false; }
        return true;
    }
    private Task<UserDto?> ReadDto(Guid id, CancellationToken ct) => db.Users.AsNoTracking().Where(x => x.Id == id).Select(ToDto()).SingleOrDefaultAsync(ct);
    private static System.Linq.Expressions.Expression<Func<User, UserDto>> ToDto() => x => new(x.Id, x.FullName, x.Email, new CurrentUserRoleDto(x.Role.Id, x.Role.Name), x.Municipality == null ? null : new CurrentUserMunicipalityDto(x.Municipality.Id, x.Municipality.Name), x.IsActive, x.CreatedAt, x.LastLoginAt);
    private ObjectResult BadPagination() => Problem(statusCode: 400, title: "Invalid pagination", detail: "page must be at least 1 and pageSize must be between 1 and 100.");
    private ObjectResult ConflictProblem(string detail) => Problem(statusCode: 409, title: "Conflict", detail: detail);
}
