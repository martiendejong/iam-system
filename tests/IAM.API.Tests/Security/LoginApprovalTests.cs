using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IAM.API.Tests.Security;

/// <summary>
/// Sign in on a new browser by approving on a signed-in device: the rules that keep it safe.
/// The browser that started a request is the only one that can finish it, the approver has to pick the number the new
/// browser shows, a request signs in at most once and only for the account it names, and starting is rate limited.
/// </summary>
public class LoginApprovalTests
{
    private sealed class CountingNotifier : ILoginApprovalNotifier
    {
        public int Calls { get; private set; }
        public Task NotifyAsync(Guid userId, LoginApproval approval, CancellationToken ct = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private static IAMDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new IAMDbContext(options);
    }

    private static (LoginApprovalService Service, IAMDbContext Context, CountingNotifier Notifier) Create()
    {
        var context = CreateContext();
        var notifier = new CountingNotifier();
        return (new LoginApprovalService(context, notifier, NullLogger<LoginApprovalService>.Instance), context, notifier);
    }

    private static async Task<User> AddUserAsync(IAMDbContext context, string name, bool active = true)
    {
        var user = new User
        {
            Email = $"{name}@example.com",
            PasswordHash = "x",
            FirstName = name,
            LastName = "Test",
            IsActive = active
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    private const string Chrome = "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 Chrome/130.0 Mobile Safari/537.36";

    [Fact]
    public async Task Start_with_a_known_email_names_the_user_and_sends_one_push()
    {
        var (service, context, notifier) = Create();
        var user = await AddUserAsync(context, "martien");

        var started = await service.StartAsync("Martien@Example.com", true, "10.0.0.1", Chrome);

        Assert.NotNull(started);
        var row = await context.LoginApprovals.SingleAsync();
        Assert.Equal(user.Id, row.UserId);
        Assert.Equal(1, notifier.Calls);
        Assert.NotEqual(started!.PollSecret, row.PollSecretHash); // only the hash is stored
        Assert.InRange(started.MatchNumber, 10, 99);
        Assert.Equal("Chrome on Android", row.RequestDevice);
    }

    [Fact]
    public async Task Start_with_an_unknown_or_inactive_email_looks_the_same_but_names_nobody_and_sends_nothing()
    {
        var (service, context, notifier) = Create();
        await AddUserAsync(context, "gone", active: false);

        var unknown = await service.StartAsync("nobody@example.com", true, "10.0.0.1", Chrome);
        var inactive = await service.StartAsync("gone@example.com", true, "10.0.0.2", Chrome);

        Assert.NotNull(unknown);
        Assert.NotNull(inactive);
        Assert.All(await context.LoginApprovals.ToListAsync(), a => Assert.Null(a.UserId));
        Assert.Equal(0, notifier.Calls);
    }

    [Fact]
    public async Task Right_number_approves_and_the_starting_browser_signs_in_exactly_once()
    {
        var (service, context, _) = Create();
        var user = await AddUserAsync(context, "martien");
        var started = (await service.StartAsync(user.Email, true, "10.0.0.1", Chrome))!;

        Assert.Equal(LoginApprovalStatus.Pending, await service.GetStatusAsync(started.Id, started.PollSecret));
        Assert.Equal(ApproveOutcome.Approved, await service.ApproveAsync(started.Id, user.Id, Guid.NewGuid(), started.MatchNumber));
        Assert.Equal(LoginApprovalStatus.Approved, await service.GetStatusAsync(started.Id, started.PollSecret));

        var first = await service.ConsumeAsync(started.Id, started.PollSecret);
        Assert.True(first.Success);
        Assert.Equal(user.Id, first.User!.Id);
        Assert.True(first.RememberMe);

        var second = await service.ConsumeAsync(started.Id, started.PollSecret);
        Assert.False(second.Success);
    }

    [Fact]
    public async Task Consume_before_approval_or_with_the_wrong_secret_fails()
    {
        var (service, context, _) = Create();
        var user = await AddUserAsync(context, "martien");
        var started = (await service.StartAsync(user.Email, true, "10.0.0.1", Chrome))!;

        Assert.False((await service.ConsumeAsync(started.Id, started.PollSecret)).Success);

        await service.ApproveAsync(started.Id, user.Id, null, started.MatchNumber);

        Assert.False((await service.ConsumeAsync(started.Id, "not-the-secret")).Success);
        Assert.Null(await service.GetStatusAsync(started.Id, "not-the-secret"));
        Assert.True((await service.ConsumeAsync(started.Id, started.PollSecret)).Success);
    }

    [Fact]
    public async Task Three_wrong_numbers_deny_the_request_and_even_the_right_number_no_longer_works()
    {
        var (service, context, _) = Create();
        var user = await AddUserAsync(context, "martien");
        var started = (await service.StartAsync(user.Email, true, "10.0.0.1", Chrome))!;
        var wrong = started.MatchNumber == 10 ? 11 : 10;

        Assert.Equal(ApproveOutcome.WrongNumber, await service.ApproveAsync(started.Id, user.Id, null, wrong));
        Assert.Equal(ApproveOutcome.WrongNumber, await service.ApproveAsync(started.Id, user.Id, null, wrong));
        Assert.Equal(ApproveOutcome.TooManyWrong, await service.ApproveAsync(started.Id, user.Id, null, wrong));

        Assert.Equal(ApproveOutcome.NotPending, await service.ApproveAsync(started.Id, user.Id, null, started.MatchNumber));
        Assert.Equal(LoginApprovalStatus.Denied, await service.GetStatusAsync(started.Id, started.PollSecret));
    }

    [Fact]
    public async Task A_request_that_names_a_user_cannot_be_seen_or_approved_by_someone_else()
    {
        var (service, context, _) = Create();
        var owner = await AddUserAsync(context, "owner");
        var other = await AddUserAsync(context, "other");
        var started = (await service.StartAsync(owner.Email, true, "10.0.0.1", Chrome))!;

        Assert.Null(await service.GetForApproverAsync(started.Id, other.Id));
        Assert.Equal(ApproveOutcome.WrongUser, await service.ApproveAsync(started.Id, other.Id, null, started.MatchNumber));
        Assert.False(await service.DenyAsync(started.Id, other.Id));
        Assert.Empty(await service.ListPendingAsync(other.Id));
        Assert.Single(await service.ListPendingAsync(owner.Id));
        Assert.Equal(LoginApprovalStatus.Pending, await service.GetStatusAsync(started.Id, started.PollSecret));
    }

    [Fact]
    public async Task A_qr_request_without_an_email_signs_in_as_whoever_approves_it()
    {
        var (service, context, notifier) = Create();
        var user = await AddUserAsync(context, "martien");
        var started = (await service.StartAsync(null, false, "10.0.0.1", Chrome))!;

        Assert.Equal(0, notifier.Calls);
        Assert.NotNull(await service.GetForApproverAsync(started.Id, user.Id));
        Assert.Equal(ApproveOutcome.Approved, await service.ApproveAsync(started.Id, user.Id, null, started.MatchNumber));

        var consumed = await service.ConsumeAsync(started.Id, started.PollSecret);
        Assert.True(consumed.Success);
        Assert.Equal(user.Id, consumed.User!.Id);
        Assert.False(consumed.RememberMe);
    }

    [Fact]
    public async Task An_expired_request_cannot_be_approved_or_used()
    {
        var (service, context, _) = Create();
        var user = await AddUserAsync(context, "martien");
        var started = (await service.StartAsync(user.Email, true, "10.0.0.1", Chrome))!;
        var row = await context.LoginApprovals.SingleAsync();
        row.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await context.SaveChangesAsync();

        Assert.Equal(LoginApprovalStatus.Expired, await service.GetStatusAsync(started.Id, started.PollSecret));
        Assert.Equal(ApproveOutcome.NotPending, await service.ApproveAsync(started.Id, user.Id, null, started.MatchNumber));
        Assert.Null(await service.GetForApproverAsync(started.Id, user.Id));
        Assert.Empty(await service.ListPendingAsync(user.Id));
    }

    [Fact]
    public async Task An_approval_that_is_never_picked_up_expires_too()
    {
        var (service, context, _) = Create();
        var user = await AddUserAsync(context, "martien");
        var started = (await service.StartAsync(user.Email, true, "10.0.0.1", Chrome))!;
        await service.ApproveAsync(started.Id, user.Id, null, started.MatchNumber);
        var row = await context.LoginApprovals.SingleAsync();
        row.ExpiresAt = DateTime.UtcNow.AddMinutes(-3);
        await context.SaveChangesAsync();

        Assert.False((await service.ConsumeAsync(started.Id, started.PollSecret)).Success);
    }

    [Fact]
    public async Task Denying_stops_the_request()
    {
        var (service, context, _) = Create();
        var user = await AddUserAsync(context, "martien");
        var started = (await service.StartAsync(user.Email, true, "10.0.0.1", Chrome))!;

        Assert.True(await service.DenyAsync(started.Id, user.Id));
        Assert.Equal(LoginApprovalStatus.Denied, await service.GetStatusAsync(started.Id, started.PollSecret));
        Assert.Equal(ApproveOutcome.NotPending, await service.ApproveAsync(started.Id, user.Id, null, started.MatchNumber));
        Assert.False((await service.ConsumeAsync(started.Id, started.PollSecret)).Success);
    }

    [Fact]
    public async Task Starting_is_rate_limited_per_ip()
    {
        var (service, _, _) = Create();

        for (var i = 0; i < LoginApprovalService.MaxStartsPerIpPer10Minutes; i++)
        {
            Assert.NotNull(await service.StartAsync(null, true, "10.9.9.9", Chrome));
        }

        Assert.Null(await service.StartAsync(null, true, "10.9.9.9", Chrome));
        Assert.NotNull(await service.StartAsync(null, true, "10.9.9.10", Chrome));
    }

    [Fact]
    public async Task Push_is_not_sent_when_the_user_already_has_many_pending_requests()
    {
        var (service, context, notifier) = Create();
        var user = await AddUserAsync(context, "martien");

        for (var i = 0; i < 6; i++)
        {
            await service.StartAsync(user.Email, true, $"10.0.0.{i + 1}", Chrome);
        }

        Assert.Equal(3, notifier.Calls);
    }

    [Fact]
    public async Task The_choices_hold_the_right_number_once_with_two_different_decoys_and_never_change()
    {
        var (service, context, _) = Create();
        var user = await AddUserAsync(context, "martien");
        var started = (await service.StartAsync(user.Email, true, "10.0.0.1", Chrome))!;

        var first = (await service.GetForApproverAsync(started.Id, user.Id))!;
        var again = (await service.GetForApproverAsync(started.Id, user.Id))!;

        Assert.Equal(3, first.Choices.Distinct().Count());
        Assert.Contains(started.MatchNumber, first.Choices);
        Assert.All(first.Choices, c => Assert.InRange(c, 10, 99));
        Assert.Equal(first.Choices, again.Choices);
        Assert.True(first.NamedYou);
    }

    [Fact]
    public async Task Every_step_is_written_to_the_audit_log()
    {
        var (service, context, _) = Create();
        var user = await AddUserAsync(context, "martien");
        var started = (await service.StartAsync(user.Email, true, "10.0.0.1", Chrome))!;
        await service.ApproveAsync(started.Id, user.Id, null, started.MatchNumber);

        var actions = await context.AuditLogs.Select(a => a.Action).ToListAsync();
        Assert.Contains("LoginApprovalRequested", actions);
        Assert.Contains("LoginApprovalApproved", actions);
    }
}
