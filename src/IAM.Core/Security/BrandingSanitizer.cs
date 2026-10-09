using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace IAM.Core.Security;

/// <summary>
/// Everything a tenant may put into its login page and e-mail branding goes through here (task 5150). Branding is shown
/// to anyone who opens the IAM login page and sent in e-mails, so it is restricted to safe values instead of stored as
/// submitted: custom CSS is an allow-list (no external resources, no attribute selectors, no at-rules), e-mail HTML is
/// reduced to an allow-list of tags/attributes, titles are plain text, colors and image URLs are checked, and a custom
/// domain must be a plain host name. The same checks run on save, on read (so rows stored before this fix are never
/// served as they were) and in the startup clean-up pass.
/// </summary>
public static class BrandingSanitizer
{
    public const int MaxCssLength = 20_000;
    public const int MaxEmailHtmlLength = 20_000;
    public const int MaxTitleLength = 120;
    public const int MaxSubtitleLength = 250;

    // ----- custom CSS --------------------------------------------------------------------------

    private static readonly HashSet<string> AllowedCssProperties = new(StringComparer.Ordinal)
    {
        "color", "background", "background-color", "background-image",
        "font-family", "font-size", "font-weight", "font-style", "line-height", "letter-spacing",
        "text-align", "text-transform", "text-decoration", "text-shadow",
        "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
        "padding", "padding-top", "padding-right", "padding-bottom", "padding-left",
        "border", "border-color", "border-width", "border-style", "border-radius",
        "border-top", "border-right", "border-bottom", "border-left",
        "box-shadow", "opacity",
    };

    private static readonly HashSet<string> AllowedCssFunctions = new(StringComparer.Ordinal)
    {
        "rgb", "rgba", "hsl", "hsla", "calc", "linear-gradient", "radial-gradient",
    };

    // Anything that can load a resource, run script, read an attribute/field value or reach outside the rule list.
    private static readonly string[] ForbiddenCssTokens =
    {
        "url(", "url (", "image-set", "-webkit-image", "image(", "cross-fade", "element(", "paint(", "src(",
        "expression", "behavior", "binding", "javascript", "vbscript", "attr(", "env(", "var(",
    };

    private static readonly Regex CssSelector = new(@"^[A-Za-z0-9_\-\s.#,>+~*:]+$", RegexOptions.Compiled);
    private static readonly Regex CssValue = new(@"^[A-Za-z0-9#%.,()\-+/\s'""!]+$", RegexOptions.Compiled);
    private static readonly Regex CssFunction = new(@"([A-Za-z\-]+)\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// Validates custom CSS. Returns null and the normalized text (comments removed) when it is acceptable, otherwise a
    /// clear error. Only plain rules of the form <c>selector { property: value; }</c> over an allow-list of presentation
    /// properties are accepted: no @-rules (@import, @font-face), no url()/image-set()/expression(), no attribute
    /// selectors, no escapes, no nested blocks, no markup.
    /// </summary>
    public static string? ValidateCss(string? css, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(css))
            return null;

        if (css.Length > MaxCssLength)
            return $"Custom CSS must be at most {MaxCssLength} characters.";

        if (css.Any(c => c < ' ' && c != '\t' && c != '\r' && c != '\n'))
            return "Custom CSS contains control characters.";

        if (css.IndexOfAny(new[] { '\\', '<', '@', '[', ']' }) >= 0)
            return "Custom CSS may not contain escapes, markup, @-rules or attribute selectors ( \\ < @ [ ] ).";

        // Comments are removed first so that nothing can be smuggled between the characters of a forbidden token
        // (u/**/rl(...)); the checks below run on the stripped text.
        var sb = new StringBuilder();
        for (var i = 0; i < css.Length; i++)
        {
            if (css[i] == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                    return "Custom CSS has an unterminated comment.";
                i = end + 1;
                continue;
            }
            sb.Append(css[i]);
        }

        var text = sb.ToString().Trim();
        if (text.Length == 0)
            return null;

        var lower = text.ToLowerInvariant();
        foreach (var token in ForbiddenCssTokens)
        {
            if (lower.Contains(token, StringComparison.Ordinal))
                return $"Custom CSS may not use '{token.TrimEnd('(', ' ')}'.";
        }

        var position = 0;
        while (position < text.Length)
        {
            var open = text.IndexOf('{', position);
            if (open < 0)
                return "Custom CSS must consist of rules like 'selector { property: value; }'.";

            var selector = text[position..open].Trim();
            if (selector.Length == 0 || !CssSelector.IsMatch(selector))
                return $"Custom CSS selector '{Truncate(selector)}' is not allowed (class, id, element, descendant and pseudo selectors only).";

            var close = text.IndexOf('}', open + 1);
            if (close < 0)
                return "Custom CSS has a rule without a closing brace.";

            var body = text[(open + 1)..close];
            if (body.Contains('{'))
                return "Nested CSS blocks are not allowed.";

            foreach (var declaration in body.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = declaration.Trim();
                if (trimmed.Length == 0)
                    continue;

                var colon = trimmed.IndexOf(':');
                if (colon <= 0)
                    return $"Custom CSS declaration '{Truncate(trimmed)}' is not valid.";

                var property = trimmed[..colon].Trim().ToLowerInvariant();
                var value = trimmed[(colon + 1)..].Trim();
                if (!AllowedCssProperties.Contains(property))
                    return $"Custom CSS property '{Truncate(property)}' is not allowed.";

                if (value.Length == 0 || !CssValue.IsMatch(value))
                    return $"Custom CSS value for '{property}' contains characters that are not allowed.";

                if (value.Count(c => c == '(') != value.Count(c => c == ')'))
                    return $"Custom CSS value for '{property}' has unbalanced parentheses.";

                foreach (Match function in CssFunction.Matches(value))
                {
                    if (!AllowedCssFunctions.Contains(function.Groups[1].Value.ToLowerInvariant()))
                        return $"Custom CSS function '{function.Groups[1].Value}()' is not allowed.";
                }
            }

            position = close + 1;
        }

        normalized = text;
        return null;
    }

    /// <summary>The stored CSS when it passes <see cref="ValidateCss"/>, otherwise null (never serve a rejected value).</summary>
    public static string? SafeCssOrNull(string? css) =>
        !string.IsNullOrWhiteSpace(css) && ValidateCss(css, out var normalized) == null ? normalized : null;

    // ----- plain values --------------------------------------------------------------------------

    private static readonly Regex HexColor = new(@"^#([0-9A-Fa-f]{3}|[0-9A-Fa-f]{4}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.Compiled);

    public static string? ValidateColor(string? color, string field) =>
        string.IsNullOrWhiteSpace(color) || HexColor.IsMatch(color.Trim())
            ? null
            : $"{field} must be a hex color such as #4F46E5.";

    /// <summary>Plain text only: no markup, no control characters, length-limited.</summary>
    public static string? ValidatePlainText(string? text, string field, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        if (text.Length > maxLength)
            return $"{field} must be at most {maxLength} characters.";

        if (text.IndexOfAny(new[] { '<', '>' }) >= 0 || text.Any(c => c < ' '))
            return $"{field} must be plain text (no HTML or control characters).";

        return null;
    }

    /// <summary>Strips markup from a stored plain-text value (used by the clean-up pass).</summary>
    public static string? StripToPlainText(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var cleaned = Regex.Replace(text, "<[^>]*>?", string.Empty);
        cleaned = new string(cleaned.Where(c => c >= ' ' && c != '<' && c != '>').ToArray()).Trim();
        if (cleaned.Length > maxLength)
            cleaned = cleaned[..maxLength];
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>An http(s) URL or a site-relative path, without anything that could break out of a url('...') or attribute.</summary>
    public static string? ValidateImageUrl(string? url, string field)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var value = url.Trim();
        if (value.Length > 2000 || value.Any(c => c <= ' ' || c is '"' or '\'' or '(' or ')' or '\\' or '<' or '>' or '`'))
            return $"{field} must be a plain URL without spaces, quotes, parentheses or markup.";

        if (value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal))
            return null;

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && string.IsNullOrEmpty(uri.UserInfo)
            ? null
            : $"{field} must be an http(s) URL or a path starting with '/'.";
    }

    public static string? SafeImageUrlOrNull(string? url) => ValidateImageUrl(url, "url") == null ? url?.Trim() : null;

    // ----- custom domain -------------------------------------------------------------------------

    private static readonly Regex HostName = new(
        @"^(?=.{1,253}$)([a-z0-9]([a-z0-9\-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$", RegexOptions.Compiled);

    /// <summary>Lower-cases and trims a custom domain; null when it is not a plain host name (no scheme, port, path, IP or localhost).</summary>
    public static string? NormalizeDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return null;

        var value = domain.Trim().TrimEnd('.').ToLowerInvariant();
        return HostName.IsMatch(value) ? value : null;
    }

    // ----- e-mail HTML ---------------------------------------------------------------------------

    private static readonly HashSet<string> VoidTags = new(StringComparer.Ordinal) { "br", "hr", "img" };

    private static readonly HashSet<string> AllowedTags = new(StringComparer.Ordinal)
    {
        "p", "br", "hr", "b", "strong", "i", "em", "u", "small", "span", "div", "a", "img",
        "h1", "h2", "h3", "h4", "ul", "ol", "li", "table", "thead", "tbody", "tr", "td", "th", "center",
    };

    // Tags whose whole content is dropped, not just the tag.
    private static readonly HashSet<string> DropContentTags = new(StringComparer.Ordinal)
    {
        "script", "style", "iframe", "object", "embed", "noscript", "template", "svg", "math", "textarea", "title",
        "head", "frameset", "frame", "applet", "xmp", "plaintext", "noframes", "select", "option", "button", "form",
    };

    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.Ordinal)
    {
        "href", "src", "alt", "title", "width", "height", "align", "valign", "colspan", "rowspan", "style",
        "cellpadding", "cellspacing", "border", "bgcolor", "target",
    };

    private static readonly Regex TagPattern = new(
        @"^<(/?)([A-Za-z][A-Za-z0-9]*)((?:[^>""']|""[^""]*""|'[^']*')*?)(/?)>", RegexOptions.Compiled);

    private static readonly Regex AttributePattern = new(
        @"([A-Za-z_:][-A-Za-z0-9_:.]*)(?:\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+)))?", RegexOptions.Compiled);

    /// <summary>
    /// Reduces e-mail header/footer HTML to an allow-list: no script/style/iframe/forms, no event handlers, no
    /// javascript:/data: URLs, no external CSS (style attributes keep only allow-listed declarations without url()).
    /// Unclosed tags are closed, comments and stray '&lt;' are removed or encoded.
    /// </summary>
    public static string? SanitizeEmailHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        if (html.Length > MaxEmailHtmlLength)
            html = html[..MaxEmailHtmlLength];

        var output = new StringBuilder();
        var open = new Stack<string>();
        var i = 0;

        while (i < html.Length)
        {
            var c = html[i];
            if (c != '<')
            {
                output.Append(c == '>' ? "&gt;" : c.ToString());
                i++;
                continue;
            }

            if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
            {
                var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? html.Length : end + 3;
                continue;
            }

            if (i + 1 < html.Length && (html[i + 1] == '!' || html[i + 1] == '?'))
            {
                // <!DOCTYPE ...>, <![CDATA[ ...]]>, <?xml ...?>: declarations carry no content, drop them.
                var declarationEnd = html.IndexOf('>', i + 2);
                i = declarationEnd < 0 ? html.Length : declarationEnd + 1;
                continue;
            }

            var match = TagPattern.Match(html[i..]);
            if (!match.Success)
            {
                // '<!DOCTYPE', '<?xml', '<' followed by a space or nothing parseable: encode, never pass through.
                output.Append("&lt;");
                i++;
                continue;
            }

            i += match.Length;
            var closing = match.Groups[1].Value == "/";
            var name = match.Groups[2].Value.ToLowerInvariant();

            if (!closing && DropContentTags.Contains(name))
            {
                var closeIndex = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                if (closeIndex < 0)
                {
                    i = html.Length;
                }
                else
                {
                    var closeEnd = html.IndexOf('>', closeIndex);
                    i = closeEnd < 0 ? html.Length : closeEnd + 1;
                }
                continue;
            }

            if (!AllowedTags.Contains(name))
                continue;

            if (closing)
            {
                if (!VoidTags.Contains(name) && open.Contains(name))
                {
                    while (open.Count > 0)
                    {
                        var top = open.Pop();
                        output.Append("</").Append(top).Append('>');
                        if (top == name)
                            break;
                    }
                }
                continue;
            }

            output.Append('<').Append(name);
            var hasHref = false;
            foreach (Match attribute in AttributePattern.Matches(match.Groups[3].Value))
            {
                var attributeName = attribute.Groups[1].Value.ToLowerInvariant();
                if (!AllowedAttributes.Contains(attributeName))
                    continue;

                var raw = attribute.Groups[2].Success ? attribute.Groups[2].Value
                    : attribute.Groups[3].Success ? attribute.Groups[3].Value
                    : attribute.Groups[4].Value;
                var value = WebUtility.HtmlDecode(raw);

                string? safe = attributeName switch
                {
                    "href" => SafeLink(value, allowMailto: true),
                    "src" => SafeLink(value, allowMailto: false),
                    "style" => SafeInlineStyle(value),
                    "target" => value is "_blank" or "_self" ? value : null,
                    _ => value.Any(ch => ch < ' ') ? null : value,
                };

                if (safe == null)
                    continue;

                if (attributeName == "href")
                    hasHref = true;

                output.Append(' ').Append(attributeName).Append("=\"").Append(WebUtility.HtmlEncode(safe)).Append('"');
            }

            if (name == "a" && hasHref)
                output.Append(" rel=\"noopener noreferrer\"");

            if (VoidTags.Contains(name))
            {
                output.Append(" />");
            }
            else
            {
                output.Append('>');
                open.Push(name);
            }
        }

        while (open.Count > 0)
            output.Append("</").Append(open.Pop()).Append('>');

        var result = output.ToString().Trim();
        return result.Length == 0 ? null : result;
    }

    /// <summary>http(s) (and optionally mailto) links only; entity/whitespace/control-character tricks are removed before the scheme is read.</summary>
    private static string? SafeLink(string value, bool allowMailto)
    {
        var compact = new string(value.Where(ch => ch > ' ' && ch != ' ').ToArray());
        if (compact.Length == 0 || compact.Length > 2000)
            return null;

        var lower = compact.ToLowerInvariant();
        if (lower.StartsWith("https://", StringComparison.Ordinal) || lower.StartsWith("http://", StringComparison.Ordinal)
            || (allowMailto && lower.StartsWith("mailto:", StringComparison.Ordinal)))
            return compact;

        return null;
    }

    private static string? SafeInlineStyle(string value)
    {
        // Same allow-list as the page CSS, applied to a declaration list.
        var error = ValidateCss("x{" + value + "}", out var normalized);
        if (error != null || normalized.Length < 3)
            return null;

        return normalized[2..^1].Trim();
    }

    private static string Truncate(string text) => text.Length <= 40 ? text : text[..40] + "...";
}
