using System.Net;
using System.Net.Http.Json;
using IAM.API.Tests.Infrastructure;
using IAM.Core.Entities;
using Xunit;

namespace IAM.API.Tests.Controllers;

public class LocationControllerTests : IClassFixture<IAMTestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly IAMTestWebApplicationFactory _factory;

    public LocationControllerTests(IAMTestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetAll_WithoutAuth_ReturnsUnauthorized()
    {
        // Act
        var response = await _client.GetAsync("/api/location");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateLocation_WithValidData_ReturnsCreated()
    {
        // Arrange
        var token = await TestHelpers.GetAdminTokenAsync(_client);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var location = new Location
        {
            Name = "Test Campus",
            Address = "123 Test Street",
            City = "Amsterdam",
            Country = "Netherlands",
            Latitude = 52.3676,
            Longitude = 4.9041
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/location", location);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<Location>();
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(location.Name, created.Name);
        Assert.Equal(location.City, created.City);
    }

    [Fact]
    public async Task GetById_ExistingLocation_ReturnsLocation()
    {
        // Arrange
        var token = await TestHelpers.GetAdminTokenAsync(_client);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Create location
        var location = new Location { Name = "Test Location", City = "Utrecht" };
        var createResponse = await _client.PostAsJsonAsync("/api/location", location);
        var created = await createResponse.Content.ReadFromJsonAsync<Location>();

        // Act
        var response = await _client.GetAsync($"/api/location/{created!.Id}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var retrieved = await response.Content.ReadFromJsonAsync<Location>();
        Assert.NotNull(retrieved);
        Assert.Equal(created.Id, retrieved.Id);
        Assert.Equal(created.Name, retrieved.Name);
    }

    [Fact]
    public async Task UpdateLocation_ExistingLocation_ReturnsUpdated()
    {
        // Arrange
        var token = await TestHelpers.GetAdminTokenAsync(_client);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Create location
        var location = new Location { Name = "Original Name", City = "Rotterdam" };
        var createResponse = await _client.PostAsJsonAsync("/api/location", location);
        var created = await createResponse.Content.ReadFromJsonAsync<Location>();

        // Modify
        created!.Name = "Updated Name";
        created.City = "Den Haag";

        // Act
        var response = await _client.PutAsJsonAsync($"/api/location/{created.Id}", created);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<Location>();
        Assert.NotNull(updated);
        Assert.Equal("Updated Name", updated.Name);
        Assert.Equal("Den Haag", updated.City);
    }

    [Fact]
    public async Task DeleteLocation_ExistingLocation_ReturnsNoContent()
    {
        // Arrange
        var token = await TestHelpers.GetAdminTokenAsync(_client);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Create location
        var location = new Location { Name = "To Delete", City = "Eindhoven" };
        var createResponse = await _client.PostAsJsonAsync("/api/location", location);
        var created = await createResponse.Content.ReadFromJsonAsync<Location>();

        // Act
        var response = await _client.DeleteAsync($"/api/location/{created!.Id}");

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify soft delete
        var getResponse = await _client.GetAsync($"/api/location/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task GetBuildings_LocationWithBuildings_ReturnsBuildings()
    {
        // Arrange
        var token = await TestHelpers.GetAdminTokenAsync(_client);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Create location
        var location = new Location { Name = "Campus", City = "Groningen" };
        var locationResponse = await _client.PostAsJsonAsync("/api/location", location);
        var createdLocation = await locationResponse.Content.ReadFromJsonAsync<Location>();

        // Create building
        var building = new Building
        {
            Name = "Building A",
            Code = "A",
            LocationId = createdLocation!.Id
        };
        await _client.PostAsJsonAsync("/api/building", building);

        // Act
        var response = await _client.GetAsync($"/api/location/{createdLocation.Id}/buildings");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var buildings = await response.Content.ReadFromJsonAsync<List<Building>>();
        Assert.NotNull(buildings);
        Assert.Single(buildings);
        Assert.Equal("Building A", buildings[0].Name);
    }
}
