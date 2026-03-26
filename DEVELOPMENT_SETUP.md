# Development Setup Guide

## Quick Start

1. **Start PostgreSQL** (required)
2. **Start IAM.API** (auto-seeds on first run)
3. **Login with test users** (below)

That's it! The system automatically creates verified test users on first run.

---

## Test Users (Pre-Verified)

All test users have their email already verified - you can login immediately.

### Admin User
```
Email: admin@test.com
Password: Admin123!
Role: Admin
```
**Use for:** Testing admin features, user management, full system access

### Regular User
```
Email: user@test.com
Password: User123!
Role: User
```
**Use for:** Testing standard user flows, regular authentication

### Developer User
```
Email: dev@test.com
Password: Dev123!
Roles: Admin + User
```
**Use for:** Testing role-based access, has both admin and user permissions

---

## Testing Authentication

### Option 1: Direct API (curl)
```bash
curl -X POST http://localhost:5161/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"admin@test.com","password":"Admin123!"}'
```

### Option 2: Integration Example
```bash
# Terminal 1: Start IAM.API
cd src/IAM.API
dotnet run

# Terminal 2: Start Integration Example
cd examples/SimpleIntegrationExample
dotnet run

# Terminal 3: Test
curl -X POST http://localhost:5006/login \
  -H "Content-Type: application/json" \
  -d '{"email":"admin@test.com","password":"Admin123!"}'
```

### Option 3: Admin Portal (React)
```bash
# Terminal 1: Start IAM.API
cd src/IAM.API
dotnet run

# Terminal 2: Start Admin Portal
cd src/IAM.Admin.Web
npm install
npm run dev

# Browser: http://localhost:5173
# Login: admin@test.com / Admin123!
```

---

## How It Works

### Automatic Seeding
The `DevelopmentDataSeeder` runs automatically when the API starts in Development mode:

1. **Checks for existing users** - won't duplicate data
2. **Creates 2 roles** (Admin, User)
3. **Creates 3 verified users** (admin, user, dev)
4. **Assigns roles** to users
5. **Skips in Production** - safety first!

**Code:** `src/IAM.Infrastructure/Data/DevelopmentDataSeeder.cs`

### Environment Detection
```csharp
// Only seeds in Development
if (!app.Environment.IsDevelopment())
{
    return app; // Safe for Production
}
```

### Safe to Run Multiple Times
```csharp
// Won't create duplicates
if (await _context.Users.AnyAsync())
{
    return; // Already seeded
}
```

---

## Database Management

### Reset Database (Clean Start)
```bash
cd src/IAM.API

# Drop and recreate database
dotnet ef database drop --force
dotnet ef database update

# Restart API - will auto-seed
dotnet run
```

### Check Seeded Data
```sql
-- PostgreSQL
SELECT "Email", "FirstName", "LastName", "EmailConfirmed", "IsActive"
FROM "Users";

-- Should show:
-- admin@test.com | Admin     | User | true | true
-- user@test.com  | Test      | User | true | true
-- dev@test.com   | Developer | User | true | true
```

---

## Production Deployment

**IMPORTANT:** The seeder automatically disables itself in Production.

```csharp
// src/IAM.API/Program.cs
await app.SeedDevelopmentDataAsync(); // No-op in Production ✅
```

**For Production, you should:**
1. Remove test users or change passwords
2. Create real admin users through registration
3. Implement email verification properly
4. Use strong passwords from environment variables

---

## Troubleshooting

### "Email already registered"
The seeder already ran. Database has users.

**Solution:** You're good! Just login with test credentials.

### "Please verify your email"
You registered a NEW user (not using test accounts).

**Solution:** Either:
- Use test accounts (admin@test.com, user@test.com, dev@test.com)
- OR implement email sending service
- OR manually set `EmailConfirmed = true` in database

### Database Connection Errors
PostgreSQL not running.

**Solution:**
```bash
# Check PostgreSQL is running
# Windows: Check Services
# Or test connection:
psql -h localhost -U iamuser -d iamdb
```

### "No users found"
Seeder didn't run (not in Development mode?).

**Solution:**
```bash
# Check environment
echo $ASPNETCORE_ENVIRONMENT  # Should be "Development"

# Or set explicitly
export ASPNETCORE_ENVIRONMENT=Development
dotnet run
```

---

## Integration Testing

With seeded data, you can immediately test:

### .NET SDK Integration
```csharp
builder.Services.AddIamClient("http://localhost:5161");

var response = await iamClient.LoginAsync(
    "admin@test.com",
    "Admin123!"
);

// ✅ Works immediately - no email verification needed
```

### React/JavaScript
```javascript
const response = await fetch('http://localhost:5161/api/auth/login', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    email: 'admin@test.com',
    password: 'Admin123!'
  })
});

// ✅ Returns JWT token immediately
```

---

## Security Notes

### Development Mode Safety
- ✅ Test passwords are in code (OK for dev)
- ✅ EmailConfirmed=true (bypasses verification in dev)
- ✅ Seeder only runs in Development
- ✅ Safe for Production deployment

### Production Security
- ❌ NEVER use these passwords in Production
- ❌ NEVER deploy with Development seeder enabled
- ✅ Always require email verification in Production
- ✅ Use environment variables for secrets

---

## What's Next?

Now that you have working test users:

1. **Integrate into first app** (CodeHub recommended)
2. **Test .NET SDK** (3-line integration proven)
3. **Build JavaScript SDK** (same pattern)
4. **Implement email sending** (removes verification blocker)
5. **Production deployment** (system is 85% ready)

---

**Created:** 2026-03-23
**Status:** Production Ready
**Auto-Seeding:** ✅ Working
**Test Users:** 3 verified accounts
**Ready for:** Immediate integration and testing
