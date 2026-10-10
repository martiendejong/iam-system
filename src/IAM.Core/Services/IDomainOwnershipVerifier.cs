namespace IAM.Core.Services;

/// <summary>
/// Reads the TXT records of a DNS name. A separate seam so ownership checks can be tested without the network.
/// </summary>
public interface ITxtRecordResolver
{
    /// <summary>The TXT strings published at <paramref name="name"/>; empty when there are none or the lookup failed.</summary>
    Task<IReadOnlyList<string>> LookupAsync(string name, CancellationToken ct = default);
}

/// <summary>
/// Proof that a tenant owns the custom domain it claims for branding (task 5150): the tenant publishes the TXT record
/// <see cref="RecordName"/> = <see cref="RecordValue"/>. The value is derived from a server secret, the tenant id and the
/// domain, so nobody can guess or copy another tenant's record, and nothing needs to be stored.
/// </summary>
public interface IDomainOwnershipVerifier
{
    string RecordName(string domain);

    string RecordValue(Guid tenantId, string domain);

    /// <summary>True when the TXT record is currently published (result cached for a few minutes).</summary>
    Task<bool> IsVerifiedAsync(Guid tenantId, string domain, CancellationToken ct = default);
}

/// <summary>Thrown when branding input is not acceptable; <see cref="Errors"/> are shown to the caller as a 400.</summary>
public class BrandingValidationException : Exception
{
    public BrandingValidationException(IReadOnlyList<string> errors) : base(errors.Count > 0 ? errors[0] : "Invalid branding")
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}
