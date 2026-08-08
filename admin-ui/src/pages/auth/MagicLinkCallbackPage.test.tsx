import { render, screen, fireEvent, waitFor, cleanup } from '@testing-library/react';
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
    verifyLoginTwoFactor: vi.fn(),
    resendLoginTwoFactorCode: vi.fn(),
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

  // Regression coverage for task 869eft100: a magic link only proves email possession.
  // An account with email 2FA enabled must not be signed in until that second factor
  // is verified too.
  describe('when the account has email 2FA enabled', () => {
    it('does not sign the user in and asks for a verification code instead', async () => {
      mockedApi.verifyMagicLink.mockResolvedValue({
        requiresTwoFactor: true,
        userId: 'user-1',
        message: 'A verification code has been sent to your email.',
      });

      renderCallbackPage('?token=abc123');

      await waitFor(() => expect(screen.getByText('A verification code has been sent to your email.')).toBeInTheDocument());
      expect(setCurrentUser).not.toHaveBeenCalled();
      expect(screen.queryByText("You're signed in!")).not.toBeInTheDocument();
    });

    it('completes sign-in and redirects once the correct code is submitted', async () => {
      mockedApi.verifyMagicLink.mockResolvedValue({
        requiresTwoFactor: true,
        userId: 'user-1',
        message: 'A verification code has been sent to your email.',
      });
      mockedApi.verifyLoginTwoFactor.mockResolvedValue({
        accessToken: 'tok',
        user: { id: '1', email: 'a@b.com', firstName: 'A', lastName: 'B' },
      });

      renderCallbackPage(`?token=abc123&returnUrl=${encodeURIComponent('/portal/profile')}`);

      await waitFor(() => expect(screen.getByLabelText('Verification code')).toBeInTheDocument());
      fireEvent.change(screen.getByLabelText('Verification code'), { target: { value: '123456' } });
      fireEvent.click(screen.getByRole('button', { name: 'Verify and Sign In' }));

      await waitFor(() => expect(mockedApi.verifyLoginTwoFactor).toHaveBeenCalledWith('user-1', '123456'));
      await waitFor(() => expect(setCurrentUser).toHaveBeenCalledWith({ id: '1', email: 'a@b.com', firstName: 'A', lastName: 'B' }));
      await waitFor(() => expect(screen.getByText('Portal Profile Page')).toBeInTheDocument(), { timeout: 2000 });
    });

    it('shows an error and does not sign in when the code is wrong', async () => {
      mockedApi.verifyMagicLink.mockResolvedValue({
        requiresTwoFactor: true,
        userId: 'user-1',
        message: 'A verification code has been sent to your email.',
      });
      mockedApi.verifyLoginTwoFactor.mockRejectedValue(new Error('invalid code'));

      renderCallbackPage('?token=abc123');

      await waitFor(() => expect(screen.getByLabelText('Verification code')).toBeInTheDocument());
      fireEvent.change(screen.getByLabelText('Verification code'), { target: { value: '000000' } });
      fireEvent.click(screen.getByRole('button', { name: 'Verify and Sign In' }));

      await waitFor(() => expect(screen.getByText('Invalid or expired code. Please try again.')).toBeInTheDocument());
      expect(setCurrentUser).not.toHaveBeenCalled();
    });

    it('resends the code on request', async () => {
      mockedApi.verifyMagicLink.mockResolvedValue({
        requiresTwoFactor: true,
        userId: 'user-1',
        message: 'A verification code has been sent to your email.',
      });
      mockedApi.resendLoginTwoFactorCode.mockResolvedValue(undefined);

      renderCallbackPage('?token=abc123');

      await waitFor(() => expect(screen.getByRole('button', { name: 'Resend code' })).toBeInTheDocument());
      fireEvent.click(screen.getByRole('button', { name: 'Resend code' }));

      await waitFor(() => expect(mockedApi.resendLoginTwoFactorCode).toHaveBeenCalledWith('user-1', '/dashboard'));
      await waitFor(() => expect(screen.getByText('A new verification code has been sent to your email.')).toBeInTheDocument());
    });
  });
});
