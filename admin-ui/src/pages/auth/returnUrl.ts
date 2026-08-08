export const DEFAULT_RETURN_URL = '/dashboard';

// Open-redirect guard: only allow same-origin local paths. Rejects absolute URLs
// (must start with '/'), protocol-relative URLs ('//host/...'), and the
// backslash variant some browsers still normalize to '//' ('/\host/...').
export function sanitizeReturnUrl(raw: string | null): string {
  if (!raw) return DEFAULT_RETURN_URL;
  if (!raw.startsWith('/') || raw.startsWith('//') || raw.startsWith('/\\')) {
    return DEFAULT_RETURN_URL;
  }
  return raw;
}

// Shared post-authentication redirect used by every login completion path
// (password, SMS OTP, 2FA email code/link, magic link). OIDC authorize requests
// need a full-page navigation so the browser sends the session cookie to the
// backend; everything else is an in-app router navigation.
export function navigateAfterAuth(returnUrl: string, navigate: (path: string) => void): void {
  if (returnUrl.startsWith('/connect/') || returnUrl.startsWith('/auth/connect/')) {
    const fullUrl = returnUrl.startsWith('/auth/') ? returnUrl : '/auth' + returnUrl;
    window.location.href = fullUrl;
  } else {
    navigate(returnUrl);
  }
}
