import { render, screen, fireEvent, waitFor, cleanup } from '@testing-library/react';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import type { ReactNode } from 'react';
import RoleFormPage from './RoleFormPage';
import { useAuth } from '../../context/AuthContext';
import { api } from '../../services/api';

vi.mock('../../context/AuthContext', () => ({ useAuth: vi.fn() }));
vi.mock('../../components/layout/DashboardLayout', () => ({
  default: ({ children }: { children: ReactNode }) => <div>{children}</div>,
}));
vi.mock('../../services/api', () => ({
  api: {
    getTenants: vi.fn(),
    getMyTenants: vi.fn(),
    createRole: vi.fn(),
    updateRole: vi.fn(),
    getRole: vi.fn(),
  },
}));

const mockedUseAuth = vi.mocked(useAuth);
const mockedApi = vi.mocked(api);

const OWN = '11111111-1111-1111-1111-111111111111';
const OTHER = '22222222-2222-2222-2222-222222222222';
const FUTURE = '2999-01-01T00:00:00Z';

function signIn(roles: Array<{ name: string; tenantId?: string | null; expiresAt?: string | null }>) {
  mockedUseAuth.mockReturnValue({
    user: { id: 'u1', email: 'a@b.c', firstName: 'A', lastName: 'B', isActive: true, emailConfirmed: true,
      createdAt: '', updatedAt: '', roles: roles.map((r, i) => ({ id: `r${i}`, ...r })) },
  } as unknown as ReturnType<typeof useAuth>);
}

function renderForm(path = '/roles/new') {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/roles/new" element={<RoleFormPage />} />
        <Route path="/roles/:id" element={<RoleFormPage />} />
        <Route path="/roles" element={<div>Roles list</div>} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('RoleFormPage tenant picker (task 5222)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockedApi.getMyTenants.mockResolvedValue([
      { id: OWN, name: 'Own Building' },
      { id: OTHER, name: 'Only a Member Here' },
    ]);
    mockedApi.getTenants.mockResolvedValue([
      { id: OWN, name: 'Own Building' },
      { id: OTHER, name: 'Other Root' },
    ]);
    mockedApi.createRole.mockResolvedValue({});
  });
  afterEach(cleanup);

  it('offers a BuildingOwner only the tenants they own and sends the tenantId', async () => {
    signIn([{ name: 'BuildingOwner', tenantId: OWN }, { name: 'User', tenantId: OTHER }]);
    renderForm();

    const select = (await screen.findByLabelText(/^Tenant/)) as HTMLSelectElement;
    await waitFor(() => expect(select.value).toBe(OWN)); // single owned tenant is preselected
    const labels = Array.from(select.options).map((o) => o.textContent);
    expect(labels).toContain('Own Building');
    expect(labels).not.toContain('Only a Member Here');
    expect(labels).not.toContain('Global (all tenants)');

    fireEvent.change(screen.getByLabelText(/Role Name/), { target: { value: 'Caretaker' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create Role' }));

    await waitFor(() => expect(mockedApi.createRole).toHaveBeenCalledTimes(1));
    expect(mockedApi.createRole).toHaveBeenCalledWith({ name: 'Caretaker', description: '', tenantId: OWN });
    expect(mockedApi.getTenants).not.toHaveBeenCalled();
  });

  it('ignores expired BuildingOwner assignments and global ones', async () => {
    signIn([
      { name: 'BuildingOwner', tenantId: OWN, expiresAt: '2000-01-01T00:00:00Z' },
      { name: 'BuildingOwner', tenantId: null },
    ]);
    renderForm();

    expect(await screen.findByRole('alert')).toHaveTextContent(/do not own an active tenant/i);
    expect(screen.getByRole('button', { name: 'Create Role' })).toBeDisabled();
    expect(mockedApi.createRole).not.toHaveBeenCalled();
  });

  it('SuperAdmin defaults to Global and sends no tenantId', async () => {
    signIn([{ name: 'SuperAdmin', tenantId: null, expiresAt: FUTURE }]);
    renderForm();

    const select = (await screen.findByLabelText(/^Tenant/)) as HTMLSelectElement;
    await waitFor(() => expect(select.options.length).toBe(3));
    expect(select.value).toBe('');
    expect(select.options[0].textContent).toBe('Global (all tenants)');

    fireEvent.change(screen.getByLabelText(/Role Name/), { target: { value: 'PlatformAuditor' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create Role' }));

    await waitFor(() => expect(mockedApi.createRole).toHaveBeenCalledTimes(1));
    expect(mockedApi.createRole).toHaveBeenCalledWith({ name: 'PlatformAuditor', description: '' });
  });

  it('SuperAdmin can pick a tenant instead', async () => {
    signIn([{ name: 'SuperAdmin', tenantId: null }]);
    renderForm();

    const select = (await screen.findByLabelText(/^Tenant/)) as HTMLSelectElement;
    await waitFor(() => expect(select.options.length).toBe(3));
    fireEvent.change(select, { target: { value: OTHER } });
    fireEvent.change(screen.getByLabelText(/Role Name/), { target: { value: 'Tenant Role' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create Role' }));

    await waitFor(() => expect(mockedApi.createRole).toHaveBeenCalledTimes(1));
    expect(mockedApi.createRole).toHaveBeenCalledWith({ name: 'Tenant Role', description: '', tenantId: OTHER });
  });

  it('shows the API refusal text, not the generic message', async () => {
    signIn([{ name: 'BuildingOwner', tenantId: OWN }]);
    mockedApi.createRole.mockRejectedValue({
      response: { status: 403, data: { error: 'Building owners can only create roles in tenants they own.' } },
    });
    renderForm();

    await waitFor(() => expect((screen.getByLabelText(/^Tenant/) as HTMLSelectElement).value).toBe(OWN));
    fireEvent.change(screen.getByLabelText(/Role Name/), { target: { value: 'Admin' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create Role' }));

    expect(await screen.findByText('Building owners can only create roles in tenants they own.')).toBeTruthy();
    expect(screen.queryByText('Failed to create role')).toBeNull();
  });

  it('requires a BuildingOwner with several tenants to choose one', async () => {
    const SECOND = '33333333-3333-3333-3333-333333333333';
    mockedApi.getMyTenants.mockResolvedValue([
      { id: OWN, name: 'Own Building' },
      { id: SECOND, name: 'Second Building' },
    ]);
    signIn([{ name: 'BuildingOwner', tenantId: OWN }, { name: 'BuildingOwner', tenantId: SECOND }]);
    renderForm();

    const select = (await screen.findByLabelText(/^Tenant/)) as HTMLSelectElement;
    await waitFor(() => expect(select.options.length).toBe(3));
    expect(select.value).toBe('');

    fireEvent.change(screen.getByLabelText(/Role Name/), { target: { value: 'Caretaker' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create Role' }));

    expect(await screen.findByText('Pick one of your tenants')).toBeTruthy();
    expect(mockedApi.createRole).not.toHaveBeenCalled();
  });

  it('edit mode shows the system warning from isSystem and has no tenant picker', async () => {
    signIn([{ name: 'SuperAdmin', tenantId: null }]);
    mockedApi.getRole.mockResolvedValue({ id: 'x', name: 'BuildingOwner', description: 'd', isSystem: true, tenantId: null });
    renderForm('/roles/x');

    expect(await screen.findByText(/This is a system role/)).toBeTruthy();
    expect(screen.queryByLabelText(/^Tenant/)).toBeNull();
  });
});
