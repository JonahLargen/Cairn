using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cairn.AspNetCore.Internal;

/// <summary>Resolves link targets to absolute URLs using the ASP.NET Core <see cref="LinkGenerator"/>.</summary>
internal sealed class LinkGeneratorUrlResolver(LinkGenerator linkGenerator, IHttpContextAccessor accessor) : ILinkUrlResolver
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
        return http is null ? null : linkGenerator.GetUriByName(http, route.RouteName, route.RouteValues);
    }
}
