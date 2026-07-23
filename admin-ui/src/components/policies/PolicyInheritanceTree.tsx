import { useState, useEffect } from 'react';
import { Link } from 'react-router-dom';
import DashboardLayout from '../layout/DashboardLayout';
import { policyApi } from '../../services/policyApi';
import { api } from '../../services/api';
import type { Policy, TenantNode } from '../../types/policies';

interface TenantRaw {
  id: string;
  name: string;
  type: string;
  parentId?: string;
}

function buildTree(tenants: TenantRaw[], policies: Policy[]): TenantNode[] {
  const tenantMap = new Map<string, TenantNode>();

  // Create nodes
  tenants.forEach((t) => {
    tenantMap.set(t.id, {
      id: t.id,
      name: t.name,
      type: t.type,
      parentId: t.parentId,
      children: [],
      policies: [],
      inheritedPolicies: [],
    });
  });

  // Assign direct policies
  policies.forEach((p) => {
    const node = tenantMap.get(p.tenantId);
    if (node) {
      node.policies.push(p);
    }
  });

  // Compute inherited policies by walking the tree
  // A policy inherits to children if scope=Children or scope=Descendants
  // A policy inherits to all descendants if scope=Descendants
  function propagateInheritance(node: TenantNode, inheritedFromParent: Policy[]) {
    node.inheritedPolicies = [...inheritedFromParent];

    // Policies from this node that propagate to children
    const toPropagate: Policy[] = [];
    const toPropagateDescendants: Policy[] = [];

    node.policies.forEach((p) => {
      if (p.inheritanceScope === 'Children' || p.inheritanceScope === 'Descendants') {
        toPropagate.push(p);
      }
      if (p.inheritanceScope === 'Descendants') {
        toPropagateDescendants.push(p);
      }
    });

    // From the inherited list, keep propagating Descendants-scoped ones
    const continuePropagating = inheritedFromParent.filter(
      (p) => p.inheritanceScope === 'Descendants'
    );

    const childInherited = [...toPropagate, ...continuePropagating];

    node.children.forEach((child) => {
      propagateInheritance(child, childInherited);
    });
  }

  // Build hierarchy
  const roots: TenantNode[] = [];
  tenantMap.forEach((node) => {
    if (node.parentId) {
      const parent = tenantMap.get(node.parentId);
      if (parent) {
        parent.children.push(node);
      } else {
        roots.push(node);
      }
    } else {
      roots.push(node);
    }
  });

  // Sort children by name within each level
  function sortChildren(node: TenantNode) {
    node.children.sort((a, b) => a.name.localeCompare(b.name));
    node.children.forEach(sortChildren);
  }
  roots.sort((a, b) => a.name.localeCompare(b.name));
  roots.forEach(sortChildren);

  // Propagate inherited policies
  roots.forEach((root) => propagateInheritance(root, []));

  return roots;
}

const TENANT_TYPE_ICONS: Record<string, string> = {
  Organization: 'M19 21V5a2 2 0 00-2-2H7a2 2 0 00-2 2v16m14 0h2m-2 0h-5m-9 0H3m2 0h5M9 7h1m-1 4h1m4-4h1m-1 4h1m-5 10v-5a1 1 0 011-1h2a1 1 0 011 1v5m-4 0h4',
  Building: 'M3 21h18M3 10h18M3 7l9-4 9 4M4 10h16v11H4V10z',
  Floor: 'M4 5a1 1 0 011-1h14a1 1 0 011 1v2a1 1 0 01-1 1H5a1 1 0 01-1-1V5zM4 13a1 1 0 011-1h6a1 1 0 011 1v6a1 1 0 01-1 1H5a1 1 0 01-1-1v-6z',
  Room: 'M3 12l2-2m0 0l7-7 7 7M5 10v10a1 1 0 001 1h3m10-11l2 2m-2-2v10a1 1 0 01-1 1h-3m-6 0a1 1 0 001-1v-4a1 1 0 011-1h2a1 1 0 011 1v4a1 1 0 001 1m-6 0h6',
  Device: 'M9 3v2m6-2v2M9 19v2m6-2v2M5 9H3m2 6H3m18-6h-2m2 6h-2M7 19h10a2 2 0 002-2V7a2 2 0 00-2-2H7a2 2 0 00-2 2v10a2 2 0 002 2zM9 9h6v6H9V9z',
};

const TENANT_TYPE_COLORS: Record<string, string> = {
  Organization: 'text-indigo-600 bg-indigo-50 border-indigo-200',
  Building: 'text-blue-600 bg-blue-50 border-blue-200',
  Floor: 'text-green-600 bg-green-50 border-green-200',
  Room: 'text-yellow-600 bg-yellow-50 border-yellow-200',
  Device: 'text-orange-600 bg-orange-50 border-orange-200',
};

interface TreeNodeProps {
  node: TenantNode;
  depth: number;
  isLast: boolean;
  parentLines: boolean[];
}

function TreeNode({ node, depth, isLast, parentLines }: TreeNodeProps) {
  const [expanded, setExpanded] = useState(depth < 2);
  const hasChildren = node.children.length > 0;
  const hasPolicies = node.policies.length > 0 || node.inheritedPolicies.length > 0;
  const typeColor = TENANT_TYPE_COLORS[node.type] || 'text-gray-600 bg-gray-50 border-gray-200';
  const iconPath = TENANT_TYPE_ICONS[node.type] || TENANT_TYPE_ICONS.Device;

  // Compute effective permissions summary
  const allPolicies = [...node.policies, ...node.inheritedPolicies];
  const allowCount = allPolicies.filter((p) => p.effect === 'Allow' && p.isActive).length;
  const denyCount = allPolicies.filter((p) => p.effect === 'Deny' && p.isActive).length;

  return (
    <div className="relative">
      {/* Vertical connector lines from parent nodes */}
      {depth > 0 && parentLines.map((showLine, idx) => (
        showLine && (
          <div
            key={idx}
            className="absolute border-l-2 border-gray-300"
            style={{
              left: `${idx * 32 + 16}px`,
              top: 0,
              bottom: 0,
            }}
          />
        )
      ))}

      {/* Horizontal connector to this node */}
      {depth > 0 && (
        <>
          {/* Vertical line segment */}
          <div
            className="absolute border-l-2 border-gray-300"
            style={{
              left: `${(depth - 1) * 32 + 16}px`,
              top: 0,
              height: '24px',
            }}
          />
          {/* Horizontal line segment */}
          <div
            className="absolute border-t-2 border-gray-300"
            style={{
              left: `${(depth - 1) * 32 + 16}px`,
              top: '24px',
              width: '16px',
            }}
          />
          {/* If last child, cover the extra vertical line */}
          {isLast && (
            <div
              className="absolute bg-white"
              style={{
                left: `${(depth - 1) * 32 + 15}px`,
                top: '24px',
                bottom: 0,
                width: '4px',
              }}
            />
          )}
        </>
      )}

      {/* Node Content */}
      <div
        className="relative"
        style={{ paddingLeft: `${depth * 32}px` }}
      >
        <div className={`flex items-start py-2 group`}>
          {/* Expand/Collapse Toggle */}
          <button
            type="button"
            onClick={() => setExpanded(!expanded)}
            className={`flex-shrink-0 w-6 h-6 flex items-center justify-center rounded transition-colors ${
              hasChildren
                ? 'text-gray-500 hover:text-gray-700 hover:bg-gray-100 cursor-pointer'
                : 'text-transparent cursor-default'
            }`}
          >
            {hasChildren && (
              <svg
                className={`h-4 w-4 transition-transform duration-200 ${expanded ? 'rotate-90' : ''}`}
                fill="none"
                stroke="currentColor"
                viewBox="0 0 24 24"
              >
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
              </svg>
            )}
          </button>

          {/* Tenant Icon & Name */}
          <div className={`flex items-center gap-2 px-3 py-1.5 rounded-lg border ${typeColor} ml-1`}>
            <svg className="h-4 w-4 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d={iconPath} />
            </svg>
            <span className="text-sm font-medium">{node.name}</span>
            <span className="text-xs opacity-70">({node.type})</span>
          </div>

          {/* Policy Summary Badges */}
          {hasPolicies && (
            <div className="flex items-center gap-1.5 ml-3">
              {allowCount > 0 && (
                <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-green-100 text-green-700">
                  {allowCount} Allow
                </span>
              )}
              {denyCount > 0 && (
                <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-red-100 text-red-700">
                  {denyCount} Deny
                </span>
              )}
            </div>
          )}
        </div>

        {/* Policy Details (when expanded) */}
        {expanded && hasPolicies && (
          <div className="ml-7 mb-2">
            {/* Direct Policies */}
            {node.policies.length > 0 && (
              <div className="mb-2">
                <div className="text-xs font-semibold text-gray-500 uppercase tracking-wider mb-1 pl-2">
                  Direct Policies
                </div>
                <div className="space-y-1">
                  {node.policies.map((policy) => (
                    <PolicyBadge key={policy.id} policy={policy} inherited={false} />
                  ))}
                </div>
              </div>
            )}

            {/* Inherited Policies */}
            {node.inheritedPolicies.length > 0 && (
              <div>
                <div className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-1 pl-2">
                  Inherited Policies
                </div>
                <div className="space-y-1">
                  {node.inheritedPolicies.map((policy) => (
                    <PolicyBadge key={`inherited-${policy.id}`} policy={policy} inherited={true} />
                  ))}
                </div>
              </div>
            )}
          </div>
        )}
      </div>

      {/* Children */}
      {expanded &&
        node.children.map((child, idx) => (
          <TreeNode
            key={child.id}
            node={child}
            depth={depth + 1}
            isLast={idx === node.children.length - 1}
            parentLines={[...parentLines, idx !== node.children.length - 1]}
          />
        ))}
    </div>
  );
}

interface PolicyBadgeProps {
  policy: Policy;
  inherited: boolean;
}

function PolicyBadge({ policy, inherited }: PolicyBadgeProps) {
  const effectColor =
    policy.effect === 'Allow'
      ? 'border-green-200 bg-green-50'
      : 'border-red-200 bg-red-50';

  const effectTextColor =
    policy.effect === 'Allow' ? 'text-green-700' : 'text-red-700';

  return (
    <div
      className={`flex items-center justify-between px-3 py-1.5 rounded border text-sm ${
        inherited ? 'opacity-70 border-dashed' : ''
      } ${effectColor}`}
    >
      <div className="flex items-center gap-2 min-w-0">
        <span
          className={`inline-flex items-center px-1.5 py-0.5 rounded text-xs font-bold ${effectTextColor}`}
        >
          {policy.effect === 'Allow' ? 'ALLOW' : 'DENY'}
        </span>
        <span className="text-gray-800 font-medium truncate">{policy.name}</span>
        <span className="text-gray-500 text-xs font-mono truncate">
          {policy.resource}:{policy.action}
        </span>
        {inherited && (
          <span className="inline-flex items-center px-1.5 py-0.5 rounded bg-gray-200 text-gray-600 text-xs">
            inherited
          </span>
        )}
        {!policy.isActive && (
          <span className="inline-flex items-center px-1.5 py-0.5 rounded bg-gray-200 text-gray-500 text-xs">
            inactive
          </span>
        )}
      </div>
      <div className="flex items-center gap-2 ml-2 flex-shrink-0">
        <span className="text-xs text-gray-400">P:{policy.priority}</span>
        <Link
          to={`/policies/${policy.id}`}
          className="text-indigo-600 hover:text-indigo-800 text-xs font-medium"
        >
          Edit
        </Link>
      </div>
    </div>
  );
}

export default function PolicyInheritanceTree() {
  const [tree, setTree] = useState<TenantNode[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  useEffect(() => {
    loadData();
  }, []);

  const loadData = async () => {
    try {
      setLoading(true);
      const [tenants, policies] = await Promise.all([
        api.getTenants(),
        policyApi.getPolicies(),
      ]);
      const treeData = buildTree(tenants, policies);
      setTree(treeData);
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load data');
    } finally {
      setLoading(false);
    }
  };

  return (
    <DashboardLayout>
      <div className="px-4 sm:px-6 lg:px-8">
        {/* Header */}
        <div className="sm:flex sm:items-center">
          <div className="sm:flex-auto">
            <h1 className="text-2xl font-semibold text-gray-900">Policy Inheritance Tree</h1>
            <p className="mt-2 text-sm text-gray-700">
              Visualize how policies propagate through the tenant hierarchy. Direct policies are shown in
              solid borders, inherited policies in dashed borders.
            </p>
          </div>
          <div className="mt-4 sm:mt-0 sm:ml-16 sm:flex-none flex gap-2">
            <Link
              to="/policies"
              className="inline-flex items-center justify-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50"
            >
              Back to Policies
            </Link>
            <Link
              to="/policies/new"
              className="inline-flex items-center justify-center rounded-md border border-transparent bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700"
            >
              Create Policy
            </Link>
          </div>
        </div>

        {/* Legend */}
        <div className="mt-6 bg-white rounded-lg shadow p-4">
          <h3 className="text-xs font-semibold text-gray-500 uppercase tracking-wider mb-3">Legend</h3>
          <div className="flex flex-wrap gap-4 text-xs">
            <div className="flex items-center gap-2">
              <div className="w-4 h-4 rounded border-2 border-green-400 bg-green-50"></div>
              <span className="text-gray-600">Allow Policy (direct)</span>
            </div>
            <div className="flex items-center gap-2">
              <div className="w-4 h-4 rounded border-2 border-red-400 bg-red-50"></div>
              <span className="text-gray-600">Deny Policy (direct)</span>
            </div>
            <div className="flex items-center gap-2">
              <div className="w-4 h-4 rounded border-2 border-dashed border-green-400 bg-green-50 opacity-70"></div>
              <span className="text-gray-600">Allow Policy (inherited)</span>
            </div>
            <div className="flex items-center gap-2">
              <div className="w-4 h-4 rounded border-2 border-dashed border-red-400 bg-red-50 opacity-70"></div>
              <span className="text-gray-600">Deny Policy (inherited)</span>
            </div>
            <div className="border-l border-gray-300 pl-4 flex items-center gap-2">
              <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-gray-100 text-gray-700">Self</span>
              <span className="text-gray-500">No inheritance</span>
            </div>
            <div className="flex items-center gap-2">
              <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-700">Children</span>
              <span className="text-gray-500">Direct children</span>
            </div>
            <div className="flex items-center gap-2">
              <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-purple-100 text-purple-700">Descendants</span>
              <span className="text-gray-500">Entire subtree</span>
            </div>
          </div>
        </div>

        {/* Error */}
        {error && (
          <div className="mt-4 bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded">
            {error}
          </div>
        )}

        {/* Loading */}
        {loading ? (
          <div className="mt-8 text-center">
            <div className="inline-block animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600"></div>
            <p className="mt-2 text-sm text-gray-500">Loading inheritance tree...</p>
          </div>
        ) : tree.length === 0 ? (
          <div className="mt-8 text-center py-12 bg-white rounded-lg shadow">
            <svg className="mx-auto h-12 w-12 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 11H5m14 0a2 2 0 012 2v6a2 2 0 01-2 2H5a2 2 0 01-2-2v-6a2 2 0 012-2m14 0V9a2 2 0 00-2-2M5 11V9a2 2 0 012-2m0 0V5a2 2 0 012-2h6a2 2 0 012 2v2M7 7h10" />
            </svg>
            <h3 className="mt-2 text-sm font-medium text-gray-900">No tenants found</h3>
            <p className="mt-1 text-sm text-gray-500">Create tenants and policies to see the inheritance tree.</p>
          </div>
        ) : (
          /* Tree View */
          <div className="mt-6 bg-white rounded-lg shadow p-6 overflow-x-auto">
            <div className="min-w-[600px]">
              {tree.map((root, idx) => (
                <TreeNode
                  key={root.id}
                  node={root}
                  depth={0}
                  isLast={idx === tree.length - 1}
                  parentLines={[]}
                />
              ))}
            </div>
          </div>
        )}
      </div>
    </DashboardLayout>
  );
}
