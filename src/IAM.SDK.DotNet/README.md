# IAM.SDK.DotNet

.NET SDK for integrating with the IAM authentication system.

## Installation

```bash
dotnet add package IAM.SDK.DotNet
```

## Usage

### 1. Register in Startup/Program.cs

```csharp
builder.Services.AddIamClient("https://iam.yourdomain.com");
```

### 2. Inject and Use

```csharp
public class MyController : ControllerBase
{
    private readonly IIamAuthClient _iamClient;

    public MyController(IIamAuthClient iamClient)
    {
        _iamClient = iamClient;
    }

    public async Task<IActionResult> Login(string email, string password)
    {
        var response = await _iamClient.LoginAsync(email, password);
        // Store tokens, redirect user
        return Ok(response);
    }
}
```

## Features

- ✅ Simple authentication (email/password)
- ✅ Token management (access + refresh)
- ✅ Automatic token refresh
- ✅ Dependency injection ready
- ✅ Strongly typed
- ✅ Async/await support

## Examples

See `examples/` directory for complete integration examples.
