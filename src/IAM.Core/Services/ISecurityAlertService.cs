using IAM.Core.Entities;

namespace IAM.Core.Services;

/// <summary>
/// Service for managing security alert rules, firing alerts, SIEM integration, and auto-response.
/// </summary>
public interface ISecurityAlertService
{
    // ── Alert Rules ─────────────────────────────────────────────
    Task<AlertRule> CreateRuleAsync(AlertRule rule, CancellationToken ct = default);
    Task<AlertRule?> GetRuleAsync(Guid id, CancellationToken ct = default);
    Task<List<AlertRule>> GetRulesAsync(Guid? tenantId = null, CancellationToken ct = default);
    Task<AlertRule> UpdateRuleAsync(Guid id, AlertRule updated, CancellationToken ct = default);
    Task<bool> DeleteRuleAsync(Guid id, CancellationToken ct = default);

    // ── Security Alerts ─────────────────────────────────────────
    Task<SecurityAlert> FireAlertAsync(Guid ruleId, string title, string? detailsJson, Guid? tenantId = null, CancellationToken ct = default);
    Task<List<SecurityAlert>> GetActiveAlertsAsync(Guid? tenantId = null, CancellationToken ct = default);
    Task<List<SecurityAlert>> GetAlertHistoryAsync(Guid? tenantId = null, int skip = 0, int take = 100, CancellationToken ct = default);
    Task<SecurityAlert?> AcknowledgeAlertAsync(Guid alertId, Guid userId, CancellationToken ct = default);

    // ── Alert Condition Evaluation (called by background worker) ──
    Task EvaluateAlertConditionsAsync(CancellationToken ct = default);

    // ── SIEM Integrations ───────────────────────────────────────
    Task<SiemIntegration> CreateSiemIntegrationAsync(SiemIntegration integration, CancellationToken ct = default);
    Task<SiemIntegration?> GetSiemIntegrationAsync(Guid id, CancellationToken ct = default);
    Task<List<SiemIntegration>> GetSiemIntegrationsAsync(Guid? tenantId = null, CancellationToken ct = default);
    Task<SiemIntegration> UpdateSiemIntegrationAsync(Guid id, SiemIntegration updated, CancellationToken ct = default);
    Task<bool> DeleteSiemIntegrationAsync(Guid id, CancellationToken ct = default);
    Task ExportToSiemAsync(string eventType, object payload, Guid? tenantId = null, CancellationToken ct = default);
}
