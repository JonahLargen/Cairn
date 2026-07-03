using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Emits the response headers declared by Cairn endpoint metadata — today the deprecation headers of
/// <see cref="DeprecationMetadata"/>. The headers are set in <c>Response.OnStarting</c> so the selected
/// endpoint is known regardless of where the middleware sits relative to routing, and so the metadata
/// reaches every endpoint type (MVC controller actions as well as minimal-API handlers).
/// </summary>
internal sealed class CairnHeadersMiddleware(RequestDelegate next, EndpointDataSource endpoints)
{
    // Whether any endpoint carries deprecation metadata, computed once per endpoint-table generation. When
    // none does (the overwhelmingly common case), the per-request OnStarting callback is pure overhead —
    // skip registering it. Invalidated by the endpoint change token, so dynamically added endpoints are seen.
    private volatile object? _anyDeprecated;

    public Task Invoke(HttpContext context)
    {
        if (AnyDeprecatedEndpoint())
        {
            RegisterHeaderCallback(context);
        }

        return next(context);
    }

    private bool AnyDeprecatedEndpoint()
    {
        if (_anyDeprecated is bool cached)
        {
            return cached;
        }

        // Capture the token guarding the endpoint list before scanning it, so a racing endpoint change can
        // only clear this computation, never strand a stale one.
        var token = endpoints.GetChangeToken();
        token.RegisterChangeCallback(static state => ((CairnHeadersMiddleware)state!)._anyDeprecated = null, this);

        var any = false;
        foreach (var endpoint in endpoints.Endpoints)
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
