using System.Security.Claims;
using IAM.API.Authorization;
using IAM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IAM.API.Controllers;

[ApiController]
[Route("api/ca")]
[Authorize]
public class CertificateAuthorityController : ControllerBase
{
    private readonly ICertificateAuthorityService _caService;
    private readonly ITenantAccessResolver _access;

    public CertificateAuthorityController(ICertificateAuthorityService caService, ITenantAccessResolver access)
    {
        _caService = caService;
        _access = access;
    }

    // Task 5148. Certificates issued here are device identities that brokers and gateways trust, and issue/renew
    // return the private key, so issue, renew, revoke, info and expiring need SuperAdmin or a building owner/manager
    // (TenantAdmin/BuildingOwner/BuildingManager UserRoles row) of the device's own tenant. The tenant always comes
    // from the stored device or certificate, never from the request; a caller who manages some other tenant gets the
    // same 404 as for an unknown id. Device and service-account tokens are refused. The public CA certificate, the
    // CRL and validation stay open: they are how devices and brokers verify a certificate.

    private ObjectResult Forbidden() =>
        StatusCode(StatusCodes.Status403Forbidden,
            new { error = "Only SuperAdmin or a building owner/manager of the device's tenant can use the certificate authority." });

    /// <summary>
    /// Get CA information including certificate statistics
    /// </summary>
    [HttpGet("info")]
    public async Task<IActionResult> GetCaInfo(CancellationToken ct)
    {
        var access = await _access.ResolveAsync(User, ct);
        if (!access.CanManageAny)
            return Forbidden();

        var info = await _caService.GetCaInfoAsync(ct);

        return Ok(new
        {
            subjectName = info.SubjectName,
            notBefore = info.NotBefore,
            notAfter = info.NotAfter,
            thumbprint = info.Thumbprint,
            certificatesIssued = info.CertificatesIssued,
            certificatesRevoked = info.CertificatesRevoked,
            certificatesActive = info.CertificatesActive
        });
    }

    /// <summary>
    /// Download the CA root certificate in PEM format
    /// </summary>
    [HttpGet("certificate")]
    public async Task<IActionResult> GetCaCertificate(CancellationToken ct)
    {
        var info = await _caService.GetCaInfoAsync(ct);

        return Content(info.CaCertificatePem, "application/x-pem-file");
    }

    /// <summary>
    /// Issue a new certificate for a device
    /// </summary>
    [HttpPost("issue")]
    public async Task<IActionResult> IssueCertificate(
        [FromBody] IssueCertificateRequestDto request,
        CancellationToken ct)
    {
        // Authorize before anything else, so an unauthorized caller learns nothing about which devices exist.
        var access = await _access.ResolveAsync(User, ct);
        if (!access.CanManageAny)
            return Forbidden();

        if (request.DeviceId == Guid.Empty)
        {
            return BadRequest(new { error = "DeviceId is required" });
        }

        var validityDays = request.ValidityDays ?? CertificateIssuingRules.DefaultValidityDays;
        var keySizeBits = request.KeySizeBits ?? CertificateIssuingRules.AllowedKeySizes[0];
        var problem = CertificateIssuingRules.Check(
            validityDays, keySizeBits, request.CommonName, request.Organization, request.OrganizationalUnit);
        if (problem != null)
        {
            return BadRequest(new { error = problem });
        }

        var deviceTenantId = await _caService.GetDeviceTenantIdAsync(request.DeviceId, ct);
        if (deviceTenantId is not { } tenantId || !access.CanManage(tenantId))
        {
            return NotFound(new { error = "Device not found" });
        }

        var userId = GetCurrentUserId();

        var certRequest = new CertificateRequest
        {
            CommonName = request.CommonName,
            Organization = request.Organization,
            OrganizationalUnit = request.OrganizationalUnit,
            ValidityDays = validityDays,
            KeySizeInBits = keySizeBits
        };

        try
        {
            var (certificate, privateKeyPem) = await _caService.IssueCertificateAsync(
                request.DeviceId, certRequest, userId, ct);

            return Ok(new
            {
                certificateId = certificate.Id,
                certificatePem = certificate.CertificatePem,
                privateKeyPem = privateKeyPem, // ONLY returned once
                thumbprint = certificate.Thumbprint,
                serialNumber = certificate.SerialNumber,
                subjectName = certificate.SubjectName,
                issuerName = certificate.IssuerName,
                notBefore = certificate.NotBefore,
                notAfter = certificate.NotAfter,
                status = certificate.Status
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Revoke a certificate
    /// </summary>
    [HttpPost("revoke/{certificateId:guid}")]
    public async Task<IActionResult> RevokeCertificate(
        Guid certificateId,
        [FromBody] RevokeCertificateRequestDto request,
        CancellationToken ct)
    {
        var access = await _access.ResolveAsync(User, ct);
        if (!access.CanManageAny)
            return Forbidden();

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest(new { error = "Reason is required" });
        }

        var certificateTenantId = await _caService.GetCertificateTenantIdAsync(certificateId, ct);
        if (certificateTenantId is not { } tenantId || !access.CanManage(tenantId))
        {
            return NotFound(new { error = "Certificate not found" });
        }

        var userId = GetCurrentUserId();

        var success = await _caService.RevokeCertificateAsync(
            certificateId, request.Reason, userId, ct);

        if (!success)
        {
            return NotFound(new { error = "Certificate not found" });
        }

        return Ok(new { message = "Certificate revoked successfully", certificateId });
    }

    /// <summary>
    /// Validate a certificate (check CA trust chain, expiry, revocation)
    /// </summary>
    [HttpPost("validate")]
    public async Task<IActionResult> ValidateCertificate(
        [FromBody] ValidateCertificateRequestDto request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CertificatePem))
        {
            return BadRequest(new { error = "CertificatePem is required" });
        }

        var result = await _caService.ValidateCertificateAsync(request.CertificatePem, ct);

        return Ok(new
        {
            isValid = result.IsValid,
            error = result.Error,
            isExpired = result.IsExpired,
            isRevoked = result.IsRevoked,
            isTrusted = result.IsTrusted,
            notBefore = result.NotBefore,
            notAfter = result.NotAfter,
            subjectName = result.SubjectName,
            serialNumber = result.SerialNumber
        });
    }

    /// <summary>
    /// List certificates expiring within the specified number of days
    /// </summary>
    [HttpGet("expiring")]
    public async Task<IActionResult> GetExpiringCertificates(
        [FromQuery] int days = 30,
        CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(User, ct);
        if (!access.CanManageAny)
            return Forbidden();

        if (days < 1 || days > 3650)
        {
            return BadRequest(new { error = "Days must be between 1 and 3650" });
        }

        // null = every tenant (SuperAdmin / platform admin key); otherwise only the tenants the caller manages.
        var certificates = await _caService.GetExpiringCertificatesAsync(days, access.ManageableTenants, ct);

        return Ok(certificates.Select(c => new
        {
            certificateId = c.Id,
            deviceId = c.DeviceId,
            deviceName = c.Device?.Name,
            serialNumber = c.SerialNumber,
            thumbprint = c.Thumbprint,
            subjectName = c.SubjectName,
            notBefore = c.NotBefore,
            notAfter = c.NotAfter,
            daysUntilExpiry = (int)(c.NotAfter - DateTime.UtcNow).TotalDays,
            status = c.Status
        }));
    }

    /// <summary>
    /// Renew a certificate (issues new cert, revokes old one)
    /// </summary>
    [HttpPost("renew/{certificateId:guid}")]
    public async Task<IActionResult> RenewCertificate(
        Guid certificateId,
        CancellationToken ct)
    {
        var access = await _access.ResolveAsync(User, ct);
        if (!access.CanManageAny)
            return Forbidden();

        var certificateTenantId = await _caService.GetCertificateTenantIdAsync(certificateId, ct);
        if (certificateTenantId is not { } tenantId || !access.CanManage(tenantId))
        {
            return NotFound(new { error = "Certificate not found" });
        }

        var userId = GetCurrentUserId();

        try
        {
            var (newCertificate, privateKeyPem) = await _caService.RenewCertificateAsync(
                certificateId, userId, ct);

            return Ok(new
            {
                certificateId = newCertificate.Id,
                certificatePem = newCertificate.CertificatePem,
                privateKeyPem = privateKeyPem, // ONLY returned once
                thumbprint = newCertificate.Thumbprint,
                serialNumber = newCertificate.SerialNumber,
                subjectName = newCertificate.SubjectName,
                issuerName = newCertificate.IssuerName,
                notBefore = newCertificate.NotBefore,
                notAfter = newCertificate.NotAfter,
                status = newCertificate.Status,
                renewedFromCertificateId = certificateId
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Download the Certificate Revocation List (CRL) in PEM format
    /// </summary>
    [HttpGet("crl")]
    [AllowAnonymous] // CRL should be publicly accessible for certificate validation
    public async Task<IActionResult> GetCrl(CancellationToken ct)
    {
        var crl = await _caService.GetCrlPemAsync(ct);

        return Content(crl, "application/x-pem-file");
    }

    // ─── Private helpers ──────────────────────────────────────────────

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;

        if (Guid.TryParse(userIdClaim, out var userId))
            return userId;

        return null;
    }
}

// ─── Request DTOs ──────────────────────────────────────────────────────

public class IssueCertificateRequestDto
{
    public Guid DeviceId { get; set; }
    public string CommonName { get; set; } = string.Empty;
    public string? Organization { get; set; }
    public string? OrganizationalUnit { get; set; }
    public int? ValidityDays { get; set; }
    public int? KeySizeBits { get; set; }
}

public class RevokeCertificateRequestDto
{
    public string Reason { get; set; } = string.Empty;
}

public class ValidateCertificateRequestDto
{
    public string CertificatePem { get; set; } = string.Empty;
}
