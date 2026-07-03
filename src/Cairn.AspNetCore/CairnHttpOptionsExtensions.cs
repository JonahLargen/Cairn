using Cairn.AspNetCore.Internal;
using Microsoft.AspNetCore.Builder;

namespace Cairn.AspNetCore;

/// <summary>Extension methods for answering <c>OPTIONS</c> requests with the route's allowed methods.</summary>
public static class CairnHttpOptionsExtensions
{
    /// <summary>
    /// Answers <c>OPTIONS</c> requests with <c>204 No Content</c> and an <c>Allow</c> header listing the HTTP
    /// methods of every endpoint on the requested path — so clients can discover a resource's methods without
    /// the app mapping OPTIONS by hand. An OPTIONS endpoint the app maps itself still wins; unknown paths fall
    /// through (404). Add it once, after routing (with minimal hosting, anywhere in the pipeline).
    /// </summary>
    /// <remarks>
    /// CORS preflights (OPTIONS requests carrying <c>Access-Control-Request-Method</c>) are always passed
    /// through untouched so <c>UseCors</c> can answer them with the <c>Access-Control-*</c> headers browsers
    /// require; the relative ordering of the two middlewares therefore doesn't matter for preflights. Note
    /// that the handler answers without any authorization check — no endpoint is matched, so endpoint
    /// authorization never runs, wherever the handler sits in the pipeline. If advertising a path's methods
    /// to anonymous callers is a concern, map OPTIONS explicitly on the routes that need protection (an
    /// app-mapped OPTIONS endpoint always wins) instead of using this handler for them.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IApplicationBuilder UseCairnOptionsHandler(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<CairnOptionsMiddleware>();
    }
}
