using System.Net.Http.Headers;
using System.Text.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using IAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Task 5571: a plain tenant member reading IoT devices must not receive the stream address, protocol, IP address,
/// port, capabilities or metadata; tenant managers still do.
/// </summary>
public class IoTDeviceStreamFieldVisibilityTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private static readonly Guid RootTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly string[] Hidden = { "streamUrl", "streamProtocol", "ipAddress", "port", "capabilities", "metadata" };
    private const string Secret = "rtsp://admin:hunter2@10.9.8.7:554/live";

    private readonly IAMTestWebApplicationFactory _factory;
    public IoTDeviceStreamFieldVisibilityTests(IAMTestWebApplicationFactory factory) => _factory = factory;

    private sealed record Seed(Guid TenantId, Guid RoomId, Guid DeviceId, string DeviceKey);

    private async Task<Seed> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"T {Guid.NewGuid():N}", IsActive = true };
        var location = new Location { Name = "L", TenantId = tenant.Id };
        var building = new Building { Name = "B", LocationId = location.Id, TenantId = tenant.Id };
        var floor = new Floor { Name = "F", BuildingId = building.Id, TenantId = tenant.Id };
        var room = new Room { Name = "R", FloorId = floor.Id, TenantId = tenant.Id };
        var device = new IoTDevice
        {
            Name = "Lobby camera",
            DeviceId = $"cam-{Guid.NewGuid():N}",
            Type = DeviceType.IPCamera,
            RoomId = room.Id,
            TenantId = tenant.Id,
            SupportsStreaming = true,
            StreamUrl = Secret,
            StreamProtocol = "RTSP",
            IpAddress = "10.9.8.7",
            Port = 554,
            Capabilities = "{\"ptz\":true}",
            Metadata = "{\"owner\":\"x\"}"
        };
        db.Tenants.Add(tenant);
        db.Locations.Add(location);
        db.Buildings.Add(building);
        db.Floors.Add(floor);
        db.Rooms.Add(room);
        db.IoTDevices.Add(device);
        await db.SaveChangesAsync();
        return new Seed(tenant.Id, room.Id, device.Id, device.DeviceId);
    }

    private async Task<HttpClient> PersonAsync(string role, Guid tenantId)
    {
        Guid userId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAMDbContext>();
            var user = new User { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@iot-vis.test", PasswordHash = "x" };
            db.Users.Add(user);
            var r = await db.Roles.FirstOrDefaultAsync(x => x.Name == role);
            if (r == null)
            {
                r = new Role { Id = Guid.NewGuid(), Name = role, Description = role, TenantId = RootTenantId };
                db.Roles.Add(r);
            }
            db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = user.Id, RoleId = r.Id, TenantId = tenantId, GrantedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            userId = user.Id;
        }
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            TestAuthenticationHelper.GenerateJwtToken(userId, $"{userId:N}@iot-vis.test", new[] { role }, null));
        return client;
    }

    private static string[] Urls(Seed s) => new[]
    {
        "/api/iotdevice",
        $"/api/iotdevice/{s.DeviceId}",
        $"/api/iotdevice/device-id/{s.DeviceKey}",
        $"/api/iotdevice/room/{s.RoomId}",
        $"/api/iotdevice/type/{DeviceType.IPCamera}",
        $"/api/iotdevice/status/{DeviceStatus.Offline}",
        "/api/iotdevice/streaming",
        $"/api/room/{s.RoomId}/devices",
        $"/api/room/{s.RoomId}"
    };

    private static IEnumerable<JsonElement> Devices(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray().ToList();
        if (root.TryGetProperty("devices", out var d)) return d.EnumerateArray().ToList();
        return new[] { root };
    }

    [Fact]
    public async Task PlainMember_NeverSees_StreamFields_OnAnyDeviceRead()
    {
        var s = await SeedAsync();
        var member = await PersonAsync("User", s.TenantId);

        foreach (var url in Urls(s))
        {
            var response = await member.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{url} returned {(int)response.StatusCode}");
            Assert.DoesNotContain("hunter2", body);
            Assert.DoesNotContain("10.9.8.7", body);

            using var doc = JsonDocument.Parse(body);
            foreach (var device in Devices(doc.RootElement))
                foreach (var field in Hidden)
                    Assert.False(device.TryGetProperty(field, out _), $"{url} leaked {field}");
        }
    }

    [Fact]
    public async Task PlainMember_StillSees_SafeFields()
    {
        var s = await SeedAsync();
        var member = await PersonAsync("User", s.TenantId);

        using var doc = JsonDocument.Parse(await member.GetStringAsync($"/api/iotdevice/{s.DeviceId}"));
        Assert.Equal("Lobby camera", doc.RootElement.GetProperty("name").GetString());
        Assert.True(doc.RootElement.GetProperty("supportsStreaming").GetBoolean());
        Assert.Equal(s.RoomId, doc.RootElement.GetProperty("roomId").GetGuid());
    }

    [Theory]
    [InlineData("TenantAdmin")]
    [InlineData("BuildingManager")]
    [InlineData("BuildingOwner")]
    public async Task Manager_StillGets_FullDevice_OnEveryRead(string role)
    {
        var s = await SeedAsync();
        var manager = await PersonAsync(role, s.TenantId);

        foreach (var url in Urls(s).Take(8))
        {
            var body = await manager.GetStringAsync(url);
            using var doc = JsonDocument.Parse(body);
            var device = Devices(doc.RootElement).Single();
            Assert.Equal(Secret, device.GetProperty("streamUrl").GetString());
            Assert.Equal("10.9.8.7", device.GetProperty("ipAddress").GetString());
            Assert.Equal(554, device.GetProperty("port").GetInt32());
        }
    }

    [Fact]
    public async Task SuperAdmin_GetsFullDevice()
    {
        var s = await SeedAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            TestAuthenticationHelper.GenerateJwtToken(Guid.NewGuid(), "super@iot-vis.test", new[] { "SuperAdmin" }, null));
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/iotdevice/{s.DeviceId}?tenantId={s.TenantId}"));
        Assert.Equal(Secret, doc.RootElement.GetProperty("streamUrl").GetString());
    }
}
