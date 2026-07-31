import { describe, it, expect } from 'vitest';
import { sanitizeReturnUrl } from './returnUrl';

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
