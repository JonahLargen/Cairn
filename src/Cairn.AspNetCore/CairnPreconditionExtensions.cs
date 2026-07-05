using Cairn.AspNetCore.Internal;
using Microsoft.AspNetCore.Builder;

namespace Cairn.AspNetCore;

/// <summary>Extension methods for advertising an endpoint's write preconditions in the OpenAPI document.</summary>
public static class CairnPreconditionExtensions
{
    /// <summary>
    /// Declares that this endpoint (or route group) evaluates write preconditions with
    /// <see cref="CairnPreconditions.Evaluate"/>, so the OpenAPI integrations (Cairn.OpenApi, Cairn.Swashbuckle)
    /// document a <c>412 Precondition Failed</c> response (as <c>application/problem+json</c>, carrying the
    /// current validator in an <c>ETag</c> header) and — when <paramref name="requireIfMatch"/> is set — a
    /// <c>428 Precondition Required</c> response, matching the statuses <c>Evaluate</c> returns.
    /// <para>
    /// This is a documentation marker only: it leaves endpoint metadata the OpenAPI generators read and has no
    /// runtime effect on its own. You still call <see cref="CairnPreconditions.Evaluate"/> inside the handler to
    /// actually enforce the preconditions — pass the same <paramref name="requireIfMatch"/> value there so the
    /// document and the behavior agree. Being metadata, it applies to MVC controller endpoints as well as
    /// minimal-API handlers, and needs neither <c>AddCairn</c> nor a middleware.
    /// </para>
    /// </summary>
    /// <typeparam name="TBuilder">The endpoint or route group builder type.</typeparam>
    /// <param name="builder">The endpoint or route group builder.</param>
    /// <param name="requireIfMatch">Whether the endpoint requires a conditional header (a missing one fails with <c>428</c>); when set, the operation also documents a <c>428 Precondition Required</c> response. Pass the same value to <see cref="CairnPreconditions.Evaluate"/>.</param>
    /// <returns>The same <paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static TBuilder WithPreconditions<TBuilder>(this TBuilder builder, bool requireIfMatch = false)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        // A stateless presence marker per documented status (412 always; 428 only when a conditional header is
        // required) — the OpenAPI generators, which don't reference this assembly, match them by full type name.
        builder.Add(endpoint =>
        {
            endpoint.Metadata.Add(PreconditionMetadata.Instance);
            if (requireIfMatch)
            {
                endpoint.Metadata.Add(PreconditionRequiredMetadata.Instance);
            }
        });

        return builder;
    }
}
