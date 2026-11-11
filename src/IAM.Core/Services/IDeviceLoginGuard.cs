namespace IAM.Core.Services;

/// <summary>
/// Task 5159: the state a device login needs to remember between requests: failed attempts per device (with a
/// temporary lock) and the nonces that were already used. Both are per device, never per caller address.
/// </summary>
public interface IDeviceLoginGuard
{
    /// <summary>True while the device is locked after repeated failures. A locked device cannot log in, whatever it sends.</summary>
    bool IsLockedOut(string deviceId);

    /// <summary>Counts one failed login for the device; enough of them inside the window lock it for a while.</summary>
    void RecordFailure(string deviceId);

    /// <summary>A successful login clears the failures of that device.</summary>
    void RecordSuccess(string deviceId);

    /// <summary>
    /// Marks the nonce as used for this device. Returns false when it was already used, which makes the request a
    /// replay. The check and the mark are one atomic step, so two simultaneous replays cannot both pass.
    /// </summary>
    bool TryUseNonce(string deviceId, string nonce);
}

/// <summary>
/// Task 5159: decides whether a certificate login comes from a trusted edge gateway. The certificate PEM is public,
/// so a bare PEM from anyone else must never turn into a device token.
/// </summary>
public interface IDeviceGatewayVerifier
{
    /// <summary>True when <paramref name="presentedKey"/> equals one of the configured gateway keys. Always false when none is configured.</summary>
    bool IsTrusted(string? presentedKey);
}
