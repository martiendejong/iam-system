/**
 * Defence in depth for tenant branding on the login page (task 5150). The API validates branding on save and again on
 * read; this is the last check before the value reaches the DOM. It is deliberately stricter than the server and
 * mirrors its rules: no at-rules, escapes, markup, attribute selectors or anything that can load a resource.
 */
const FORBIDDEN_CSS = /[\\<@[\]]|url\s*\(|image-set|image\s*\(|expression|behavior|binding|javascript|vbscript|attr\s*\(|env\s*\(|var\s*\(|src\s*\(/i;

export function isSafeCustomCss(css: string | null | undefined): css is string {
  if (!css || css.length > 20000) {
    return false;
  }

  // Comments are removed first so a forbidden token cannot be split by one (u/**/rl).
  const stripped = css.replace(/\/\*[\s\S]*?\*\//g, '');
  if (stripped.includes('/*')) {
    return false;
  }

  return !FORBIDDEN_CSS.test(stripped);
}

/** An http(s) URL or a site-relative path without anything that could break out of url('...'). */
export function isSafeImageUrl(url: string | null | undefined): url is string {
  if (!url || url.length > 2000 || /[\s"'()\\<>`]/.test(url)) {
    return false;
  }

  if (url.startsWith('/') && !url.startsWith('//')) {
    return true;
  }

  try {
    const parsed = new URL(url);
    return (parsed.protocol === 'https:' || parsed.protocol === 'http:') && !parsed.username && !parsed.password;
  } catch {
    return false;
  }
}
