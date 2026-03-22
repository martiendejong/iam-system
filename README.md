# 🏢 Enterprise IAM & Smart Building Management System

> **Mission:** Build the world's most advanced Identity & Access Management system combined with an IoT-enabled Smart Building platform that surpasses Microsoft Azure AD, Google IAM, and Auth0.

## 🎯 Vision

A unified SSO/IAM system that powers:
- **All our applications** (Bliek, CodeHub, SEO God, Personality Test, DataDrivenAI, and future apps)
- **Smart Building Management** with AI-powered analytics
- **10+ user types** from real estate agents to building residents
- **IoT device integration** with MQTT streaming and real-time control
- **Enterprise-grade security** with MFA, AD/LDAP, passwordless auth, and Zero Trust

## 🏗️ Architecture

### Tech Stack
- **Backend:** ASP.NET Core 9.0 (.NET 9)
- **Database:** PostgreSQL 16 + Redis 7
- **Frontend:** React 19 + Vite + Tailwind CSS
- **Mobile:** React Native (iOS + Android)
- **IoT:** MQTT, SignalR (real-time)
- **AI/ML:** Python (TensorFlow/PyTorch)

### Project Structure
```
iam-system/
├── src/
│   ├── IAM.Core/              # Domain models, interfaces
│   ├── IAM.Infrastructure/    # Database, external integrations
│   ├── IAM.API/              # REST API (OAuth2/OIDC server)
│   ├── IAM.Admin.Web/        # Admin portal (React)
│   ├── IAM.SDK.DotNet/       # .NET client SDK (NuGet)
│   ├── IAM.SDK.JavaScript/   # JS/React client SDK (NPM)
│   ├── Building.Core/        # Building management domain
│   ├── Building.IoT/         # IoT device integration
│   ├── Building.Analytics/   # AI analytics service
│   └── Building.Web/         # Building management portal
├── tests/
│   ├── IAM.Core.Tests/
│   ├── IAM.API.Tests/
│   ├── IAM.Integration.Tests/
│   └── Building.Tests/
├── docs/
│   ├── architecture.md
│   ├── api-reference.md
│   ├── deployment.md
│   └── phase1-plan.md
└── docker/
    ├── Dockerfile.api
    ├── Dockerfile.admin
    └── docker-compose.yml
```

## 🔐 Core IAM Features

### Authentication
- ✅ OAuth2 / OpenID Connect
- ✅ JWT tokens (access + refresh)
- ✅ Multi-Factor Authentication (TOTP, SMS, Email, Push, FIDO2)
- ✅ Passwordless authentication (Magic links, WebAuthn, QR codes)
- ✅ Social login (Google, Microsoft, Apple)
- ✅ SAML 2.0 (enterprise SSO)

### Authorization
- ✅ Advanced RBAC (role hierarchy, delegation, SoD)
- ✅ Resource-based permissions (Building.Read, HVAC.Control, etc.)
- ✅ Contextual permissions (per-building, per-floor, per-device)
- ✅ Time-limited access (contractors, temporary staff)
- ✅ Just-In-Time (JIT) access provisioning

### Multi-Tenancy
- ✅ Hierarchical tenants (Building → Floor → Room → Device)
- ✅ Data isolation (tenant-scoped queries)
- ✅ Cross-tenant delegation (contractors working on multiple buildings)
- ✅ Tenant-specific configuration

### Enterprise Integration
- ✅ Active Directory / LDAP sync
- ✅ Kerberos authentication
- ✅ Group → Role mapping
- ✅ Multi-forest support

### Audit & Compliance
- ✅ Immutable audit log
- ✅ SIEM export (Splunk, Elastic)
- ✅ Compliance reports (GDPR, SOC2, ISO 27001)
- ✅ Tamper detection

## 🏢 Smart Building Features

### User Types
1. **Real Estate Agent** - Property listings, showings, client CRM
2. **Consumer/Buyer** - Browse properties, schedule viewings
3. **Seller** - Manage property listing
4. **Building Owner** - Asset management, financial overview
5. **Building Manager** - Operations, maintenance
6. **Contractor** - Work orders, time-limited access
7. **Cleaning Company** - Access schedules
8. **Security** - Monitoring, access control
9. **Staff** - Building operations
10. **Residents** - Tenant access, amenities

### IoT Integration
- ✅ HVAC systems
- ✅ Access control (doors, gates, elevators)
- ✅ Sensors (temperature, humidity, CO2, occupancy)
- ✅ Security cameras
- ✅ Lighting systems
- ✅ Energy/water/gas meters
- ✅ Fire & safety systems

### AI Analytics
- ✅ Energy optimization (HVAC scheduling, cost forecasting)
- ✅ Predictive maintenance (device failure prediction)
- ✅ Occupancy analytics (space utilization, meeting room optimization)
- ✅ Security intelligence (unusual activity detection, video analytics)
- ✅ Cost analytics (budget forecasting, ROI analysis)

## 📱 Applications

### Web Portals
- **Real Estate Agent Portal** (integrates with Bliek)
- **Building Owner Dashboard**
- **Building Manager Portal**
- **Consumer App** (property search)
- **Seller Portal**
- **Contractor Portal**
- **Resident App**

### Mobile Apps
- **React Native** (iOS + Android)
- QR code access (doors, parking)
- Push notifications
- Offline mode
- Biometric authentication

### Admin Panel
- User management
- Role/permission configuration
- Tenant management
- Audit log viewer
- Analytics dashboard
- System configuration

## 🚀 Phase 1: Core IAM Foundation (NOW)

**Goal:** Build foundational IAM system with SSO capability.

**Timeline:** 4-6 weeks

**Deliverables:**
1. ✅ Repository setup + project structure
2. ✅ PostgreSQL database schema (Users, Tenants, Roles, Permissions, AuditLogs)
3. ✅ OAuth2/OIDC authentication API
4. ✅ JWT token management (access + refresh)
5. ✅ User registration + login (email verification)
6. ✅ RBAC authorization API
7. ✅ Custom authorization middleware
8. ✅ Admin UI dashboard (React)
9. ✅ .NET SDK (NuGet package)
10. ✅ React SDK (NPM package)

**Success Criteria:**
- User can register, verify email, login
- SSO works for at least 2 applications (Bliek + CodeHub)
- Admin can manage users, roles, permissions
- Full audit trail of all actions

## 📅 Future Phases

### Phase 2: Advanced IAM & Building Platform (Weeks 7-12)
- MFA (TOTP, SMS, WebAuthn)
- Active Directory integration
- Building/Floor/Room data model
- IoT device registry
- MQTT broker setup
- Real Estate Agent portal

### Phase 3: IoT & AI Integration (Weeks 13-18)
- HVAC device integration
- Real-time data streaming
- AI analytics engine
- Mobile apps (React Native)
- Consumer/Seller portals

### Phase 4: Enterprise Features (Weeks 19-24)
- Passwordless authentication
- SAML 2.0
- Adaptive authentication (risk-based MFA)
- Privileged Access Management
- Access certification workflows
- Zero Trust architecture

## 🔗 Integration with Existing Apps

All current applications will integrate via SSO:

1. **Bliek (Real Estate Agency AI)** - First integration target
2. **CodeHub** - Developer learning platform
3. **SEO God** - WordPress SEO automation
4. **Personality Test** - Assessment platform
5. **DataDrivenAI** - Event-driven orchestration
6. **Password Manager** - Credential vault
7. **WhatsApp Bridge** - Messaging integration

## 📊 Success Metrics

- **Security:** Zero breaches, 100% MFA adoption for admins
- **Performance:** <100ms auth response time, 99.99% uptime
- **Usability:** Single sign-on across all apps, <30s first-time setup
- **Scale:** Support 10,000+ users, 100+ buildings, 10,000+ IoT devices
- **Cost:** <$100/month infrastructure (vs $1,560/year for Auth0)
- **Compliance:** SOC 2 Type II certified within 12 months

## 🛠️ Development

### Prerequisites
- .NET 9 SDK
- Node.js 20+
- PostgreSQL 16
- Redis 7
- Docker (optional)

### Setup
```bash
# Clone repo
git clone https://github.com/martiendejong/iam-system.git
cd iam-system

# Backend
cd src/IAM.API
dotnet restore
dotnet ef database update
dotnet run

# Admin UI
cd src/IAM.Admin.Web
npm install
npm run dev

# Access
- API: https://localhost:5001
- Admin UI: http://localhost:5173
- OpenID Configuration: https://localhost:5001/.well-known/openid-configuration
```

### Testing
```bash
# Unit tests
dotnet test

# Integration tests
cd tests/IAM.Integration.Tests
dotnet test

# E2E tests
cd tests/IAM.E2E.Tests
npm test
```

## 📚 Documentation

- [Architecture Overview](docs/architecture.md)
- [API Reference](docs/api-reference.md)
- [Phase 1 Implementation Plan](docs/phase1-plan.md)
- [Deployment Guide](docs/deployment.md)
- [SDK Documentation](docs/sdk-guide.md)

## 🤝 Contributing

This is an internal project. Development workflow:
1. Create feature branch from `develop`
2. Implement feature + tests
3. Create PR to `develop`
4. After code review, merge to `develop`
5. Release branches merge to `main`

## 📄 License

Proprietary - All rights reserved.

---

**Built with ❤️ to revolutionize identity management and smart building operations.**
