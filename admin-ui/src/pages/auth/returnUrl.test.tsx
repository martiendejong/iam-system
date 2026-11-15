import { describe, it, expect, vi } from 'vitest';
import { sanitizeReturnUrl, navigateAfterAuth, getBasePath } from './returnUrl';

describe('sanitizeReturnUrl', () => {
  it('falls back to /dashboard when returnUrl is absent', () => {
    expect(sanitizeReturnUrl(null)).toBe('/dashboard');
  });

  it('falls back to /dashboard for an empty string', () => {
    expect(sanitizeReturnUrl('')).toBe('/dashboard');
  });

  it('allows a decoded local OIDC authorize path', () => {
    expect(sanitizeReturnUrl('/connect/authorize?client_id=jengo-agi&redirect_uri=%2Fjengo-agi%2F')).toBe(
      '/connect/authorize?client_id=jengo-agi&redirect_uri=%2Fjengo-agi%2F'
    );
  });

  it('allows an ordinary local path', () => {
    expect(sanitizeReturnUrl('/portal/profile')).toBe('/portal/profile');
  });

  it('rejects an absolute URL (does not start with /)', () => {
    expect(sanitizeReturnUrl('https://evil.example.com/phish')).toBe('/dashboard');
  });

  it('rejects a protocol-relative URL (//host/...)', () => {
    expect(sanitizeReturnUrl('//evil.example.com/phish')).toBe('/dashboard');
  });

  it('rejects the backslash open-redirect variant (/\\host/...)', () => {
    expect(sanitizeReturnUrl('/\\evil.example.com/phish')).toBe('/dashboard');
  });

  it('rejects a bare protocol string like javascript: (does not start with /)', () => {
    expect(sanitizeReturnUrl('javascript:alert(1)')).toBe('/dashboard');
  });
});

describe('getBasePath', () => {
  it('is /auth when the browser is on the /auth deployment, even with the default VITE_BASE_PATH', () => {
    expect(getBasePath('/auth/login')).toBe('/auth');
    expect(getBasePath('/auth')).toBe('/auth');
  });

  it('is empty at a host root (e.g. /login)', () => {
    expect(getBasePath('/login')).toBe('');
    expect(getBasePath('/')).toBe('');
    expect(getBasePath('/authors')).toBe('');
  });
});

describe('navigateAfterAuth under the /auth deployment (unchanged behaviour)', () => {
  const setLocation = (href: string, pathname: string) =>
    Object.defineProperty(window, 'location', { value: { href, pathname }, writable: true });

  it('uses window.location.href for OIDC /connect/ paths', () => {
    const navigate = vi.fn();
    setLocation('', '/auth/login');
    navigateAfterAuth('/connect/authorize?client_id=x', navigate);
    expect(window.location.href).toBe('/auth/connect/authorize?client_id=x');
    expect(navigate).not.toHaveBeenCalled();
  });

  it('keeps an already prefixed /auth/connect/ path as is', () => {
    const navigate = vi.fn();
    setLocation('', '/auth/login');
    navigateAfterAuth('/auth/connect/authorize?client_id=x', navigate);
    expect(window.location.href).toBe('/auth/connect/authorize?client_id=x');
    expect(navigate).not.toHaveBeenCalled();
  });

  it('uses window.location.href for paths outside the /auth basename (e.g. /jengo-agi/)', () => {
    const navigate = vi.fn();
    setLocation('', '/auth/login');
    navigateAfterAuth('/jengo-agi/', navigate);
    expect(window.location.href).toBe('/jengo-agi/');
    expect(navigate).not.toHaveBeenCalled();
  });

  it('calls navigate() for paths inside the /auth basename', () => {
    const navigate = vi.fn();
    setLocation('', '/auth/login');
    navigateAfterAuth('/auth/dashboard', navigate);
    expect(navigate).toHaveBeenCalledWith('/auth/dashboard');
  });
});

describe('navigateAfterAuth at a host root (VITE_BASE_PATH=/)', () => {
  const setLocation = () =>
    Object.defineProperty(window, 'location', { value: { href: '', pathname: '/login' }, writable: true });

  it('goes to /connect/authorize without any /auth prefix', () => {
    const navigate = vi.fn();
    setLocation();
    navigateAfterAuth('/connect/authorize?client_id=taskmanager', navigate);
    expect(window.location.href).toBe('/connect/authorize?client_id=taskmanager');
    expect(navigate).not.toHaveBeenCalled();
  });

  it('uses the router for in-app paths', () => {
    const navigate = vi.fn();
    setLocation();
    navigateAfterAuth('/dashboard', navigate);
    expect(navigate).toHaveBeenCalledWith('/dashboard');
  });
});
