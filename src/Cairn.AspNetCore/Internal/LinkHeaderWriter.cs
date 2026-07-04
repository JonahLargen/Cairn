using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Advertises a resource's computed links as an RFC 8288 <c>Link</c> response header, so clients and
/// intermediaries that never parse the body still see its navigation links. Only the top-level (context)
/// resource's links are emitted — an embedded child or every element of a collection would multiply the
/// header set — and a templated link is skipped, as an RFC 6570 template is not a valid URI-Reference for a
/// header target. The header is appended (never assigned), so it composes with the <c>rel="deprecation"</c>
/// link <c>WithDeprecation</c> emits.
/// </summary>
internal static class LinkHeaderWriter
{
    private const char Delete = '\u007f';

    public static void Emit(HttpContext http, ResourceHypermedia hypermedia)
    {
        if (hypermedia.Links is not { Count: > 0 } links)
        {
            return;
        }

        // Allocate only once a representable link is found: many rels (curies, and any templated navigation
        // link) never reach the header, and a resource may carry none that do.
        StringBuilder? builder = null;
        foreach (var (relation, value) in links)
        {
            foreach (var link in value.Links)
            {
                // A templated link (RFC 6570) is not a valid URI-Reference; an href carrying a control
                // character or an angle bracket can't be embedded in the <target> without corrupting the
                // header. Skip either rather than emit something malformed or injectable.
                if (link.Templated == true || !IsSafeTarget(link.Href))
                {
                    continue;
                }

                builder ??= new StringBuilder();
                if (builder.Length > 0)
                {
                    builder.Append(", ");
                }

                AppendLink(builder, relation, link);
            }
        }

        if (builder is { Length: > 0 })
        {
            http.Response.Headers.Append(HeaderNames.Link, builder.ToString());
        }
    }

    // <href>; rel="rel"; title="..."; ... — mirroring the standard RFC 8288 target attributes the link
    // carries. hreflang is a bare Language-Tag (no quoted form in the ABNF); the rest are quoted-strings.
    private static void AppendLink(StringBuilder builder, string relation, HalLink link)
    {
        builder.Append('<').Append(link.Href).Append(">; rel=");
        AppendQuoted(builder, relation);
        AppendQuotedParam(builder, "title", link.Title);
        AppendQuotedParam(builder, "name", link.Name);
        AppendQuotedParam(builder, "type", link.Type);
        AppendQuotedParam(builder, "profile", link.Profile);
        AppendHreflang(builder, link.Hreflang);
    }

    // A target may hold anything but the delimiters that close it (angle brackets) and the control characters
    // that would let a value break out of the header field. An empty href has no target to point at.
    private static bool IsSafeTarget(string href)
    {
        if (href.Length == 0)
        {
            return false;
        }

        foreach (var c in href)
        {
            if (c < ' ' || c == Delete || c == '<' || c == '>')
            {
                return false;
            }
        }

        return true;
    }

    private static void AppendQuotedParam(StringBuilder builder, string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        builder.Append("; ").Append(name).Append('=');
        AppendQuoted(builder, value);
    }

    // RFC 7230 quoted-string: escape the backslash and double-quote; drop control characters, which can't
    // appear in a header field at all (dropping neuters any CR/LF header-injection attempt in a config value).
    private static void AppendQuoted(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var c in value)
        {
            if (c < ' ' || c == Delete)
            {
                continue;
            }

            if (c is '"' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        builder.Append('"');
    }

    private static void AppendHreflang(StringBuilder builder, string? hreflang)
    {
        if (string.IsNullOrEmpty(hreflang) || !IsLanguageTag(hreflang))
        {
            return;
        }

        builder.Append("; hreflang=").Append(hreflang);
    }

    // A Language-Tag is a token (letters, digits, hyphens); anything else can't be emitted unquoted, so the
    // attribute is simply omitted rather than risk a malformed header.
    private static bool IsLanguageTag(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
            {
                return false;
            }
        }

        return true;
    }
}
