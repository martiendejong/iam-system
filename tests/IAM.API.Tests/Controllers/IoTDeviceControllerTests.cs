using System.Net;
using System.Net.Http.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using Xunit;

namespace IAM.API.Tests.Controllers;

public class IoTDeviceControllerTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly IAMTestWebApplicationFactory _factory;

    public IoTDeviceControllerTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CreateDevice_WithValidData_ReturnsCreated()
    {
        // Arrange
        var token = await TestHelpers.GetAdminTokenAsync(_client);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Create hierarchy: Location -> Building -> Floor -> Room
        var location = await CreateLocationAsync("Campus");
        var building = await CreateBuildingAsync("Building A", location.Id);
        var floor = await CreateFloorAsync("Ground Floor", 0, building.Id);
        var room = await CreateRoomAsync("Room 101", floor.Id);

        var device = new IoTDevice
        {
            Name = "Camera 1",
            DeviceId = "CAM-001",
            Type = DeviceType.IPCamera,
            RoomId = room.Id,
            SupportsStreaming = true,
            StreamUrl = "rtsp://camera1.local/stream",
            StreamProtocol = "RTSP",
            IpAddress = "192.168.1.100",
            Port = 554
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/iotdevice", device);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<IoTDevice>();
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(device.Name, created.Name);
        Assert.Equal(device.DeviceId, created.DeviceId);
        Assert.True(created.SupportsStreaming);
    }

    [Fact]
    public async Task GetByDeviceId_ExistingDevice_ReturnsDevice()
    {
        // Arrange
        var token = await TestHelpers.GetAdminTokenAsync(_client);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var room = await CreateFullHierarchyAsync("Campus", "Building", "Floor", "Room");
        var device = new IoTDevice
        {
            Name = "Sensor 1",
            DeviceId = "SENS-001",
            Type = DeviceType.TemperatureSensor,
            RoomId = room.Id
        };
        var createResponse = await _client.PostAsJsonAsync("/api/iotdevice", device);
        var created = await createResponse.Content.ReadFromJsonAsync<IoTDevice>();

        // Act
        var response = await _client.GetAsync($"/api/iotdevice/device-id/{created!.DeviceId}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var retrieved = await response.Content.ReadFromJsonAsync<IoTDevice>();
        Assert.NotNull(retrieved);
        Assert.Equal(created.DeviceId, retrieved.DeviceId);
    }

    [Fact]
    public async Task GetByType_MultipleDevices_ReturnsFilteredDevices()
    {
        // Arrange
        var token = await TestHelpers.GetAdminTokenAsync(_client);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var room = await CreateFullHierarchyAsync("Campus", "Building", "Floor", "Room");

        // Create multiple device types
        await _client.PostAsJsonAsync("/api/iotdevice", new IoTDevice
        {
            Name = "Camera 1",
            DeviceId = "CAM-100",
            Type = DeviceType.Camera,
            RoomId = room.Id
        });

        await _client.PostAsJsonAsync("/api/iotdevice", new IoTDevice
        {
            Name = "Camera 2",
            DeviceId = "CAM-101",
            Type = DeviceType.Camera,
            RoomId = room.Id
        });

        await _client.PostAsJsonAsync("/api/iotdevice", new IoTDevice
        {
            Name = "Sensor 1",
            DeviceId = "SENS-100",
            Type = DeviceType.Sensor,
            RoomId = room.Id
        });

        // Act
        var response = await _client.GetAsync($"/api/iotdevice/type/{DeviceType.Camera}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var devices = await response.Content.ReadFromJsonAsync<List<IoTDevice>>();
        Assert.NotNull(devices);
        Assert.Equal(2, devices.Count);
        Assert.All(devices, d => Assert.Equal(DeviceType.Camera, d.Type));
    }

    [Fact]
    public async Task UpdateStatus_ExistingDevice_UpdatesStatus()
    {
        // Arrange
        var token = await TestHelpers.GetAdminTokenAsync(_client);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var room = await CreateFullHierarchyAsync("Campus", "Building", "Floor", "Room");
        var device = new IoTDevice
        {
            Name = "Device 1",
            DeviceId = "DEV-001",
            Type = DeviceType.Sensor,
            RoomId = room.Id,
            Status = DeviceStatus.Offline
        };
        var createResponse = await _client.PostAsJsonAsync("/api/iotdevice", device);
        var created = await createResponse.Content.ReadFromJsonAsync<IoTDevice>();

        // Act
        var response = await _client.PatchAsync(
            $"/api/iotdevice/{created!.Id}/status",
            JsonContent.Create(DeviceStatus.Online));

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify status updated
        var getResponse = await _client.GetAsync($"/api/iotdevice/{created.Id}");
        var updated = await getResponse.Content.ReadFromJsonAsync<IoTDevice>();
        Assert.Equal(DeviceStatus.Online, updated!.Status);
    }

    [Fact]
    public async Task RecordHeartbeat_ExistingDevice_UpdatesLastSeen()
    {
        // Arrange
        var token = await TestHelpers.GetAdminTokenAsync(_client);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var room = await CreateFullHierarchyAsync("Campus", "Building", "Floor", "Room");
        var device = new IoTDevice
        {
            Name = "Device 1",
            DeviceId = "DEV-002",
            Type = DeviceType.Sensor,
            RoomId = room.Id
        };
        var createResponse = await _client.PostAsJsonAsync("/api/iotdevice", device);
        var created = await createResponse.Content.ReadFromJsonAsync<IoTDevice>();

        var beforeHeartbeat = DateTime.UtcNow;
        await Task.Delay(100); // Small delay to ensure different timestamp

        // Act
        var response = await _client.PostAsync($"/api/iotdevice/{created!.Id}/heartbeat", null);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify LastSeenAt updated
        var getResponse = await _client.GetAsync($"/api/iotdevice/{created.Id}");
        var updated = await getResponse.Content.ReadFromJsonAsync<IoTDevice>();
        Assert.NotNull(updated!.LastSeenAt);
        Assert.True(updated.LastSeenAt > beforeHeartbeat);
        Assert.Equal(DeviceStatus.Online, updated.Status);
    }

    [Fact]
    public async Task GetStreamingDevices_MultipleDevices_ReturnsOnlyStreaming()
    {
        // Arrange
        var token = await TestHelpers.GetAdminTokenAsync(_client);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var room = await CreateFullHierarchyAsync("Campus", "Building", "Floor", "Room");

        // Create streaming device
        await _client.PostAsJsonAsync("/api/iotdevice", new IoTDevice
        {
            Name = "Camera 1",
            DeviceId = "CAM-200",
            Type = DeviceType.Camera,
            RoomId = room.Id,
            SupportsStreaming = true
        });

        // Create non-streaming device
        await _client.PostAsJsonAsync("/api/iotdevice", new IoTDevice
        {
            Name = "Sensor 1",
            DeviceId = "SENS-200",
            Type = DeviceType.Sensor,
            RoomId = room.Id,
            SupportsStreaming = false
        });

        // Act
        var response = await _client.GetAsync("/api/iotdevice/streaming");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var devices = await response.Content.ReadFromJsonAsync<List<IoTDevice>>();
        Assert.NotNull(devices);
        Assert.All(devices, d => Assert.True(d.SupportsStreaming));
    }

    // Helper methods
    private async Task<Location> CreateLocationAsync(string name)
    {
        var location = new Location { Name = name, City = "Test City" };
        var response = await _client.PostAsJsonAsync("/api/location", location);
        return (await response.Content.ReadFromJsonAsync<Location>())!;
    }

    private async Task<Building> CreateBuildingAsync(string name, Guid locationId)
    {
        var building = new Building { Name = name, LocationId = locationId };
        var response = await _client.PostAsJsonAsync("/api/building", building);
        return (await response.Content.ReadFromJsonAsync<Building>())!;
    }

    private async Task<Floor> CreateFloorAsync(string name, int floorNumber, Guid buildingId)
    {
        var floor = new Floor { Name = name, FloorNumber = floorNumber, BuildingId = buildingId };
        var response = await _client.PostAsJsonAsync("/api/floor", floor);
        return (await response.Content.ReadFromJsonAsync<Floor>())!;
    }

    private async Task<Room> CreateRoomAsync(string name, Guid floorId)
    {
        var room = new Room { Name = name, RoomNumber = "101", Type = RoomType.Office, FloorId = floorId };
        var response = await _client.PostAsJsonAsync("/api/room", room);
        return (await response.Content.ReadFromJsonAsync<Room>())!;
    }

    private async Task<Room> CreateFullHierarchyAsync(string locationName, string buildingName, string floorName, string roomName)
    {
        var location = await CreateLocationAsync(locationName);
        var building = await CreateBuildingAsync(buildingName, location.Id);
        var floor = await CreateFloorAsync(floorName, 0, building.Id);
        return await CreateRoomAsync(roomName, floor.Id);
    }
}
