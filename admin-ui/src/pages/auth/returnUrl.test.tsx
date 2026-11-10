import { describe, it, expect, vi } from 'vitest';
import { sanitizeReturnUrl, navigateAfterAuth } from './returnUrl';

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

describe('navigateAfterAuth', () => {
  it('uses window.location.href for OIDC /connect/ paths', () => {
    const navigate = vi.fn();
    const original = window.location.href;
    Object.defineProperty(window, 'location', { value: { href: original }, writable: true });
    navigateAfterAuth('/connect/authorize?client_id=x', navigate);
    expect(window.location.href).toBe('/auth/connect/authorize?client_id=x');
    expect(navigate).not.toHaveBeenCalled();
  });

  it('uses window.location.href for paths outside the /auth basename (e.g. /jengo-agi/)', () => {
    const navigate = vi.fn();
    Object.defineProperty(window, 'location', { value: { href: '' }, writable: true });
    navigateAfterAuth('/jengo-agi/', navigate);
    expect(window.location.href).toBe('/jengo-agi/');
    expect(navigate).not.toHaveBeenCalled();
  });

  it('calls navigate() for paths inside the /auth basename', () => {
    const navigate = vi.fn();
    navigateAfterAuth('/auth/dashboard', navigate);
    expect(navigate).toHaveBeenCalledWith('/auth/dashboard');
  });
});
