using Cairn.Hypermedia;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Cairn.OpenApi;

/// <summary>
/// Documents an opted-in endpoint's hypermedia on its operation: the per-format schemas of the media types it
/// can negotiate (<c>application/hal+json</c> and <c>application/prs.hal-forms+json</c>) on each response that
/// carries a Cairn-linked type, an <c>ETag</c> header and <c>304</c> response when it uses <c>WithETag</c>, and
/// <c>deprecated: true</c> when it uses <c>WithDeprecation</c>. The link media types are a no-op when Cairn
/// itself is not registered; the ETag and deprecation markers do not depend on it.
/// </summary>
internal sealed class HypermediaOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        // Deprecation and ETag are endpoint conventions (WithDeprecation/WithETag) independent of the link
        // configuration, so they are documented whether or not AddCairn registered the link provider.
        HypermediaJsonSchemas.MarkDeprecated(context.Description, operation);
        HypermediaJsonSchemas.DocumentETag(context.Description, operation);

        if (context.ApplicationServices.GetService<ILinkConfigProvider>() is { } provider)
        {
            HypermediaJsonSchemas.AddNegotiatedMediaTypes(context.Description, operation, provider, context.ApplicationServices.GetService<IPaginationEnvelopeProvider>());
        }

        return Task.CompletedTask;
    }
}
