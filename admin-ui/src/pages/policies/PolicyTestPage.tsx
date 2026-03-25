import { useState, useEffect } from 'react';
import { Link } from 'react-router-dom';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { policyApi } from '../../services/policyApi';
import { api } from '../../services/api';
import type { PolicyEvaluationResult } from '../../types/policies';
import type { User } from '../../types';

export default function PolicyTestPage() {
  const [users, setUsers] = useState<User[]>([]);
  const [tenants, setTenants] = useState<any[]>([]);
  const [loading, setLoading] = useState(true);
  const [evaluating, setEvaluating] = useState(false);
  const [error, setError] = useState('');

  // Form state
  const [selectedUserId, setSelectedUserId] = useState('');
  const [selectedTenantId, setSelectedTenantId] = useState('');
  const [resource, setResource] = useState('');
  const [action, setAction] = useState('');

  // Results
  const [result, setResult] = useState<PolicyEvaluationResult | null>(null);
  const [evaluationHistory, setEvaluationHistory] = useState<
    Array<{
      request: { userId: string; tenantId: string; resource: string; action: string };
      result: PolicyEvaluationResult;
      timestamp: string;
      userName: string;
      tenantName: string;
    }>
  >([]);

  useEffect(() => {
    loadData();
  }, []);

  const loadData = async () => {
    try {
      setLoading(true);
      const [usersData, tenantsData] = await Promise.all([
        api.getUsers(),
        api.getTenants(),
      ]);
      setUsers(usersData);
      setTenants(tenantsData);
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load data');
    } finally {
      setLoading(false);
    }
  };

  const getUserName = (userId: string) => {
    const user = users.find((u) => u.id === userId);
    return user ? `${user.firstName} ${user.lastName}` : userId;
  };

  const getTenantName = (tenantId: string) => {
    const tenant = tenants.find((t) => t.id === tenantId);
    return tenant ? `${tenant.name} (${tenant.type})` : tenantId;
  };

  const handleEvaluate = async () => {
    if (!selectedUserId || !selectedTenantId || !resource || !action) {
      setError('All fields are required');
      return;
    }

    try {
      setEvaluating(true);
      setError('');
      setResult(null);

      const evalResult = await policyApi.evaluatePolicy({
        userId: selectedUserId,
        tenantId: selectedTenantId,
        resource,
        action,
      });

      setResult(evalResult);

      // Add to history
      setEvaluationHistory((prev) => [
        {
          request: {
            userId: selectedUserId,
            tenantId: selectedTenantId,
            resource,
            action,
          },
          result: evalResult,
          timestamp: new Date().toISOString(),
          userName: getUserName(selectedUserId),
          tenantName: getTenantName(selectedTenantId),
        },
        ...prev.slice(0, 19), // Keep last 20
      ]);
    } catch (err: any) {
      setError(err.response?.data?.message || 'Policy evaluation failed');
    } finally {
      setEvaluating(false);
    }
  };

  // Build tenant hierarchy path for display
  const buildTenantPath = (tenantId: string): string[] => {
    const path: string[] = [];
    let current = tenants.find((t) => t.id === tenantId);
    while (current) {
      path.unshift(`${current.name} (${current.type})`);
      current = current.parentId
        ? tenants.find((t) => t.id === current!.parentId)
        : undefined;
    }
    return path;
  };

  if (loading) {
    return (
      <DashboardLayout>
        <div className="flex items-center justify-center min-h-screen">
          <div className="text-center">
            <div className="inline-block animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600"></div>
            <p className="mt-2 text-sm text-gray-500">Loading...</p>
          </div>
        </div>
      </DashboardLayout>
    );
  }

  return (
    <DashboardLayout>
      <div className="px-4 sm:px-6 lg:px-8">
        {/* Header */}
        <div className="sm:flex sm:items-center">
          <div className="sm:flex-auto">
            <h1 className="text-2xl font-semibold text-gray-900">Policy Tester</h1>
            <p className="mt-2 text-sm text-gray-700">
              Simulate policy evaluations to debug access control. Test "why can't user X access resource Y?"
            </p>
          </div>
          <div className="mt-4 sm:mt-0 sm:ml-16 sm:flex-none">
            <Link
              to="/policies"
              className="inline-flex items-center justify-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50"
            >
              Back to Policies
            </Link>
          </div>
        </div>

        <div className="mt-6 grid grid-cols-1 lg:grid-cols-2 gap-6">
          {/* Left Column: Test Form */}
          <div>
            <div className="bg-white shadow sm:rounded-lg">
              <div className="px-4 py-5 sm:p-6">
                <h3 className="text-lg font-medium text-gray-900 mb-4">Evaluation Parameters</h3>

                {error && (
                  <div className="mb-4 bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded text-sm">
                    {error}
                  </div>
                )}

                <div className="space-y-4">
                  {/* User */}
                  <div>
                    <label htmlFor="userId" className="block text-sm font-medium text-gray-700">
                      User *
                    </label>
                    <select
                      id="userId"
                      value={selectedUserId}
                      onChange={(e) => setSelectedUserId(e.target.value)}
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    >
                      <option value="">Select a user</option>
                      {users.map((user) => (
                        <option key={user.id} value={user.id}>
                          {user.firstName} {user.lastName} ({user.email})
                        </option>
                      ))}
                    </select>
                  </div>

                  {/* Tenant */}
                  <div>
                    <label htmlFor="tenantId" className="block text-sm font-medium text-gray-700">
                      Tenant (Context) *
                    </label>
                    <select
                      id="tenantId"
                      value={selectedTenantId}
                      onChange={(e) => setSelectedTenantId(e.target.value)}
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    >
                      <option value="">Select a tenant</option>
                      {tenants.map((tenant) => (
                        <option key={tenant.id} value={tenant.id}>
                          {tenant.name} ({tenant.type})
                        </option>
                      ))}
                    </select>

                    {/* Tenant Hierarchy Path */}
                    {selectedTenantId && (
                      <div className="mt-2 flex items-center gap-1 text-xs text-gray-500">
                        <span className="font-medium">Path:</span>
                        {buildTenantPath(selectedTenantId).map((segment, idx, arr) => (
                          <span key={idx} className="flex items-center gap-1">
                            <span className="bg-gray-100 px-1.5 py-0.5 rounded">{segment}</span>
                            {idx < arr.length - 1 && (
                              <svg className="h-3 w-3 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
                              </svg>
                            )}
                          </span>
                        ))}
                      </div>
                    )}
                  </div>

                  {/* Resource */}
                  <div>
                    <label htmlFor="resource" className="block text-sm font-medium text-gray-700">
                      Resource *
                    </label>
                    <input
                      type="text"
                      id="resource"
                      value={resource}
                      onChange={(e) => setResource(e.target.value)}
                      placeholder="e.g., Door, Camera, HVAC"
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    />
                  </div>

                  {/* Action */}
                  <div>
                    <label htmlFor="action" className="block text-sm font-medium text-gray-700">
                      Action *
                    </label>
                    <input
                      type="text"
                      id="action"
                      value={action}
                      onChange={(e) => setAction(e.target.value)}
                      placeholder="e.g., Unlock, View, Control"
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    />
                    <div className="mt-2 flex flex-wrap gap-1">
                      {['View', 'Create', 'Update', 'Delete', 'Execute', 'Control', 'Unlock'].map(
                        (a) => (
                          <button
                            key={a}
                            type="button"
                            onClick={() => setAction(a)}
                            className={`px-2 py-1 text-xs rounded border ${
                              action === a
                                ? 'bg-indigo-600 text-white border-indigo-600'
                                : 'bg-white text-gray-600 border-gray-300 hover:bg-gray-50'
                            }`}
                          >
                            {a}
                          </button>
                        )
                      )}
                    </div>
                  </div>

                  {/* Evaluate Button */}
                  <button
                    type="button"
                    onClick={handleEvaluate}
                    disabled={evaluating || !selectedUserId || !selectedTenantId || !resource || !action}
                    className="w-full inline-flex justify-center items-center py-2.5 px-4 border border-transparent shadow-sm text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500 disabled:opacity-50 disabled:cursor-not-allowed"
                  >
                    {evaluating ? (
                      <>
                        <div className="inline-block animate-spin rounded-full h-4 w-4 border-b-2 border-white mr-2"></div>
                        Evaluating...
                      </>
                    ) : (
                      <>
                        <svg className="h-4 w-4 mr-2" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 10V3L4 14h7v7l9-11h-7z" />
                        </svg>
                        Evaluate Access
                      </>
                    )}
                  </button>
                </div>
              </div>
            </div>

            {/* Quick Test Scenarios */}
            <div className="mt-4 bg-white shadow sm:rounded-lg">
              <div className="px-4 py-5 sm:p-6">
                <h3 className="text-sm font-medium text-gray-900 mb-3">Quick Test Scenarios</h3>
                <div className="space-y-2">
                  <button
                    type="button"
                    onClick={() => {
                      setResource('Door');
                      setAction('Unlock');
                    }}
                    className="w-full text-left px-3 py-2 text-sm rounded border border-gray-200 hover:bg-gray-50 transition-colors"
                  >
                    <span className="font-medium text-gray-700">Door Unlock</span>
                    <span className="text-gray-500 ml-2">- Can user unlock doors?</span>
                  </button>
                  <button
                    type="button"
                    onClick={() => {
                      setResource('Camera');
                      setAction('View');
                    }}
                    className="w-full text-left px-3 py-2 text-sm rounded border border-gray-200 hover:bg-gray-50 transition-colors"
                  >
                    <span className="font-medium text-gray-700">Camera View</span>
                    <span className="text-gray-500 ml-2">- Can user view cameras?</span>
                  </button>
                  <button
                    type="button"
                    onClick={() => {
                      setResource('HVAC');
                      setAction('Control');
                    }}
                    className="w-full text-left px-3 py-2 text-sm rounded border border-gray-200 hover:bg-gray-50 transition-colors"
                  >
                    <span className="font-medium text-gray-700">HVAC Control</span>
                    <span className="text-gray-500 ml-2">- Can user control climate?</span>
                  </button>
                  <button
                    type="button"
                    onClick={() => {
                      setResource('*');
                      setAction('Admin');
                    }}
                    className="w-full text-left px-3 py-2 text-sm rounded border border-gray-200 hover:bg-gray-50 transition-colors"
                  >
                    <span className="font-medium text-gray-700">Admin Access</span>
                    <span className="text-gray-500 ml-2">- Full admin on all resources?</span>
                  </button>
                </div>
              </div>
            </div>
          </div>

          {/* Right Column: Results */}
          <div>
            {/* Current Result */}
            {result && (
              <div className="bg-white shadow sm:rounded-lg mb-6">
                <div className="px-4 py-5 sm:p-6">
                  <h3 className="text-lg font-medium text-gray-900 mb-4">Evaluation Result</h3>

                  {/* Main Result Banner */}
                  <div
                    className={`rounded-lg p-4 mb-4 ${
                      result.isAllowed
                        ? 'bg-green-50 border-2 border-green-200'
                        : 'bg-red-50 border-2 border-red-200'
                    }`}
                  >
                    <div className="flex items-center">
                      <div
                        className={`flex-shrink-0 h-10 w-10 rounded-full flex items-center justify-center ${
                          result.isAllowed ? 'bg-green-500' : 'bg-red-500'
                        }`}
                      >
                        {result.isAllowed ? (
                          <svg className="h-6 w-6 text-white" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                          </svg>
                        ) : (
                          <svg className="h-6 w-6 text-white" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                          </svg>
                        )}
                      </div>
                      <div className="ml-4">
                        <h4
                          className={`text-lg font-bold ${
                            result.isAllowed ? 'text-green-800' : 'text-red-800'
                          }`}
                        >
                          Access {result.isAllowed ? 'ALLOWED' : 'DENIED'}
                        </h4>
                        <p className={`text-sm ${result.isAllowed ? 'text-green-700' : 'text-red-700'}`}>
                          {result.reason || 'No additional reason provided'}
                        </p>
                      </div>
                    </div>
                  </div>

                  {/* Evaluation Stats */}
                  <div className="grid grid-cols-2 gap-4 mb-4">
                    <div className="bg-gray-50 rounded p-3">
                      <div className="text-xs text-gray-500 uppercase tracking-wider">Policies Evaluated</div>
                      <div className="text-2xl font-bold text-gray-900">{result.evaluatedPoliciesCount}</div>
                    </div>
                    <div className="bg-gray-50 rounded p-3">
                      <div className="text-xs text-gray-500 uppercase tracking-wider">Evaluation Time</div>
                      <div className="text-2xl font-bold text-gray-900">{result.evaluationTimeMs}ms</div>
                    </div>
                  </div>

                  {/* Matching Policy */}
                  {result.matchedPolicy && (
                    <div className="mb-4">
                      <h4 className="text-sm font-medium text-gray-700 mb-2">Deciding Policy</h4>
                      <div
                        className={`rounded-lg border-2 p-3 ${
                          result.matchedPolicy.effect === 'Allow'
                            ? 'border-green-300 bg-green-50'
                            : 'border-red-300 bg-red-50'
                        }`}
                      >
                        <div className="flex items-center justify-between">
                          <div>
                            <span className="font-medium text-gray-900">{result.matchedPolicy.name}</span>
                            <div className="text-xs text-gray-500 mt-0.5">
                              {result.matchedPolicy.resource}:{result.matchedPolicy.action} | Priority: {result.matchedPolicy.priority} | Scope: {result.matchedPolicy.inheritanceScope}
                            </div>
                          </div>
                          <Link
                            to={`/policies/${result.matchedPolicy.id}`}
                            className="text-indigo-600 hover:text-indigo-800 text-sm font-medium"
                          >
                            Edit
                          </Link>
                        </div>
                      </div>
                    </div>
                  )}

                  {/* All Evaluated Policies */}
                  {result.evaluatedPolicies && result.evaluatedPolicies.length > 0 && (
                    <div>
                      <h4 className="text-sm font-medium text-gray-700 mb-2">
                        All Evaluated Policies ({result.evaluatedPolicies.length})
                      </h4>
                      <div className="space-y-1.5 max-h-64 overflow-y-auto">
                        {result.evaluatedPolicies.map((policy, idx) => (
                          <div
                            key={policy.id || idx}
                            className={`flex items-center justify-between px-3 py-2 rounded text-sm border ${
                              result.matchedPolicy?.id === policy.id
                                ? 'ring-2 ring-indigo-500 border-indigo-300 bg-indigo-50'
                                : 'border-gray-200 bg-white'
                            }`}
                          >
                            <div className="flex items-center gap-2">
                              <span className="text-xs text-gray-400 w-5">#{idx + 1}</span>
                              <span
                                className={`inline-flex px-1.5 py-0.5 rounded text-xs font-bold ${
                                  policy.effect === 'Allow'
                                    ? 'bg-green-100 text-green-700'
                                    : 'bg-red-100 text-red-700'
                                }`}
                              >
                                {policy.effect}
                              </span>
                              <span className="text-gray-800">{policy.name}</span>
                              <span className="text-gray-400 text-xs font-mono">
                                {policy.resource}:{policy.action}
                              </span>
                            </div>
                            <span className="text-xs text-gray-500">P:{policy.priority}</span>
                          </div>
                        ))}
                      </div>
                    </div>
                  )}

                  {/* Tenant Hierarchy Path */}
                  {selectedTenantId && (
                    <div className="mt-4 pt-4 border-t border-gray-200">
                      <h4 className="text-sm font-medium text-gray-700 mb-2">Evaluation Path (Tenant Hierarchy)</h4>
                      <div className="flex items-center flex-wrap gap-1">
                        {buildTenantPath(selectedTenantId).map((segment, idx, arr) => (
                          <div key={idx} className="flex items-center gap-1">
                            <span
                              className={`inline-flex items-center px-2.5 py-1 rounded text-xs font-medium ${
                                idx === arr.length - 1
                                  ? 'bg-indigo-100 text-indigo-800 ring-2 ring-indigo-300'
                                  : 'bg-gray-100 text-gray-700'
                              }`}
                            >
                              {segment}
                            </span>
                            {idx < arr.length - 1 && (
                              <svg className="h-4 w-4 text-gray-400 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
                              </svg>
                            )}
                          </div>
                        ))}
                      </div>
                      <p className="mt-2 text-xs text-gray-500">
                        Policies are evaluated from the target tenant up through the hierarchy. Inherited policies from parent scopes are included based on their inheritance scope.
                      </p>
                    </div>
                  )}
                </div>
              </div>
            )}

            {/* No Result Placeholder */}
            {!result && !evaluating && (
              <div className="bg-white shadow sm:rounded-lg mb-6">
                <div className="px-4 py-12 text-center">
                  <svg className="mx-auto h-12 w-12 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" />
                  </svg>
                  <h3 className="mt-2 text-sm font-medium text-gray-900">No evaluation yet</h3>
                  <p className="mt-1 text-sm text-gray-500">
                    Fill in the parameters on the left and click "Evaluate Access" to test policy evaluation.
                  </p>
                </div>
              </div>
            )}

            {/* Evaluation History */}
            {evaluationHistory.length > 0 && (
              <div className="bg-white shadow sm:rounded-lg">
                <div className="px-4 py-5 sm:p-6">
                  <div className="flex items-center justify-between mb-3">
                    <h3 className="text-sm font-medium text-gray-900">
                      Evaluation History ({evaluationHistory.length})
                    </h3>
                    <button
                      type="button"
                      onClick={() => setEvaluationHistory([])}
                      className="text-xs text-gray-500 hover:text-gray-700"
                    >
                      Clear
                    </button>
                  </div>
                  <div className="space-y-2 max-h-80 overflow-y-auto">
                    {evaluationHistory.map((entry, idx) => (
                      <div
                        key={idx}
                        className={`flex items-center justify-between px-3 py-2 rounded border text-sm ${
                          entry.result.isAllowed
                            ? 'border-green-200 bg-green-50'
                            : 'border-red-200 bg-red-50'
                        }`}
                      >
                        <div className="min-w-0">
                          <div className="flex items-center gap-2">
                            <span
                              className={`inline-flex h-5 w-5 items-center justify-center rounded-full text-xs font-bold text-white ${
                                entry.result.isAllowed ? 'bg-green-500' : 'bg-red-500'
                              }`}
                            >
                              {entry.result.isAllowed ? 'A' : 'D'}
                            </span>
                            <span className="font-medium text-gray-800 truncate">
                              {entry.userName}
                            </span>
                          </div>
                          <div className="text-xs text-gray-500 mt-0.5 truncate">
                            {entry.request.resource}:{entry.request.action} @ {entry.tenantName}
                          </div>
                        </div>
                        <div className="text-xs text-gray-400 flex-shrink-0 ml-2">
                          {new Date(entry.timestamp).toLocaleTimeString()}
                        </div>
                      </div>
                    ))}
                  </div>
                </div>
              </div>
            )}
          </div>
        </div>
      </div>
    </DashboardLayout>
  );
}
