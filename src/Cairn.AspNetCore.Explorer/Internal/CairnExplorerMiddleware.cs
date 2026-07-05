using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Cairn.AspNetCore.Explorer.Internal;

/// <summary>
/// Terminal middleware that serves the embedded single-page explorer at the configured path (and its
/// trailing-slash form). Every other request — a different path, or a non-<c>GET</c> method on this one —
/// passes through untouched, so mounting the explorer never shadows the API it explores.
/// </summary>
internal sealed class CairnExplorerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly PathString _path;
    private readonly byte[] _html;

    public CairnExplorerMiddleware(RequestDelegate next, PathString path, byte[] html)
    {
        _next = next;
        _path = path;
        _html = html;
    }

    public Task Invoke(HttpContext context)
    {
        var request = context.Request;
        if (HttpMethods.IsGet(request.Method)
            && request.Path.StartsWithSegments(_path, out var remaining)
            && (!remaining.HasValue || remaining.Value == "/"))
        {
            return WritePageAsync(context);
        }

        return _next(context);
    }

    private Task WritePageAsync(HttpContext context)
    {
        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength = _html.Length;
        // The page is generated per app configuration and is not a cacheable API resource; keep it fresh in
        // dev, and stop content sniffing from second-guessing the declared type.
        response.Headers[HeaderNames.CacheControl] = "no-store";
        response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        return response.Body.WriteAsync(_html, 0, _html.Length);
    }
}
