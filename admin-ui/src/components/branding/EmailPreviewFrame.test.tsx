import { describe, it, expect, vi } from 'vitest';
import { render } from '@testing-library/react';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import EmailPreviewFrame from './EmailPreviewFrame';

describe('EmailPreviewFrame', () => {
  const payload = '<img src=x onerror=alert(1)><script>alert(2)</script>';

  it('renders in a frame with an empty sandbox and executes nothing in the admin page', () => {
    const alertSpy = vi.fn();
    vi.stubGlobal('alert', alertSpy);
    const { container } = render(<EmailPreviewFrame headerHtml={payload} footerHtml={payload} primaryColor="#112233" />);
    const frame = container.querySelector('iframe')!;
    expect(frame.getAttribute('sandbox')).toBe('');
    expect(frame.getAttribute('sandbox')).not.toMatch(/allow-/);
    expect(container.querySelector('img, script')).toBeNull();
    expect(alertSpy).not.toHaveBeenCalled();
    vi.unstubAllGlobals();
  });

  it('keeps normal header, footer and the primary-colour button in the preview document', () => {
    const header = '<table><tr><td style="background:#fff"><img src="https://example.com/logo.png" alt="Logo"></td></tr></table>';
    const footer = '<p><a href="https://example.com">Site</a></p>';
    const { container } = render(<EmailPreviewFrame headerHtml={header} footerHtml={footer} primaryColor="#112233" />);
    const doc = container.querySelector('iframe')!.getAttribute('srcdoc')!;
    expect(doc).toContain(header);
    expect(doc).toContain(footer);
    expect(doc).toContain('background:#112233');
    expect(doc).toContain('Action Button');
  });

  it('refuses a non-hex primary colour', () => {
    const { container } = render(<EmailPreviewFrame headerHtml="" footerHtml="" primaryColor="red;}</style><script>x</script>" />);
    const doc = container.querySelector('iframe')!.getAttribute('srcdoc')!;
    expect(doc).not.toContain('<script>x');
    expect(doc).toContain('background:#4F46E5');
  });

  it('admin UI source has no raw HTML insertion', () => {
    const page = readFileSync(resolve(__dirname, '../../pages/branding/TenantBrandingPage.tsx'), 'utf8');
    expect(page).not.toMatch(/dangerouslySetInnerHTML|innerHTML/);
  });
});
