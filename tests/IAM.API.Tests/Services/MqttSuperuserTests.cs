using IAM.Core.Entities;
using IAM.Core.Services;
using IAM.Infrastructure.Data;
using IAM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IAM.API.Tests.Services;

/// <summary>Task 5483: only the exact "*" permission makes a device an MQTT broker superuser.</summary>
public class MqttSuperuserTests
{
    private static (MqttAuthService svc, IAMDbContext db) Create()
    {
        var options = new DbContextOptionsBuilder<IAMDbContext>()
            .UseInMemoryDatabase($"mqtt-su-{Guid.NewGuid():N}").Options;
        var db = new IAMDbContext(options);
        var auth = new DeviceAuthenticationService(db, new ConfigurationBuilder().Build());
        return (new MqttAuthService(db, auth, NullLogger<MqttAuthService>.Instance), db);
    }

    private static async Task Seed(IAMDbContext db, string id, string permissionsJson, bool active = true)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"T {Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        db.Devices.Add(new Device
        {
            DeviceId = id, Name = id, TenantId = tenant.Id, IsActive = active, Permissions = permissionsJson
        });
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("[\"acme:*\"]")]
    [InlineData("[\"acme:hq:floor-3:sensor:*\"]")]
    [InlineData("[\"a:b:read\",\"acme:hq:*\"]")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task Scoped_wildcards_and_empty_permissions_are_not_superuser(string perms)
    {
        var (svc, db) = Create();
        await Seed(db, "dev1", perms);
        Assert.False(await svc.IsSuperuserAsync("dev1"));
    }

    [Fact]
    public async Task Exact_star_is_superuser()
    {
        var (svc, db) = Create();
        await Seed(db, "dev1", "[\"*\"]");
        Assert.True(await svc.IsSuperuserAsync("dev1"));
    }

    [Fact]
    public async Task Unknown_inactive_and_blank_clients_are_not_superuser()
    {
        var (svc, db) = Create();
        await Seed(db, "off", "[\"*\"]", active: false);
        Assert.False(await svc.IsSuperuserAsync("off"));
        Assert.False(await svc.IsSuperuserAsync("missing"));
        Assert.False(await svc.IsSuperuserAsync(" "));
    }

    [Fact]
    public async Task Scoped_wildcard_device_is_allowed_inside_scope_and_denied_outside()
    {
        var (svc, db) = Create();
        await Seed(db, "dev1", "[\"acme:hq:floor-3:sensor:*\"]");
        var inside = await svc.CheckAclAsync("dev1", "acme/hq/floor-3/sensor/t1/telemetry", MqttAclAction.Publish);
        var otherFloor = await svc.CheckAclAsync("dev1", "acme/hq/floor-4/sensor/t1/telemetry", MqttAclAction.Publish);
        var otherTenant = await svc.CheckAclAsync("dev1", "other/hq/floor-3/sensor/t1/telemetry", MqttAclAction.Subscribe);
        Assert.True(inside.Allowed);
        Assert.False(otherFloor.Allowed);
        Assert.False(otherTenant.Allowed);
    }
}
