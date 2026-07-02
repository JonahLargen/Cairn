using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cairn.AspNetCore.Internal;

/// <summary>Resolves link targets to absolute URLs using the ASP.NET Core <see cref="LinkGenerator"/>.</summary>
internal sealed class LinkGeneratorUrlResolver(LinkGenerator linkGenerator, IHttpContextAccessor accessor, CairnOptions options) : ILinkUrlResolver
{
    public string? Resolve(LinkTarget target) => target switch
    {
        ExplicitLinkTarget explicitTarget => explicitTarget.Href,
        RouteLinkTarget route => ResolveRoute(route),
        _ => null,
    };

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
        return url is not null && options.TransformUrl is { } transform ? transform(http, url) : url;
    }

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
