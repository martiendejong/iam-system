using System.Security.Cryptography;
using System.Text;

namespace IAM.Core.Mobile;

public enum LinkedAppRequestState { Pending, Approved, Denied, Consumed, Expired }

public enum LinkedAppConsumeResult { Ok, UnknownRequest, NotApproved, Expired, AlreadyConsumed, PkceMismatch }

/// <summary>
/// Task 5623 (IAM Mobile 01): request/consume logic of the linked-app sign-in PROOF. The browser creates a login request
/// (holding only the PKCE challenge), the verified app link opens the app, the user confirms with biometrics/PIN in the app,
/// and the ORIGINAL browser window consumes the approval with its PKCE verifier. No own cryptography beyond SHA-256 (RFC 7636).
/// Single use, short lived, bound to the PKCE challenge. In-memory only: proof code, not wired into production login.
/// </summary>
public sealed class LinkedAppSignInProof
{
    private sealed class Entry
    {
        public required string Challenge;
        public required DateTimeOffset ExpiresAt;
        public LinkedAppRequestState State = LinkedAppRequestState.Pending;
        public string? ApprovedUser;
    }

    private readonly Dictionary<string, Entry> _requests = new();
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now;
    public TimeSpan Lifetime { get; }

    public LinkedAppSignInProof(TimeSpan? lifetime = null, Func<DateTimeOffset>? now = null)
    {
        Lifetime = lifetime ?? TimeSpan.FromMinutes(2);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Creates a pending request and returns its opaque 256-bit id (goes in the app link).</summary>
    public string Create(string pkceChallengeS256)
    {
        if (string.IsNullOrWhiteSpace(pkceChallengeS256)) throw new ArgumentException("PKCE challenge required", nameof(pkceChallengeS256));
        var id = Base64Url(RandomNumberGenerator.GetBytes(32));
        lock (_gate) _requests[id] = new Entry { Challenge = pkceChallengeS256, ExpiresAt = _now() + Lifetime };
        return id;
    }

    /// <summary>The app confirms (after the OS biometric/PIN prompt). Only a pending, unexpired request can be approved.</summary>
    public bool Approve(string requestId, string userId)
    {
        lock (_gate)
        {
            if (!_requests.TryGetValue(requestId, out var e) || Refresh(e) != LinkedAppRequestState.Pending) return false;
            e.State = LinkedAppRequestState.Approved;
            e.ApprovedUser = userId;
            return true;
        }
    }

    public bool Deny(string requestId)
    {
        lock (_gate)
        {
            if (!_requests.TryGetValue(requestId, out var e) || Refresh(e) != LinkedAppRequestState.Pending) return false;
            e.State = LinkedAppRequestState.Denied;
            return true;
        }
    }

    public LinkedAppRequestState? GetState(string requestId)
    {
        lock (_gate) return _requests.TryGetValue(requestId, out var e) ? Refresh(e) : null;
    }

    /// <summary>The original browser consumes the approval with its PKCE verifier. A wrong verifier does not burn the request.</summary>
    public LinkedAppConsumeResult Consume(string requestId, string codeVerifier, out string? userId)
    {
        userId = null;
        lock (_gate)
        {
            if (!_requests.TryGetValue(requestId, out var e)) return LinkedAppConsumeResult.UnknownRequest;
            switch (Refresh(e))
            {
                case LinkedAppRequestState.Expired: return LinkedAppConsumeResult.Expired;
                case LinkedAppRequestState.Consumed: return LinkedAppConsumeResult.AlreadyConsumed;
                case LinkedAppRequestState.Pending:
                case LinkedAppRequestState.Denied: return LinkedAppConsumeResult.NotApproved;
            }
            var computed = ChallengeFor(codeVerifier ?? "");
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(e.Challenge)))
                return LinkedAppConsumeResult.PkceMismatch;
            e.State = LinkedAppRequestState.Consumed;
            userId = e.ApprovedUser;
            return LinkedAppConsumeResult.Ok;
        }
    }

    private LinkedAppRequestState Refresh(Entry e)
    {
        if ((e.State == LinkedAppRequestState.Pending || e.State == LinkedAppRequestState.Approved) && _now() >= e.ExpiresAt)
            e.State = LinkedAppRequestState.Expired;
        return e.State;
    }

    public static string ChallengeFor(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
