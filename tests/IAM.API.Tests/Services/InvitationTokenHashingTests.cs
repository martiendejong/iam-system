using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IAM.API.Tests.Services;

/// <summary>Task 5578: invitations store only a SHA-256 hash of the e-mailed token.</summary>
public class InvitationTokenHashingTests
{
    private sealed class Scene
    {
        public required DbContextOptions<IAMDbContext> Options { get; init; }
        public required Tenant Tenant { get; init; }
        public required Role Role { get; init; }
        public required User Sender { get; init; }
        public RecordingEmailService Email { get; } = new();
    }

    private static Scene CreateScene()
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var context = new IAMDbContext(options);
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"Tenant {Guid.NewGuid():N}", IsActive = true };
        var role = new Role { Id = Guid.NewGuid(), Name = "Resident", Permissions = "[\"Room.View\"]" };
        var sender = new User { Email = $"{Guid.NewGuid():N}@sender.test", PasswordHash = "not-used", EmailConfirmed = true, IsActive = true };
        context.AddRange(tenant, role, sender);
        context.SaveChanges();
        return new Scene { Options = options, Tenant = tenant, Role = role, Sender = sender };
    }

    private static InvitationService Service(IAMDbContext context, Scene scene) =>
        new(context, scene.Email, new ConfigurationBuilder().AddInMemoryCollection().Build(), NullLogger<InvitationService>.Instance);

    private static async Task<InvitationSendResult> SendAsync(Scene scene, string email = "new.person@invited.test")
    {
        using var context = new IAMDbContext(scene.Options);
        return await Service(context, scene).SendInvitationAsync(email, scene.Tenant.Id, scene.Role.Id, scene.Sender.Id);
    }

    [Fact]
    public void Hasher_MatchesKnownSha256Vector_AsLowercaseHex()
    {
        // Same value PostgreSQL's encode(sha256(convert_to('abc','UTF8')),'hex') yields (used by the migration).
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", TokenHasher.Sha256Hex("abc"));
    }

    [Fact]
    public async Task Send_StoresOnlyTheHash_AndEmailsTheRawToken()
    {
        var scene = CreateScene();
        var result = await SendAsync(scene);

        var emailed = Assert.Single(scene.Email.SentInvitationTokens);
        Assert.Equal(emailed, result.RawToken);

        using var context = new IAMDbContext(scene.Options);
        var stored = (await context.Set<Invitation>().SingleAsync()).Token;
        Assert.NotEqual(emailed, stored);
        Assert.Equal(TokenHasher.Sha256Hex(emailed), stored);
        Assert.DoesNotContain(emailed, context.Set<Invitation>().Select(i => i.Token));
    }

    [Fact]
    public async Task Lookup_ByEmailedToken_Works_ByStoredHashOrWrongToken_DoesNot()
    {
        var scene = CreateScene();
        var result = await SendAsync(scene);

        using var context = new IAMDbContext(scene.Options);
        var service = Service(context, scene);
        Assert.NotNull(await service.GetInvitationByTokenAsync(result.RawToken));
        Assert.Null(await service.GetInvitationByTokenAsync("wrong-token"));
        // The stored hash is not a credential: presenting it must not find the invitation.
        Assert.Null(await service.GetInvitationByTokenAsync(result.Invitation.Token));
    }

    [Fact]
    public async Task Accept_WithEmailedToken_Succeeds()
    {
        var scene = CreateScene();
        var result = await SendAsync(scene);

        using var context = new IAMDbContext(scene.Options);
        var accepted = await Service(context, scene).AcceptInvitationAsync(result.RawToken, "Str0ngTestPassw0rd!", "New", "Person");

        Assert.True(accepted.Success, accepted.Error);
    }

    [Fact]
    public async Task Accept_WithWrongToken_OrStoredHash_IsRejected()
    {
        var scene = CreateScene();
        var result = await SendAsync(scene);

        using var context = new IAMDbContext(scene.Options);
        var service = Service(context, scene);
        Assert.False((await service.AcceptInvitationAsync("wrong-token", "Str0ngTestPassw0rd!", "New", "Person")).Success);
        Assert.False((await service.AcceptInvitationAsync(result.Invitation.Token, "Str0ngTestPassw0rd!", "New", "Person")).Success);
    }

    [Fact]
    public async Task Accept_MigratedRow_HashedFromLegacyRawToken_StillWorks()
    {
        // The migration rewrites Token = sha256hex(raw) for rows created before the change;
        // the already-mailed raw link must keep working.
        var scene = CreateScene();
        const string legacyRaw = "legacyRawTokenFromBeforeTheChange_0123456789";
        using (var seed = new IAMDbContext(scene.Options))
        {
            seed.Add(new Invitation
            {
                Email = "legacy@invited.test", TenantId = scene.Tenant.Id, RoleId = scene.Role.Id,
                Token = TokenHasher.Sha256Hex(legacyRaw), Status = "Pending", InvitedByUserId = scene.Sender.Id,
                ExpiresAt = DateTime.UtcNow.AddDays(3)
            });
            await seed.SaveChangesAsync();
        }

        using var context = new IAMDbContext(scene.Options);
        var accepted = await Service(context, scene).AcceptInvitationAsync(legacyRaw, "Str0ngTestPassw0rd!", "Old", "Invitee");
        Assert.True(accepted.Success, accepted.Error);
    }
}
