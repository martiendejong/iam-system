# End-to-End IAM System Test

## Test Date: 2026-03-22

### Test Scenario
Verify the complete authentication flow from SimpleIntegrationExample → IAM.SDK.DotNet → IAM.API → Database

### Setup

1. **Prerequisites Check**
   - ✅ PostgreSQL running on port 5432
   - ❌ Redis NOT running on port 6379 (appears to be optional)
   - ✅ Database migrations applied
   - ✅ IAM.API builds successfully
   - ✅ IAM.SDK.DotNet builds successfully
   - ✅ SimpleIntegrationExample builds successfully

2. **API Configuration**
   - Running on: http://localhost:5161 (from launchSettings.json)
   - Database: iamdb on localhost:5432
   - JWT configured with development secret

### Test Steps

#### Step 1: Start IAM.API
```bash
cd E:\projects\iam-system\src\IAM.API
dotnet run
# Expected: API starts on http://localhost:5161
```

#### Step 2: Test Login Endpoint Directly
```bash
curl -X POST http://localhost:5161/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"admin@test.com","password":"Admin123!"}'
```

**Expected Response:**
```json
{
  "accessToken": "eyJhbGc...",
  "refreshToken": "...",
  "user": {
    "email": "admin@test.com",
    "firstName": "Admin",
    "lastName": "User"
  }
}
```

#### Step 3: Update SimpleIntegrationExample Configuration
Update Program.cs to use correct URL:
```csharp
builder.Services.AddIamClient("http://localhost:5161");
```

#### Step 4: Run SimpleIntegrationExample
```bash
cd E:\projects\iam-system\examples\SimpleIntegrationExample
dotnet run
# Expected: Example app starts on http://localhost:5000
```

#### Step 5: Test Integration Example
```bash
curl -X POST http://localhost:5000/login \
  -H "Content-Type: application/json" \
  -d '{"email":"admin@test.com","password":"Admin123!"}'
```

**Expected:** Same response as Step 2, proving the SDK works!

#### Step 6: Test Protected Endpoint
```bash
# Use token from step 5
curl http://localhost:5000/me \
  -H "Authorization: Bearer <token-from-step-5>"
```

**Expected:** User details returned

### Current Status
- API starts successfully on port 5161
- OpenIddict initialization working
- Database connectivity confirmed
- Ready for Step 2: Test admin user exists

### Next Action
Check if default admin user exists in database, or create seed data.
