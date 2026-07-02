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
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IApplicationBuilder UseCairnOptionsHandler(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<CairnOptionsMiddleware>();
    }
}
