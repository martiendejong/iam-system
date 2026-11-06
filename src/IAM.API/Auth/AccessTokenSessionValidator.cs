using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace IAM.API.Auth;

/// <summary>
/// Task 5628: a JWT access token is only as good as the session behind it. Logout, password reset, admin
/// deactivation, "revoke all sessions" and refresh-token replay detection all revoke the refresh token, but an
/// already-issued access token used to stay valid until it expired. Tokens issued with a refresh token carry a
/// <c>refresh_token_id</c> claim; this validator checks it against server state on every request:
/// the refresh token must exist and be unrevoked, and its user must be active and not locked out.
/// Tokens without the claim (client-credentials, service accounts, API keys) are not bound to a session and
/// are left alone. The verdict is cached for <see cref="CacheWindow"/>, so revocation takes effect within
/// that window and a burst of requests costs one lookup. A failing lookup rejects the request (fail closed).
/// </summary>
public class AccessTokenSessionValidator
{
    public const string RefreshTokenIdClaim = "refresh_token_id";
    public static readonly TimeSpan DefaultCacheWindow = TimeSpan.FromSeconds(30);

    private readonly IAMDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AccessTokenSessionValidator> _logger;

    public AccessTokenSessionValidator(IAMDbContext db, IMemoryCache cache, IConfiguration configuration,
        ILogger<AccessTokenSessionValidator> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
        CacheWindow = int.TryParse(configuration["Jwt:SessionCheckCacheSeconds"], out var s) && s >= 0
            ? TimeSpan.FromSeconds(s)
            : DefaultCacheWindow;
    }

    public TimeSpan CacheWindow { get; }

    /// <summary>True when the session behind the token is still usable. Throws if the lookup itself fails.</summary>
    public async Task<bool> IsSessionActiveAsync(Guid refreshTokenId, CancellationToken ct = default)
    {
        if (CacheWindow <= TimeSpan.Zero)
        {
            return await LookupAsync(refreshTokenId, ct);
        }

        var key = $"iam:session-active:{refreshTokenId}";
        if (_cache.TryGetValue(key, out bool cached))
        {
            return cached;
        }

        var active = await LookupAsync(refreshTokenId, ct);
        _cache.Set(key, active, CacheWindow);
        return active;
    }

    /// <summary>The database lookup. Virtual so tests can count how often the cache lets a request through.</summary>
    protected virtual async Task<bool> LookupAsync(Guid refreshTokenId, CancellationToken ct)
    {
        var row = await _db.RefreshTokens.AsNoTracking()
            .Where(rt => rt.Id == refreshTokenId)
            .Select(rt => new { rt.RevokedAt, rt.User.IsActive, rt.User.IsLockedOut, rt.User.LockoutEnd })
            .FirstOrDefaultAsync(ct);

        if (row == null || row.RevokedAt.HasValue || !row.IsActive)
        {
            return false;
        }

        return !(row.IsLockedOut && row.LockoutEnd > DateTime.UtcNow);
    }

    /// <summary>Hook for <c>JwtBearerEvents.OnTokenValidated</c>.</summary>
    public static async Task OnTokenValidatedAsync(TokenValidatedContext context)
    {
        var claim = context.Principal?.FindFirst(RefreshTokenIdClaim)?.Value;
        if (claim == null)
        {
            return; // not bound to a session
        }

        var validator = context.HttpContext.RequestServices.GetRequiredService<AccessTokenSessionValidator>();
        if (!Guid.TryParse(claim, out var refreshTokenId))
        {
            context.Fail("Access token carries a malformed refresh_token_id claim.");
            return;
        }

        try
        {
            if (!await validator.IsSessionActiveAsync(refreshTokenId, context.HttpContext.RequestAborted))
            {
                context.Fail("The session behind this access token was revoked or its user was deactivated.");
            }
        }
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            validator._logger.LogError(ex, "Session check for refresh token {RefreshTokenId} failed; rejecting the request", refreshTokenId);
            context.Fail("The session behind this access token could not be verified.");
        }
    }
}
