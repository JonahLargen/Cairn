namespace Cairn;

/// <summary>Describes where a link or affordance points, resolved to a URL by the host.</summary>
public abstract record LinkTarget
{
    private protected LinkTarget()
    {
    }

    /// <summary>Targets a named route, optionally with route values.</summary>
    public static LinkTarget Route(string routeName, object? routeValues = null) => new RouteLinkTarget(routeName, routeValues);

    /// <summary>Targets an explicit URI (or URI template).</summary>
    public static LinkTarget Uri(string href, bool templated = false) => new ExplicitLinkTarget(href, templated);
}

/// <summary>A target identified by a named route and optional route values.</summary>
public sealed record RouteLinkTarget(string RouteName, object? RouteValues) : LinkTarget;

/// <summary>A target identified by an explicit URI.</summary>
public sealed record ExplicitLinkTarget(string Href, bool Templated) : LinkTarget;
