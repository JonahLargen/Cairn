using Cairn.Hypermedia;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Cairn.OpenApi;

/// <summary>
/// Adds the hypermedia media types an opted-in endpoint can negotiate (<c>application/hal+json</c> and
/// <c>application/prs.hal-forms+json</c>) to each response that carries a Cairn-linked type, reusing the
/// <c>application/json</c> entry's schema. A no-op when Cairn itself is not registered.
/// </summary>
internal sealed class HypermediaOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.ApplicationServices.GetService<ILinkConfigProvider>() is { } provider)
        {
            HypermediaJsonSchemas.AddNegotiatedMediaTypes(context.Description, operation, provider);
        }

        return Task.CompletedTask;
    }
}
