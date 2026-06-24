import type { ReactNode } from 'react';
import { NavLink, useNavigate } from 'react-router-dom';
import { useAuth } from '../../context/AuthContext';

interface DashboardLayoutProps {
  children: ReactNode;
}

const NAV_ITEMS: { to: string; label: string }[] = [
  { to: '/dashboard', label: 'Dashboard' },
  { to: '/users', label: 'Users' },
  { to: '/roles', label: 'Roles' },
  { to: '/tenants', label: 'Tenants' },
  { to: '/devices', label: 'Devices' },
  { to: '/policies', label: 'Policies' },
  { to: '/identity-providers', label: 'Identity Providers' },
  { to: '/directory-sync', label: 'Directory Sync' },
  { to: '/access-requests', label: 'Access Requests' },
  { to: '/workflow-templates', label: 'Workflows' },
  { to: '/audit', label: 'Audit Log' },
  { to: '/compliance', label: 'Compliance' },
  { to: '/invitations', label: 'Invitations' },
  { to: '/consent', label: 'Consent' },
  { to: '/scim', label: 'SCIM' },
  { to: '/bulk-operations', label: 'Bulk Ops' },
  { to: '/claims-mapping', label: 'Claims' },
  { to: '/branding', label: 'Branding' },
  { to: '/network-policy', label: 'Network Policy' },
  { to: '/pam', label: 'PAM' },
  { to: '/secrets', label: 'Secrets Vault' },
  { to: '/risk', label: 'Risk' },
  { to: '/security-alerts', label: 'Alerts' },
  { to: '/visitors', label: 'Visitors' },
  { to: '/service-accounts', label: 'Service Accounts' },
  { to: '/regions', label: 'Regions' },
  { to: '/delegation', label: 'Delegation' },
];

export default function DashboardLayout({ children }: DashboardLayoutProps) {
  const { user, logout } = useAuth();
  const navigate = useNavigate();

  const handleLogout = async () => {
    await logout();
    navigate('/login');
  };

  return (
    <div className="flex h-screen bg-gray-100">
      {/* Left Sidebar */}
      <aside className="w-64 flex-shrink-0 bg-white border-r border-gray-200 flex flex-col">
        <div className="h-16 flex items-center px-6 border-b border-gray-200 flex-shrink-0">
          <h1 className="text-xl font-bold text-gray-900">IAM System</h1>
        </div>

        <nav className="flex-1 overflow-y-auto py-4 px-3 space-y-1">
          {NAV_ITEMS.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              className={({ isActive }) =>
                `block px-3 py-2 rounded-md text-sm font-medium ${
                  isActive
                    ? 'bg-indigo-50 text-indigo-700'
                    : 'text-gray-600 hover:bg-gray-50 hover:text-gray-900'
                }`
              }
            >
              {item.label}
            </NavLink>
          ))}
        </nav>

        <div className="border-t border-gray-200 p-4 flex-shrink-0">
          <div className="text-sm text-gray-700 mb-2 truncate">
            {user?.firstName} {user?.lastName}
          </div>
          <button
            onClick={handleLogout}
            className="w-full inline-flex items-center justify-center px-4 py-2 border border-transparent text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700"
          >
            Logout
          </button>
        </div>
      </aside>

      {/* Main Content */}
      <main className="flex-1 overflow-y-auto">
        <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
          {children}
        </div>
      </main>
    </div>
  );
}
