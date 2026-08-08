import { render, screen, waitFor, cleanup } from '@testing-library/react';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import MagicLinkCallbackPage from './MagicLinkCallbackPage';
import { useAuth } from '../../context/AuthContext';
import { api } from '../../services/api';

vi.mock('../../context/AuthContext', () => ({
  useAuth: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  api: {
    verifyMagicLink: vi.fn(),
  },
}));

const mockedUseAuth = vi.mocked(useAuth);
const mockedApi = vi.mocked(api);

function renderCallbackPage(query: string) {
  return render(
    <MemoryRouter initialEntries={[`/magic-link${query}`]}>
      <Routes>
        <Route path="/magic-link" element={<MagicLinkCallbackPage />} />
        <Route path="/dashboard" element={<div>Dashboard Page</div>} />
        <Route path="/portal/profile" element={<div>Portal Profile Page</div>} />
        <Route path="/login" element={<div>Login Page</div>} />
      </Routes>
    </MemoryRouter>
  );
}

describe('MagicLinkCallbackPage', () => {
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

  it('shows a loading state while verifying the token', () => {
    mockedApi.verifyMagicLink.mockReturnValue(new Promise(() => {}));

    renderCallbackPage('?token=abc123');

    expect(screen.getByText('Signing you in…')).toBeInTheDocument();
  });

  it('signs the user in and redirects to a plain local returnUrl', async () => {
    mockedApi.verifyMagicLink.mockResolvedValue({
      accessToken: 'tok',
      user: { id: '1', email: 'a@b.com', firstName: 'A', lastName: 'B' },
    });

    renderCallbackPage(`?token=abc123&returnUrl=${encodeURIComponent('/portal/profile')}`);

    await waitFor(() => expect(mockedApi.verifyMagicLink).toHaveBeenCalledWith('abc123'));
    await waitFor(() => expect(setCurrentUser).toHaveBeenCalledWith({ id: '1', email: 'a@b.com', firstName: 'A', lastName: 'B' }));
    expect(screen.getByText("You're signed in!")).toBeInTheDocument();

    await waitFor(() => expect(screen.getByText('Portal Profile Page')).toBeInTheDocument(), { timeout: 2000 });
    expect(window.location.href).toBe('');
  });

  it('does a full-page navigation to the OIDC authorize endpoint', async () => {
    mockedApi.verifyMagicLink.mockResolvedValue({
      accessToken: 'tok',
      user: { id: '1', email: 'a@b.com', firstName: 'A', lastName: 'B' },
    });

    renderCallbackPage(`?token=abc123&returnUrl=${encodeURIComponent('/connect/authorize?client_id=jengo-agi')}`);

    await waitFor(
      () => expect(window.location.href).toBe('/auth/connect/authorize?client_id=jengo-agi'),
      { timeout: 2000 }
    );
  });

  it('falls back to /dashboard when returnUrl is an open-redirect attempt', async () => {
    mockedApi.verifyMagicLink.mockResolvedValue({
      accessToken: 'tok',
      user: { id: '1', email: 'a@b.com', firstName: 'A', lastName: 'B' },
    });

    renderCallbackPage(`?token=abc123&returnUrl=${encodeURIComponent('https://evil.example.com/phish')}`);

    await waitFor(() => expect(screen.getByText('Dashboard Page')).toBeInTheDocument(), { timeout: 2000 });
    expect(window.location.href).toBe('');
  });

  it('shows an error with a way back to login when the token is invalid or expired', async () => {
    mockedApi.verifyMagicLink.mockRejectedValue(new Error('invalid token'));

    renderCallbackPage('?token=expired-token');

    await waitFor(() => expect(screen.getByText('Sign-in failed')).toBeInTheDocument());
    expect(setCurrentUser).not.toHaveBeenCalled();
    screen.getByRole('link', { name: 'Back to sign in' });
  });

  it('shows an error when no token is present in the URL', async () => {
    renderCallbackPage('');

    await waitFor(() => expect(screen.getByText('Sign-in failed')).toBeInTheDocument());
    expect(mockedApi.verifyMagicLink).not.toHaveBeenCalled();
  });
});
