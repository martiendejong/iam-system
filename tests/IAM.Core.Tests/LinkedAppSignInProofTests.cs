using IAM.Core.Mobile;

namespace IAM.Core.Tests;

public class LinkedAppSignInProofTests
{
    private const string Verifier = "test-verifier-0123456789-abcdefghijklmnopqrstuvwxyz";
    private DateTimeOffset _t = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private LinkedAppSignInProof Make() => new(TimeSpan.FromMinutes(2), () => _t);

    [Fact]
    public void Happy_path_returns_user_once()
    {
        var p = Make();
        var id = p.Create(LinkedAppSignInProof.ChallengeFor(Verifier));
        Assert.True(p.Approve(id, "test-user"));
        Assert.Equal(LinkedAppConsumeResult.Ok, p.Consume(id, Verifier, out var user));
        Assert.Equal("test-user", user);
        Assert.Equal(LinkedAppConsumeResult.AlreadyConsumed, p.Consume(id, Verifier, out _));
    }

    [Fact]
    public void Pending_or_denied_cannot_be_consumed()
    {
        var p = Make();
        var id = p.Create(LinkedAppSignInProof.ChallengeFor(Verifier));
        Assert.Equal(LinkedAppConsumeResult.NotApproved, p.Consume(id, Verifier, out _));
        Assert.True(p.Deny(id));
        Assert.Equal(LinkedAppConsumeResult.NotApproved, p.Consume(id, Verifier, out _));
        Assert.False(p.Approve(id, "u"));
    }

    [Fact]
    public void Wrong_verifier_is_refused_without_burning_the_request()
    {
        var p = Make();
        var id = p.Create(LinkedAppSignInProof.ChallengeFor(Verifier));
        p.Approve(id, "u");
        Assert.Equal(LinkedAppConsumeResult.PkceMismatch, p.Consume(id, "attacker", out var u));
        Assert.Null(u);
        Assert.Equal(LinkedAppConsumeResult.Ok, p.Consume(id, Verifier, out _));
    }

    [Fact]
    public void Expiry_blocks_approve_and_consume()
    {
        var p = Make();
        var id = p.Create(LinkedAppSignInProof.ChallengeFor(Verifier));
        p.Approve(id, "u");
        _t = _t.AddMinutes(2);
        Assert.Equal(LinkedAppConsumeResult.Expired, p.Consume(id, Verifier, out _));
        var id2 = p.Create(LinkedAppSignInProof.ChallengeFor(Verifier));
        _t = _t.AddMinutes(3);
        Assert.False(p.Approve(id2, "u"));
        Assert.Equal(LinkedAppRequestState.Expired, p.GetState(id2));
    }

    [Fact]
    public void Unknown_request_and_empty_challenge()
    {
        var p = Make();
        Assert.Equal(LinkedAppConsumeResult.UnknownRequest, p.Consume("nope", Verifier, out _));
        Assert.Throws<ArgumentException>(() => p.Create(""));
    }

    [Fact]
    public void Ids_are_unique_and_long()
    {
        var p = Make();
        var a = p.Create("c");
        var b = p.Create("c");
        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 43);
    }

    [Fact]
    public void Challenge_matches_rfc7636_example()
        => Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            LinkedAppSignInProof.ChallengeFor("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
}
