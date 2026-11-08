using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IAM.Infrastructure.Services;

/// <summary>
/// Task 5852: see <see cref="IAccountGuessingGuard"/>. Configuration (all under RateLimiting):
/// AccountGuessDistinctSources (default 4, 0 disables), AccountGuessWindowMinutes (default 30),
/// AccountGuessAlertMaxPerHour (default 20, a global cap so invented e-mails cannot flood alerts),
/// AccountGuessAlertEmail (optional alert recipient), AccountGuessPepper (optional key; falls back
/// to the JWT signing key).
/// </summary>
public sealed class AccountGuessingGuard : IAccountGuessingGuard
{
    internal const int DefaultDistinctSources = 4;
    internal const int DefaultWindowMinutes = 30;
    internal const int DefaultAlertMaxPerHour = 20;
    internal const string RuleName = "Distributed Password Guessing (per account)";

    private static long _lastPruneTicks;

    private readonly IAMDbContext _context;
    private readonly ISecurityAlertService? _alerts;
    private readonly ILogger<AccountGuessingGuard> _logger;
    private readonly TimeProvider _time;
    private readonly int _distinctSources;
    private readonly int _windowMinutes;
    private readonly int _alertMaxPerHour;
    private readonly string? _alertEmail;
    private readonly byte[] _pepper;

    public AccountGuessingGuard(
        IAMDbContext context,
        IConfiguration configuration,
        ILogger<AccountGuessingGuard> logger,
        ISecurityAlertService? alerts = null,
        TimeProvider? timeProvider = null)
    {
        _context = context;
        _alerts = alerts;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _distinctSources = configuration.GetValue("RateLimiting:AccountGuessDistinctSources", DefaultDistinctSources);
        _windowMinutes = Math.Max(1, configuration.GetValue("RateLimiting:AccountGuessWindowMinutes", DefaultWindowMinutes));
        _alertMaxPerHour = Math.Max(0, configuration.GetValue("RateLimiting:AccountGuessAlertMaxPerHour", DefaultAlertMaxPerHour));
        _alertEmail = configuration["RateLimiting:AccountGuessAlertEmail"];
        var pepper = configuration["RateLimiting:AccountGuessPepper"] ?? configuration["Jwt:SecretKey"] ?? "iam-account-guess-pepper";
        _pepper = Encoding.UTF8.GetBytes(pepper);
    }

    private bool Enabled => _distinctSources > 0;

    public async Task RecordFailureAsync(string? email, string? ipAddress, Guid? accountId, bool accountExists, CancellationToken ct = default)
    {
        if (!Enabled) return;

        var now = _time.GetUtcNow().UtcDateTime;
        var cutoff = now.AddMinutes(-_windowMinutes);
        var accountKey = Key("a:" + NormalizeEmail(email));
        var sourceKey = Key("s:" + LoginThrottle.NormalizeIp(ipAddress));

        var signal = await _context.LoginFailureSignals.FindAsync(new object[] { accountKey, sourceKey }, ct);
        if (signal == null)
        {
            _context.LoginFailureSignals.Add(new LoginFailureSignal { AccountKey = accountKey, SourceKey = sourceKey, LastFailureAt = now });
        }
        else
        {
            signal.LastFailureAt = now;
        }

        // Drop this account's rows that fell out of the window, so the table stays small.
        var stale = await _context.LoginFailureSignals
            .Where(s => s.AccountKey == accountKey && s.LastFailureAt <= cutoff && s.SourceKey != sourceKey)
            .ToListAsync(ct);
        _context.LoginFailureSignals.RemoveRange(stale);
        await _context.SaveChangesAsync(ct);

        await PruneOccasionallyAsync(now, ct);

        var distinct = await CountDistinctAsync(accountKey, cutoff, ct);
        if (distinct >= _distinctSources && accountExists)
        {
            await TryRaiseAlertAsync(accountKey, accountId, distinct, now, ct);
        }
    }

    public async Task<bool> IsStepUpRequiredAsync(string? email, CancellationToken ct = default)
    {
        if (!Enabled) return false;
        var cutoff = _time.GetUtcNow().UtcDateTime.AddMinutes(-_windowMinutes);
        var distinct = await CountDistinctAsync(Key("a:" + NormalizeEmail(email)), cutoff, ct);
        return distinct >= _distinctSources;
    }

    private Task<int> CountDistinctAsync(string accountKey, DateTime cutoff, CancellationToken ct) =>
        _context.LoginFailureSignals.CountAsync(s => s.AccountKey == accountKey && s.LastFailureAt > cutoff, ct);

    private async Task TryRaiseAlertAsync(string accountKey, Guid? accountId, int distinct, DateTime now, CancellationToken ct)
    {
        try
        {
            var windowStart = now.AddMinutes(-_windowMinutes);
            var mark = await _context.AccountGuessingAlertMarks.FindAsync(new object[] { accountKey }, ct);
            if (mark != null && mark.AlertedAt > windowStart) return;

            // Global cap: marks are one per account, so counting recent ones bounds total alerts.
            var hourAgo = now.AddHours(-1);
            var recent = await _context.AccountGuessingAlertMarks.CountAsync(m => m.AlertedAt > hourAgo, ct);
            if (recent >= _alertMaxPerHour) return;

            if (mark == null)
                _context.AccountGuessingAlertMarks.Add(new AccountGuessingAlertMark { AccountKey = accountKey, AlertedAt = now });
            else
                mark.AlertedAt = now;
            await _context.SaveChangesAsync(ct); // mark first, so a failing alert is not retried on every attempt

            if (_alerts == null) return;

            var rule = await EnsureRuleAsync(ct);
            // Counts only: no typed password, no addresses, no e-mail.
            var details = JsonSerializer.Serialize(new
            {
                accountId,
                distinctSources = distinct,
                windowMinutes = _windowMinutes,
                action = "email step-up required for sign-in"
            });
            await _alerts.FireAlertAsync(rule.Id,
                $"Distributed password guessing: {distinct} sources failed against one account in {_windowMinutes} min",
                details, null, ct);
        }
        catch (Exception ex)
        {
            // The brake itself does not depend on the alert.
            _logger.LogError(ex, "Raising the account-guessing alert failed");
        }
    }

    private async Task<AlertRule> EnsureRuleAsync(CancellationToken ct)
    {
        var rule = await _context.AlertRules.FirstOrDefaultAsync(r => r.Name == RuleName, ct);
        if (rule != null) return rule;

        var channels = string.IsNullOrWhiteSpace(_alertEmail)
            ? "[]"
            : JsonSerializer.Serialize(new[] { new { type = "email", target = _alertEmail } });
        rule = new AlertRule
        {
            Name = RuleName,
            Condition = "{\"type\":\"account_password_guessing\"}",
            Severity = AlertSeverity.Warning,
            Channels = channels,
            // Once-per-account-per-window is enforced by AccountGuessingAlertMark; a rule-wide
            // cooldown would suppress other accounts' alerts.
            CooldownMinutes = 0,
            IsActive = true
        };
        _context.AlertRules.Add(rule);
        await _context.SaveChangesAsync(ct);
        return rule;
    }

    /// <summary>Removes rows of every account that fell out of the window, at most every 10 minutes per process.</summary>
    private async Task PruneOccasionallyAsync(DateTime now, CancellationToken ct)
    {
        var last = Interlocked.Read(ref _lastPruneTicks);
        if (now.Ticks - last < TimeSpan.FromMinutes(10).Ticks) return;
        if (Interlocked.CompareExchange(ref _lastPruneTicks, now.Ticks, last) != last) return;

        var cutoff = now.AddMinutes(-_windowMinutes);
        var old = await _context.LoginFailureSignals.Where(s => s.LastFailureAt <= cutoff).Take(1000).ToListAsync(ct);
        _context.LoginFailureSignals.RemoveRange(old);
        var markCutoff = now.AddHours(-2).AddMinutes(-_windowMinutes);
        var oldMarks = await _context.AccountGuessingAlertMarks.Where(m => m.AlertedAt <= markCutoff).Take(1000).ToListAsync(ct);
        _context.AccountGuessingAlertMarks.RemoveRange(oldMarks);
        await _context.SaveChangesAsync(ct);
    }

    private static string NormalizeEmail(string? email) =>
        (email ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();

    private string Key(string value) =>
        Convert.ToHexString(HMACSHA256.HashData(_pepper, Encoding.UTF8.GetBytes(value)));
}
