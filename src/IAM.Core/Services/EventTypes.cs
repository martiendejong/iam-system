namespace IAM.Core.Services;

public static class IamEventTypes
{
    // User events
    public const string UserCreated = "user.created";
    public const string UserUpdated = "user.updated";
    public const string UserDeleted = "user.deleted";
    public const string UserLoginSuccess = "user.login.success";
    public const string UserLoginFailed = "user.login.failed";
    public const string UserPasswordChanged = "user.password.changed";
    public const string UserMfaEnabled = "user.mfa.enabled";
    public const string UserMfaDisabled = "user.mfa.disabled";

    // Role/Permission events
    public const string RoleAssigned = "role.assigned";
    public const string RoleRevoked = "role.revoked";
    public const string PolicyCreated = "policy.created";
    public const string PolicyUpdated = "policy.updated";

    // Device events
    public const string DeviceRegistered = "device.registered";
    public const string DeviceAuthenticated = "device.authenticated";
    public const string DeviceDeactivated = "device.deactivated";
    public const string DeviceOffline = "device.offline";

    // Certificate events
    public const string CertificateIssued = "certificate.issued";
    public const string CertificateRevoked = "certificate.revoked";
    public const string CertificateExpiring = "certificate.expiring";

    // Tenant events
    public const string TenantCreated = "tenant.created";
    public const string TenantUpdated = "tenant.updated";

    // Security events
    public const string SecurityAlert = "security.alert";
    public const string EmergencyOverrideActivated = "security.emergency_override.activated";
    public const string EmergencyOverrideDeactivated = "security.emergency_override.deactivated";

    public static readonly string[] All = new[]
    {
        UserCreated, UserUpdated, UserDeleted, UserLoginSuccess, UserLoginFailed,
        UserPasswordChanged, UserMfaEnabled, UserMfaDisabled,
        RoleAssigned, RoleRevoked, PolicyCreated, PolicyUpdated,
        DeviceRegistered, DeviceAuthenticated, DeviceDeactivated, DeviceOffline,
        CertificateIssued, CertificateRevoked, CertificateExpiring,
        TenantCreated, TenantUpdated,
        SecurityAlert, EmergencyOverrideActivated, EmergencyOverrideDeactivated
    };
}
