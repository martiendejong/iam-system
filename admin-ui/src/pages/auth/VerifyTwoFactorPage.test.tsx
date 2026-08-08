import { render, screen, waitFor, cleanup } from '@testing-library/react';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import VerifyTwoFactorPage from './VerifyTwoFactorPage';
import { useAuth } from '../../context/AuthContext';
import { api } from '../../services/api';

vi.mock('../../context/AuthContext', () => ({
  useAuth: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  api: {
    verifyLoginTwoFactor: vi.fn(),
  },
}));

const mockedUseAuth = vi.mocked(useAuth);
const mockedApi = vi.mocked(api);

function renderVerifyPage(query: string) {
  return render(
    <MemoryRouter initialEntries={[`/verify-2fa${query}`]}>
      <Routes>
        <Route path="/verify-2fa" element={<VerifyTwoFactorPage />} />
        <Route path="/dashboard" element={<div>Dashboard Page</div>} />
        <Route path="/portal/profile" element={<div>Portal Profile Page</div>} />
      </Routes>
    </MemoryRouter>
  );
}

describe('VerifyTwoFactorPage returnUrl navigation', () => {
  const setCurrentUser = vi.fn();
  let originalLocation: Location;

  beforeEach(() => {
    vi.clearAllMocks();
    mockedUseAuth.mockReturnValue({
      isAuthenticated: false,
      loading: false,
      user: null,
      login: vi.fn(),
      logout: vi.fn(),
      setCurrentUser,
    });

    originalLocation = window.location;
    Object.defineProperty(window, 'location', {
      writable: true,
      value: { ...originalLocation, href: '' },
    });
  });

  afterEach(() => {
    cleanup();
    Object.defineProperty(window, 'location', {
      writable: true,
      value: originalLocation,
    });
  });

  it('redirects to a plain local returnUrl instead of /dashboard', async () => {
    mockedApi.verifyLoginTwoFactor.mockResolvedValue({
      accessToken: 'tok',
      user: { id: '1', email: 'a@b.com', firstName: 'A', lastName: 'B' },
    });

    renderVerifyPage(`?userId=user-1&code=123456&returnUrl=${encodeURIComponent('/portal/profile')}`);

    await waitFor(() => expect(mockedApi.verifyLoginTwoFactor).toHaveBeenCalledWith('user-1', '123456'));
    await waitFor(() => expect(screen.getByText('Portal Profile Page')).toBeInTheDocument(), { timeout: 2000 });
    expect(window.location.href).toBe('');
  });

  it('does a full-page navigation to the OIDC authorize endpoint', async () => {
    mockedApi.verifyLoginTwoFactor.mockResolvedValue({
      accessToken: 'tok',
      user: { id: '1', email: 'a@b.com', firstName: 'A', lastName: 'B' },
    });

    renderVerifyPage(`?userId=user-1&code=123456&returnUrl=${encodeURIComponent('/connect/authorize?client_id=jengo-agi')}`);

    await waitFor(() => expect(window.location.href).toBe('/auth/connect/authorize?client_id=jengo-agi'), { timeout: 2000 });
  });

  it('falls back to /dashboard when returnUrl is absent', async () => {
    mockedApi.verifyLoginTwoFactor.mockResolvedValue({
      accessToken: 'tok',
      user: { id: '1', email: 'a@b.com', firstName: 'A', lastName: 'B' },
    });

    renderVerifyPage('?userId=user-1&code=123456');

    await waitFor(() => expect(screen.getByText('Dashboard Page')).toBeInTheDocument(), { timeout: 2000 });
  });

  it('falls back to /dashboard when returnUrl is an open-redirect attempt', async () => {
    mockedApi.verifyLoginTwoFactor.mockResolvedValue({
      accessToken: 'tok',
      user: { id: '1', email: 'a@b.com', firstName: 'A', lastName: 'B' },
    });

    renderVerifyPage(`?userId=user-1&code=123456&returnUrl=${encodeURIComponent('https://evil.example.com/phish')}`);

    await waitFor(() => expect(screen.getByText('Dashboard Page')).toBeInTheDocument(), { timeout: 2000 });
    expect(window.location.href).toBe('');
  });

  it('shows an error state when userId or code is missing', async () => {
    renderVerifyPage('');

    await waitFor(() => expect(screen.getByText('Verification failed')).toBeInTheDocument());
    expect(mockedApi.verifyLoginTwoFactor).not.toHaveBeenCalled();
  });
});
