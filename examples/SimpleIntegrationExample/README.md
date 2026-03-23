# Simple IAM SDK Integration Example

Minimal example showing how to integrate IAM.SDK.DotNet into an ASP.NET Core application.

## Setup

1. Make sure IAM.API is running on https://localhost:5001
2. Run this example:

```bash
dotnet run
```

## Endpoints

### POST /login
Login with email and password:

```bash
curl -X POST http://localhost:5000/login \
  -H "Content-Type: application/json" \
  -d '{"email":"admin@test.com","password":"Admin123!"}'
```

Response:
```json
{
  "message": "Login successful!",
  "user": {
    "email": "admin@test.com",
    "firstName": "Admin",
    "lastName": "User"
  },
  "accessToken": "eyJhbGc..."
}
```

### GET /me
Get current authenticated user:

```bash
curl http://localhost:5000/me \
  -H "Authorization: Bearer YOUR_TOKEN_HERE"
```

### POST /logout
Logout:

```bash
curl -X POST http://localhost:5000/logout
```

## Integration Code

The entire integration is just 3 lines:

```csharp
// 1. Add SDK to services
builder.Services.AddIamClient("https://localhost:5001");

// 2. Inject in endpoint
app.MapPost("/login", async (IIamAuthClient iamClient, LoginDto dto) =>
{
    // 3. Use it!
    var response = await iamClient.LoginAsync(dto.Email, dto.Password);
    return Results.Ok(response);
});
```

**That's it!** The SDK handles all the HTTP communication, token management, and error handling.

## Next Steps

For production applications, you'd typically:
1. Store tokens in cookies or secure storage
2. Add proper error handling
3. Implement token refresh logic
4. Add authorization middleware

See the main IAM.SDK.DotNet documentation for advanced usage.
