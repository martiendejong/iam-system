import { render, screen, waitFor, cleanup } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import type { ReactNode } from 'react';
import RolesPage from './RolesPage';
import { api } from '../../services/api';

vi.mock('../../components/layout/DashboardLayout', () => ({
  default: ({ children }: { children: ReactNode }) => <div>{children}</div>,
}));
vi.mock('../../services/api', () => ({
  api: { getRoles: vi.fn(), getTenants: vi.fn(), getMyTenants: vi.fn(), deleteRole: vi.fn() },
}));

const mockedApi = vi.mocked(api);

describe('RolesPage (task 5222)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockedApi.getTenants.mockRejectedValue(new Error('403'));
    mockedApi.getMyTenants.mockResolvedValue([{ id: 't1', name: 'Own Building' }]);
  });
  afterEach(cleanup);

  it('shows each role tenant (or Global) plus the System badge and no Delete for system roles', async () => {
    mockedApi.getRoles.mockResolvedValue([
      { id: 'r1', name: 'Caretaker', description: 'x', category: 'Real Estate', isSystem: false, tenantId: 't1' },
      { id: 'r2', name: 'SuperAdmin', description: 'y', category: 'System', isSystem: true, tenantId: null },
    ]);
    render(<MemoryRouter><RolesPage /></MemoryRouter>);

    await screen.findByText('Caretaker');
    await waitFor(() => expect(screen.getByText('Own Building')).toBeTruthy());
    expect(screen.getByText('Global')).toBeTruthy();
    expect(screen.getAllByText('System').length).toBeGreaterThan(0);
    // Exactly one Delete button: the non-system role.
    expect(screen.getAllByRole('button', { name: 'Delete' })).toHaveLength(1);
  });
});
