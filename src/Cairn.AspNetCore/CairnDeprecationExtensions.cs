using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Cairn.AspNetCore;

/// <summary>Extension methods for advertising an endpoint's deprecation via response headers.</summary>
public static class CairnDeprecationExtensions
{
    /// <summary>
    /// Emits deprecation headers on this endpoint's (or route group's) responses: a <c>Deprecation</c> header
    /// (RFC 9745 — <c>@unix-seconds</c> when <paramref name="deprecatedAt"/> is given, the draft-compatible
    /// <c>true</c> otherwise), a <c>Sunset</c> header (RFC 8594) when <paramref name="sunset"/> is given, and a
    /// <c>Link</c> header with <c>rel="deprecation"</c> when <paramref name="link"/> points at documentation.
    /// This complements the link-level <c>Deprecated(...)</c>/<c>WithDeprecation(...)</c> hypermedia attribute:
    /// the headers mark the endpoint itself, so clients that never parse the body still see the deprecation.
    /// </summary>
    /// <typeparam name="TBuilder">The endpoint or route group builder type.</typeparam>
    /// <param name="builder">The endpoint or route group builder.</param>
    /// <param name="sunset">When the endpoint is expected to become unresponsive (the <c>Sunset</c> header).</param>
    /// <param name="link">A URL documenting the deprecation, emitted as a <c>Link</c> header with <c>rel="deprecation"</c>.</param>
    /// <param name="deprecatedAt">When the endpoint became (or becomes) deprecated; emitted as the <c>Deprecation</c> header's date.</param>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static TBuilder WithDeprecation<TBuilder>(this TBuilder builder, DateTimeOffset? sunset = null, string? link = null, DateTimeOffset? deprecatedAt = null)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        // RFC 9745 uses a structured-field Date (@ + seconds since the epoch); RFC 8594 uses an HTTP-date.
        var deprecation = deprecatedAt is { } at
            ? string.Create(CultureInfo.InvariantCulture, $"@{at.ToUnixTimeSeconds()}")
            : "true";
        var sunsetDate = sunset?.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
        var linkHeader = link is null ? null : $"<{link}>; rel=\"deprecation\"";

        return builder.AddEndpointFilter(async (invocation, next) =>
        {
            var headers = invocation.HttpContext.Response.Headers;
            headers["Deprecation"] = deprecation;
            if (sunsetDate is not null)
            {
                headers["Sunset"] = sunsetDate;
            }

            if (linkHeader is not null)
            {
                headers.Append(HeaderNames.Link, linkHeader);
            }

            return await next(invocation);
        });
    }
}
