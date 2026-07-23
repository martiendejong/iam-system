using System.Security.Claims;
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

    public CertificateAuthorityController(ICertificateAuthorityService caService)
    {
        _caService = caService;
    }

    /// <summary>
    /// Get CA information including certificate statistics
    /// </summary>
    [HttpGet("info")]
    public async Task<IActionResult> GetCaInfo(CancellationToken ct)
    {
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
        if (string.IsNullOrWhiteSpace(request.CommonName))
        {
            return BadRequest(new { error = "CommonName is required" });
        }

        if (request.DeviceId == Guid.Empty)
        {
            return BadRequest(new { error = "DeviceId is required" });
        }

        var userId = GetCurrentUserId();

        var certRequest = new CertificateRequest
        {
            CommonName = request.CommonName,
            Organization = request.Organization,
            OrganizationalUnit = request.OrganizationalUnit,
            ValidityDays = request.ValidityDays ?? 365,
            KeySizeInBits = request.KeySizeBits ?? 2048
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
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest(new { error = "Reason is required" });
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
        if (days < 1 || days > 3650)
        {
            return BadRequest(new { error = "Days must be between 1 and 3650" });
        }

        var certificates = await _caService.GetExpiringCertificatesAsync(days, ct);

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
