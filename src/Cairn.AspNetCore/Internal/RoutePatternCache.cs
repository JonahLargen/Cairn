using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Caches the name → <c>RoutePattern</c> lookup templated links resolve through. Without it, every
/// templated link scans the full endpoint table — per link, per item, per request. The map is built lazily
/// from a consistent endpoint snapshot and invalidated by <see cref="EndpointDataSource.GetChangeToken"/>,
/// so dynamically added endpoints are picked up on the next lookup.
/// </summary>
internal sealed class RoutePatternCache(EndpointDataSource endpoints)
{
    private volatile Dictionary<string, RoutePattern>? _patterns;

    /// <summary>Endpoint names are matched case-insensitively, like <c>LinkGenerator</c>'s address schemes.</summary>
    public RoutePattern? Find(string routeName)
    {
        var patterns = _patterns ?? BuildAndPublish();
        return patterns.TryGetValue(routeName, out var pattern) ? pattern : null;
    }

    private Dictionary<string, RoutePattern> BuildAndPublish()
    {
        // Register invalidation against the token that guards the endpoint list about to be read, before
        // reading it: a data-source change racing this build can only ever clear the map, never leave a
        // stale one published. Concurrent builders may each do the work once; the map contents are identical.
        var token = endpoints.GetChangeToken();
        token.RegisterChangeCallback(static state => ((RoutePatternCache)state!)._patterns = null, this);

        var patterns = new Dictionary<string, RoutePattern>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in endpoints.Endpoints)
        {
            if (endpoint is not RouteEndpoint route)
            {
                continue;
            }

            // TryAdd keeps the first endpoint carrying a name, matching the linear scan this cache replaces
            // (which returned the first endpoint whose route name or endpoint name matched).
            if (route.Metadata.GetMetadata<IRouteNameMetadata>()?.RouteName is { } name)
            {
                patterns.TryAdd(name, route.RoutePattern);
            }

            if (route.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName is { } endpointName)
            {
                patterns.TryAdd(endpointName, route.RoutePattern);
            }
        }

        _patterns = patterns;
        return patterns;
    }
}
