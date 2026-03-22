# Phase 1: Core IAM Foundation - Implementation Plan

**Timeline:** 4-6 weeks
**Goal:** Production-ready OAuth2/OIDC authentication server with RBAC and SSO capability

---

## Week 1: Foundation & Infrastructure

### Day 1-2: Project Setup
- ✅ Create repository structure
- ✅ Setup .NET solution file
- ✅ Create projects: IAM.Core, IAM.Infrastructure, IAM.API
- ✅ Configure NuGet packages:
  - IdentityServer4 or OpenIddict (OAuth2/OIDC)
  - EF Core 9.0 + Npgsql
  - BCrypt.Net for password hashing
  - StackExchange.Redis for caching
  - Serilog for logging
- ✅ Setup Docker Compose (PostgreSQL + Redis)
- ✅ Configure GitHub Actions CI/CD

**Deliverables:**
- Buildable solution
- Running Docker environment
- CI pipeline passing

### Day 3-5: Database Schema & Migrations
- ✅ Design core tables:
  - Users (Id, Email, PasswordHash, EmailConfirmed, TwoFactorEnabled, etc.)
  - Tenants (Id, Name, Slug, ParentTenantId, Settings)
  - Roles (Id, TenantId, Name, IsSystemRole, Permissions)
  - UserRoles (UserId, RoleId, TenantId, GrantedAt, ExpiresAt)
  - Permissions (Id, Resource, Action, Description)
  - RolePermissions (RoleId, PermissionId)
  - Delegations (DelegatorId, DelegateeId, RoleId, TenantId, ExpiresAt)
  - AuditLogs (UserId, Action, Resource, Details, IpAddress, CreatedAt)
  - RefreshTokens (UserId, Token, ExpiresAt, RevokedAt)
- ✅ Create EF Core DbContext
- ✅ Configure FluentAPI relationships
- ✅ Create initial migration: `20260322_InitialIAMSchema`
- ✅ Seed system roles and permissions

**Deliverables:**
- Database schema created
- Seed data script
- Migration runs successfully

---

## Week 2: Authentication Engine

### Day 6-7: OAuth2/OIDC Setup
- ✅ Configure OpenIddict or IdentityServer4
- ✅ Implement OAuth2 flows:
  - Authorization Code Flow (web apps)
  - Refresh Token Flow
  - Client Credentials Flow (service-to-service)
- ✅ Setup JWT token generation (RS256 signing)
- ✅ Configure `.well-known/openid-configuration` endpoint
- ✅ Implement PKCE (for mobile apps)

**Endpoints:**
- `/oauth/authorize`
- `/oauth/token`
- `/oauth/introspect`
- `/oauth/revoke`
- `/oauth/userinfo`
- `/.well-known/openid-configuration`

### Day 8-10: User Management API
- ✅ POST `/api/auth/register`
  - Validate email uniqueness
  - Hash password (BCrypt, 12 rounds)
  - Generate email verification token
  - Send verification email (SendGrid or SMTP)
  - Response: User object (no token yet)

- ✅ POST `/api/auth/verify-email`
  - Validate token
  - Mark email as confirmed
  - Response: Access token + Refresh token

- ✅ POST `/api/auth/login`
  - Validate credentials
  - Check email confirmed
  - Optional: MFA code verification
  - Generate JWT access token (15min expiry)
  - Generate refresh token (7 days expiry)
  - Set HttpOnly cookie for refresh token
  - Log successful login (audit)
  - Response: Tokens + user info

- ✅ POST `/api/auth/refresh`
  - Validate refresh token (from cookie)
  - Check not revoked
  - Generate new access token
  - Optional: Rotate refresh token
  - Response: New access token

- ✅ POST `/api/auth/logout`
  - Revoke refresh token
  - Clear cookies
  - Log logout event
  - Response: 200 OK

- ✅ POST `/api/auth/forgot-password`
  - Generate password reset token
  - Send reset email
  - Response: 200 OK (always, for security)

- ✅ POST `/api/auth/reset-password`
  - Validate reset token
  - Hash new password
  - Invalidate all existing refresh tokens
  - Response: 200 OK

**Deliverables:**
- Complete authentication flow working
- Email verification functional
- Password reset working
- JWT tokens validated

---

## Week 3: Authorization & RBAC

### Day 11-12: RBAC Data Model
- ✅ Implement Role entity with permissions (JSONB array)
- ✅ Create permission constants:
  ```csharp
  public static class Permissions
  {
      public const string BuildingView = "Building.View";
      public const string BuildingManage = "Building.Manage";
      public const string HVACControl = "HVAC.Control";
      public const string UserInvite = "User.Invite";
      // ... etc
  }
  ```
- ✅ Seed system roles:
  - SuperAdmin (all permissions)
  - BuildingOwner
  - BuildingManager
  - RealEstateAgent
  - Contractor
  - Resident

### Day 13-15: RBAC API
- ✅ POST `/api/roles` (Admin only)
  - Create role with permissions
  - Optional: TenantId for tenant-specific roles

- ✅ GET `/api/roles?tenantId={id}`
  - List all roles
  - Filter by tenant
  - Include permission count

- ✅ POST `/api/users/{userId}/roles`
  - Assign role to user
  - Optional: TenantId (role context)
  - Optional: ExpiresAt (time-limited)
  - Log in audit trail

- ✅ DELETE `/api/users/{userId}/roles/{roleId}`
  - Remove role assignment
  - Log in audit trail

- ✅ GET `/api/users/{userId}/permissions?tenantId={id}`
  - Aggregate all permissions from user's roles
  - Filter by tenant context
  - Cache result in Redis (15min TTL)

- ✅ GET `/api/users/me/permissions`
  - Get current user's permissions
  - Used by frontend to hide/show features

### Day 16-17: Authorization Middleware
- ✅ Custom authorization policy provider
- ✅ Permission requirement: `[Authorize(Policy = "RequirePermission:Building.Manage")]`
- ✅ Permission handler:
  ```csharp
  public class PermissionHandler : AuthorizationHandler<PermissionRequirement>
  {
      protected override async Task HandleRequirementAsync(...)
      {
          // Get userId from claims
          // Check Redis cache for permissions
          // If not cached, query database and cache
          // Check if user has required permission
          // context.Succeed(requirement) if authorized
      }
  }
  ```
- ✅ Tenant context middleware (set `CurrentTenantId` from header/claim)
- ✅ Cache invalidation on role/permission changes

**Deliverables:**
- RBAC fully functional
- Authorization middleware working
- Permissions cached efficiently

---

## Week 4: Admin UI & SDKs

### Day 18-20: React Admin Portal
- ✅ Create Vite + React project
- ✅ Setup Tailwind CSS + shadcn/ui
- ✅ Configure React Router
- ✅ Setup TanStack Query (data fetching)
- ✅ Implement authentication:
  - Login page
  - SSO login via our own IAM (dogfooding!)
  - Protected routes

**Pages:**
1. **Users List**
   - Table with: Email, Name, Roles, Status, Last Login
   - Search by email/name
   - Filter by role, status
   - Pagination (50/page)
   - Actions: Edit, Deactivate, Reset Password

2. **User Details**
   - Profile tab: Name, Email, Phone, Status
   - Roles tab: Assigned roles per tenant, Add/Remove role
   - Permissions tab: All permissions (read-only, aggregated)
   - Activity tab: Recent actions from audit log
   - MFA tab: Enable/disable, view backup codes

3. **Roles Management**
   - List all roles
   - Create new role
   - Edit role permissions
   - Delete role (if not system role)
   - View users with this role

4. **Audit Log Viewer**
   - Table: Timestamp, User, Action, Resource, IP Address
   - Filter by user, action, date range
   - Export to CSV
   - Real-time updates via SignalR

**Components:**
- `<DataTable>` (reusable)
- `<PermissionCheckbox>` (for role editor)
- `<UserRoleAssignment>` (modal)
- `<AuditLogStream>` (with SignalR)

### Day 21-22: .NET SDK (NuGet Package)
- ✅ Create `IAM.SDK.DotNet` project (Class Library)
- ✅ Target: net8.0, net9.0 (multi-targeting)
- ✅ Package metadata (Name, Description, Version, Authors)

**Features:**
- `AddIamAuthentication()` extension method:
  ```csharp
  services.AddIamAuthentication(options =>
  {
      options.Authority = "https://iam.ourcompany.com";
      options.Audience = "bliek-api";
      options.RequireHttpsMetadata = true;
  });
  ```
- `[RequirePermission("Building.Manage")]` attribute
- `ICurrentUser` service (get current user ID, email, tenant)
- `IPermissionChecker` service:
  ```csharp
  var hasPermission = await _permissionChecker.HasPermissionAsync("Building.Manage");
  ```
- Token refresh interceptor (auto-refresh on 401)
- Audit log helper (auto-log actions)

**Packaging:**
- NuGet package: `OurCompany.IAM.SDK`
- Version: 1.0.0-beta1
- Publish to nuget.org or private feed

### Day 23-24: React SDK (NPM Package)
- ✅ Create `IAM.SDK.JavaScript` project
- ✅ TypeScript + React
- ✅ Bundle with Rollup

**Features:**
- `<AuthProvider>` context provider
- `useAuth()` hook:
  ```javascript
  const { user, login, logout, isAuthenticated } = useAuth();
  ```
- `usePermissions()` hook:
  ```javascript
  const { hasPermission } = usePermissions();
  if (hasPermission('Building.Manage')) { ... }
  ```
- `<ProtectedRoute>` component:
  ```javascript
  <ProtectedRoute permission="Building.View">
    <Dashboard />
  </ProtectedRoute>
  ```
- `<RequirePermission>` component:
  ```javascript
  <RequirePermission permission="User.Invite">
    <InviteUserButton />
  </RequirePermission>
  ```
- Automatic token refresh
- Secure token storage (HttpOnly cookies)

**Packaging:**
- NPM package: `@ourcompany/iam-react`
- Version: 1.0.0-beta1
- Publish to npm or private registry

**Deliverables:**
- Admin UI fully functional
- .NET SDK published
- React SDK published
- SDKs tested with sample apps

---

## Week 5-6: Integration & Testing

### Day 25-27: SSO Integration with Bliek
- ✅ Update Bliek backend to use `.NET SDK`
- ✅ Update Bliek frontend to use `@ourcompany/iam-react`
- ✅ Configure Bliek as OAuth2 client
- ✅ Test full SSO flow:
  1. User visits Bliek
  2. Redirected to IAM login
  3. User logs in
  4. Redirected back to Bliek with auth code
  5. Bliek exchanges code for tokens
  6. User accesses Bliek features
- ✅ Test logout (single logout)
- ✅ Test token refresh

### Day 28-29: SSO Integration with CodeHub
- ✅ Update CodeHub backend/frontend
- ✅ Configure CodeHub as OAuth2 client
- ✅ Test SSO flow
- ✅ **Cross-app SSO test:**
  - Login to Bliek
  - Navigate to CodeHub
  - Should be automatically logged in (no second login)

### Day 30: Testing & Bug Fixes
- ✅ Unit tests (target 80% coverage):
  - IAM.Core.Tests
  - IAM.Infrastructure.Tests
  - IAM.API.Tests
- ✅ Integration tests:
  - Full auth flow (register → verify → login → refresh → logout)
  - Role assignment flow
  - Permission checking
  - Audit logging
- ✅ E2E tests (Playwright):
  - Admin UI workflows
  - SSO login from Bliek
  - SSO login from CodeHub
- ✅ Security testing:
  - SQL injection attempts
  - XSS attempts
  - CSRF protection
  - Token tampering
  - Brute force protection (rate limiting)
- ✅ Performance testing:
  - Auth response time <100ms
  - Permission check <10ms (cached)
  - 1000 concurrent logins
- ✅ Fix all critical/high bugs

---

## Success Criteria

**Phase 1 is COMPLETE when:**

✅ **Authentication**
- User can register with email
- Email verification working
- User can login with email/password
- Password reset functional
- JWT tokens generated correctly
- Refresh tokens working
- Logout revokes tokens

✅ **Authorization**
- Roles can be created/edited/deleted
- Permissions can be assigned to roles
- Users can be assigned roles
- Permission checking works via middleware
- Permissions cached in Redis
- Cache invalidates on role changes

✅ **SSO**
- OAuth2/OIDC server running
- `.well-known/openid-configuration` accessible
- At least 2 apps integrated (Bliek + CodeHub)
- Cross-app SSO working (login once, access both)
- Single logout working

✅ **Admin UI**
- Can manage users (create, edit, deactivate, reset password)
- Can manage roles (create, edit, delete, assign permissions)
- Can view audit logs (filter, search, export)
- Real-time updates working (SignalR)

✅ **SDKs**
- .NET SDK published to NuGet
- React SDK published to NPM
- Both SDKs tested and working

✅ **Security**
- All passwords hashed with BCrypt
- HTTPS enforced
- CORS configured correctly
- Rate limiting on login endpoint
- SQL injection protected (parameterized queries)
- XSS protected (React auto-escapes)
- CSRF protection (SameSite cookies)

✅ **Quality**
- 80%+ code coverage
- All integration tests passing
- E2E tests passing
- No critical/high bugs
- Performance benchmarks met

✅ **Documentation**
- API documentation complete
- SDK usage guides written
- Deployment guide created
- Architecture diagram updated

---

## Next Steps (Phase 2 Preview)

After Phase 1 completes, we'll move to Phase 2:

1. **Multi-Factor Authentication**
   - TOTP (Google Authenticator)
   - SMS via Twilio
   - WebAuthn/FIDO2

2. **Active Directory Integration**
   - LDAP sync
   - Group → Role mapping
   - Kerberos authentication

3. **Building Management Platform**
   - Building/Floor/Room data model
   - IoT device registry
   - Real Estate Agent portal (integrate with Bliek)

4. **Advanced Features**
   - Delegation workflows
   - Approval workflows
   - Access certification

---

## Risk Mitigation

**Risks:**

1. **OAuth2 complexity** → Use OpenIddict (simpler than IdentityServer4)
2. **Performance issues** → Implement Redis caching early
3. **Security vulnerabilities** → Regular security audits, penetration testing
4. **Scope creep** → Strict adherence to Phase 1 tasks only
5. **Integration challenges** → Test SSO integration early (Week 5)

**Mitigation:**
- Daily standups to track progress
- Weekly demos of working features
- Continuous deployment to staging environment
- Automated testing (CI/CD)
- Code reviews before merge

---

## Team & Resources

**Development:**
- Backend: 1 developer (focus on IAM.API, database)
- Frontend: 1 developer (focus on Admin UI, React SDK)
- Full-stack: 1 developer (helps both, focuses on SDKs + integration)

**Tools:**
- GitHub (version control)
- ClickUp (task tracking)
- Figma (UI design)
- Postman (API testing)
- Docker (local development)

**Budget:**
- $0/month (all open-source tools)
- SendGrid free tier (email)
- GitHub Actions free tier (CI/CD)
- PostgreSQL/Redis self-hosted

---

**Let's build the future of identity management! 🚀**
