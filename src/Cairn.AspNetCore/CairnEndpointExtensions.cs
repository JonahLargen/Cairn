using Cairn.AspNetCore.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Cairn.AspNetCore;

/// <summary>Extension methods for opting endpoints into Cairn hypermedia.</summary>
public static class CairnEndpointExtensions
{
    /// <summary>
    /// Projects hypermedia links and affordances onto this endpoint's response. The returned value — and
    /// each element of a returned collection — is linked according to its runtime type's configuration.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static RouteHandlerBuilder WithLinks(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context);
            await CairnLinkRecorder.RecordAsync(context.HttpContext, result);
            return result;
        });
    }

    /// <summary>
    /// Sets how pagination links are built for this endpoint or route group, overriding the global default.
    /// </summary>
    /// <param name="builder">The endpoint or route group builder.</param>
    /// <param name="pageLink">Builds the URL for a page number from the current request.</param>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="pageLink"/> is <see langword="null"/>.</exception>
    public static TBuilder WithPageLinks<TBuilder>(this TBuilder builder, Func<HttpRequest, int, string> pageLink)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(pageLink);

        return builder.WithMetadata(new PageLinkMetadata(pageLink));
    }

    /// <summary>
    /// Sets how cursor pagination links are built for this endpoint or route group, overriding the global default.
    /// </summary>
    /// <param name="builder">The endpoint or route group builder.</param>
    /// <param name="cursorLink">Builds the URL for a cursor from the current request.</param>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="cursorLink"/> is <see langword="null"/>.</exception>
    public static TBuilder WithCursorLinks<TBuilder>(this TBuilder builder, Func<HttpRequest, string, string> cursorLink)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(cursorLink);

        return builder.WithMetadata(new CursorLinkMetadata(cursorLink));
    }
}
