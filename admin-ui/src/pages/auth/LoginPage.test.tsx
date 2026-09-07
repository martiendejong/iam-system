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
    resendLoginTwoFactorCode: vi.fn(),
    verifyLoginTwoFactor: vi.fn(),
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

    await waitFor(() =>
      expect(login).toHaveBeenCalledWith({
        email: 'a@b.com',
        password: 'secret',
        returnUrl: '/connect/authorize?client_id=jengo-agi&redirect_uri=%2Fjengo-agi%2F',
        rememberMe: false,
      })
    );
    await waitFor(() =>
      expect(window.location.href).toBe('/auth/connect/authorize?client_id=jengo-agi&redirect_uri=%2Fjengo-agi%2F')
    );
  });

  it('passes rememberMe: true to login when the checkbox is checked', async () => {
    login.mockResolvedValue({ user: { id: '1', email: 'a@b.com' } });

    renderLoginPage('/portal/profile');

    fireEvent.change(screen.getByLabelText('Email address'), { target: { value: 'a@b.com' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'secret' } });
    fireEvent.click(screen.getByLabelText('Remember me'));
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    await waitFor(() =>
      expect(login).toHaveBeenCalledWith({
        email: 'a@b.com',
        password: 'secret',
        returnUrl: '/portal/profile',
        rememberMe: true,
      })
    );
  });

  it('defaults the "Remember me" checkbox to unchecked', () => {
    renderLoginPage('/portal/profile');

    expect(screen.getByLabelText('Remember me')).not.toBeChecked();
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

  it('includes the returnUrl when requesting a magic link', async () => {
    const post = vi.fn().mockResolvedValue({ data: { message: 'sent' } });
    mockedApi.getClient.mockReturnValue({ post } as never);

    renderLoginPage('/connect/authorize?client_id=jengo-agi');

    fireEvent.click(screen.getByRole('button', { name: 'Magic Link' }));
    fireEvent.change(screen.getByLabelText('Email address'), { target: { value: 'a@b.com' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send Magic Link' }));

    await waitFor(() =>
      expect(post).toHaveBeenCalledWith('/auth/magic-link/request', {
        email: 'a@b.com',
        returnUrl: '/connect/authorize?client_id=jengo-agi',
      })
    );
  });

  it('keeps the returnUrl when resending a 2FA code', async () => {
    login.mockResolvedValue({ requiresTwoFactor: true, userId: 'user-1', message: 'Code sent' });
    mockedApi.resendLoginTwoFactorCode.mockResolvedValue(undefined);

    renderLoginPage('/portal/profile');

    fireEvent.change(screen.getByLabelText('Email address'), { target: { value: 'a@b.com' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'secret' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    await waitFor(() => expect(screen.getByRole('button', { name: 'Resend code' })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: 'Resend code' }));

    await waitFor(() =>
      expect(mockedApi.resendLoginTwoFactorCode).toHaveBeenCalledWith('user-1', '/portal/profile')
    );
  });

  // Regression coverage for task 869eft1jr: an SMS OTP only proves phone possession.
  // An account with email 2FA enabled must not be signed in until that second factor
  // is verified too — mirrors the password + 2FA and magic-link + 2FA flows (869eft100).
  describe('when the account has email 2FA enabled', () => {
    it('does not sign the user in after SMS code verification and asks for the second factor instead', async () => {
      const post = vi.fn().mockResolvedValue({
        data: { requiresTwoFactor: true, userId: 'user-1', message: 'A verification code has been sent to your email.' },
      });
      mockedApi.getClient.mockReturnValue({ post } as never);

      renderLoginPage('/portal/profile');

      fireEvent.click(screen.getByRole('button', { name: 'SMS Code' }));
      fireEvent.change(screen.getByLabelText('Phone number'), { target: { value: '+15551234567' } });
      fireEvent.click(screen.getByRole('button', { name: 'Send Verification Code' }));
      await waitFor(() => expect(post).toHaveBeenCalledWith('/auth/otp/sms/request', { phoneNumber: '+15551234567' }));

      fireEvent.change(screen.getByLabelText('Verification code'), { target: { value: '123456' } });
      fireEvent.click(screen.getByRole('button', { name: 'Verify Code' }));

      await waitFor(() =>
        expect(post).toHaveBeenCalledWith('/auth/otp/sms/verify', { phoneNumber: '+15551234567', code: '123456' })
      );
      await waitFor(() => expect(screen.getByText('A verification code has been sent to your email.')).toBeInTheDocument());
      expect(setCurrentUser).not.toHaveBeenCalled();
      expect(window.location.href).toBe('');
    });

    it('completes sign-in via the second factor once the correct code is submitted', async () => {
      const post = vi.fn().mockResolvedValue({
        data: { requiresTwoFactor: true, userId: 'user-1', message: 'A verification code has been sent to your email.' },
      });
      mockedApi.getClient.mockReturnValue({ post } as never);
      mockedApi.verifyLoginTwoFactor.mockResolvedValue({
        user: { id: '1', email: 'a@b.com', firstName: 'A', lastName: 'B' },
      });

      renderLoginPage('/portal/profile');

      fireEvent.click(screen.getByRole('button', { name: 'SMS Code' }));
      fireEvent.change(screen.getByLabelText('Phone number'), { target: { value: '+15551234567' } });
      fireEvent.click(screen.getByRole('button', { name: 'Send Verification Code' }));
      await waitFor(() => expect(post).toHaveBeenCalledWith('/auth/otp/sms/request', { phoneNumber: '+15551234567' }));

      fireEvent.change(screen.getByLabelText('Verification code'), { target: { value: '123456' } });
      fireEvent.click(screen.getByRole('button', { name: 'Verify Code' }));
      await waitFor(() => expect(screen.getByLabelText('Verification code')).toBeInTheDocument());

      fireEvent.change(screen.getByLabelText('Verification code'), { target: { value: '654321' } });
      fireEvent.click(screen.getByRole('button', { name: 'Verify and Sign In' }));

      await waitFor(() => expect(mockedApi.verifyLoginTwoFactor).toHaveBeenCalledWith('user-1', '654321', false));
      await waitFor(() => expect(setCurrentUser).toHaveBeenCalledWith({ id: '1', email: 'a@b.com', firstName: 'A', lastName: 'B' }));
      await waitFor(() => expect(screen.getByText('Portal Profile Page')).toBeInTheDocument());
    });
  });
});
