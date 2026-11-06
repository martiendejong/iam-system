import type { CurrentUserRole, User } from '../../types';

/** A tenant the role form can offer: id plus a display name. */
export interface TenantOption {
  id: string;
  name: string;
}

function isActive(role: CurrentUserRole, now: Date): boolean {
  return !role.expiresAt || new Date(role.expiresAt).getTime() > now.getTime();
}

/** True when the user holds an active SuperAdmin role (the "Global" choice is theirs alone). */
export function isSuperAdmin(user: User | null | undefined, now: Date = new Date()): boolean {
  return !!user?.roles?.some((r) => r.name === 'SuperAdmin' && isActive(r, now));
}

/**
 * Tenant ids the user owns: active BuildingOwner assignments scoped to a tenant. A global
 * (tenant-less) assignment or a role the user merely holds elsewhere never counts - the API
 * only lets an owner create roles inside tenants they own.
 */
export function ownedTenantIds(user: User | null | undefined, now: Date = new Date()): string[] {
  const ids = (user?.roles ?? [])
    .filter((r) => r.name === 'BuildingOwner' && !!r.tenantId && isActive(r, now))
    .map((r) => r.tenantId as string);
  return Array.from(new Set(ids));
}

/** Owned tenants with a name, in the order the tenant list gave them. Never offers a tenant the user only belongs to. */
export function ownedTenantOptions(
  user: User | null | undefined,
  myTenants: Array<{ id: string; name: string }>,
  now: Date = new Date(),
): TenantOption[] {
  const owned = new Set(ownedTenantIds(user, now));
  return myTenants.filter((t) => owned.has(t.id)).map((t) => ({ id: t.id, name: t.name }));
}

/** The API answers {error}; older endpoints answer {message}. Always show the API's own reason. */
export function apiErrorMessage(err: any, fallback: string): string {
  const data = err?.response?.data;
  if (typeof data === 'string' && data.trim()) return data;
  return data?.error || data?.message || fallback;
}

/** "Global" for a tenant-less role, else the tenant's name (or a short id when the name is unknown). */
export function roleScopeLabel(tenantId: string | null | undefined, names: Record<string, string>): string {
  if (!tenantId) return 'Global';
  return names[tenantId] ?? `Tenant ${tenantId.slice(0, 8)}`;
}
