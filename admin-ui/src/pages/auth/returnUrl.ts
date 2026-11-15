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

// Base path the SPA is served under, without trailing slash ('/auth/' -> '/auth', host root -> '').
// VITE_BASE_PATH wins when it names a sub-path. When it is '/' or unset, the image may still be
// served behind a gateway under /auth (the original deployment: the Docker image defaults to '/'),
// so the URL the browser is actually on decides: /auth or /auth/... means '/auth', anything else
// (for example /login at a customer host root) means ''.
export function getBasePath(pathname: string = window.location.pathname ?? ''): string {
  const configured = (import.meta.env.VITE_BASE_PATH ?? '/').replace(/\/$/, '');
  if (configured) return configured;
  return pathname === '/auth' || pathname.startsWith('/auth/') ? '/auth' : '';
}

// Shared post-authentication redirect used by every login completion path
// (password, SMS OTP, 2FA email code/link, magic link). OIDC authorize requests
// need a full-page navigation so the browser sends the session cookie to the
// backend; everything else is an in-app router navigation.
// The backend sees the path with the base path already stripped, so /connect/*
// return URLs get the base path prepended ('/auth' under the /auth deployment,
// nothing at the host root).
export function navigateAfterAuth(
  returnUrl: string,
  navigate: (path: string) => void,
  basePath: string = getBasePath()
): void {
  const prefixed = basePath ? basePath + '/connect/' : '';
  if (returnUrl.startsWith('/connect/') || (prefixed && returnUrl.startsWith(prefixed))) {
    window.location.href = returnUrl.startsWith('/connect/') ? basePath + returnUrl : returnUrl;
  } else if (basePath && !returnUrl.startsWith(basePath + '/') && returnUrl !== basePath) {
    // returnUrl is outside the React app's basename (e.g. /auth) — React Router
    // cannot cross the basename boundary, so a full-page navigation is required.
    window.location.href = returnUrl;
  } else {
    navigate(returnUrl);
  }
}
