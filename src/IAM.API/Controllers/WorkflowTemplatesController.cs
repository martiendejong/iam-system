using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/workflow-templates")]
[Authorize]
public class WorkflowTemplatesController : ControllerBase
{
    private readonly IAccessRequestService _accessRequestService;
    private readonly ILogger<WorkflowTemplatesController> _logger;

    public WorkflowTemplatesController(
        IAccessRequestService accessRequestService,
        ILogger<WorkflowTemplatesController> logger)
    {
        _accessRequestService = accessRequestService;
        _logger = logger;
    }

    /// <summary>
    /// Get all workflow templates, optionally filtered by tenant.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetTemplates([FromQuery] Guid? tenantId, CancellationToken ct)
    {
        var templates = await _accessRequestService.GetTemplatesAsync(tenantId, ct);
        return Ok(templates.Select(MapTemplateToResponse));
    }

    /// <summary>
    /// Get a specific workflow template by ID.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetTemplate(Guid id, CancellationToken ct)
    {
        var template = await _accessRequestService.GetTemplateAsync(id, ct);
        if (template == null)
            return NotFound(new { error = "Workflow template not found" });

        return Ok(MapTemplateToResponse(template));
    }

    /// <summary>
    /// Create a new workflow template.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateTemplate([FromBody] WorkflowTemplateDto dto, CancellationToken ct)
    {
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

        var created = await _accessRequestService.CreateTemplateAsync(template, ct);
        return Ok(MapTemplateToResponse(created));
    }

    /// <summary>
    /// Update an existing workflow template.
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateTemplate(Guid id, [FromBody] WorkflowTemplateDto dto, CancellationToken ct)
    {
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
        var deleted = await _accessRequestService.DeleteTemplateAsync(id, ct);
        if (!deleted)
            return NotFound(new { error = "Workflow template not found" });

        return Ok(new { message = "Template deleted successfully" });
    }

    // ─── Helpers ─────────────────────────────────────────────

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
