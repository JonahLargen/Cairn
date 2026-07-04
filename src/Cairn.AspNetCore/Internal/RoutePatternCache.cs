using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Primitives;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Caches the name → <c>RoutePattern</c> lookup templated links resolve through. Without it, every
/// templated link scans the full endpoint table — per link, per item, per request. The map is built lazily
/// and invalidated by <see cref="EndpointDataSource.GetChangeToken"/>, so dynamically added endpoints are
/// picked up on the next lookup.
/// </summary>
internal sealed class RoutePatternCache
{
    private readonly EndpointDataSource _endpoints;
    private volatile Dictionary<string, RoutePattern>? _patterns;

    public RoutePatternCache(EndpointDataSource endpoints)
    {
        _endpoints = endpoints;

        // ChangeToken.OnChange re-registers itself after every fire, so invalidation stays live across
        // successive endpoint changes. Registering a one-shot callback inside the build instead can strand a
        // stale map: if the token fires (clearing the field) while a build is in flight, the build then
        // overwrites the nulled field with its stale snapshot and no live token remains to clear it again.
        ChangeToken.OnChange(endpoints.GetChangeToken, () => _patterns = null);
    }

    /// <summary>Endpoint names are matched case-insensitively, like <c>LinkGenerator</c>'s address schemes.</summary>
    public RoutePattern? Find(string routeName)
    {
        // Concurrent builders may each do the work once; the map contents are identical.
        var patterns = _patterns ??= Build();
        return patterns.TryGetValue(routeName, out var pattern) ? pattern : null;
    }

    private Dictionary<string, RoutePattern> Build()
    {
        var patterns = new Dictionary<string, RoutePattern>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in _endpoints.Endpoints)
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

        return patterns;
    }
}
