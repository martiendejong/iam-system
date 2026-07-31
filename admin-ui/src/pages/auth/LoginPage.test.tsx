import { render, screen, fireEvent, waitFor, cleanup } from '@testing-library/react';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import LoginPage from './LoginPage';
import { useAuth } from '../../context/AuthContext';
import { api } from '../../services/api';
import { brandingApi } from '../../services/brandingApi';

vi.mock('../../context/AuthContext', () => ({
  useAuth: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  api: {
    getIdentityProviders: vi.fn(),
    getClient: vi.fn(),
  },
}));

vi.mock('../../services/brandingApi', () => ({
  brandingApi: {
    getPublicBranding: vi.fn(),
    getBrandingByDomain: vi.fn(),
  },
}));

const mockedUseAuth = vi.mocked(useAuth);
const mockedApi = vi.mocked(api);
const mockedBrandingApi = vi.mocked(brandingApi);

function renderLoginPage(returnUrl: string) {
  return render(
    <MemoryRouter initialEntries={[`/login?returnUrl=${encodeURIComponent(returnUrl)}`]}>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/dashboard" element={<div>Dashboard Page</div>} />
        <Route path="/portal/profile" element={<div>Portal Profile Page</div>} />
      </Routes>
    </MemoryRouter>
  );
}

describe('LoginPage returnUrl navigation', () => {
  const setCurrentUser = vi.fn();
  const login = vi.fn();
  let originalLocation: Location;

  beforeEach(() => {
    vi.clearAllMocks();
    mockedUseAuth.mockReturnValue({
      isAuthenticated: false,
      loading: false,
      user: null,
      login,
      logout: vi.fn(),
      setCurrentUser,
    });
    mockedApi.getIdentityProviders.mockResolvedValue([]);
    mockedBrandingApi.getBrandingByDomain.mockRejectedValue(new Error('no branding'));
    mockedBrandingApi.getPublicBranding.mockRejectedValue(new Error('no branding'));

    originalLocation = window.location;
    // jsdom throws "Not implemented: navigation" on a real assignment to
    // window.location.href; stub it so we can assert what the app tried to set.
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

  it('does a full-page navigation to the OIDC authorize endpoint after password login', async () => {
    login.mockResolvedValue({ user: { id: '1', email: 'a@b.com' } });

    renderLoginPage('/connect/authorize?client_id=jengo-agi&redirect_uri=%2Fjengo-agi%2F');

    fireEvent.change(screen.getByLabelText('Email address'), { target: { value: 'a@b.com' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'secret' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    await waitFor(() => expect(login).toHaveBeenCalledWith({ email: 'a@b.com', password: 'secret' }));
    await waitFor(() =>
      expect(window.location.href).toBe('/auth/connect/authorize?client_id=jengo-agi&redirect_uri=%2Fjengo-agi%2F')
    );
  });

  it('performs an in-app navigation to a plain local returnUrl after password login', async () => {
    login.mockResolvedValue({ user: { id: '1', email: 'a@b.com' } });

    renderLoginPage('/portal/profile');

    fireEvent.change(screen.getByLabelText('Email address'), { target: { value: 'a@b.com' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'secret' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    await waitFor(() => expect(screen.getByText('Portal Profile Page')).toBeInTheDocument());
    expect(window.location.href).toBe('');
  });

  it('falls back to /dashboard when returnUrl is an open-redirect attempt', async () => {
    login.mockResolvedValue({ user: { id: '1', email: 'a@b.com' } });

    renderLoginPage('https://evil.example.com/phish');

    fireEvent.change(screen.getByLabelText('Email address'), { target: { value: 'a@b.com' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'secret' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    await waitFor(() => expect(screen.getByText('Dashboard Page')).toBeInTheDocument());
    expect(window.location.href).toBe('');
  });

  it('honors the OIDC returnUrl after SMS OTP verification', async () => {
    const post = vi.fn().mockResolvedValue({
      data: {
        accessToken: 'tok',
        user: { id: '1', email: 'a@b.com', firstName: 'A', lastName: 'B' },
      },
    });
    mockedApi.getClient.mockReturnValue({ post } as never);

    renderLoginPage('/connect/authorize?client_id=jengo-agi');

    fireEvent.click(screen.getByRole('button', { name: 'SMS Code' }));
    fireEvent.change(screen.getByLabelText('Phone number'), { target: { value: '+15551234567' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send Verification Code' }));
    await waitFor(() => expect(post).toHaveBeenCalledWith('/auth/otp/sms/request', { phoneNumber: '+15551234567' }));

    fireEvent.change(screen.getByLabelText('Verification code'), { target: { value: '123456' } });
    fireEvent.click(screen.getByRole('button', { name: 'Verify Code' }));

    await waitFor(() =>
      expect(post).toHaveBeenCalledWith('/auth/otp/sms/verify', { phoneNumber: '+15551234567', code: '123456' })
    );
    await waitFor(() => expect(setCurrentUser).toHaveBeenCalledWith({ id: '1', email: 'a@b.com', firstName: 'A', lastName: 'B' }));
    await waitFor(() => expect(window.location.href).toBe('/auth/connect/authorize?client_id=jengo-agi'));
  });
});
