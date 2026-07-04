using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.WebUtilities;

namespace Cairn.AspNetCore.Internal;

/// <summary>Resolves link targets to absolute URLs using the ASP.NET Core <see cref="LinkGenerator"/>.</summary>
internal sealed class LinkGeneratorUrlResolver(
    LinkGenerator linkGenerator,
    IHttpContextAccessor accessor,
    CairnOptions options,
    RoutePatternCache patterns) : ILinkUrlResolver
{
    public string? Resolve(LinkTarget target) => target switch
    {
        ExplicitLinkTarget explicitTarget => explicitTarget.Href,
        RouteLinkTarget route => ResolveRoute(route),
        RouteTemplateLinkTarget template => ResolveRouteTemplate(template),
        _ => null,
    };

    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Generated Routes.* targets supply route values as Dictionary<string, object?>, which LinkGenerator copies into a RouteValueDictionary without reflection. Anonymous-object route values in hand-written configs do reflect over the object's properties and are documented (docs/articles/aot.md) as requiring dictionaries in trimmed applications.")]
    private string? ResolveRoute(RouteLinkTarget route)
    {
        var http = accessor.HttpContext;
        if (http is null)
        {
            return null;
        }

        var url = options.UrlStyle == LinkUrlStyle.PathRelative
            ? linkGenerator.GetPathByName(http, route.RouteName, route.RouteValues)
            : options.PublicBaseUri is { } publicBase
                ? linkGenerator.GetUriByName(route.RouteName, route.RouteValues, publicBase.Scheme, Host(publicBase), BasePath(publicBase))
                : linkGenerator.GetUriByName(http, route.RouteName, route.RouteValues);
        return Transform(http, url);
    }

    // Renders the named route's pattern as an RFC 6570 URI template: supplied route values are bound into the
    // path (extras become literal query parameters, matching LinkGenerator), and the remaining route parameters
    // stay as {placeholder} variables for the client to expand.
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Generated Routes.* targets supply route values as Dictionary<string, object?>, which RouteValueDictionary copies without reflection. Anonymous-object route values in hand-written configs do reflect over the object's properties and are documented (docs/articles/aot.md) as requiring dictionaries in trimmed applications.")]
    private string? ResolveRouteTemplate(RouteTemplateLinkTarget target)
    {
        var http = accessor.HttpContext;
        if (http is null || patterns.Find(target.RouteName) is not { } pattern)
        {
            return null;
        }

        var values = new RouteValueDictionary(target.RouteValues);
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = new StringBuilder();
        foreach (var segment in pattern.PathSegments)
        {
            path.Append('/');
            foreach (var part in segment.Parts)
            {
                switch (part)
                {
                    case RoutePatternLiteralPart literal:
                        path.Append(literal.Content);
                        break;
                    case RoutePatternSeparatorPart separator:
                        path.Append(separator.Content);
                        break;
                    case RoutePatternParameterPart parameter
                        when values.TryGetValue(parameter.Name, out var bound) && bound is not null:
                        consumed.Add(parameter.Name);
                        var text = Convert.ToString(bound, CultureInfo.InvariantCulture) ?? string.Empty;
                        // A catch-all ({*slug}/{**slug}) captures a whole path: its '/' separators are
                        // structural, so escape each segment but keep the slashes rather than encoding them
                        // into %2F (which would produce the wrong URL for a value like "docs/intro/setup").
                        path.Append(parameter.IsCatchAll ? EscapeCatchAll(text) : Uri.EscapeDataString(text));
                        break;
                    case RoutePatternParameterPart parameter:
                        // An unbound parameter stays a template variable. A catch-all keeps its marker as an
                        // RFC 6570 reserved expansion ({+slug}) so the client expands a multi-segment value
                        // with its '/' separators intact; a normal parameter is a simple {slug}.
                        path.Append('{');
                        if (parameter.IsCatchAll)
                        {
                            path.Append('+');
                        }

                        path.Append(parameter.Name).Append('}');
                        break;
                }
            }
        }

        if (path.Length == 0)
        {
            path.Append('/');
        }

        var url = $"{TemplateBase(http)}{path}";
        foreach (var (key, value) in values)
        {
            if (!consumed.Contains(key) && value is not null)
            {
                url = QueryHelpers.AddQueryString(url, key, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
            }
        }

        return Transform(http, url);
    }

    // Percent-encodes each segment of a catch-all value while preserving the '/' separators between them.
    private static string EscapeCatchAll(string value)
    {
        var segments = value.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            segments[i] = Uri.EscapeDataString(segments[i]);
        }

        return string.Join('/', segments);
    }

    // The prefix ahead of the route path, honoring the configured URL style (mirrors ResolveRoute's handling).
    private string TemplateBase(HttpContext http)
        => options.UrlStyle == LinkUrlStyle.PathRelative
            ? http.Request.PathBase.ToString()
            : options.PublicBaseUri is { } publicBase
                ? $"{publicBase.Scheme}://{publicBase.Authority}{BasePath(publicBase)}"
                : $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";

    private string? Transform(HttpContext http, string? url)
        => url is not null && options.TransformUrl is { } transform ? transform(http, url) : url;

    // The scheme's default port is omitted, matching Uri.Authority (FromUriComponent would render ":443").
    private static HostString Host(Uri publicBase)
        => publicBase.IsDefaultPort ? new HostString(publicBase.Host) : new HostString(publicBase.Host, publicBase.Port);

    /// <summary>The base URI's path as a path base (trailing slash trimmed so it composes with route paths).</summary>
    internal static PathString BasePath(Uri publicBase)
    {
        var path = publicBase.AbsolutePath.TrimEnd('/');
        return path.Length == 0 ? default : new PathString(path);
    }
}
