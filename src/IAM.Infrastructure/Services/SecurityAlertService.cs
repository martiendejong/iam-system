using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Manages security alert rules, fires alerts with notification channels,
/// evaluates built-in alert conditions, executes auto-responses, and exports events to SIEM.
/// </summary>
public class SecurityAlertService : ISecurityAlertService
{
    private readonly IAMDbContext _context;
    private readonly IEmailService _emailService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SecurityAlertService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public SecurityAlertService(
        IAMDbContext context,
        IEmailService emailService,
        IHttpClientFactory httpClientFactory,
        ILogger<SecurityAlertService> logger)
    {
        _context = context;
        _emailService = emailService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // ════════════════════════════════════════════════════════════
    //  Alert Rules CRUD
    // ════════════════════════════════════════════════════════════

    public async Task<AlertRule> CreateRuleAsync(AlertRule rule, CancellationToken ct = default)
    {
        rule.Id = Guid.NewGuid();
        rule.CreatedAt = DateTime.UtcNow;
        rule.UpdatedAt = DateTime.UtcNow;

        _context.AlertRules.Add(rule);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Alert rule created: {RuleName} (severity: {Severity})", rule.Name, rule.Severity);
        return rule;
    }

    public async Task<AlertRule?> GetRuleAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.AlertRules
            .Include(r => r.Alerts.OrderByDescending(a => a.CreatedAt).Take(5))
            .FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<List<AlertRule>> GetRulesAsync(Guid? tenantId = null, CancellationToken ct = default)
    {
        var query = _context.AlertRules.AsQueryable();
        if (tenantId.HasValue)
            query = query.Where(r => r.TenantId == tenantId || r.TenantId == null);

        return await query.OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
    }

    public async Task<AlertRule> UpdateRuleAsync(Guid id, AlertRule updated, CancellationToken ct = default)
    {
        var rule = await _context.AlertRules.FindAsync(new object[] { id }, ct)
            ?? throw new KeyNotFoundException($"Alert rule {id} not found");

        rule.Name = updated.Name;
        rule.Condition = updated.Condition;
        rule.Severity = updated.Severity;
        rule.Channels = updated.Channels;
        rule.CooldownMinutes = updated.CooldownMinutes;
        rule.AutoResponseAction = updated.AutoResponseAction;
        rule.IsActive = updated.IsActive;
        rule.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Alert rule updated: {RuleName}", rule.Name);
        return rule;
    }

    public async Task<bool> DeleteRuleAsync(Guid id, CancellationToken ct = default)
    {
        var rule = await _context.AlertRules.FindAsync(new object[] { id }, ct);
        if (rule == null) return false;

        _context.AlertRules.Remove(rule);
        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Alert rule deleted: {RuleName}", rule.Name);
        return true;
    }

    // ════════════════════════════════════════════════════════════
    //  Security Alerts
    // ════════════════════════════════════════════════════════════

    public async Task<SecurityAlert> FireAlertAsync(
        Guid ruleId, string title, string? detailsJson,
        Guid? tenantId = null, CancellationToken ct = default)
    {
        var rule = await _context.AlertRules.FindAsync(new object[] { ruleId }, ct);
        if (rule == null)
            throw new KeyNotFoundException($"Alert rule {ruleId} not found");

        // Cooldown check: don't fire if a recent alert exists for this rule
        var cooldownCutoff = DateTime.UtcNow.AddMinutes(-rule.CooldownMinutes);
        var recentAlert = await _context.SecurityAlerts
            .Where(a => a.RuleId == ruleId && a.CreatedAt > cooldownCutoff)
            .AnyAsync(ct);

        if (recentAlert)
        {
            _logger.LogDebug("Alert suppressed by cooldown for rule {RuleName}", rule.Name);
            // Return a placeholder alert indicating suppression
            return new SecurityAlert
            {
                Id = Guid.Empty,
                RuleId = ruleId,
                Title = $"[Suppressed] {title}",
                Severity = rule.Severity
            };
        }

        var alert = new SecurityAlert
        {
            TenantId = tenantId ?? rule.TenantId,
            RuleId = ruleId,
            Severity = rule.Severity,
            Title = title,
            Details = detailsJson,
            CreatedAt = DateTime.UtcNow
        };

        // Execute auto-response if configured
        if (!string.IsNullOrEmpty(rule.AutoResponseAction))
        {
            alert.AutoResponseAction = rule.AutoResponseAction;
            await ExecuteAutoResponseAsync(rule.AutoResponseAction, detailsJson, ct);
        }

        _context.SecurityAlerts.Add(alert);
        await _context.SaveChangesAsync(ct);

        _logger.LogWarning("Security alert fired: {Title} (severity: {Severity})", title, alert.Severity);

        // Send notifications via configured channels (fire and forget, don't block)
        _ = Task.Run(async () =>
        {
            try { await SendNotificationsAsync(rule, alert); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to send alert notifications for {AlertId}", alert.Id); }
        }, CancellationToken.None);

        // Export to SIEM
        _ = Task.Run(async () =>
        {
            try { await ExportToSiemAsync("security.alert", alert, alert.TenantId); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to export alert to SIEM for {AlertId}", alert.Id); }
        }, CancellationToken.None);

        return alert;
    }

    public async Task<List<SecurityAlert>> GetActiveAlertsAsync(Guid? tenantId = null, CancellationToken ct = default)
    {
        var query = _context.SecurityAlerts
            .Include(a => a.Rule)
            .Where(a => a.AcknowledgedAt == null);

        if (tenantId.HasValue)
            query = query.Where(a => a.TenantId == tenantId || a.TenantId == null);

        return await query
            .OrderByDescending(a => a.Severity)
            .ThenByDescending(a => a.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<List<SecurityAlert>> GetAlertHistoryAsync(
        Guid? tenantId = null, int skip = 0, int take = 100, CancellationToken ct = default)
    {
        var query = _context.SecurityAlerts
            .Include(a => a.Rule)
            .Include(a => a.AcknowledgedByUser)
            .AsQueryable();

        if (tenantId.HasValue)
            query = query.Where(a => a.TenantId == tenantId || a.TenantId == null);

        return await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<SecurityAlert?> AcknowledgeAlertAsync(Guid alertId, Guid userId, CancellationToken ct = default)
    {
        var alert = await _context.SecurityAlerts.FindAsync(new object[] { alertId }, ct);
        if (alert == null) return null;

        alert.AcknowledgedAt = DateTime.UtcNow;
        alert.AcknowledgedByUserId = userId;

        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Alert {AlertId} acknowledged by user {UserId}", alertId, userId);
        return alert;
    }

    // ════════════════════════════════════════════════════════════
    //  Built-in Alert Condition Evaluation
    // ════════════════════════════════════════════════════════════

    public async Task EvaluateAlertConditionsAsync(CancellationToken ct = default)
    {
        var activeRules = await _context.AlertRules
            .Where(r => r.IsActive)
            .ToListAsync(ct);

        foreach (var rule in activeRules)
        {
            try
            {
                var conditionDoc = JsonDocument.Parse(rule.Condition);
                var root = conditionDoc.RootElement;

                if (!root.TryGetProperty("type", out var typeElement))
                    continue;

                var conditionType = typeElement.GetString();

                switch (conditionType)
                {
                    case "brute_force":
                        await EvaluateBruteForceAsync(rule, root, ct);
                        break;
                    case "impossible_travel":
                        await EvaluateImpossibleTravelAsync(rule, root, ct);
                        break;
                    case "privilege_escalation":
                        await EvaluatePrivilegeEscalationAsync(rule, root, ct);
                        break;
                    case "mass_deletion":
                        await EvaluateMassDeletionAsync(rule, root, ct);
                        break;
                    default:
                        _logger.LogDebug("Unknown alert condition type: {Type}", conditionType);
                        break;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Invalid JSON condition for rule {RuleId}: {RuleName}", rule.Id, rule.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating alert rule {RuleId}: {RuleName}", rule.Id, rule.Name);
            }
        }
    }

    /// <summary>
    /// Brute force: N failed logins within M minutes for the same user/IP
    /// Condition: { "type": "brute_force", "threshold": 5, "windowMinutes": 5 }
    /// </summary>
    private async Task EvaluateBruteForceAsync(AlertRule rule, JsonElement condition, CancellationToken ct)
    {
        var threshold = condition.TryGetProperty("threshold", out var t) ? t.GetInt32() : 5;
        var windowMinutes = condition.TryGetProperty("windowMinutes", out var w) ? w.GetInt32() : 5;
        var since = DateTime.UtcNow.AddMinutes(-windowMinutes);

        // Group failed login attempts by user in the time window
        var failedAttempts = await _context.AuditLogs
            .Where(a => a.Action == "LoginFailed" && a.CreatedAt >= since)
            .GroupBy(a => a.UserId ?? Guid.Empty)
            .Select(g => new { UserId = g.Key, Count = g.Count(), LastIp = g.OrderByDescending(x => x.CreatedAt).First().IpAddress })
            .Where(g => g.Count >= threshold)
            .ToListAsync(ct);

        foreach (var attempt in failedAttempts)
        {
            var details = JsonSerializer.Serialize(new
            {
                userId = attempt.UserId,
                failedAttempts = attempt.Count,
                windowMinutes,
                lastIpAddress = attempt.LastIp
            }, JsonOptions);

            await FireAlertAsync(
                rule.Id,
                $"Brute force detected: {attempt.Count} failed logins in {windowMinutes}min",
                details,
                rule.TenantId,
                ct);
        }
    }

    /// <summary>
    /// Impossible travel: logins from geographically distant IPs within a short timeframe.
    /// Condition: { "type": "impossible_travel", "windowMinutes": 60 }
    /// Detects different IPs for the same user within the window (simplified without geo lookup).
    /// </summary>
    private async Task EvaluateImpossibleTravelAsync(AlertRule rule, JsonElement condition, CancellationToken ct)
    {
        var windowMinutes = condition.TryGetProperty("windowMinutes", out var w) ? w.GetInt32() : 60;
        var since = DateTime.UtcNow.AddMinutes(-windowMinutes);

        // Find users with successful logins from multiple distinct IPs in the window
        var suspiciousLogins = await _context.AuditLogs
            .Where(a => a.Action == "Login" && a.CreatedAt >= since && a.IpAddress != null)
            .GroupBy(a => a.UserId ?? Guid.Empty)
            .Select(g => new
            {
                UserId = g.Key,
                DistinctIps = g.Select(x => x.IpAddress).Distinct().ToList(),
                Logins = g.OrderBy(x => x.CreatedAt).ToList()
            })
            .Where(g => g.DistinctIps.Count >= 2)
            .ToListAsync(ct);

        foreach (var suspicious in suspiciousLogins)
        {
            var details = JsonSerializer.Serialize(new
            {
                userId = suspicious.UserId,
                distinctIps = suspicious.DistinctIps,
                loginCount = suspicious.Logins.Count,
                windowMinutes
            }, JsonOptions);

            await FireAlertAsync(
                rule.Id,
                $"Impossible travel: user logged in from {suspicious.DistinctIps.Count} different IPs in {windowMinutes}min",
                details,
                rule.TenantId,
                ct);
        }
    }

    /// <summary>
    /// Privilege escalation: role assignments granting elevated permissions.
    /// Condition: { "type": "privilege_escalation", "windowMinutes": 30 }
    /// </summary>
    private async Task EvaluatePrivilegeEscalationAsync(AlertRule rule, JsonElement condition, CancellationToken ct)
    {
        var windowMinutes = condition.TryGetProperty("windowMinutes", out var w) ? w.GetInt32() : 30;
        var since = DateTime.UtcNow.AddMinutes(-windowMinutes);

        var escalations = await _context.AuditLogs
            .Where(a => (a.Action == "RoleAssigned" || a.Action == "PermissionGranted") && a.CreatedAt >= since)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

        if (escalations.Count >= 3)
        {
            var details = JsonSerializer.Serialize(new
            {
                escalationCount = escalations.Count,
                windowMinutes,
                recentActions = escalations.Take(10).Select(e => new
                {
                    action = e.Action,
                    userId = e.UserId,
                    createdAt = e.CreatedAt,
                    details = e.Details
                })
            }, JsonOptions);

            await FireAlertAsync(
                rule.Id,
                $"Privilege escalation: {escalations.Count} role/permission changes in {windowMinutes}min",
                details,
                rule.TenantId,
                ct);
        }
    }

    /// <summary>
    /// Mass deletion: bulk delete operations within a short window.
    /// Condition: { "type": "mass_deletion", "threshold": 10, "windowMinutes": 10 }
    /// </summary>
    private async Task EvaluateMassDeletionAsync(AlertRule rule, JsonElement condition, CancellationToken ct)
    {
        var threshold = condition.TryGetProperty("threshold", out var t) ? t.GetInt32() : 10;
        var windowMinutes = condition.TryGetProperty("windowMinutes", out var w) ? w.GetInt32() : 10;
        var since = DateTime.UtcNow.AddMinutes(-windowMinutes);

        var deletions = await _context.AuditLogs
            .Where(a => a.Action.Contains("Deleted") && a.CreatedAt >= since)
            .GroupBy(a => a.UserId ?? Guid.Empty)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .Where(g => g.Count >= threshold)
            .ToListAsync(ct);

        foreach (var deletion in deletions)
        {
            var details = JsonSerializer.Serialize(new
            {
                userId = deletion.UserId,
                deletionCount = deletion.Count,
                windowMinutes,
                threshold
            }, JsonOptions);

            await FireAlertAsync(
                rule.Id,
                $"Mass deletion: {deletion.Count} deletions by single user in {windowMinutes}min",
                details,
                rule.TenantId,
                ct);
        }
    }

    // ════════════════════════════════════════════════════════════
    //  Auto-Response Actions
    // ════════════════════════════════════════════════════════════

    private async Task ExecuteAutoResponseAsync(string action, string? detailsJson, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(detailsJson)) return;

        try
        {
            var details = JsonDocument.Parse(detailsJson);
            var root = details.RootElement;

            if (!root.TryGetProperty("userId", out var userIdElement))
                return;

            var userIdStr = userIdElement.GetString() ?? userIdElement.ToString();
            if (!Guid.TryParse(userIdStr, out var userId))
                return;

            var user = await _context.Users.FindAsync(new object[] { userId }, ct);
            if (user == null) return;

            switch (action)
            {
                case "lock_account":
                    user.IsActive = false;
                    user.UpdatedAt = DateTime.UtcNow;
                    _logger.LogWarning("Auto-response: Account locked for user {UserId}", userId);
                    break;

                case "force_mfa":
                    user.TwoFactorEnabled = true;
                    user.UpdatedAt = DateTime.UtcNow;
                    _logger.LogWarning("Auto-response: MFA forced for user {UserId}", userId);
                    break;

                case "kill_sessions":
                    var sessions = await _context.UserSessions
                        .Where(s => s.UserId == userId && !s.IsRevoked)
                        .ToListAsync(ct);
                    foreach (var session in sessions)
                    {
                        session.IsRevoked = true;
                        session.RevokedAt = DateTime.UtcNow;
                        session.RevokedReason = "security_alert";
                    }
                    _logger.LogWarning("Auto-response: {Count} sessions killed for user {UserId}", sessions.Count, userId);
                    break;

                default:
                    _logger.LogWarning("Unknown auto-response action: {Action}", action);
                    break;
            }

            await _context.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute auto-response action: {Action}", action);
        }
    }

    // ════════════════════════════════════════════════════════════
    //  Notification Channels
    // ════════════════════════════════════════════════════════════

    private async Task SendNotificationsAsync(AlertRule rule, SecurityAlert alert)
    {
        try
        {
            var channels = JsonSerializer.Deserialize<List<AlertChannel>>(rule.Channels, JsonOptions);
            if (channels == null) return;

            foreach (var channel in channels)
            {
                try
                {
                    switch (channel.Type?.ToLowerInvariant())
                    {
                        case "email":
                            if (!string.IsNullOrEmpty(channel.Target))
                            {
                                await _emailService.SendSessionAlertAsync(
                                    channel.Target,
                                    "Security Alert",
                                    $"[{alert.Severity}] {alert.Title}",
                                    alert.Details ?? "No additional details");
                            }
                            break;

                        case "webhook":
                            if (!string.IsNullOrEmpty(channel.Target))
                            {
                                var client = _httpClientFactory.CreateClient("WebhookDelivery");
                                var payload = JsonSerializer.Serialize(new
                                {
                                    alertId = alert.Id,
                                    severity = alert.Severity.ToString(),
                                    title = alert.Title,
                                    details = alert.Details,
                                    ruleId = alert.RuleId,
                                    ruleName = rule.Name,
                                    timestamp = alert.CreatedAt,
                                    autoResponse = alert.AutoResponseAction
                                }, JsonOptions);

                                var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                                await client.PostAsync(channel.Target, content);
                            }
                            break;

                        case "slack":
                            if (!string.IsNullOrEmpty(channel.Target))
                            {
                                var slackClient = _httpClientFactory.CreateClient("WebhookDelivery");
                                var severityEmoji = alert.Severity switch
                                {
                                    AlertSeverity.Critical => ":rotating_light:",
                                    AlertSeverity.Warning => ":warning:",
                                    _ => ":information_source:"
                                };
                                var slackPayload = JsonSerializer.Serialize(new
                                {
                                    text = $"{severityEmoji} *{alert.Severity}* | {alert.Title}",
                                    blocks = new[]
                                    {
                                        new
                                        {
                                            type = "section",
                                            text = new { type = "mrkdwn", text = $"{severityEmoji} *Security Alert: {alert.Title}*\n*Severity:* {alert.Severity}\n*Rule:* {rule.Name}\n*Time:* {alert.CreatedAt:u}" }
                                        }
                                    }
                                }, JsonOptions);

                                var slackContent = new StringContent(slackPayload, System.Text.Encoding.UTF8, "application/json");
                                await slackClient.PostAsync(channel.Target, slackContent);
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send alert via {ChannelType} to {Target}", channel.Type, channel.Target);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse alert channels JSON for rule {RuleId}", rule.Id);
        }
    }

    // ════════════════════════════════════════════════════════════
    //  SIEM Integrations
    // ════════════════════════════════════════════════════════════

    public async Task<SiemIntegration> CreateSiemIntegrationAsync(SiemIntegration integration, CancellationToken ct = default)
    {
        integration.Id = Guid.NewGuid();
        integration.CreatedAt = DateTime.UtcNow;
        integration.UpdatedAt = DateTime.UtcNow;

        _context.SiemIntegrations.Add(integration);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("SIEM integration created: {Name} ({Type})", integration.Name, integration.Type);
        return integration;
    }

    public async Task<SiemIntegration?> GetSiemIntegrationAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.SiemIntegrations.FindAsync(new object[] { id }, ct);
    }

    public async Task<List<SiemIntegration>> GetSiemIntegrationsAsync(Guid? tenantId = null, CancellationToken ct = default)
    {
        var query = _context.SiemIntegrations.AsQueryable();
        if (tenantId.HasValue)
            query = query.Where(s => s.TenantId == tenantId || s.TenantId == null);

        return await query.OrderByDescending(s => s.CreatedAt).ToListAsync(ct);
    }

    public async Task<SiemIntegration> UpdateSiemIntegrationAsync(Guid id, SiemIntegration updated, CancellationToken ct = default)
    {
        var integration = await _context.SiemIntegrations.FindAsync(new object[] { id }, ct)
            ?? throw new KeyNotFoundException($"SIEM integration {id} not found");

        integration.Name = updated.Name;
        integration.Type = updated.Type;
        integration.EndpointUrl = updated.EndpointUrl;
        integration.AuthConfig = updated.AuthConfig;
        integration.Format = updated.Format;
        integration.EventFilter = updated.EventFilter;
        integration.IsActive = updated.IsActive;
        integration.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("SIEM integration updated: {Name}", integration.Name);
        return integration;
    }

    public async Task<bool> DeleteSiemIntegrationAsync(Guid id, CancellationToken ct = default)
    {
        var integration = await _context.SiemIntegrations.FindAsync(new object[] { id }, ct);
        if (integration == null) return false;

        _context.SiemIntegrations.Remove(integration);
        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("SIEM integration deleted: {Name}", integration.Name);
        return true;
    }

    public async Task ExportToSiemAsync(string eventType, object payload, Guid? tenantId = null, CancellationToken ct = default)
    {
        var integrations = await _context.SiemIntegrations
            .Where(s => s.IsActive)
            .ToListAsync(ct);

        if (tenantId.HasValue)
            integrations = integrations.Where(s => s.TenantId == tenantId || s.TenantId == null).ToList();

        foreach (var siem in integrations)
        {
            // Check event filter
            if (!string.IsNullOrEmpty(siem.EventFilter))
            {
                try
                {
                    var filter = JsonSerializer.Deserialize<List<string>>(siem.EventFilter, JsonOptions);
                    if (filter != null && !filter.Contains("*") && !filter.Contains(eventType))
                        continue;
                }
                catch { /* Invalid filter = accept all */ }
            }

            try
            {
                var client = _httpClientFactory.CreateClient("WebhookDelivery");

                // Build SIEM-formatted payload
                var siemPayload = BuildSiemPayload(siem, eventType, payload);

                // Apply auth config
                ApplyAuthConfig(client, siem);

                var content = new StringContent(siemPayload, System.Text.Encoding.UTF8, "application/json");
                var response = await client.PostAsync(siem.EndpointUrl, content, ct);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "SIEM export failed for {SiemName}: {StatusCode}",
                        siem.Name, response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to export event to SIEM {SiemName}", siem.Name);
            }
        }
    }

    private string BuildSiemPayload(SiemIntegration siem, string eventType, object payload)
    {
        var envelope = new
        {
            timestamp = DateTime.UtcNow,
            source = "iam-system",
            eventType,
            siemType = siem.Type.ToString(),
            tenantId = siem.TenantId,
            data = payload
        };

        return siem.Format?.ToUpperInvariant() switch
        {
            "CEF" => $"CEF:0|IAM|SecurityEvent|1.0|{eventType}|{eventType}|5|msg={JsonSerializer.Serialize(payload, JsonOptions)}",
            "LEEF" => $"LEEF:1.0|IAM|SecurityEvent|1.0|{eventType}|src=iam-system\tmsg={JsonSerializer.Serialize(payload, JsonOptions)}",
            _ => JsonSerializer.Serialize(envelope, JsonOptions)
        };
    }

    private void ApplyAuthConfig(HttpClient client, SiemIntegration siem)
    {
        if (string.IsNullOrEmpty(siem.AuthConfig)) return;

        try
        {
            var auth = JsonDocument.Parse(siem.AuthConfig);
            var root = auth.RootElement;

            if (root.TryGetProperty("apiKey", out var apiKey))
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {apiKey.GetString()}");

            if (root.TryGetProperty("headerName", out var headerName) &&
                root.TryGetProperty("headerValue", out var headerValue))
                client.DefaultRequestHeaders.TryAddWithoutValidation(headerName.GetString()!, headerValue.GetString()!);

            // Splunk HEC token
            if (root.TryGetProperty("hecToken", out var hecToken))
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Splunk {hecToken.GetString()}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply auth config for SIEM {SiemName}", siem.Name);
        }
    }
}

/// <summary>
/// Represents a notification channel in alert rule configuration
/// </summary>
internal class AlertChannel
{
    public string? Type { get; set; }
    public string? Target { get; set; }
}
