import { describe, it, expect } from 'vitest';
import type { User } from '../../types';
import { apiErrorMessage, isSuperAdmin, ownedTenantIds, ownedTenantOptions, roleScopeLabel } from './roleTenantAccess';

const NOW = new Date('2026-10-10T12:00:00Z');

function userWith(roles: NonNullable<User['roles']>): User {
  return { id: 'u', email: 'e', firstName: 'f', lastName: 'l', isActive: true, emailConfirmed: true, createdAt: '', updatedAt: '', roles };
}

describe('roleTenantAccess', () => {
  it('owned tenants = active tenant-scoped BuildingOwner entries only', () => {
    const user = userWith([
      { id: '1', name: 'BuildingOwner', tenantId: 'a' },
      { id: '2', name: 'BuildingOwner', tenantId: 'a' },
      { id: '3', name: 'BuildingOwner', tenantId: 'b', expiresAt: '2026-01-01T00:00:00Z' },
      { id: '4', name: 'BuildingOwner', tenantId: null },
      { id: '5', name: 'BuildingManager', tenantId: 'c' },
      { id: '6', name: 'BuildingOwner', tenantId: 'd', expiresAt: '2027-01-01T00:00:00Z' },
    ]);
    expect(ownedTenantIds(user, NOW)).toEqual(['a', 'd']);
  });

  it('never offers a tenant the user only belongs to', () => {
    const user = userWith([{ id: '1', name: 'BuildingOwner', tenantId: 'a' }]);
    const options = ownedTenantOptions(user, [{ id: 'a', name: 'A' }, { id: 'z', name: 'Member only' }], NOW);
    expect(options).toEqual([{ id: 'a', name: 'A' }]);
  });

  it('isSuperAdmin needs an active SuperAdmin role', () => {
    expect(isSuperAdmin(userWith([{ id: '1', name: 'SuperAdmin' }]), NOW)).toBe(true);
    expect(isSuperAdmin(userWith([{ id: '1', name: 'SuperAdmin', expiresAt: '2026-01-01T00:00:00Z' }]), NOW)).toBe(false);
    expect(isSuperAdmin(userWith([{ id: '1', name: 'BuildingOwner', tenantId: 'a' }]), NOW)).toBe(false);
    expect(isSuperAdmin(null, NOW)).toBe(false);
  });

  it('apiErrorMessage prefers the API error text', () => {
    expect(apiErrorMessage({ response: { data: { error: 'nope' } } }, 'fallback')).toBe('nope');
    expect(apiErrorMessage({ response: { data: { message: 'old' } } }, 'fallback')).toBe('old');
    expect(apiErrorMessage({ response: { data: 'plain text' } }, 'fallback')).toBe('plain text');
    expect(apiErrorMessage(new Error('x'), 'fallback')).toBe('fallback');
  });

  it('roleScopeLabel says Global for tenant-less roles', () => {
    expect(roleScopeLabel(null, {})).toBe('Global');
    expect(roleScopeLabel(undefined, {})).toBe('Global');
    expect(roleScopeLabel('abc', { abc: 'Acme' })).toBe('Acme');
    expect(roleScopeLabel('12345678-aaaa', {})).toBe('Tenant 12345678');
  });
});
