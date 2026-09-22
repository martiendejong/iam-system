import { useEffect, useState } from 'react';
import { Users, Shield, Building2, Activity } from 'lucide-react';
import { usersApi } from '../api/users';
import { rolesApi } from '../api/roles';
import { tenantsApi } from '../api/tenants';

interface Stats {
  totalUsers: number;
  activeUsers: number;
  totalRoles: number;
  totalTenants: number;
}

export function DashboardPage() {
  const [stats, setStats] = useState<Stats>({
    totalUsers: 0,
    activeUsers: 0,
    totalRoles: 0,
    totalTenants: 0,
  });
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    loadStats();
  }, []);

  const loadStats = async () => {
    try {
      const [usersData, rolesData, tenantsData] = await Promise.all([
        usersApi.getAll(1, 1),
        rolesApi.getAll(1, 1),
        tenantsApi.getAll(1, 1),
      ]);

      setStats({
        totalUsers: usersData.totalCount,
        activeUsers: usersData.totalCount, // TODO: Filter active users
        totalRoles: rolesData.totalCount,
        totalTenants: tenantsData.totalCount,
      });
    } catch (error) {
      console.error('Failed to load stats:', error);
    } finally {
      setLoading(false);
    }
  };

  const statCards = [
    {
      name: 'Total Users',
      value: stats.totalUsers,
      icon: Users,
      color: 'bg-blue-500',
    },
    {
      name: 'Active Users',
      value: stats.activeUsers,
      icon: Activity,
      color: 'bg-green-500',
    },
    {
      name: 'Roles',
      value: stats.totalRoles,
      icon: Shield,
      color: 'bg-purple-500',
    },
    {
      name: 'Tenants',
      value: stats.totalTenants,
      icon: Building2,
      color: 'bg-orange-500',
    },
  ];

  return (
    <div>
      <div className="mb-8">
        <h1 className="text-3xl font-bold text-gray-900">Dashboard</h1>
        <p className="mt-2 text-gray-600">
          Welcome to your IAM administration portal
        </p>
      </div>

      {/* Stats */}
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-6 mb-8">
        {statCards.map((stat) => (
          <div key={stat.name} className="card">
            <div className="flex items-center justify-between">
              <div>
                <p className="text-sm font-medium text-gray-600">{stat.name}</p>
                <p className="mt-2 text-3xl font-bold text-gray-900">
                  {loading ? '...' : stat.value.toLocaleString()}
                </p>
              </div>
              <div className={`p-3 rounded-lg ${stat.color}`}>
                <stat.icon className="w-6 h-6 text-white" />
              </div>
            </div>
          </div>
        ))}
      </div>

      {/* Quick actions */}
      <div className="card">
        <h2 className="text-xl font-semibold text-gray-900 mb-4">
          Quick Actions
        </h2>
        <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
          <a
            href="/users"
            className="p-4 border border-gray-200 rounded-lg hover:border-primary-300 hover:bg-primary-50 transition-colors"
          >
            <Users className="w-6 h-6 text-primary-600 mb-2" />
            <h3 className="font-medium text-gray-900">Manage Users</h3>
            <p className="text-sm text-gray-600 mt-1">
              Create, edit, and manage user accounts
            </p>
          </a>

          <a
            href="/roles"
            className="p-4 border border-gray-200 rounded-lg hover:border-primary-300 hover:bg-primary-50 transition-colors"
          >
            <Shield className="w-6 h-6 text-primary-600 mb-2" />
            <h3 className="font-medium text-gray-900">Configure Roles</h3>
            <p className="text-sm text-gray-600 mt-1">
              Define roles and permissions
            </p>
          </a>

          <a
            href="/audit"
            className="p-4 border border-gray-200 rounded-lg hover:border-primary-300 hover:bg-primary-50 transition-colors"
          >
            <Activity className="w-6 h-6 text-primary-600 mb-2" />
            <h3 className="font-medium text-gray-900">View Audit Log</h3>
            <p className="text-sm text-gray-600 mt-1">
              Monitor system activity and changes
            </p>
          </a>
        </div>
      </div>
    </div>
  );
}
