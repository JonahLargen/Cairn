using Cairn.Hypermedia;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Cairn.OpenApi;

/// <summary>
/// Adds the <c>_links</c>, <c>_embedded</c>, <c>_actions</c>, and <c>_templates</c> shape to the schemas of
/// Cairn-linked resource types, and the pagination <c>_links</c> to pagination envelopes (which the wire
/// always decorates). A no-op when Cairn itself is not registered (<c>AddCairn</c> was not called).
/// </summary>
internal sealed class HypermediaSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var provider = context.ApplicationServices.GetService<ILinkConfigProvider>();
        if (provider is not null)
        {
            // The schema is generated from the serializer's JsonTypeInfo, which already carries Cairn's
            // emit-stage contract properties as empty placeholders. Only a property backed by a real member
            // (an AttributeProvider) is the DTO's own and must keep the user's schema.
            bool DeclaredByType(string name)
            {
                foreach (var property in context.JsonTypeInfo.Properties)
                {
                    if (string.Equals(property.Name, name, StringComparison.Ordinal))
                    {
                        return property.AttributeProvider is not null;
                    }
                }

                return false;
            }

            if (provider.GetConfig(context.JsonTypeInfo.Type) is not null)
            {
                HypermediaJsonSchemas.Apply(schema, DeclaredByType);
            }
            else if (HypermediaJsonSchemas.IsPaginationEnvelope(context.JsonTypeInfo.Type, out var cursor))
            {
                HypermediaJsonSchemas.ApplyPaginationLinks(schema, cursor, DeclaredByType);
            }
        }

        return Task.CompletedTask;
    }
}
