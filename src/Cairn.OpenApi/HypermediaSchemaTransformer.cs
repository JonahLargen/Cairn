using Cairn.Hypermedia;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Cairn.OpenApi;

/// <summary>
/// Adds the <c>_links</c>, <c>_embedded</c>, <c>_actions</c>, and <c>_templates</c> shape to the schemas of
/// Cairn-linked resource types. A no-op when Cairn itself is not registered (<c>AddCairn</c> was not called).
/// </summary>
internal sealed class HypermediaSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var provider = context.ApplicationServices.GetService<ILinkConfigProvider>();
        if (provider is not null && provider.GetConfig(context.JsonTypeInfo.Type) is not null)
        {
            HypermediaJsonSchemas.Apply(schema);
        }

        return Task.CompletedTask;
    }
}
