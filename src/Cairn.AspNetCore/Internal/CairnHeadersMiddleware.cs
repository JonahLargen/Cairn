using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Emits the response headers declared by Cairn endpoint metadata — today the deprecation headers of
/// <see cref="DeprecationMetadata"/>. The headers are set in <c>Response.OnStarting</c> so the selected
/// endpoint is known regardless of where the middleware sits relative to routing, and so the metadata
/// reaches every endpoint type (MVC controller actions as well as minimal-API handlers).
/// </summary>
internal sealed class CairnHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly EndpointDataSource _endpoints;

    // Whether any endpoint carries deprecation metadata, computed once per endpoint-table generation. When
    // none does (the overwhelmingly common case), the per-request OnStarting callback is pure overhead —
    // skip registering it. Invalidated by the endpoint change token, so dynamically added endpoints are seen.
    private volatile object? _anyDeprecated;

    public CairnHeadersMiddleware(RequestDelegate next, EndpointDataSource endpoints)
    {
        _next = next;
        _endpoints = endpoints;

        // ChangeToken.OnChange re-registers after every fire, so invalidation stays live across successive
        // endpoint changes — unlike a one-shot callback registered inside the scan, which a change racing the
        // scan can spend before the stale result is published, stranding it.
        ChangeToken.OnChange(endpoints.GetChangeToken, () => _anyDeprecated = null);
    }

    public Task Invoke(HttpContext context)
    {
        if (AnyDeprecatedEndpoint())
        {
            RegisterHeaderCallback(context);
        }

        return _next(context);
    }

    private bool AnyDeprecatedEndpoint()
    {
        if (_anyDeprecated is bool cached)
        {
            return cached;
        }

        var any = false;
        foreach (var endpoint in _endpoints.Endpoints)
        {
            if (endpoint.Metadata.GetMetadata<DeprecationMetadata>() is not null)
            {
                any = true;
                break;
            }
        }

        _anyDeprecated = any;
        return any;
    }

    private static void RegisterHeaderCallback(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var http = (HttpContext)state;
            if (http.GetEndpoint()?.Metadata.GetMetadata<DeprecationMetadata>() is { } deprecation)
            {
                var headers = http.Response.Headers;
                headers["Deprecation"] = deprecation.Deprecation;
                if (deprecation.Sunset is not null)
                {
                    headers["Sunset"] = deprecation.Sunset;
                }

                if (deprecation.Link is not null)
                {
                    headers.Append(HeaderNames.Link, deprecation.Link);
                }
            }

            return Task.CompletedTask;
        }, context);
    }
}

/// <summary>
/// Auto-registers <see cref="CairnHeadersMiddleware"/> at the front of the pipeline when <c>AddCairn</c> is
/// called, so no <c>app.UseX()</c> call is needed for the metadata-declared headers to be emitted.
/// </summary>
internal sealed class CairnHeadersStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        => app =>
        {
            app.UseMiddleware<CairnHeadersMiddleware>();
            next(app);
        };
}
