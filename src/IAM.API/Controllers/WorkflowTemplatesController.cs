using System.Security.Claims;
using Hazina.Security.ApiKeys;
using IAM.API.Authorization;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/workflow-templates")]
[Authorize]
public class WorkflowTemplatesController : ControllerBase
{
    private readonly IAccessRequestService _accessRequestService;
    private readonly IAMDbContext _context;
    private readonly ILogger<WorkflowTemplatesController> _logger;

    public WorkflowTemplatesController(
        IAccessRequestService accessRequestService,
        IAMDbContext context,
        ILogger<WorkflowTemplatesController> logger)
    {
        _accessRequestService = accessRequestService;
        _context = context;
        _logger = logger;
    }

    // Task 5152. A template decides who approves access requests and whether they are approved automatically, so
    // templates are an administrator surface: a SuperAdmin manages every template (global ones too), an active
    // BuildingOwner/BuildingManager of a tenant (TenantManagementAuthority, same rule as invitations) manages that
    // tenant's templates and can read the global ones. Everybody else - ordinary users, device and service-account
    // tokens, API keys (a key is not a user and must not inherit its issuer's roles) - gets 403. The tenant of an
    // existing template always comes from the stored row, never from the request body.

    private sealed record Caller(Guid UserId, bool IsSuperAdmin);

    private Caller? GetCaller()
    {
        if (User.IsApiKey() || User.FindFirst(ServiceAccountAuthorization.TokenTypeClaim) != null)
            return null;

        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(claim, out var userId) ? new Caller(userId, User.IsInRole("SuperAdmin")) : null;
    }

    private ObjectResult Forbidden() =>
        StatusCode(StatusCodes.Status403Forbidden,
            new { error = "Only a SuperAdmin or an owner/manager of the template's tenant can manage workflow templates." });

    /// <summary>The caller when they administer at least one tenant (or are SuperAdmin), otherwise null - checked before any lookup by id.</summary>
    private async Task<Caller?> RequireAdministratorAsync(CancellationToken ct)
    {
        var caller = GetCaller();
        if (caller == null)
            return null;

        return await TenantManagementAuthority.ManagesAnyTenantAsync(_context, caller.UserId, caller.IsSuperAdmin, ct)
            ? caller
            : null;
    }

    /// <summary>Global templates (no tenant) are for SuperAdmin only; a tenant's templates for whoever manages that tenant.</summary>
    private async Task<bool> CanManageAsync(Caller caller, Guid? templateTenantId, CancellationToken ct)
    {
        if (caller.IsSuperAdmin)
            return true;

        if (templateTenantId == null)
            return false;

        return await TenantManagementAuthority.ResolveAsync(_context, caller.UserId, false, templateTenantId.Value, ct) != null;
    }

    /// <summary>
    /// Get all workflow templates, optionally filtered by tenant.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetTemplates([FromQuery] Guid? tenantId, CancellationToken ct)
    {
        var caller = await RequireAdministratorAsync(ct);
        if (caller == null)
            return Forbidden();

        if (tenantId.HasValue && !await CanManageAsync(caller, tenantId, ct))
            return Forbidden();

        var templates = await _accessRequestService.GetTemplatesAsync(tenantId, ct);

        if (!caller.IsSuperAdmin)
        {
            // Without a tenant filter the list holds every tenant's templates: keep the global ones and the caller's own.
            var managed = (await TenantManagementAuthority.GetManagedTenantIdsAsync(_context, caller.UserId, ct)).ToHashSet();
            templates = templates.Where(t => t.TenantId == null || managed.Contains(t.TenantId.Value)).ToList();
        }

        return Ok(templates.Select(MapTemplateToResponse));
    }

    /// <summary>
    /// Get a specific workflow template by ID.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetTemplate(Guid id, CancellationToken ct)
    {
        var caller = await RequireAdministratorAsync(ct);
        if (caller == null)
            return Forbidden();

        var template = await _accessRequestService.GetTemplateAsync(id, ct);
        if (template == null)
            return NotFound(new { error = "Workflow template not found" });

        // Global templates may be read by any tenant administrator; a tenant's templates only by its administrators.
        if (template.TenantId != null && !await CanManageAsync(caller, template.TenantId, ct))
            return Forbidden();

        return Ok(MapTemplateToResponse(template));
    }

    /// <summary>
    /// Create a new workflow template.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateTemplate([FromBody] WorkflowTemplateDto dto, CancellationToken ct)
    {
        // Authorize on the tenant in the body before anything else: it is the only tenant a new template has.
        var caller = await RequireAdministratorAsync(ct);
        if (caller == null || !await CanManageAsync(caller, dto.TenantId, ct))
            return Forbidden();

        if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.ResourceType))
            return BadRequest(new { error = "Name and resource type are required." });

        var template = new WorkflowTemplate
        {
            TenantId = dto.TenantId,
            Name = dto.Name,
            Description = dto.Description,
            ResourceType = dto.ResourceType,
            Steps = dto.Steps ?? "[]",
            AutoExpireHours = dto.AutoExpireHours,
            AutoApproveRules = dto.AutoApproveRules,
            IsActive = dto.IsActive ?? true
        };

        try
        {
            var created = await _accessRequestService.CreateTemplateAsync(template, ct);
            return Ok(MapTemplateToResponse(created));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Update an existing workflow template.
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateTemplate(Guid id, [FromBody] WorkflowTemplateDto dto, CancellationToken ct)
    {
        var denied = await AuthorizeStoredTemplateAsync(id, ct);
        if (denied != null)
            return denied;

        if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.ResourceType))
            return BadRequest(new { error = "Name and resource type are required." });

        try
        {
            var updated = new WorkflowTemplate
            {
                Name = dto.Name,
                Description = dto.Description,
                ResourceType = dto.ResourceType,
                Steps = dto.Steps ?? "[]",
                AutoExpireHours = dto.AutoExpireHours,
                AutoApproveRules = dto.AutoApproveRules,
                IsActive = dto.IsActive ?? true
            };

            var result = await _accessRequestService.UpdateTemplateAsync(id, updated, ct);
            return Ok(MapTemplateToResponse(result));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Delete a workflow template.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteTemplate(Guid id, CancellationToken ct)
    {
        var denied = await AuthorizeStoredTemplateAsync(id, ct);
        if (denied != null)
            return denied;

        var deleted = await _accessRequestService.DeleteTemplateAsync(id, ct);
        if (!deleted)
            return NotFound(new { error = "Workflow template not found" });

        return Ok(new { message = "Template deleted successfully" });
    }

    // ─── Helpers ─────────────────────────────────────────────

    /// <summary>Gate, lookup and tenant check for an existing template: the 403 / 404 to return, or null when the caller may change it.</summary>
    private async Task<IActionResult?> AuthorizeStoredTemplateAsync(Guid id, CancellationToken ct)
    {
        var caller = await RequireAdministratorAsync(ct);
        if (caller == null)
            return Forbidden();

        var template = await _accessRequestService.GetTemplateAsync(id, ct);
        if (template == null)
            return NotFound(new { error = "Workflow template not found" });

        if (!await CanManageAsync(caller, template.TenantId, ct))
            return Forbidden();

        return null;
    }

    private static object MapTemplateToResponse(WorkflowTemplate t) => new
    {
        id = t.Id,
        tenantId = t.TenantId,
        tenantName = t.Tenant?.Name,
        name = t.Name,
        description = t.Description,
        resourceType = t.ResourceType,
        steps = t.Steps,
        autoExpireHours = t.AutoExpireHours,
        autoApproveRules = t.AutoApproveRules,
        isActive = t.IsActive,
        createdAt = t.CreatedAt,
        updatedAt = t.UpdatedAt
    };
}

public record WorkflowTemplateDto(
    string Name,
    string? Description,
    string ResourceType,
    Guid? TenantId,
    string? Steps,
    int? AutoExpireHours,
    string? AutoApproveRules,
    bool? IsActive
);
