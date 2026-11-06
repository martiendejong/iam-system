using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IAM.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

/// <summary>
/// DNS-over-HTTPS TXT lookup (JSON API, fixed resolver host). IAM has no DNS library, and the system resolver cannot
/// return TXT records; a failed lookup is simply "no records", which keeps a custom domain unverified.
/// </summary>
public class DohTxtRecordResolver : ITxtRecordResolver
{
    private const string Endpoint = "https://cloudflare-dns.com/dns-query";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DohTxtRecordResolver> _logger;

    public DohTxtRecordResolver(IHttpClientFactory httpClientFactory, ILogger<DohTxtRecordResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> LookupAsync(string name, CancellationToken ct = default)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("DomainVerification");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Endpoint}?name={Uri.EscapeDataString(name)}&type=TXT");
            request.Headers.Accept.ParseAdd("application/dns-json");
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return Array.Empty<string>();

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!json.RootElement.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            var records = new List<string>();
            foreach (var answer in answers.EnumerateArray())
            {
                if (answer.TryGetProperty("type", out var type) && type.GetInt32() == 16
                    && answer.TryGetProperty("data", out var data) && data.GetString() is { } text)
                    records.Add(text.Trim('"'));
            }
            return records;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TXT lookup for {Name} failed", name);
            return Array.Empty<string>();
        }
    }
}

public class DomainOwnershipVerifier : IDomainOwnershipVerifier
{
    public const string RecordPrefix = "_iam-verify.";
    public const string ValuePrefix = "iam-verify=";

    private static readonly TimeSpan PositiveTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(1);

    private readonly ITxtRecordResolver _resolver;
    private readonly byte[]? _key;
    private readonly ConcurrentDictionary<string, (bool Verified, DateTime At)> _cache = new(StringComparer.Ordinal);

    public DomainOwnershipVerifier(ITxtRecordResolver resolver, IConfiguration configuration)
    {
        _resolver = resolver;
        var secret = configuration["Branding:DomainVerificationKey"] ?? configuration["Jwt:SecretKey"];
        _key = string.IsNullOrEmpty(secret) ? null : Encoding.UTF8.GetBytes(secret);
    }

    public string RecordName(string domain) => RecordPrefix + domain.Trim().TrimEnd('.').ToLowerInvariant();

    public string RecordValue(Guid tenantId, string domain)
    {
        if (_key == null)
            return string.Empty;

        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{tenantId:N}|{domain.Trim().TrimEnd('.').ToLowerInvariant()}"));
        return ValuePrefix + Convert.ToHexString(mac)[..32].ToLowerInvariant();
    }

    public async Task<bool> IsVerifiedAsync(Guid tenantId, string domain, CancellationToken ct = default)
    {
        var expected = RecordValue(tenantId, domain);
        if (expected.Length == 0)
            return false;

        var cacheKey = $"{tenantId:N}|{domain.ToLowerInvariant()}";
        if (_cache.TryGetValue(cacheKey, out var cached)
            && DateTime.UtcNow - cached.At < (cached.Verified ? PositiveTtl : NegativeTtl))
            return cached.Verified;

        var records = await _resolver.LookupAsync(RecordName(domain), ct);
        var verified = records.Any(r => string.Equals(r.Trim(), expected, StringComparison.Ordinal));
        _cache[cacheKey] = (verified, DateTime.UtcNow);
        return verified;
    }
}
