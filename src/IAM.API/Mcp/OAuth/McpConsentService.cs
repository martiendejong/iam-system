using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Primitives;

namespace IAM.API.Mcp.OAuth;

/// <summary>
/// Explicit consent for dynamically registered MCP clients. The IAM authorization endpoint auto-approves
/// first-party apps, but an MCP connector is a third party the user has never configured, so the user sees a plain
/// statement of what the connector can read and must press Allow. The Allow button carries a time-limited,
/// data-protection signed token bound to (user, client, redirect_uri, code_challenge, state): a page on another
/// site cannot forge it (it cannot read this page), and it cannot be replayed for another client, user or request.
/// </summary>
public sealed class McpConsentService
{
    public const string TokenField = "mcp_consent";
    public const string DecisionField = "mcp_decision";
    public const string AllowValue = "allow";
    public const string DenyValue = "deny";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ITimeLimitedDataProtector _protector;

    public McpConsentService(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("IAM.Mcp.Consent.v1").ToTimeLimitedDataProtector();
    }

    /// <summary>Binds the consent to the exact authorization request being approved.</summary>
    public static string Fingerprint(string? redirectUri, string? codeChallenge, string? state, string? scope)
    {
        var raw = string.Join("\n", redirectUri ?? "", codeChallenge ?? "", state ?? "", scope ?? "");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    public string CreateToken(Guid userId, string clientId, string fingerprint) =>
        _protector.Protect($"{userId:N}|{clientId}|{fingerprint}", Lifetime);

    public bool IsValid(string? token, Guid userId, string clientId, string fingerprint)
    {
        if (string.IsNullOrEmpty(token)) return false;
        try
        {
            var payload = _protector.Unprotect(token);
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(payload),
                Encoding.UTF8.GetBytes($"{userId:N}|{clientId}|{fingerprint}"));
        }
        catch (CryptographicException)
        {
            return false; // expired, tampered or signed for another purpose
        }
    }

    /// <summary>The consent page. Everything user- or client-controlled is HTML-encoded.</summary>
    public static string RenderPage(
        string clientName,
        string redirectHost,
        string userEmail,
        string consentToken,
        IEnumerable<KeyValuePair<string, StringValues>> requestParameters)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

        var hidden = new StringBuilder();
        foreach (var (key, values) in requestParameters)
        {
            if (key is TokenField or DecisionField) continue;
            foreach (var value in values)
                hidden.Append($"<input type=\"hidden\" name=\"{E(key)}\" value=\"{E(value)}\">");
        }
        hidden.Append($"<input type=\"hidden\" name=\"{TokenField}\" value=\"{E(consentToken)}\">");

        return
            "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            "<title>Connect an AI assistant to Jengo IAM</title>" +
            "<style>body{font-family:'Segoe UI',sans-serif;background:#f1f5f9;display:flex;align-items:center;justify-content:center;min-height:100vh;margin:0;padding:1rem;box-sizing:border-box}" +
            ".card{background:#fff;padding:2rem 2.25rem;border-radius:12px;box-shadow:0 4px 24px rgba(0,0,0,.08);max-width:560px;width:100%}" +
            "h2{margin:0 0 .5rem}ul{padding-left:1.2rem}li{margin:.35rem 0}.muted{color:#475569;font-size:.9rem}" +
            ".btns{display:flex;gap:.75rem;margin-top:1.5rem}button{font:inherit;padding:.65rem 1.4rem;border-radius:8px;border:1px solid #cbd5e1;background:#fff;cursor:pointer}" +
            "button.allow{background:#0f172a;color:#fff;border-color:#0f172a}</style></head><body><div class=\"card\">" +
            $"<h2>Connect <strong>{E(clientName)}</strong> to Jengo IAM?</h2>" +
            $"<p class=\"muted\">You are signed in as <strong>{E(userEmail)}</strong>. The application name is chosen by the application itself and is not verified. " +
            $"After you decide you are sent back to <strong>{E(redirectHost)}</strong>.</p>" +
            "<p><strong>If you allow this, the assistant can read (read-only):</strong></p><ul>" +
            "<li>who you are: your name, email address, account status, whether two-factor is on, and your roles;</li>" +
            "<li>your own sign-in sessions and trusted devices: when, from which IP address and device, never the session tokens;</li>" +
            "<li><strong>if you are an IAM administrator, also:</strong> the list of users (name, email, status, roles), the registered OAuth applications " +
            "(names and redirect URLs, never their secrets), the active sign-in sessions of all users, and recent audit and login events.</li></ul>" +
            "<p><strong>It cannot:</strong> create, change, delete or invite anyone, reset a password, revoke a session or change a role. " +
            "It never receives passwords, password hashes, API keys, tokens or two-factor secrets, and the access it gets is not a login to any other Jengo application.</p>" +
            "<p class=\"muted\">Whatever the assistant reads is sent to the company behind it (for example OpenAI or Anthropic) as part of your conversation. " +
            "Access lasts until you disconnect the connector in the assistant or an IAM administrator removes the application; deactivating your IAM account stops it immediately. " +
            "Only allow this if you started the connection yourself.</p>" +
            "<form method=\"post\" action=\"/connect/authorize\">" + hidden +
            $"<div class=\"btns\"><button class=\"allow\" type=\"submit\" name=\"{DecisionField}\" value=\"{AllowValue}\">Allow read-only access</button>" +
            $"<button type=\"submit\" name=\"{DecisionField}\" value=\"{DenyValue}\">Cancel</button></div></form>" +
            "</div></body></html>";
    }
}
