namespace IAM.Core.Services;

public static class CacheKeys
{
    public const string PolicyEvaluation = "policy:eval:";
    public const string DeviceClaims = "device:claims:";
    public const string DevicePermissions = "device:perms:";
    public const string UserPermissions = "user:perms:";
    public const string TenantHierarchy = "tenant:hierarchy:";
    public const string GroupMembers = "group:members:";
    public const string ApiKeyValidation = "apikey:";
    public const string RateLimit = "ratelimit:";
    public const string SessionValidation = "session:";

    public static string ForPolicyEval(Guid userId, Guid tenantId, string resource)
        => $"{PolicyEvaluation}{userId}:{tenantId}:{resource}";

    public static string ForDeviceClaims(string deviceId)
        => $"{DeviceClaims}{deviceId}";

    public static string ForDevicePermissions(string deviceId)
        => $"{DevicePermissions}{deviceId}";

    public static string ForUserPermissions(Guid userId, Guid tenantId)
        => $"{UserPermissions}{userId}:{tenantId}";

    public static string ForTenantHierarchy(Guid tenantId)
        => $"{TenantHierarchy}{tenantId}";

    public static string ForRateLimit(string identifier)
        => $"{RateLimit}{identifier}";
}
