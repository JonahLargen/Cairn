using Cairn.AspNetCore.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Cairn.AspNetCore;

/// <summary>Extension methods for opting endpoints into Cairn hypermedia.</summary>
public static class CairnEndpointExtensions
{
    /// <summary>
    /// Projects hypermedia links and affordances onto this endpoint's (or route group's) responses. The
    /// returned value — carried by an <c>IResult</c> (e.g. <c>TypedResults.Ok(...)</c>) or returned bare —
    /// and each element of a returned collection is linked according to its runtime type's configuration.
    /// A bare deferred sequence (LINQ query, <c>IQueryable</c>) is materialized once so it is not enumerated
    /// a second time by the serializer.
    /// </summary>
    /// <param name="builder">The endpoint or route group builder.</param>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static TBuilder WithLinks<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddEndpointFilterFactory((_, next) => async invocation =>
        {
            var result = await next(invocation);
            return await CairnLinkRecorder.RecordResultAsync(invocation.HttpContext, result);
        });

        // Leave a discoverable marker in the endpoint metadata: the filter above is invisible to the
        // OpenAPI/Swagger document generators, so without it they cannot tell an opted-in endpoint from one
        // that returns a configured type as plain JSON, and would advertise hal+json negotiation on both.
        return builder.WithMetadata(CairnLinksMetadata.Instance);
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

    /// <summary>
    /// Forces the hypermedia <paramref name="format"/> for this endpoint or route group, overriding content
    /// negotiation and the global default.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static TBuilder WithHypermediaFormat<TBuilder>(this TBuilder builder, HypermediaFormat format)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithMetadata(new HypermediaFormatMetadata(format));
    }

    /// <summary>
    /// Forces a custom hypermedia format (registered with <see cref="CairnOptions.AddFormatter"/>) for this
    /// endpoint or route group by its <paramref name="mediaType"/>, overriding content negotiation and the
    /// global default. Requests fail if no formatter is registered for the media type.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="mediaType"/> is null or whitespace.</exception>
    public static TBuilder WithHypermediaFormat<TBuilder>(this TBuilder builder, string mediaType)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);

        return builder.WithMetadata(new HypermediaFormatMetadata(mediaType));
    }
}
