using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// Service for managing access request and approval workflows.
/// Supports single/multi-level approval chains, quorum voting, auto-approve rules, and expiry.
/// </summary>
public interface IAccessRequestService
{
    /// <summary>
    /// Submit a new access request. Finds a matching workflow template
    /// and creates the appropriate approval steps.
    /// </summary>
    Task<AccessRequest> CreateRequestAsync(
        Guid requesterId,
        string resourceType,
        Guid? resourceId,
        Guid? roleId,
        Guid? tenantId,
        string justification,
        AccessRequestPriority priority = AccessRequestPriority.Normal,
        CancellationToken ct = default);

    /// <summary>
    /// Get an access request by ID with approval steps.
    /// </summary>
    Task<AccessRequest?> GetRequestAsync(Guid requestId, CancellationToken ct = default);

    /// <summary>
    /// Get access requests submitted by a specific user.
    /// </summary>
    Task<List<AccessRequest>> GetMyRequestsAsync(Guid requesterId, CancellationToken ct = default);

    /// <summary>
    /// Get pending approval steps assigned to a specific approver (by user ID or role membership).
    /// This is the approver's inbox.
    /// </summary>
    Task<List<AccessRequest>> GetPendingApprovalsAsync(Guid approverId, CancellationToken ct = default);

    /// <summary>
    /// Approve an access request step. If all steps are approved, the request is approved.
    /// </summary>
    Task<AccessRequest> ApproveAsync(Guid requestId, Guid approverId, string? comment = null, CancellationToken ct = default);

    /// <summary>
    /// Deny an access request. Immediately sets the request to Denied status.
    /// </summary>
    Task<AccessRequest> DenyAsync(Guid requestId, Guid approverId, string? comment = null, CancellationToken ct = default);

    /// <summary>
    /// Cancel an access request (by the requester).
    /// </summary>
    Task<AccessRequest> CancelAsync(Guid requestId, Guid requesterId, CancellationToken ct = default);

    /// <summary>
    /// Expire all access requests that have passed their ExpiresAt deadline.
    /// Called by the background worker.
    /// </summary>
    Task<int> ExpireOverdueRequestsAsync(CancellationToken ct = default);

    // ─── Workflow Templates ─────────────────────────────────────

    /// <summary>
    /// Create a new workflow template.
    /// </summary>
    Task<WorkflowTemplate> CreateTemplateAsync(WorkflowTemplate template, CancellationToken ct = default);

    /// <summary>
    /// Get a workflow template by ID.
    /// </summary>
    Task<WorkflowTemplate?> GetTemplateAsync(Guid templateId, CancellationToken ct = default);

    /// <summary>
    /// Get all workflow templates, optionally filtered by tenant.
    /// </summary>
    Task<List<WorkflowTemplate>> GetTemplatesAsync(Guid? tenantId = null, CancellationToken ct = default);

    /// <summary>
    /// Update an existing workflow template.
    /// </summary>
    Task<WorkflowTemplate> UpdateTemplateAsync(Guid templateId, WorkflowTemplate updated, CancellationToken ct = default);

    /// <summary>
    /// Delete a workflow template.
    /// </summary>
    Task<bool> DeleteTemplateAsync(Guid templateId, CancellationToken ct = default);
}
