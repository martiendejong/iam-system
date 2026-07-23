import { BrowserRouter as Router, Routes, Route, Navigate } from 'react-router-dom';
import { AuthProvider } from './context/AuthContext';
import LoginPage from './pages/auth/LoginPage';
import VerifyEmailPage from './pages/auth/VerifyEmailPage';
import ResetPasswordPage from './pages/auth/ResetPasswordPage';
import DashboardPage from './pages/DashboardPage';
import UsersPage from './pages/users/UsersPage';
import UserEditPage from './pages/users/UserEditPage';
import UserCreatePage from './pages/users/UserCreatePage';
import RolesPage from './pages/roles/RolesPage';
import RoleFormPage from './pages/roles/RoleFormPage';
import TenantsPage from './pages/tenants/TenantsPage';
import TenantFormPage from './pages/tenants/TenantFormPage';
import ClientsPage from './pages/oauth/ClientsPage';
import ClientFormPage from './pages/oauth/ClientFormPage';
import DevicesPage from './pages/devices/DevicesPage';
import DeviceDetailPage from './pages/devices/DeviceDetailPage';
import DeviceRegisterPage from './pages/devices/DeviceRegisterPage';
import PoliciesPage from './pages/policies/PoliciesPage';
import PolicyFormPage from './pages/policies/PolicyFormPage';
import PolicyTestPage from './pages/policies/PolicyTestPage';
import PolicyInheritanceTree from './components/policies/PolicyInheritanceTree';
import AuditLogPage from './pages/AuditLogPage';
import ComplianceDashboardPage from './pages/ComplianceDashboardPage';
import PortalProfilePage from './pages/portal/PortalProfilePage';
import PortalSecurityPage from './pages/portal/PortalSecurityPage';
import PortalSessionsPage from './pages/portal/PortalSessionsPage';
import PortalPasskeysPage from './pages/portal/PortalPasskeysPage';
import PortalActivityPage from './pages/portal/PortalActivityPage';
import IdentityProvidersPage from './pages/identity-providers/IdentityProvidersPage';
import IdentityProviderFormPage from './pages/identity-providers/IdentityProviderFormPage';
import ConsentManagementPage from './pages/consent/ConsentManagementPage';
import PortalPrivacyPage from './pages/portal/PortalPrivacyPage';
import PortalLinkedAccountsPage from './pages/portal/PortalLinkedAccountsPage';
import InvitationsPage from './pages/invitations/InvitationsPage';
import AcceptInvitePage from './pages/invitations/AcceptInvitePage';
import DirectorySyncPage from './pages/directory-sync/DirectorySyncPage';
import AccessRequestsPage from './pages/workflows/AccessRequestsPage';
import WorkflowTemplatesPage from './pages/workflows/WorkflowTemplatesPage';
import ScimConfigPage from './pages/scim/ScimConfigPage';
import ClaimsMappingPage from './pages/claims/ClaimsMappingPage';
import TenantBrandingPage from './pages/branding/TenantBrandingPage';
import NetworkPolicyPage from './pages/network/NetworkPolicyPage';
import PamDashboardPage from './pages/pam/PamDashboardPage';
import BulkOperationsPage from './pages/bulk/BulkOperationsPage';
import RiskDashboardPage from './pages/risk/RiskDashboardPage';
import SecretsVaultPage from './pages/secrets/SecretsVaultPage';
import SecurityAlertsPage from './pages/alerts/SecurityAlertsPage';
import VisitorManagementPage from './pages/visitors/VisitorManagementPage';
import RegionDashboardPage from './pages/regions/RegionDashboardPage';
import ServiceAccountsPage from './pages/service-accounts/ServiceAccountsPage';
import DelegationPage from './pages/delegation/DelegationPage';
import ProtectedRoute from './components/common/ProtectedRoute';

function App() {
  return (
    <Router basename="/auth">
      <AuthProvider>
        <Routes>
          {/* Public routes */}
          <Route path="/login" element={<LoginPage />} />
          <Route path="/verify-email" element={<VerifyEmailPage />} />
          <Route path="/reset-password" element={<ResetPasswordPage />} />
          <Route path="/accept-invite" element={<AcceptInvitePage />} />

          {/* Protected routes */}
          <Route
            path="/dashboard"
            element={
              <ProtectedRoute>
                <DashboardPage />
              </ProtectedRoute>
            }
          />

          {/* User Management */}
          <Route
            path="/users"
            element={
              <ProtectedRoute>
                <UsersPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/users/new"
            element={
              <ProtectedRoute>
                <UserCreatePage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/users/:id"
            element={
              <ProtectedRoute>
                <UserEditPage />
              </ProtectedRoute>
            }
          />

          {/* Role Management */}
          <Route
            path="/roles"
            element={
              <ProtectedRoute>
                <RolesPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/roles/new"
            element={
              <ProtectedRoute>
                <RoleFormPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/roles/:id"
            element={
              <ProtectedRoute>
                <RoleFormPage />
              </ProtectedRoute>
            }
          />

          {/* Tenant Management */}
          <Route
            path="/tenants"
            element={
              <ProtectedRoute>
                <TenantsPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/tenants/new"
            element={
              <ProtectedRoute>
                <TenantFormPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/tenants/:id"
            element={
              <ProtectedRoute>
                <TenantFormPage />
              </ProtectedRoute>
            }
          />

          {/* OAuth2 Client Management */}
          <Route
            path="/oauth/clients"
            element={
              <ProtectedRoute>
                <ClientsPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/oauth/clients/new"
            element={
              <ProtectedRoute>
                <ClientFormPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/oauth/clients/:id"
            element={
              <ProtectedRoute>
                <ClientFormPage />
              </ProtectedRoute>
            }
          />

          {/* Device Management */}
          <Route
            path="/devices"
            element={
              <ProtectedRoute>
                <DevicesPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/devices/register"
            element={
              <ProtectedRoute>
                <DeviceRegisterPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/devices/:id"
            element={
              <ProtectedRoute>
                <DeviceDetailPage />
              </ProtectedRoute>
            }
          />

          {/* Policy Management */}
          <Route
            path="/policies"
            element={
              <ProtectedRoute>
                <PoliciesPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/policies/new"
            element={
              <ProtectedRoute>
                <PolicyFormPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/policies/test"
            element={
              <ProtectedRoute>
                <PolicyTestPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/policies/inheritance"
            element={
              <ProtectedRoute>
                <PolicyInheritanceTree />
              </ProtectedRoute>
            }
          />
          <Route
            path="/policies/:id"
            element={
              <ProtectedRoute>
                <PolicyFormPage />
              </ProtectedRoute>
            }
          />

          {/* Identity Providers */}
          <Route
            path="/identity-providers"
            element={
              <ProtectedRoute>
                <IdentityProvidersPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/identity-providers/new"
            element={
              <ProtectedRoute>
                <IdentityProviderFormPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/identity-providers/:id"
            element={
              <ProtectedRoute>
                <IdentityProviderFormPage />
              </ProtectedRoute>
            }
          />

          {/* Audit & Compliance */}
          <Route
            path="/audit"
            element={
              <ProtectedRoute>
                <AuditLogPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/compliance"
            element={
              <ProtectedRoute>
                <ComplianceDashboardPage />
              </ProtectedRoute>
            }
          />

          {/* Invitations & Organization */}
          <Route
            path="/invitations"
            element={
              <ProtectedRoute>
                <InvitationsPage />
              </ProtectedRoute>
            }
          />

          {/* Directory Sync (LDAP/AD) */}
          <Route
            path="/directory-sync"
            element={
              <ProtectedRoute>
                <DirectorySyncPage />
              </ProtectedRoute>
            }
          />

          {/* Access Requests & Workflows */}
          <Route
            path="/access-requests"
            element={
              <ProtectedRoute>
                <AccessRequestsPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/workflow-templates"
            element={
              <ProtectedRoute>
                <WorkflowTemplatesPage />
              </ProtectedRoute>
            }
          />

          {/* SCIM Provisioning */}
          <Route
            path="/scim"
            element={
              <ProtectedRoute>
                <ScimConfigPage />
              </ProtectedRoute>
            }
          />

          {/* Claims Mapping & Token Configuration */}
          <Route
            path="/claims-mapping"
            element={
              <ProtectedRoute>
                <ClaimsMappingPage />
              </ProtectedRoute>
            }
          />

          {/* Tenant Branding */}
          <Route
            path="/branding"
            element={
              <ProtectedRoute>
                <TenantBrandingPage />
              </ProtectedRoute>
            }
          />

          {/* Network Policy (IP Allowlist, Geo Restrictions, Geofencing) */}
          <Route
            path="/network-policy"
            element={
              <ProtectedRoute>
                <NetworkPolicyPage />
              </ProtectedRoute>
            }
          />

          {/* Privileged Access Management (PAM) */}
          <Route
            path="/pam"
            element={
              <ProtectedRoute>
                <PamDashboardPage />
              </ProtectedRoute>
            }
          />

          {/* Bulk Operations */}
          <Route
            path="/bulk-operations"
            element={
              <ProtectedRoute>
                <BulkOperationsPage />
              </ProtectedRoute>
            }
          />

          {/* Consent & GDPR Management */}
          <Route
            path="/consent"
            element={
              <ProtectedRoute>
                <ConsentManagementPage />
              </ProtectedRoute>
            }
          />

          {/* User Self-Service Portal */}
          <Route path="/portal" element={<Navigate to="/portal/profile" replace />} />
          <Route
            path="/portal/profile"
            element={
              <ProtectedRoute>
                <PortalProfilePage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/portal/security"
            element={
              <ProtectedRoute>
                <PortalSecurityPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/portal/sessions"
            element={
              <ProtectedRoute>
                <PortalSessionsPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/portal/passkeys"
            element={
              <ProtectedRoute>
                <PortalPasskeysPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/portal/activity"
            element={
              <ProtectedRoute>
                <PortalActivityPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/portal/privacy"
            element={
              <ProtectedRoute>
                <PortalPrivacyPage />
              </ProtectedRoute>
            }
          />
          <Route
            path="/portal/linked-accounts"
            element={
              <ProtectedRoute>
                <PortalLinkedAccountsPage />
              </ProtectedRoute>
            }
          />

          {/* Secrets Vault */}
          <Route
            path="/secrets"
            element={
              <ProtectedRoute>
                <SecretsVaultPage />
              </ProtectedRoute>
            }
          />

          {/* Risk Assessment */}
          <Route
            path="/risk"
            element={
              <ProtectedRoute>
                <RiskDashboardPage />
              </ProtectedRoute>
            }
          />

          {/* Security Alerts & SIEM */}
          <Route
            path="/security-alerts"
            element={
              <ProtectedRoute>
                <SecurityAlertsPage />
              </ProtectedRoute>
            }
          />

          {/* Visitor Management */}
          <Route
            path="/visitors"
            element={
              <ProtectedRoute>
                <VisitorManagementPage />
              </ProtectedRoute>
            }
          />

          {/* Service Accounts (API Gateway / Service Mesh) */}
          <Route
            path="/service-accounts"
            element={
              <ProtectedRoute>
                <ServiceAccountsPage />
              </ProtectedRoute>
            }
          />

          {/* Multi-Region High Availability */}
          <Route
            path="/regions"
            element={
              <ProtectedRoute>
                <RegionDashboardPage />
              </ProtectedRoute>
            }
          />

          {/* Delegation & Segregation of Duties */}
          <Route
            path="/delegation"
            element={
              <ProtectedRoute>
                <DelegationPage />
              </ProtectedRoute>
            }
          />

          {/* Default redirect */}
          <Route path="/" element={<Navigate to="/dashboard" replace />} />
          <Route path="*" element={<Navigate to="/dashboard" replace />} />
        </Routes>
      </AuthProvider>
    </Router>
  );
}

export default App;
