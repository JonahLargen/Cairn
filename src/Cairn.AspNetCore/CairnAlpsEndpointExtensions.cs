using Cairn.AspNetCore.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cairn.AspNetCore;

/// <summary>Extension methods for serving ALPS profile documents generated from the registered link configurations.</summary>
public static class CairnAlpsEndpointExtensions
{
    /// <summary>The media type ALPS profile documents are served as.</summary>
    public const string AlpsMediaType = "application/alps+json";

    /// <summary>
    /// Serves ALPS profile documents (application-level profile semantics,
    /// <c>application/alps+json</c>) generated from every registered <see cref="LinkConfig{T}"/>: an index of
    /// the profiles at <see cref="CairnAlpsOptions.Path"/> (default <c>/alps</c>), and one document per
    /// resource type at <c>{Path}/{profile-name}</c>. A profile describes the type's application semantics —
    /// its serialized fields as <c>semantic</c> descriptors, its declared links as <c>safe</c> descriptors,
    /// and its declared affordances as <c>safe</c>/<c>idempotent</c>/<c>unsafe</c> descriptors (by HTTP
    /// method) with the <c>Accepts&lt;TInput&gt;</c> fields nested — so generic hypermedia clients can be
    /// pointed at the vocabulary the API actually speaks.
    /// </summary>
    /// <remarks>
    /// Documents describe every declared transition, including those gated by <c>When</c> or
    /// <c>RequireAuthorization</c> — an ALPS profile is the vocabulary of what a resource <em>can</em> offer,
    /// not what one response contains. Field names come from the host's
    /// <see cref="Microsoft.AspNetCore.Http.Json.JsonOptions"/> serializer contract, so they match the wire.
    /// The endpoints are excluded from API descriptions (OpenAPI).
    /// </remarks>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="configure">Optional configuration of the mount path and profile naming.</param>
    /// <returns>A builder for adding conventions (authorization, caching, ...) to both endpoints.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is <see langword="null"/>.</exception>
    public static IEndpointConventionBuilder MapCairnAlps(this IEndpointRouteBuilder endpoints, Action<CairnAlpsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = new CairnAlpsOptions();
        configure?.Invoke(options);

        // Built on first request, once: the registry is frozen when CairnOptions resolves, and the serializer
        // contract (for wire-accurate field names) comes from the same options minimal APIs serialize with.
        var services = endpoints.ServiceProvider;
        var catalog = new Lazy<AlpsProfileCatalog>(() =>
        {
            var cairn = services.GetService<CairnOptions>()
                ?? throw new InvalidOperationException("Cairn: MapCairnAlps requires Cairn's services. Call services.AddCairn(...) and register link configs before mapping the ALPS endpoints.");
            var serializer = services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
            return new AlpsProfileCatalog(cairn.Registry, serializer, options.Path, options.ProfileName);
        });

        // RequestDelegate handlers (not Delegate ones) keep the mapping free of request-delegate compilation,
        // so the package stays trim-/AOT-analysis clean under IsAotCompatible.
        var group = endpoints.MapGroup(options.Path);

        group.MapGet("", (RequestDelegate)(context =>
            WriteAsync(context, catalog.Value.IndexFor(context.Request.PathBase.Value ?? string.Empty), "application/json")))
            .ExcludeFromDescription();

        group.MapGet("/{profile}", (RequestDelegate)(context =>
        {
            var profile = context.Request.RouteValues["profile"] as string;
            var document = profile is null ? null : catalog.Value.DocumentFor(context.Request.PathBase.Value ?? string.Empty, profile);
            if (document is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }

            return WriteAsync(context, document, AlpsMediaType);
        }))
            .ExcludeFromDescription();

        return group;
    }

    private static Task WriteAsync(HttpContext context, byte[] payload, string contentType)
    {
        context.Response.ContentType = contentType;
        context.Response.ContentLength = payload.Length;
        return context.Response.Body.WriteAsync(payload.AsMemory(), context.RequestAborted).AsTask();
    }
}
