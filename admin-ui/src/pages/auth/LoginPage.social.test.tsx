import { render, screen, waitFor, cleanup } from '@testing-library/react';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import LoginPage from './LoginPage';
import { useAuth } from '../../context/AuthContext';
import { api } from '../../services/api';
import { brandingApi } from '../../services/brandingApi';

vi.mock('../../context/AuthContext', () => ({ useAuth: vi.fn() }));
vi.mock('../../services/api', () => ({
  api: {
    getPublicIdentityProviders: vi.fn(),
    getSocialAuthUrl: vi.fn(),
    socialAuthCallback: vi.fn(),
    getClient: vi.fn(),
    resendLoginTwoFactorCode: vi.fn(),
    verifyLoginTwoFactor: vi.fn(),
  },
}));
vi.mock('../../services/brandingApi', () => ({
  brandingApi: { getPublicBranding: vi.fn(), getBrandingByDomain: vi.fn() },
}));

const mockedApi = vi.mocked(api);

function renderReturn(query: string) {
  return render(
    <MemoryRouter initialEntries={[`/login?${query}`]}>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/dashboard" element={<div>Dashboard Page</div>} />
      </Routes>
    </MemoryRouter>
  );
}

describe('LoginPage social sign-in return leg (task 5493)', () => {
  const setCurrentUser = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
    sessionStorage.clear();
    vi.mocked(useAuth).mockReturnValue({
      isAuthenticated: false, loading: false, user: null,
      login: vi.fn(), logout: vi.fn(), setCurrentUser,
    });
    mockedApi.getPublicIdentityProviders.mockResolvedValue([]);
    vi.mocked(brandingApi.getBrandingByDomain).mockRejectedValue(new Error('no branding'));
    vi.mocked(brandingApi.getPublicBranding).mockRejectedValue(new Error('no branding'));
  });

  afterEach(() => cleanup());

  it('sends the stored browser secret with the code and state, then signs in', async () => {
    sessionStorage.setItem('iam.social.flow', JSON.stringify({ providerId: 'p1', state: 's1', browserSecret: 'sec' }));
    mockedApi.socialAuthCallback.mockResolvedValue({ user: { id: '1', email: 'a@b.com' } } as never);

    renderReturn('code=abc&state=s1');

    await waitFor(() => expect(mockedApi.socialAuthCallback).toHaveBeenCalledWith('p1', 'abc', 's1', 'sec'));
    await waitFor(() => expect(setCurrentUser).toHaveBeenCalled());
    expect(sessionStorage.getItem('iam.social.flow')).toBeNull();
  });

  it('does not call the callback when no secret was kept by this browser', async () => {
    renderReturn('code=abc&state=s1');

    await waitFor(() => expect(screen.getByText(/not started in this browser/i)).toBeInTheDocument());
    expect(mockedApi.socialAuthCallback).not.toHaveBeenCalled();
  });

  it('does not call the callback when the returned state differs from the stored one', async () => {
    sessionStorage.setItem('iam.social.flow', JSON.stringify({ providerId: 'p1', state: 'other', browserSecret: 'sec' }));

    renderReturn('code=abc&state=s1');

    await waitFor(() => expect(screen.getByText(/not started in this browser/i)).toBeInTheDocument());
    expect(mockedApi.socialAuthCallback).not.toHaveBeenCalled();
  });

  it('moves to the 2FA step when the callback asks for a second factor', async () => {
    sessionStorage.setItem('iam.social.flow', JSON.stringify({ providerId: 'p1', state: 's1', browserSecret: 'sec' }));
    mockedApi.socialAuthCallback.mockResolvedValue({ requiresTwoFactor: true, userId: 'u1', message: 'Code sent' } as never);

    renderReturn('code=abc&state=s1');

    await waitFor(() => expect(screen.getByText('Code sent')).toBeInTheDocument());
    expect(setCurrentUser).not.toHaveBeenCalled();
  });
});
