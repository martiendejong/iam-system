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
