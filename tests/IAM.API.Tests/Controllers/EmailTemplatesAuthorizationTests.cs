using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>Task 4716: email templates are limited to administrators of the tenant; template values are encoded.</summary>
public class EmailTemplatesAuthorizationTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private static readonly Guid RootTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly IAMTestWebApplicationFactory _factory;

    public EmailTemplatesAuthorizationTests(IAMTestWebApplicationFactory factory) => _factory = factory;

    private async Task<Guid> SeedTenantWithTemplateAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"T {Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        db.EmailTemplates.Add(new EmailTemplate { TenantId = tenant.Id, Key = "Invitation", Subject = "orig", BodyHtml = "<p>orig</p>", IsActive = true });
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<HttpClient> UserWithRoleAsync(string roleName, Guid? tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var user = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@emailtpl-authz.test", PasswordHash = "x" };
        db.Users.Add(user);
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == roleName);
        if (role == null)
        {
            role = new Role { Id = Guid.NewGuid(), Name = roleName, Description = roleName, TenantId = RootTenantId };
            db.Roles.Add(role);
        }
        if (tenantId != null)
            db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = user.Id, RoleId = role.Id, TenantId = tenantId, GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            TestAuthenticationHelper.GenerateJwtToken(user.Id, user.Email, new[] { roleName }, null));
        return client;
    }

    private async Task<string?> StoredSubjectAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        return (await db.EmailTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.TenantId == tenantId && t.Key == "Invitation"))?.Subject;
    }

    private static readonly object Body = new { subject = "evil", bodyHtml = "<a href='https://evil.test/?t={{Token}}'>x</a>" };

    [Fact]
    public async Task Admin_of_other_tenant_gets_403_and_nothing_changes()
    {
        var victim = await SeedTenantWithTemplateAsync();
        var own = await SeedTenantWithTemplateAsync();
        var attacker = await UserWithRoleAsync("TenantAdmin", own);

        Assert.Equal(HttpStatusCode.Forbidden, (await attacker.GetAsync($"/api/email-templates?tenantId={victim}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await attacker.GetAsync($"/api/email-templates/Invitation?tenantId={victim}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await attacker.PutAsJsonAsync($"/api/email-templates/Invitation?tenantId={victim}", Body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await attacker.DeleteAsync($"/api/email-templates/Invitation?tenantId={victim}")).StatusCode);
        Assert.Equal("orig", await StoredSubjectAsync(victim));
    }

    [Fact]
    public async Task Non_admin_gets_403_on_all_actions()
    {
        var tenant = await SeedTenantWithTemplateAsync();
        var member = await UserWithRoleAsync("Member", tenant);

        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/email-templates?tenantId={tenant}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/email-templates/Invitation?tenantId={tenant}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync($"/api/email-templates/Invitation?tenantId={tenant}", Body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/api/email-templates/Invitation?tenantId={tenant}")).StatusCode);
        Assert.Equal("orig", await StoredSubjectAsync(tenant));
    }

    [Fact]
    public async Task Tenant_admin_can_read_write_and_delete_own_template()
    {
        var tenant = await SeedTenantWithTemplateAsync();
        var admin = await UserWithRoleAsync("TenantAdmin", tenant);

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/email-templates?tenantId={tenant}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/email-templates/Invitation?tenantId={tenant}", Body)).StatusCode);
        Assert.Equal("evil", await StoredSubjectAsync(tenant));
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/email-templates/Invitation?tenantId={tenant}")).StatusCode);
        Assert.Null(await StoredSubjectAsync(tenant));
    }

    [Fact]
    public async Task SuperAdmin_can_write_any_tenant()
    {
        var tenant = await SeedTenantWithTemplateAsync();
        var sa = await UserWithRoleAsync("SuperAdmin", null);
        Assert.Equal(HttpStatusCode.OK, (await sa.PutAsJsonAsync($"/api/email-templates/Invitation?tenantId={tenant}", Body)).StatusCode);
    }

    [Fact]
    public void Hostile_names_render_as_text_and_token_is_url_escaped()
    {
        var values = new Dictionary<string, string>
        {
            ["{{TenantName}}"] = "<script>alert(1)</script>",
            ["{{InviterName}}"] = "\"><img src=x onerror=1>",
            ["{{Token}}"] = "a b&c=d",
            ["{{Email}}"] = "a@b.test"
        };
        var html = InvitationService.RenderTemplate("<p>{{TenantName}} {{InviterName}}</p><a href=\"/p?token={{Token}}\">go</a>", values, html: true);

        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("token=a%20b%26c%3Dd", html);

        var subject = InvitationService.RenderTemplate("Hi {{TenantName}}", new() { ["{{TenantName}}"] = "x\r\nBcc: a@b" }, html: false);
        Assert.DoesNotContain("\n", subject);
    }
}
