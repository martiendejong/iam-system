using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

public class AccessRequestService : IAccessRequestService
{
    private readonly IAMDbContext _context;
    private readonly IEventBus _eventBus;
    private readonly IEmailService _emailService;
    private readonly ILogger<AccessRequestService> _logger;

    public AccessRequestService(
        IAMDbContext context,
        IEventBus eventBus,
        IEmailService emailService,
        ILogger<AccessRequestService> logger)
    {
        _context = context;
        _eventBus = eventBus;
        _emailService = emailService;
        _logger = logger;
    }

    // ─── Access Requests ─────────────────────────────────────

    public async Task<AccessRequest> CreateRequestAsync(
        Guid requesterId,
        string resourceType,
        Guid? resourceId,
        Guid? roleId,
        Guid? tenantId,
        string justification,
        AccessRequestPriority priority = AccessRequestPriority.Normal,
        CancellationToken ct = default)
    {
        var request = new AccessRequest
        {
            RequesterId = requesterId,
            ResourceType = resourceType,
            ResourceId = resourceId,
            RoleId = roleId,
            TenantId = tenantId,
            Justification = justification,
            Priority = priority,
            Status = AccessRequestStatus.Pending
        };

        // Find matching workflow template
        var template = await FindMatchingTemplateAsync(resourceType, tenantId, ct);

        if (template != null)
        {
            request.WorkflowTemplateId = template.Id;

            // Set auto-expiry from template
            if (template.AutoExpireHours.HasValue)
            {
                request.ExpiresAt = DateTime.UtcNow.AddHours(template.AutoExpireHours.Value);
            }

            // Check auto-approve rules
            if (ShouldAutoApprove(template, request))
            {
                request.Status = AccessRequestStatus.Approved;
                request.UpdatedAt = DateTime.UtcNow;

                _context.AccessRequests.Add(request);

                // Add audit log
                _context.AuditLogs.Add(new AuditLog
                {
                    UserId = requesterId,
                    TenantId = tenantId,
                    Action = "AccessRequestAutoApproved",
                    Resource = "AccessRequest",
                    Details = JsonSerializer.Serialize(new
                    {
                        requestId = request.Id,
                        resourceType,
                        resourceId,
                        roleId,
                        templateId = template.Id
                    })
                });

                await _context.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "Access request {RequestId} auto-approved for user {RequesterId} via template {TemplateId}",
                    request.Id, requesterId, template.Id);

                await PublishEventAsync("access_request.auto_approved", request, ct);

                return request;
            }

            // Create approval steps from template
            var stepDefinitions = ParseStepDefinitions(template.Steps);
            foreach (var stepDef in stepDefinitions.OrderBy(s => s.Order))
            {
                var step = new ApprovalStep
                {
                    AccessRequestId = request.Id,
                    StepOrder = stepDef.Order,
                    ApproverId = stepDef.ApproverUserId,
                    ApproverRoleId = stepDef.ApproverRoleId,
                    QuorumCount = stepDef.QuorumCount > 0 ? stepDef.QuorumCount : 1,
                    Status = ApprovalStepStatus.Pending
                };
                request.ApprovalSteps.Add(step);
            }
        }
        else
        {
            // No template found: set a default 72-hour expiry
            request.ExpiresAt = DateTime.UtcNow.AddHours(72);
        }

        _context.AccessRequests.Add(request);

        _context.AuditLogs.Add(new AuditLog
        {
            UserId = requesterId,
            TenantId = tenantId,
            Action = "AccessRequestCreated",
            Resource = "AccessRequest",
            Details = JsonSerializer.Serialize(new
            {
                requestId = request.Id,
                resourceType,
                resourceId,
                roleId,
                priority = priority.ToString(),
                stepsCount = request.ApprovalSteps.Count
            })
        });

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Access request {RequestId} created by user {RequesterId} for {ResourceType}, {StepCount} approval steps",
            request.Id, requesterId, resourceType, request.ApprovalSteps.Count);

        await PublishEventAsync("access_request.created", request, ct);
        await NotifyApproversAsync(request, ct);

        return request;
    }

    public async Task<AccessRequest?> GetRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        return await _context.AccessRequests
            .Include(r => r.Requester)
            .Include(r => r.Role)
            .Include(r => r.Tenant)
            .Include(r => r.WorkflowTemplate)
            .Include(r => r.ApprovalSteps)
                .ThenInclude(s => s.Approver)
            .Include(r => r.ApprovalSteps)
                .ThenInclude(s => s.ApproverRole)
            .Include(r => r.ApprovalSteps)
                .ThenInclude(s => s.DecidedByUser)
            .FirstOrDefaultAsync(r => r.Id == requestId, ct);
    }

    public async Task<List<AccessRequest>> GetMyRequestsAsync(Guid requesterId, CancellationToken ct = default)
    {
        return await _context.AccessRequests
            .Include(r => r.Role)
            .Include(r => r.Tenant)
            .Include(r => r.ApprovalSteps)
            .Where(r => r.RequesterId == requesterId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<List<AccessRequest>> GetPendingApprovalsAsync(Guid approverId, CancellationToken ct = default)
    {
        // Find role IDs the approver holds
        var approverRoleIds = await _context.UserRoles
            .Where(ur => ur.UserId == approverId)
            .Select(ur => ur.RoleId)
            .ToListAsync(ct);

        // Find access requests that have a pending step where the approver is either:
        // 1. Directly assigned as the approver, OR
        // 2. A member of the approver role
        // AND the step is the current active step (lowest pending step order)
        var requestIds = await _context.Set<ApprovalStep>()
            .Where(s => s.Status == ApprovalStepStatus.Pending
                && s.AccessRequest.Status == AccessRequestStatus.Pending
                && (s.ApproverId == approverId
                    || (s.ApproverRoleId != null && approverRoleIds.Contains(s.ApproverRoleId.Value))))
            .Select(s => s.AccessRequestId)
            .Distinct()
            .ToListAsync(ct);

        // Filter to only requests where the pending step for this approver is the current active step
        var requests = await _context.AccessRequests
            .Include(r => r.Requester)
            .Include(r => r.Role)
            .Include(r => r.Tenant)
            .Include(r => r.ApprovalSteps)
            .Where(r => requestIds.Contains(r.Id))
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.CreatedAt)
            .ToListAsync(ct);

        // Post-filter: only include requests where the approver's step is the current active step
        return requests.Where(r =>
        {
            var currentStep = r.ApprovalSteps
                .Where(s => s.Status == ApprovalStepStatus.Pending)
                .OrderBy(s => s.StepOrder)
                .FirstOrDefault();

            if (currentStep == null) return false;

            return currentStep.ApproverId == approverId
                || (currentStep.ApproverRoleId != null && approverRoleIds.Contains(currentStep.ApproverRoleId.Value));
        }).ToList();
    }

    public async Task<AccessRequest> ApproveAsync(Guid requestId, Guid approverId, string? comment = null, CancellationToken ct = default)
    {
        var request = await _context.AccessRequests
            .Include(r => r.ApprovalSteps)
            .FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw new InvalidOperationException($"Access request {requestId} not found");

        if (request.Status != AccessRequestStatus.Pending)
            throw new InvalidOperationException($"Access request {requestId} is not in Pending status");

        // Find the current active step (lowest order pending step)
        var currentStep = request.ApprovalSteps
            .Where(s => s.Status == ApprovalStepStatus.Pending)
            .OrderBy(s => s.StepOrder)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("No pending approval steps found");

        // Verify the approver is authorized for this step
        await VerifyApproverAuthorizationAsync(currentStep, approverId, ct);

        // Record the approval
        currentStep.ApprovalsReceived++;
        currentStep.DecidedByUserId = approverId;
        currentStep.Comment = comment;
        currentStep.DecidedAt = DateTime.UtcNow;

        // Check if quorum is met
        if (currentStep.ApprovalsReceived >= currentStep.QuorumCount)
        {
            currentStep.Status = ApprovalStepStatus.Approved;
        }

        // Check if all steps are now approved
        var allStepsApproved = request.ApprovalSteps
            .All(s => s.Status == ApprovalStepStatus.Approved || s.Status == ApprovalStepStatus.Skipped);

        if (allStepsApproved)
        {
            request.Status = AccessRequestStatus.Approved;
            request.UpdatedAt = DateTime.UtcNow;

            _logger.LogInformation("Access request {RequestId} fully approved", requestId);
            await PublishEventAsync("access_request.approved", request, ct);
        }
        else if (currentStep.Status == ApprovalStepStatus.Approved)
        {
            // Notify approvers of the next step
            _logger.LogInformation(
                "Access request {RequestId} step {StepOrder} approved, advancing to next step",
                requestId, currentStep.StepOrder);
        }

        request.UpdatedAt = DateTime.UtcNow;

        _context.AuditLogs.Add(new AuditLog
        {
            UserId = approverId,
            TenantId = request.TenantId,
            Action = "AccessRequestStepApproved",
            Resource = "AccessRequest",
            Details = JsonSerializer.Serialize(new
            {
                requestId,
                stepId = currentStep.Id,
                stepOrder = currentStep.StepOrder,
                comment,
                approvalsReceived = currentStep.ApprovalsReceived,
                quorumCount = currentStep.QuorumCount,
                requestFullyApproved = allStepsApproved
            })
        });

        await _context.SaveChangesAsync(ct);

        // Notify requester if fully approved
        if (request.Status == AccessRequestStatus.Approved)
        {
            await NotifyRequesterAsync(request, "approved", ct);
        }

        return request;
    }

    public async Task<AccessRequest> DenyAsync(Guid requestId, Guid approverId, string? comment = null, CancellationToken ct = default)
    {
        var request = await _context.AccessRequests
            .Include(r => r.ApprovalSteps)
            .FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw new InvalidOperationException($"Access request {requestId} not found");

        if (request.Status != AccessRequestStatus.Pending)
            throw new InvalidOperationException($"Access request {requestId} is not in Pending status");

        // Find the current active step
        var currentStep = request.ApprovalSteps
            .Where(s => s.Status == ApprovalStepStatus.Pending)
            .OrderBy(s => s.StepOrder)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("No pending approval steps found");

        // Verify the approver is authorized for this step
        await VerifyApproverAuthorizationAsync(currentStep, approverId, ct);

        // Denial at any step immediately denies the entire request
        currentStep.Status = ApprovalStepStatus.Denied;
        currentStep.DecidedByUserId = approverId;
        currentStep.Comment = comment;
        currentStep.DecidedAt = DateTime.UtcNow;

        request.Status = AccessRequestStatus.Denied;
        request.UpdatedAt = DateTime.UtcNow;

        _context.AuditLogs.Add(new AuditLog
        {
            UserId = approverId,
            TenantId = request.TenantId,
            Action = "AccessRequestDenied",
            Resource = "AccessRequest",
            Details = JsonSerializer.Serialize(new
            {
                requestId,
                stepId = currentStep.Id,
                stepOrder = currentStep.StepOrder,
                comment
            })
        });

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Access request {RequestId} denied by {ApproverId}", requestId, approverId);

        await PublishEventAsync("access_request.denied", request, ct);
        await NotifyRequesterAsync(request, "denied", ct);

        return request;
    }

    public async Task<AccessRequest> CancelAsync(Guid requestId, Guid requesterId, CancellationToken ct = default)
    {
        var request = await _context.AccessRequests
            .FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw new InvalidOperationException($"Access request {requestId} not found");

        if (request.RequesterId != requesterId)
            throw new InvalidOperationException("Only the requester can cancel their own request");

        if (request.Status != AccessRequestStatus.Pending)
            throw new InvalidOperationException($"Access request {requestId} is not in Pending status");

        request.Status = AccessRequestStatus.Cancelled;
        request.UpdatedAt = DateTime.UtcNow;

        _context.AuditLogs.Add(new AuditLog
        {
            UserId = requesterId,
            TenantId = request.TenantId,
            Action = "AccessRequestCancelled",
            Resource = "AccessRequest",
            Details = JsonSerializer.Serialize(new { requestId })
        });

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Access request {RequestId} cancelled by requester {RequesterId}", requestId, requesterId);

        return request;
    }

    public async Task<int> ExpireOverdueRequestsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var overdueRequests = await _context.AccessRequests
            .Where(r => r.Status == AccessRequestStatus.Pending
                && r.ExpiresAt != null
                && r.ExpiresAt < now)
            .ToListAsync(ct);

        foreach (var request in overdueRequests)
        {
            request.Status = AccessRequestStatus.Expired;
            request.UpdatedAt = now;

            _context.AuditLogs.Add(new AuditLog
            {
                UserId = request.RequesterId,
                TenantId = request.TenantId,
                Action = "AccessRequestExpired",
                Resource = "AccessRequest",
                Details = JsonSerializer.Serialize(new
                {
                    requestId = request.Id,
                    expiresAt = request.ExpiresAt
                })
            });
        }

        if (overdueRequests.Count > 0)
        {
            await _context.SaveChangesAsync(ct);
            _logger.LogInformation("Expired {Count} overdue access requests", overdueRequests.Count);
        }

        return overdueRequests.Count;
    }

    // ─── Workflow Templates ─────────────────────────────────────

    public async Task<WorkflowTemplate> CreateTemplateAsync(WorkflowTemplate template, CancellationToken ct = default)
    {
        template.CreatedAt = DateTime.UtcNow;
        template.UpdatedAt = DateTime.UtcNow;

        _context.WorkflowTemplates.Add(template);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Workflow template {TemplateId} '{Name}' created for resource type '{ResourceType}'",
            template.Id, template.Name, template.ResourceType);

        return template;
    }

    public async Task<WorkflowTemplate?> GetTemplateAsync(Guid templateId, CancellationToken ct = default)
    {
        return await _context.WorkflowTemplates
            .Include(t => t.Tenant)
            .FirstOrDefaultAsync(t => t.Id == templateId, ct);
    }

    public async Task<List<WorkflowTemplate>> GetTemplatesAsync(Guid? tenantId = null, CancellationToken ct = default)
    {
        var query = _context.WorkflowTemplates.AsQueryable();

        if (tenantId.HasValue)
            query = query.Where(t => t.TenantId == tenantId.Value || t.TenantId == null);

        return await query
            .OrderBy(t => t.ResourceType)
            .ThenBy(t => t.Name)
            .ToListAsync(ct);
    }

    public async Task<WorkflowTemplate> UpdateTemplateAsync(Guid templateId, WorkflowTemplate updated, CancellationToken ct = default)
    {
        var template = await _context.WorkflowTemplates
            .FirstOrDefaultAsync(t => t.Id == templateId, ct)
            ?? throw new InvalidOperationException($"Workflow template {templateId} not found");

        template.Name = updated.Name;
        template.Description = updated.Description;
        template.ResourceType = updated.ResourceType;
        template.Steps = updated.Steps;
        template.AutoExpireHours = updated.AutoExpireHours;
        template.AutoApproveRules = updated.AutoApproveRules;
        template.IsActive = updated.IsActive;
        template.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Workflow template {TemplateId} updated", templateId);

        return template;
    }

    public async Task<bool> DeleteTemplateAsync(Guid templateId, CancellationToken ct = default)
    {
        var template = await _context.WorkflowTemplates
            .FirstOrDefaultAsync(t => t.Id == templateId, ct);

        if (template == null)
            return false;

        _context.WorkflowTemplates.Remove(template);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Workflow template {TemplateId} deleted", templateId);

        return true;
    }

    // ─── Private Helpers ─────────────────────────────────────

    private async Task<WorkflowTemplate?> FindMatchingTemplateAsync(
        string resourceType, Guid? tenantId, CancellationToken ct)
    {
        // Try tenant-specific template first, then fall back to global
        var template = await _context.WorkflowTemplates
            .Where(t => t.IsActive && t.ResourceType == resourceType)
            .Where(t => t.TenantId == tenantId || t.TenantId == null)
            .OrderByDescending(t => t.TenantId) // Prefer tenant-specific over global
            .FirstOrDefaultAsync(ct);

        return template;
    }

    private bool ShouldAutoApprove(WorkflowTemplate template, AccessRequest request)
    {
        if (string.IsNullOrEmpty(template.AutoApproveRules))
            return false;

        try
        {
            var rules = JsonSerializer.Deserialize<List<AutoApproveRule>>(template.AutoApproveRules);
            if (rules == null || rules.Count == 0)
                return false;

            foreach (var rule in rules)
            {
                var matches = true;

                if (!string.IsNullOrEmpty(rule.ResourceType) && rule.ResourceType != request.ResourceType)
                    matches = false;

                if (rule.RoleId.HasValue && rule.RoleId != request.RoleId)
                    matches = false;

                if (!string.IsNullOrEmpty(rule.MaxPriority))
                {
                    if (Enum.TryParse<AccessRequestPriority>(rule.MaxPriority, true, out var maxPriority))
                    {
                        if (request.Priority > maxPriority)
                            matches = false;
                    }
                }

                if (matches)
                    return true;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse auto-approve rules for template {TemplateId}", template.Id);
        }

        return false;
    }

    private static List<StepDefinition> ParseStepDefinitions(string stepsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<List<StepDefinition>>(stepsJson) ?? new List<StepDefinition>();
        }
        catch
        {
            return new List<StepDefinition>();
        }
    }

    private async Task VerifyApproverAuthorizationAsync(ApprovalStep step, Guid approverId, CancellationToken ct)
    {
        if (step.ApproverId.HasValue && step.ApproverId.Value == approverId)
            return;

        if (step.ApproverRoleId.HasValue)
        {
            var hasRole = await _context.UserRoles
                .AnyAsync(ur => ur.UserId == approverId && ur.RoleId == step.ApproverRoleId.Value, ct);

            if (hasRole)
                return;
        }

        // If neither direct assignment nor role membership matches, check if approver is a SuperAdmin
        var isSuperAdmin = await _context.UserRoles
            .Include(ur => ur.Role)
            .AnyAsync(ur => ur.UserId == approverId && ur.Role.Name == "SuperAdmin", ct);

        if (isSuperAdmin)
            return;

        throw new InvalidOperationException("You are not authorized to approve or deny this step");
    }

    private async Task NotifyApproversAsync(AccessRequest request, CancellationToken ct)
    {
        try
        {
            var firstStep = request.ApprovalSteps
                .OrderBy(s => s.StepOrder)
                .FirstOrDefault();

            if (firstStep == null)
                return;

            var approverEmails = new List<string>();

            if (firstStep.ApproverId.HasValue)
            {
                var approver = await _context.Users
                    .FirstOrDefaultAsync(u => u.Id == firstStep.ApproverId.Value, ct);
                if (approver != null)
                    approverEmails.Add(approver.Email);
            }
            else if (firstStep.ApproverRoleId.HasValue)
            {
                var roleMembers = await _context.UserRoles
                    .Where(ur => ur.RoleId == firstStep.ApproverRoleId.Value)
                    .Select(ur => ur.User.Email)
                    .ToListAsync(ct);
                approverEmails.AddRange(roleMembers);
            }

            foreach (var email in approverEmails.Distinct())
            {
                try
                {
                    await _emailService.SendEmailVerificationAsync(
                        email,
                        "Approver",
                        $"You have a new access request pending your approval. Request ID: {request.Id}, Resource: {request.ResourceType}, Priority: {request.Priority}",
                        ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send approval notification to {Email}", email);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to notify approvers for request {RequestId}", request.Id);
        }
    }

    private async Task NotifyRequesterAsync(AccessRequest request, string decision, CancellationToken ct)
    {
        try
        {
            var requester = await _context.Users
                .FirstOrDefaultAsync(u => u.Id == request.RequesterId, ct);

            if (requester == null)
                return;

            await _emailService.SendEmailVerificationAsync(
                requester.Email,
                requester.FirstName,
                $"Your access request ({request.ResourceType}) has been {decision}. Request ID: {request.Id}",
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to notify requester for request {RequestId}", request.Id);
        }
    }

    private async Task PublishEventAsync(string eventType, AccessRequest request, CancellationToken ct)
    {
        try
        {
            await _eventBus.PublishAsync(eventType, new
            {
                requestId = request.Id,
                requesterId = request.RequesterId,
                resourceType = request.ResourceType,
                resourceId = request.ResourceId,
                roleId = request.RoleId,
                tenantId = request.TenantId,
                status = request.Status.ToString(),
                priority = request.Priority.ToString()
            }, request.TenantId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish event {EventType} for request {RequestId}", eventType, request.Id);
        }
    }

    // ─── Internal DTOs for JSON Parsing ─────────────────────

    private class StepDefinition
    {
        public int Order { get; set; }
        public Guid? ApproverRoleId { get; set; }
        public Guid? ApproverUserId { get; set; }
        public int QuorumCount { get; set; } = 1;
    }

    private class AutoApproveRule
    {
        public string? ResourceType { get; set; }
        public Guid? RoleId { get; set; }
        public string? MaxPriority { get; set; }
    }
}
