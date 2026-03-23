using IAM.SDK.DotNet;

var builder = WebApplication.CreateBuilder(args);

// Add IAM SDK
builder.Services.AddIamClient("https://localhost:5001");

var app = builder.Build();

// Public endpoint
app.MapGet("/", () => "Simple IAM SDK Integration Example");

// Login endpoint
app.MapPost("/login", async (IIamAuthClient iamClient, LoginDto dto) =>
{
    try
    {
        var response = await iamClient.LoginAsync(dto.Email, dto.Password);
        return Results.Ok(new
        {
            message = "Login successful!",
            user = new
            {
                response.User.Email,
                response.User.FirstName,
                response.User.LastName
            },
            accessToken = response.AccessToken
        });
    }
    catch (Exception ex)
    {
        return Results.Unauthorized();
    }
});

// Get current user (protected)
app.MapGet("/me", async (IIamAuthClient iamClient) =>
{
    try
    {
        var user = await iamClient.GetCurrentUserAsync();
        return Results.Ok(user);
    }
    catch
    {
        return Results.Unauthorized();
    }
});

// Logout endpoint
app.MapPost("/logout", async (IIamAuthClient iamClient) =>
{
    await iamClient.LogoutAsync();
    return Results.Ok(new { message = "Logged out successfully" });
});

app.Run();

record LoginDto(string Email, string Password);
