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
            // The schema is generated from the serializer's JsonTypeInfo; for a hypermedia-capable type it
            // carries Cairn's emit-stage contract properties as empty placeholders. Only a property backed by
            // a real member (an AttributeProvider) is the DTO's own and must keep the user's schema.
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

            // The transformer must decide per TYPE, not per usage: the generator shares one schema component
            // between a type's response and request-body appearances, so usage-dependent edits would be
            // order-dependent. Request bodies are covered by the applied shape itself — every hypermedia
            // property carries readOnly: true, OpenAPI's "response-only" marker.
            if (provider.GetConfig(context.JsonTypeInfo.Type) is not null)
            {
                HypermediaJsonSchemas.Apply(schema, DeclaredByType);
            }
            else if (HypermediaJsonSchemas.IsPaginationEnvelope(context.JsonTypeInfo.Type, context.ApplicationServices.GetService<IPaginationEnvelopeProvider>(), out var cursor))
            {
                // The wire only adds navigation _links to an unconfigured envelope; the remaining
                // placeholders (_embedded/_actions/_templates, custom formatter names) are phantom.
                StripPlaceholders(schema, context);
                HypermediaJsonSchemas.ApplyPaginationLinks(schema, cursor, DeclaredByType);
            }
        }

        return Task.CompletedTask;
    }

    // Removes every schema property that only exists because Cairn's contract modifier injected it —
    // identifiable as a JsonTypeInfo property with no member behind it (AttributeProvider is null). Only the
    // navigation _links (re-applied above) are real on an unconfigured envelope; the remaining placeholders,
    // including any custom formatter property names, are phantom.
    private static void StripPlaceholders(OpenApiSchema schema, OpenApiSchemaTransformerContext context)
    {
        if (schema.Properties is not { Count: > 0 } properties)
        {
            return;
        }

        foreach (var property in context.JsonTypeInfo.Properties)
        {
            if (property.AttributeProvider is null)
            {
                properties.Remove(property.Name);
            }
        }
    }
}
