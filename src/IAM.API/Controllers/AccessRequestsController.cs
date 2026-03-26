using System.Security.Claims;
using IAM.Core.Entities;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/access-requests")]
[Authorize]
public class AccessRequestsController : ControllerBase
{
    private readonly IAccessRequestService _accessRequestService;
    private readonly ILogger<AccessRequestsController> _logger;

    public AccessRequestsController(
        IAccessRequestService accessRequestService,
        ILogger<AccessRequestsController> logger)
    {
        _accessRequestService = accessRequestService;
        _logger = logger;
    }

    /// <summary>
    /// Submit a new access request.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateRequest([FromBody] CreateAccessRequestDto dto, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        try
        {
            var priority = AccessRequestPriority.Normal;
            if (!string.IsNullOrEmpty(dto.Priority) && Enum.TryParse<AccessRequestPriority>(dto.Priority, true, out var parsed))
                priority = parsed;

            var request = await _accessRequestService.CreateRequestAsync(
                userId.Value,
                dto.ResourceType,
                dto.ResourceId,
                dto.RoleId,
                dto.TenantId,
                dto.Justification,
                priority,
                ct);

            return Ok(MapRequestToResponse(request));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get my submitted access requests.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetMyRequests(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        var requests = await _accessRequestService.GetMyRequestsAsync(userId.Value, ct);
        return Ok(requests.Select(MapRequestToResponse));
    }

    /// <summary>
    /// Get access requests pending my approval (approver inbox).
    /// </summary>
    [HttpGet("pending-approvals")]
    public async Task<IActionResult> GetPendingApprovals(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        var requests = await _accessRequestService.GetPendingApprovalsAsync(userId.Value, ct);
        return Ok(requests.Select(MapRequestToResponse));
    }

    /// <summary>
    /// Get a specific access request by ID.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetRequest(Guid id, CancellationToken ct)
    {
        var request = await _accessRequestService.GetRequestAsync(id, ct);
        if (request == null)
            return NotFound(new { error = "Access request not found" });

        return Ok(MapDetailedRequestToResponse(request));
    }

    /// <summary>
    /// Approve an access request.
    /// </summary>
    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, [FromBody] ApprovalDecisionDto? dto, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        try
        {
            var request = await _accessRequestService.ApproveAsync(id, userId.Value, dto?.Comment, ct);
            return Ok(MapRequestToResponse(request));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Deny an access request.
    /// </summary>
    [HttpPost("{id:guid}/deny")]
    public async Task<IActionResult> Deny(Guid id, [FromBody] ApprovalDecisionDto? dto, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        try
        {
            var request = await _accessRequestService.DenyAsync(id, userId.Value, dto?.Comment, ct);
            return Ok(MapRequestToResponse(request));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Cancel an access request (by the requester).
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized(new { error = "User ID not found in token" });

        try
        {
            var request = await _accessRequestService.CancelAsync(id, userId.Value, ct);
            return Ok(MapRequestToResponse(request));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ─── Helpers ─────────────────────────────────────────────

    private Guid? GetUserId()
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? User.FindFirst("sub")?.Value;

        if (Guid.TryParse(claim, out var userId))
            return userId;

        return null;
    }

    private static object MapRequestToResponse(AccessRequest r) => new
    {
        id = r.Id,
        requesterId = r.RequesterId,
        requesterName = r.Requester != null ? $"{r.Requester.FirstName} {r.Requester.LastName}" : null,
        resourceType = r.ResourceType,
        resourceId = r.ResourceId,
        roleId = r.RoleId,
        roleName = r.Role?.Name,
        tenantId = r.TenantId,
        tenantName = r.Tenant?.Name,
        justification = r.Justification,
        status = r.Status.ToString(),
        priority = r.Priority.ToString(),
        createdAt = r.CreatedAt,
        updatedAt = r.UpdatedAt,
        expiresAt = r.ExpiresAt,
        stepsCount = r.ApprovalSteps?.Count ?? 0,
        currentStep = r.ApprovalSteps?
            .Where(s => s.Status == ApprovalStepStatus.Pending)
            .OrderBy(s => s.StepOrder)
            .Select(s => s.StepOrder)
            .FirstOrDefault()
    };

    private static object MapDetailedRequestToResponse(AccessRequest r) => new
    {
        id = r.Id,
        requesterId = r.RequesterId,
        requesterName = r.Requester != null ? $"{r.Requester.FirstName} {r.Requester.LastName}" : null,
        resourceType = r.ResourceType,
        resourceId = r.ResourceId,
        roleId = r.RoleId,
        roleName = r.Role?.Name,
        tenantId = r.TenantId,
        tenantName = r.Tenant?.Name,
        justification = r.Justification,
        status = r.Status.ToString(),
        priority = r.Priority.ToString(),
        workflowTemplateId = r.WorkflowTemplateId,
        workflowTemplateName = r.WorkflowTemplate?.Name,
        createdAt = r.CreatedAt,
        updatedAt = r.UpdatedAt,
        expiresAt = r.ExpiresAt,
        approvalSteps = r.ApprovalSteps?.OrderBy(s => s.StepOrder).Select(s => new
        {
            id = s.Id,
            stepOrder = s.StepOrder,
            approverId = s.ApproverId,
            approverName = s.Approver != null ? $"{s.Approver.FirstName} {s.Approver.LastName}" : null,
            approverRoleId = s.ApproverRoleId,
            approverRoleName = s.ApproverRole?.Name,
            decidedByUserId = s.DecidedByUserId,
            decidedByUserName = s.DecidedByUser != null ? $"{s.DecidedByUser.FirstName} {s.DecidedByUser.LastName}" : null,
            status = s.Status.ToString(),
            comment = s.Comment,
            quorumCount = s.QuorumCount,
            approvalsReceived = s.ApprovalsReceived,
            decidedAt = s.DecidedAt,
            createdAt = s.CreatedAt
        })
    };
}

public record CreateAccessRequestDto(
    string ResourceType,
    Guid? ResourceId,
    Guid? RoleId,
    Guid? TenantId,
    string Justification,
    string? Priority = null
);

public record ApprovalDecisionDto(string? Comment);
