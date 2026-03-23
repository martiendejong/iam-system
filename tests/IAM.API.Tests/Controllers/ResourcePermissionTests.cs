using System.Net;
using System.Net.Http.Json;
using IAM.API.Controllers;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using Xunit;

namespace IAM.API.Tests.Controllers;

/// <summary>
/// Tests for hierarchical permission inheritance
/// </summary>
public class ResourcePermissionTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly IAMTestWebApplicationFactory _factory;

    public ResourcePermissionTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GrantPermission_OnBuilding_InheritsToDevice()
    {
        // Arrange
        var token = await TestHelpers.GetAdminTokenAsync(_client);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Create hierarchy: Location -> Building -> Floor -> Room -> Device
        var (location, building, floor, room, device) = await CreateCompleteHierarchyAsync();

        // Get test user ID (the standard test user)
        var testUserId = Guid.Parse("88888888-8888-8888-8888-888888888888");

        // Grant VIEW permission on Building with inheritance
        var grantRequest = new GrantPermissionRequest
        {
            UserId = testUserId,
            ResourceType = ResourceType.Building,
            ResourceId = building.Id,
            Actions = PermissionAction.View,
            InheritToChildren = true
        };

        var grantResponse = await _client.PostAsJsonAsync("/api/resourcepermission/grant", grantRequest);
        Assert.Equal(HttpStatusCode.Created, grantResponse.StatusCode);

        // Act - Check if user has View permission on the device (4 levels down)
        var checkResponse = await _client.GetAsync(
            $"/api/resourcepermission/check?resourceType={ResourceType.IoTDevice}&resourceId={device.Id}&action={PermissionAction.View}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, checkResponse.StatusCode);
        var hasPermission = await checkResponse.Content.ReadFromJsonAsync<bool>();
        Assert.True(hasPermission, "Permission on Building should inherit to Device");
    }

    [Fact]
    public async Task GrantPermission_WithoutInheritance_DoesNotInherit()
    {
        // Arrange
        var token = await TestHelpers.GetAdminTokenAsync(_client);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var (location, building, floor, room, device) = await CreateCompleteHierarchyAsync();
        var testUserId = Guid.Parse("88888888-8888-8888-8888-888888888888");

        // Grant VIEW permission on Building WITHOUT inheritance
        var grantRequest = new GrantPermissionRequest
        {
            UserId = testUserId,
            ResourceType = ResourceType.Building,
            ResourceId = building.Id,
            Actions = PermissionAction.View,
            InheritToChildren = false  // No inheritance
        };

        await _client.PostAsJsonAsync("/api/resourcepermission/grant", grantRequest);

        // Act - Check if user has View permission on the device
        var checkResponse = await _client.GetAsync(
            $"/api/resourcepermission/check?resourceType={ResourceType.IoTDevice}&resourceId={device.Id}&action={PermissionAction.View}");

        // Assert
        var hasPermission = await checkResponse.Content.ReadFromJsonAsync<bool>();
        Assert.False(hasPermission, "Permission without inheritance should NOT apply to child resources");
    }

    [Fact]
    public async Task GrantStreamPermission_OnLocation_AllowsDeviceStreaming()
    {
        // Arrange
        var token = await TestHelpers.GetAdminTokenAsync(_client);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var (location, building, floor, room, device) = await CreateCompleteHierarchyAsync();

        // Make device streamable
        device.SupportsStreaming = true;
        await _client.PutAsJsonAsync($"/api/iotdevice/{device.Id}", device);

        var testUserId = Guid.Parse("88888888-8888-8888-8888-888888888888");

        // Grant STREAM permission on Location (top level) with inheritance
        var grantRequest = new GrantPermissionRequest
        {
            UserId = testUserId,
            ResourceType = ResourceType.Location,
            ResourceId = location.Id,
            Actions = PermissionAction.Stream,
            InheritToChildren = true
        };

        await _client.PostAsJsonAsync("/api/resourcepermission/grant", grantRequest);

        // Act - Check Stream permission on device (5 levels down)
        var checkResponse = await _client.GetAsync(
            $"/api/resourcepermission/check?resourceType={ResourceType.IoTDevice}&resourceId={device.Id}&action={PermissionAction.Stream}");

        // Assert
        var hasPermission = await checkResponse.Content.ReadFromJsonAsync<bool>();
        Assert.True(hasPermission, "Stream permission on Location should inherit all the way to Device");
    }

    [Fact]
    public async Task GetEffectivePermissions_WithMultipleLevels_CombinesPermissions()
    {
        // Arrange
        var token = await TestHelpers.GetAdminTokenAsync(_client);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var (location, building, floor, room, device) = await CreateCompleteHierarchyAsync();
        var testUserId = Guid.Parse("88888888-8888-8888-8888-888888888888");

        // Grant VIEW on Building
        await _client.PostAsJsonAsync("/api/resourcepermission/grant", new GrantPermissionRequest
        {
            UserId = testUserId,
            ResourceType = ResourceType.Building,
            ResourceId = building.Id,
            Actions = PermissionAction.View,
            InheritToChildren = true
        });

        // Grant STREAM on Floor
        await _client.PostAsJsonAsync("/api/resourcepermission/grant", new GrantPermissionRequest
        {
            UserId = testUserId,
            ResourceType = ResourceType.Floor,
            ResourceId = floor.Id,
            Actions = PermissionAction.Stream,
            InheritToChildren = true
        });

        // Grant CONTROL on Device directly
        await _client.PostAsJsonAsync("/api/resourcepermission/grant", new GrantPermissionRequest
        {
            UserId = testUserId,
            ResourceType = ResourceType.IoTDevice,
            ResourceId = device.Id,
            Actions = PermissionAction.Control,
            InheritToChildren = false
        });

        // Act - Get effective permissions (should combine all three)
        var response = await _client.GetAsync(
            $"/api/resourcepermission/effective?resourceType={ResourceType.IoTDevice}&resourceId={device.Id}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var effectivePermissions = await response.Content.ReadFromJsonAsync<PermissionAction>();

        Assert.True(effectivePermissions.HasFlag(PermissionAction.View), "Should have View from Building");
        Assert.True(effectivePermissions.HasFlag(PermissionAction.Stream), "Should have Stream from Floor");
        Assert.True(effectivePermissions.HasFlag(PermissionAction.Control), "Should have Control from direct permission");
    }

    [Fact]
    public async Task RevokePermission_RemovesAccess()
    {
        // Arrange
        var token = await TestHelpers.GetAdminTokenAsync(_client);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var (location, building, floor, room, device) = await CreateCompleteHierarchyAsync();
        var testUserId = Guid.Parse("88888888-8888-8888-8888-888888888888");

        // Grant permission
        var grantResponse = await _client.PostAsJsonAsync("/api/resourcepermission/grant", new GrantPermissionRequest
        {
            UserId = testUserId,
            ResourceType = ResourceType.Building,
            ResourceId = building.Id,
            Actions = PermissionAction.View,
            InheritToChildren = true
        });

        var permission = await grantResponse.Content.ReadFromJsonAsync<ResourcePermission>();

        // Verify permission exists
        var checkBefore = await _client.GetAsync(
            $"/api/resourcepermission/check?resourceType={ResourceType.IoTDevice}&resourceId={device.Id}&action={PermissionAction.View}");
        var hasPermissionBefore = await checkBefore.Content.ReadFromJsonAsync<bool>();
        Assert.True(hasPermissionBefore);

        // Act - Revoke permission
        var revokeResponse = await _client.DeleteAsync($"/api/resourcepermission/{permission!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        // Assert - Permission should be gone
        var checkAfter = await _client.GetAsync(
            $"/api/resourcepermission/check?resourceType={ResourceType.IoTDevice}&resourceId={device.Id}&action={PermissionAction.View}");
        var hasPermissionAfter = await checkAfter.Content.ReadFromJsonAsync<bool>();
        Assert.False(hasPermissionAfter, "Permission should be revoked");
    }

    [Fact]
    public async Task GetAccessibleResources_ReturnsOnlyAllowedDevices()
    {
        // Arrange
        var token = await TestHelpers.GetAdminTokenAsync(_client);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Create two separate hierarchies
        var (loc1, bld1, flr1, rm1, dev1) = await CreateCompleteHierarchyAsync("Campus A", "Building 1");
        var (loc2, bld2, flr2, rm2, dev2) = await CreateCompleteHierarchyAsync("Campus B", "Building 2");

        var testUserId = Guid.Parse("88888888-8888-8888-8888-888888888888");

        // Grant permission only on first building
        await _client.PostAsJsonAsync("/api/resourcepermission/grant", new GrantPermissionRequest
        {
            UserId = testUserId,
            ResourceType = ResourceType.Building,
            ResourceId = bld1.Id,
            Actions = PermissionAction.View,
            InheritToChildren = true
        });

        // Act - Get accessible devices
        var response = await _client.GetAsync($"/api/resourcepermission/accessible/{ResourceType.IoTDevice}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var accessibleDevices = await response.Content.ReadFromJsonAsync<List<Guid>>();
        Assert.NotNull(accessibleDevices);
        Assert.Contains(dev1.Id, accessibleDevices);
        Assert.DoesNotContain(dev2.Id, accessibleDevices);
    }

    [Fact]
    public async Task GetInheritedPermissions_ShowsAllParentPermissions()
    {
        // Arrange
        var token = await TestHelpers.GetAdminTokenAsync(_client);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var (location, building, floor, room, device) = await CreateCompleteHierarchyAsync();
        var testUserId = Guid.Parse("88888888-8888-8888-8888-888888888888");

        // Grant permissions at multiple levels
        await _client.PostAsJsonAsync("/api/resourcepermission/grant", new GrantPermissionRequest
        {
            UserId = testUserId,
            ResourceType = ResourceType.Location,
            ResourceId = location.Id,
            Actions = PermissionAction.View,
            InheritToChildren = true
        });

        await _client.PostAsJsonAsync("/api/resourcepermission/grant", new GrantPermissionRequest
        {
            UserId = testUserId,
            ResourceType = ResourceType.Building,
            ResourceId = building.Id,
            Actions = PermissionAction.Stream,
            InheritToChildren = true
        });

        // Act - Get inherited permissions for the device
        var response = await _client.GetAsync(
            $"/api/resourcepermission/inherited/{ResourceType.IoTDevice}/{device.Id}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var inheritedPermissions = await response.Content.ReadFromJsonAsync<List<ResourcePermission>>();
        Assert.NotNull(inheritedPermissions);
        Assert.Equal(2, inheritedPermissions.Count);

        Assert.Contains(inheritedPermissions, p => p.ResourceType == ResourceType.Location && p.Actions.HasFlag(PermissionAction.View));
        Assert.Contains(inheritedPermissions, p => p.ResourceType == ResourceType.Building && p.Actions.HasFlag(PermissionAction.Stream));
    }

    // Helper methods
    private async Task<(Location, Building, Floor, Room, IoTDevice)> CreateCompleteHierarchyAsync(
        string locationName = "Test Campus",
        string buildingName = "Test Building")
    {
        var location = new Location { Name = locationName, City = "Test City" };
        var locResponse = await _client.PostAsJsonAsync("/api/location", location);
        var createdLocation = (await locResponse.Content.ReadFromJsonAsync<Location>())!;

        var building = new Building { Name = buildingName, Code = "A", LocationId = createdLocation.Id };
        var bldResponse = await _client.PostAsJsonAsync("/api/building", building);
        var createdBuilding = (await bldResponse.Content.ReadFromJsonAsync<Building>())!;

        var floor = new Floor { Name = "Ground Floor", FloorNumber = 0, BuildingId = createdBuilding.Id };
        var flrResponse = await _client.PostAsJsonAsync("/api/floor", floor);
        var createdFloor = (await flrResponse.Content.ReadFromJsonAsync<Floor>())!;

        var room = new Room { Name = "Room 101", RoomNumber = "101", Type = RoomType.Office, FloorId = createdFloor.Id };
        var rmResponse = await _client.PostAsJsonAsync("/api/room", room);
        var createdRoom = (await rmResponse.Content.ReadFromJsonAsync<Room>())!;

        var device = new IoTDevice
        {
            Name = "Test Device",
            DeviceId = $"DEV-{Guid.NewGuid().ToString("N")[..8]}",
            Type = DeviceType.Sensor,
            RoomId = createdRoom.Id
        };
        var devResponse = await _client.PostAsJsonAsync("/api/iotdevice", device);
        var createdDevice = (await devResponse.Content.ReadFromJsonAsync<IoTDevice>())!;

        return (createdLocation, createdBuilding, createdFloor, createdRoom, createdDevice);
    }
}
