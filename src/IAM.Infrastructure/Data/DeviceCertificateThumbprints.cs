using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace IAM.Infrastructure.Data;

/// <summary>
/// Task 5159: certificates issued before this task were stored with the framework thumbprint, which is SHA-1 (40 hex
/// characters), while certificate login and the entity documentation use SHA-256 (64). A device certificate issued by
/// the CA could therefore never match at login. This recomputes the SHA-256 thumbprint of every row that does not have
/// one yet from the certificate PEM stored in the same row, so a second run finds nothing left to change.
/// </summary>
public static class DeviceCertificateThumbprints
{
    /// <summary>Stages the new thumbprints; the caller saves. Returns how many rows changed and how many PEMs could not be read.</summary>
    public static async Task<(int Updated, int Unreadable)> BackfillSha256Async(this IAMDbContext context, CancellationToken ct = default)
    {
        var legacy = await context.DeviceCertificates
            .Where(c => c.Thumbprint.Length != CertificateThumbprint.Sha256HexLength)
            .ToListAsync(ct);

        var updated = 0;
        var unreadable = 0;
        foreach (var certificate in legacy)
        {
            var sha256 = CertificateThumbprint.Sha256OfPem(certificate.CertificatePem);
            if (sha256 is null)
            {
                unreadable++;
                continue;
            }

            certificate.Thumbprint = sha256;
            updated++;
        }

        return (updated, unreadable);
    }
}
