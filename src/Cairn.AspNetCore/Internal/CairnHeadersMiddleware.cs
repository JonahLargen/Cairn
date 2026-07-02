using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Cairn.AspNetCore.Internal;

/// <summary>
/// Emits the response headers declared by Cairn endpoint metadata — today the deprecation headers of
/// <see cref="DeprecationMetadata"/>. The headers are set in <c>Response.OnStarting</c> so the selected
/// endpoint is known regardless of where the middleware sits relative to routing, and so the metadata
/// reaches every endpoint type (MVC controller actions as well as minimal-API handlers).
/// </summary>
internal sealed class CairnHeadersMiddleware(RequestDelegate next)
{
    public Task Invoke(HttpContext context)
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

        return next(context);
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
