import { safeHexColor } from '../../utils/brandingSafety';

interface EmailPreviewFrameProps {
  headerHtml: string;
  footerHtml: string;
  primaryColor: string;
}

/**
 * Builds the preview document. Tenant HTML is untrusted: it only ever lands inside this document, which is rendered in a
 * fully sandboxed frame. The meta CSP is a second layer (no scripts, no network except https images).
 */
export function buildEmailPreviewDocument({ headerHtml, footerHtml, primaryColor }: EmailPreviewFrameProps): string {
  const color = safeHexColor(primaryColor);
  return `<!DOCTYPE html><html><head><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src https:; style-src 'unsafe-inline'">
<style>body{margin:0;font-family:system-ui,sans-serif;color:#374151}.body{padding:24px}.body p{margin:0 0 8px}.btn{display:inline-block;margin-top:8px;padding:8px 16px;background:${color};color:#fff;font-size:14px;font-weight:500;border-radius:6px;text-decoration:none}</style>
</head><body>${headerHtml}<div class="body"><p>Hello User,</p><p style="color:#4b5563">This is a preview of how your branded emails will look. The actual content will vary depending on the email type.</p><a class="btn" href="#">Action Button</a></div>${footerHtml}</body></html>`;
}

export default function EmailPreviewFrame(props: EmailPreviewFrameProps) {
  return (
    <iframe
      title="Email preview"
      // Empty sandbox: no scripts, no same-origin, no forms, no popups, no top navigation. Never add allow- tokens.
      sandbox=""
      referrerPolicy="no-referrer"
      srcDoc={buildEmailPreviewDocument(props)}
      className="w-full border rounded-lg bg-white"
      style={{ height: 420 }}
    />
  );
}
