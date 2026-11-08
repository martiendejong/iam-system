using System.Security.Claims;
using Hazina.Security.ApiKeys;
using IAM.API.Authorization;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/email-templates")]
[Authorize]
public class EmailTemplatesController : ControllerBase
{
    private readonly IAMDbContext _context;

    private static readonly string[] ValidKeys = ["Invitation"];

    public EmailTemplatesController(IAMDbContext context)
    {
        _context = context;
    }

    // Task 4716. A template controls the link (and invitation token) in invitation emails, so reading or changing
    // one needs SuperAdmin/SystemAdmin or an active TenantAdmin/BuildingOwner/BuildingManager row in the tenant of
    // the request. Role claims alone confer nothing; tokens pinned to another tenant, device / service-account
    // tokens and API keys are refused. The check runs before any lookup, so unknown and foreign tenants both give 403.
    private ObjectResult Forbidden() =>
        StatusCode(StatusCodes.Status403Forbidden,
            new { error = "Only SuperAdmin, SystemAdmin or an administrator of this tenant can manage its email templates." });

    private async Task<ActionResult?> RequireTenantAdminAsync(Guid tenantId, CancellationToken ct)
    {
        if (User.IsApiKey() || User.FindFirst(ServiceAccountAuthorization.TokenTypeClaim) != null
            || User.FindFirst("token_type")?.Value is "device" or "service_account")
            return Forbidden();

        if (User.IsInRole("SuperAdmin") || User.IsInRole("SystemAdmin"))
            return null;

        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (!Guid.TryParse(subject, out var userId))
            return Forbidden();

        var tenantClaim = User.FindFirst("tenant_id")?.Value;
        if (!string.IsNullOrWhiteSpace(tenantClaim)
            && (!Guid.TryParse(tenantClaim, out var pinned) || pinned != tenantId))
            return Forbidden();

        return await TenantAdminAuthority.HasAdministratorRoleInTenantAsync(_context, userId, tenantId, ct)
            ? null
            : Forbidden();
    }

    /// <summary>
    /// List all customized email templates for a tenant
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetTemplates([FromQuery] Guid tenantId, CancellationToken ct)
    {
        if (await RequireTenantAdminAsync(tenantId, ct) is { } denied) return denied;

        var templates = await _context.Set<EmailTemplate>()
            .Where(t => t.TenantId == tenantId)
            .OrderBy(t => t.Key)
            .ToListAsync();

        return Ok(templates.Select(ToDto));
    }

    /// <summary>
    /// Get a tenant's template for a specific key (e.g. "Invitation")
    /// </summary>
    [HttpGet("{key}")]
    public async Task<IActionResult> GetTemplate([FromQuery] Guid tenantId, string key, CancellationToken ct)
    {
        if (await RequireTenantAdminAsync(tenantId, ct) is { } denied) return denied;

        var template = await _context.Set<EmailTemplate>()
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.Key == key);

        if (template == null)
        {
            return NotFound(new { error = "No custom template configured for this key; the built-in default is used." });
        }

        return Ok(ToDto(template));
    }

    /// <summary>
    /// Create or update a tenant's template for a key
    /// </summary>
    [HttpPut("{key}")]
    public async Task<IActionResult> UpsertTemplate(Guid tenantId, string key, [FromBody] UpsertEmailTemplateRequest request, CancellationToken ct)
    {
        if (await RequireTenantAdminAsync(tenantId, ct) is { } denied) return denied;

        if (!ValidKeys.Contains(key))
        {
            return BadRequest(new { error = $"Unknown template key '{key}'. Valid keys: {string.Join(", ", ValidKeys)}" });
        }

        if (string.IsNullOrWhiteSpace(request.Subject) || string.IsNullOrWhiteSpace(request.BodyHtml))
        {
            return BadRequest(new { error = "Subject and BodyHtml are required" });
        }

        var tenantExists = await _context.Tenants.AnyAsync(t => t.Id == tenantId);
        if (!tenantExists)
        {
            return NotFound(new { error = "Tenant not found" });
        }

        var template = await _context.Set<EmailTemplate>()
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.Key == key);

        if (template == null)
        {
            template = new EmailTemplate { TenantId = tenantId, Key = key };
            _context.Set<EmailTemplate>().Add(template);
        }

        template.Subject = request.Subject;
        template.BodyHtml = request.BodyHtml;
        template.IsActive = request.IsActive ?? true;
        template.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return Ok(ToDto(template));
    }

    /// <summary>
    /// Remove a tenant's custom template, reverting to the built-in default
    /// </summary>
    [HttpDelete("{key}")]
    public async Task<IActionResult> DeleteTemplate([FromQuery] Guid tenantId, string key, CancellationToken ct)
    {
        if (await RequireTenantAdminAsync(tenantId, ct) is { } denied) return denied;

        var template = await _context.Set<EmailTemplate>()
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.Key == key);

        if (template == null)
        {
            return NotFound(new { error = "Template not found" });
        }

        _context.Set<EmailTemplate>().Remove(template);
        await _context.SaveChangesAsync();

        return Ok(new { message = "Template removed; the built-in default will be used." });
    }

    private static object ToDto(EmailTemplate t) => new
    {
        id = t.Id,
        tenantId = t.TenantId,
        key = t.Key,
        subject = t.Subject,
        bodyHtml = t.BodyHtml,
        isActive = t.IsActive,
        createdAt = t.CreatedAt,
        updatedAt = t.UpdatedAt
    };
}

public record UpsertEmailTemplateRequest(string Subject, string BodyHtml, bool? IsActive);
