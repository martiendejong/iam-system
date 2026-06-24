import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import DashboardLayout from '../components/layout/DashboardLayout';
import { api } from '../services/api';

export default function DashboardPage() {
  const { user } = useAuth();
  const navigate = useNavigate();
  const [userCount, setUserCount] = useState<number | null>(null);
  const [roleCount, setRoleCount] = useState<number | null>(null);
  const [tenantCount, setTenantCount] = useState<number | null>(null);

  useEffect(() => {
    api.getUsers().then((result: any) => {
      setUserCount(result?.totalCount ?? (Array.isArray(result) ? result.length : 0));
    }).catch(() => setUserCount(0));

    api.getRoles().then((roles: any) => {
      setRoleCount(Array.isArray(roles) ? roles.length : 0);
    }).catch(() => setRoleCount(0));

    api.getTenants().then((tenants: any) => {
      setTenantCount(Array.isArray(tenants) ? tenants.length : 0);
    }).catch(() => setTenantCount(0));
  }, []);

  const fmt = (n: number | null) => n === null ? '...' : String(n);

  const stats = [
    {
      label: 'Total Users',
      value: fmt(userCount),
      bg: 'bg-indigo-500',
      light: 'bg-indigo-50',
      text: 'text-indigo-600',
      icon: (
        <svg className="w-6 h-6 text-white" fill="none" viewBox="0 0 24 24" stroke="currentColor">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M17 20h5v-2a3 3 0 00-5.356-1.857M17 20H7m10 0v-2c0-.656-.126-1.283-.356-1.857M7 20H2v-2a3 3 0 015.356-1.857M7 20v-2c0-.656.126-1.283.356-1.857m0 0a5.002 5.002 0 019.288 0M15 7a3 3 0 11-6 0 3 3 0 016 0z" />
        </svg>
      ),
      action: () => navigate('/users'),
      actionLabel: 'View all users',
    },
    {
      label: 'Roles',
      value: fmt(roleCount),
      bg: 'bg-emerald-500',
      light: 'bg-emerald-50',
      text: 'text-emerald-600',
      icon: (
        <svg className="w-6 h-6 text-white" fill="none" viewBox="0 0 24 24" stroke="currentColor">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" />
        </svg>
      ),
      action: () => navigate('/roles'),
      actionLabel: 'Manage roles',
    },
    {
      label: 'Tenants',
      value: fmt(tenantCount),
      bg: 'bg-violet-500',
      light: 'bg-violet-50',
      text: 'text-violet-600',
      icon: (
        <svg className="w-6 h-6 text-white" fill="none" viewBox="0 0 24 24" stroke="currentColor">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 21V5a2 2 0 00-2-2H7a2 2 0 00-2 2v16m14 0h2m-2 0h-5m-9 0H3m2 0h5M9 7h1m-1 4h1m4-4h1m-1 4h1m-5 10v-5a1 1 0 011-1h2a1 1 0 011 1v5m-4 0h4" />
        </svg>
      ),
      action: () => navigate('/tenants'),
      actionLabel: 'View tenants',
    },
  ];

  const quickActions = [
    {
      label: 'Add User',
      description: 'Create a new user account',
      icon: (
        <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M18 9v3m0 0v3m0-3h3m-3 0h-3m-2-5a4 4 0 11-8 0 4 4 0 018 0zM3 20a6 6 0 0112 0v1H3v-1z" />
        </svg>
      ),
      action: () => navigate('/users/new'),
      color: 'text-indigo-600 bg-indigo-50 hover:bg-indigo-100',
    },
    {
      label: 'Create Role',
      description: 'Define a new access role',
      icon: (
        <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v3m0 0v3m0-3h3m-3 0H9m12 0a9 9 0 11-18 0 9 9 0 0118 0z" />
        </svg>
      ),
      action: () => navigate('/roles/new'),
      color: 'text-emerald-600 bg-emerald-50 hover:bg-emerald-100',
    },
    {
      label: 'View All Users',
      description: 'Browse and manage users',
      icon: (
        <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 6h16M4 10h16M4 14h16M4 18h16" />
        </svg>
      ),
      action: () => navigate('/users'),
      color: 'text-blue-600 bg-blue-50 hover:bg-blue-100',
    },
    {
      label: 'Manage Roles',
      description: 'View and edit roles',
      icon: (
        <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
        </svg>
      ),
      action: () => navigate('/roles'),
      color: 'text-violet-600 bg-violet-50 hover:bg-violet-100',
    },
  ];

  return (
    <DashboardLayout>
      {/* Welcome */}
      <div className="mb-8">
        <h1 className="text-2xl font-bold text-gray-900">
          Welcome back, {user?.firstName}!
        </h1>
        <p className="mt-1 text-sm text-gray-500">
          Here's what's happening in your identity system.
        </p>
      </div>

      {/* Stats */}
      <div className="grid grid-cols-1 sm:grid-cols-3 gap-6 mb-8">
        {stats.map((stat) => (
          <div
            key={stat.label}
            className="bg-white rounded-xl shadow-sm border border-gray-100 p-6 flex items-center gap-4 cursor-pointer hover:shadow-md transition-shadow"
            onClick={stat.action}
          >
            <div className={`w-12 h-12 ${stat.bg} rounded-xl flex items-center justify-center flex-shrink-0`}>
              {stat.icon}
            </div>
            <div>
              <p className="text-sm font-medium text-gray-500">{stat.label}</p>
              <p className={`text-3xl font-bold ${stat.text}`}>{stat.value}</p>
              <p className="text-xs text-gray-400 mt-0.5">{stat.actionLabel}</p>
            </div>
          </div>
        ))}
      </div>

      {/* Quick Actions */}
      <div className="bg-white rounded-xl shadow-sm border border-gray-100 p-6">
        <h3 className="text-sm font-semibold text-gray-700 uppercase tracking-wide mb-4">Quick Actions</h3>
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-3">
          {quickActions.map((action) => (
            <button
              key={action.label}
              onClick={action.action}
              className={`flex items-center gap-3 px-4 py-3 rounded-lg text-sm font-medium transition-colors text-left ${action.color}`}
            >
              {action.icon}
              <div>
                <div className="font-semibold">{action.label}</div>
                <div className="text-xs opacity-75">{action.description}</div>
              </div>
            </button>
          ))}
        </div>
      </div>
    </DashboardLayout>
  );
}
