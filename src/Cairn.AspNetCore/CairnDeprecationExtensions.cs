using System.Globalization;
using System.Runtime.CompilerServices;
using Cairn.AspNetCore.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cairn.AspNetCore;

/// <summary>Extension methods for advertising an endpoint's deprecation via response headers.</summary>
public static class CairnDeprecationExtensions
{
    // Hosts already warned about a missing AddCairn, keyed by root provider so a second host in the same
    // process (test suites, side-by-side hosts) still gets its own diagnostic — mirroring WarnOnce, which
    // isn't available here precisely because AddCairn wasn't called.
    private static readonly ConditionalWeakTable<IServiceProvider, object> WarnedHosts = new();

    /// <summary>
    /// Emits deprecation headers on this endpoint's (or route group's) responses: a <c>Deprecation</c> header
    /// (RFC 9745 — <c>@unix-seconds</c> when <paramref name="deprecatedAt"/> is given, the draft-compatible
    /// <c>true</c> otherwise), a <c>Sunset</c> header (RFC 8594) when <paramref name="sunset"/> is given, and a
    /// <c>Link</c> header with <c>rel="deprecation"</c> when <paramref name="link"/> points at documentation.
    /// This complements the link-level <c>Deprecated(...)</c>/<c>WithDeprecation(...)</c> hypermedia attribute:
    /// the headers mark the endpoint itself, so clients that never parse the body still see the deprecation.
    /// The headers are declared as endpoint metadata and emitted by a middleware <c>AddCairn</c> registers, so
    /// they reach MVC controller endpoints (e.g. a <c>MapControllers()</c> group) as well as minimal-API handlers.
    /// Without <c>AddCairn</c> the metadata is inert (nothing emits the headers); Cairn logs a warning once per
    /// host when it detects that.
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

        var metadata = new DeprecationMetadata(deprecation, sunsetDate, linkHeader);
        builder.Add(endpoint =>
        {
            endpoint.Metadata.Add(metadata);
            WarnIfHeadersMiddlewareMissing(endpoint.ApplicationServices);
        });

        return builder;
    }

    // The headers are emitted by the middleware AddCairn registers; without it the metadata is inert. That
    // would be a silent no-op, so surface it once per host like every other Cairn misconfiguration.
    private static void WarnIfHeadersMiddlewareMissing(IServiceProvider services)
    {
        if (services.GetService<WarnOnce>() is not null || !WarnedHosts.TryAdd(services, services))
        {
            return;
        }

        services.GetService<ILoggerFactory>()?.CreateLogger("Cairn.AspNetCore").LogWarning(
            "Cairn: WithDeprecation declared deprecation headers, but AddCairn was not called, so the middleware that emits them is not registered and the headers will never be sent. Call services.AddCairn().");
    }
}
