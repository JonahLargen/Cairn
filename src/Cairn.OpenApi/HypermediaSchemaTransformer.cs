using Cairn.Hypermedia;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Cairn.OpenApi;

/// <summary>
/// Adds the <c>_links</c> and <c>_embedded</c> shape to the schemas of Cairn-linked resource types (the
/// format-specific <c>_actions</c>/<c>_templates</c> sections are added per negotiated media type by
/// <see cref="HypermediaOperationTransformer"/>), and the pagination <c>_links</c> to pagination envelopes
/// (which the wire always decorates). A no-op when Cairn itself is not registered (<c>AddCairn</c> was not
/// called).
/// </summary>
internal sealed class HypermediaSchemaTransformer : IOpenApiSchemaTransformer
{
    private static readonly string[] HypermediaNames = ["_links", "_embedded", "_actions", "_templates"];

    // The format-specific sections are documented per negotiated media type by the operation transformer, not
    // on the shared component; their emit-stage placeholders on a configured type's contract are phantom here.
    private static readonly string[] FormatSpecificNames = ["_actions", "_templates"];

    public async Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var provider = context.ApplicationServices.GetService<ILinkConfigProvider>();
        if (provider is null)
        {
            return;
        }

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
        if (provider.GetConfig(context.JsonTypeInfo.Type) is { } config)
        {
            var (embeds, childSchema) = await ResolveEmbedsAsync(config, context, cancellationToken).ConfigureAwait(false);
            HypermediaJsonSchemas.Apply(schema, DeclaredByType, embeds, childSchema);

            // Apply documents the format-neutral core (_links/_embedded); the _actions/_templates placeholders
            // the contract injected are documented per media type by the operation transformer, so strip the
            // phantom leftovers here (a DTO's own _actions/_templates member has a backing member and is kept).
            StripHypermediaPlaceholders(schema, context, FormatSpecificNames);
        }
        else if (HypermediaJsonSchemas.IsPaginationEnvelope(context.JsonTypeInfo.Type, context.ApplicationServices.GetService<IPaginationEnvelopeProvider>(), out var cursor))
        {
            // The wire only adds navigation _links to an unconfigured envelope; the remaining
            // placeholders (_embedded/_actions/_templates, custom formatter names) are phantom.
            StripPlaceholders(schema, context);
            HypermediaJsonSchemas.ApplyPaginationLinks(schema, cursor, DeclaredByType);
        }
        else
        {
            // A polymorphic base of a configured type: the contract modifier injects the emit-stage
            // placeholders so a configured subtype serialized through this base still emits its hypermedia,
            // but the base documents no hypermedia of its own. Strip the injected placeholders. Types Cairn
            // never decorates have no placeholders to strip, so this is a no-op for them.
            StripHypermediaPlaceholders(schema, context, HypermediaNames);
        }
    }

    // Resolves the schema of each declared embed's child resource type through the generator, so the typed
    // _embedded shape references the same component the child's own schema produces. Returns nulls when the
    // config declares no embeds, leaving _embedded an untyped object.
    private static async Task<(IReadOnlyList<EmbeddedResourceSchema>?, Func<Type, IOpenApiSchema>?)> ResolveEmbedsAsync(
        ICompiledLinkConfig config, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (config is not IEmbeddedResourceReportingConfig { EmbeddedResources: { Count: > 0 } embeds })
        {
            return (null, null);
        }

        var map = new Dictionary<Type, IOpenApiSchema>();
        foreach (var embed in embeds)
        {
            if (!map.ContainsKey(embed.ResourceType))
            {
                map[embed.ResourceType] = await context.GetOrCreateSchemaAsync(embed.ResourceType, null, cancellationToken).ConfigureAwait(false);
            }
        }

        return (embeds, type => map[type]);
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

    // Removes the named hypermedia placeholders (a JsonTypeInfo property with one of the given reserved names
    // and no backing member). Unlike StripPlaceholders this never touches other member-less properties, so it
    // is safe to run over every unconfigured type without disturbing a DTO's own schema, and it can be scoped
    // to just the format-specific names on a configured type (whose _links/_embedded are kept).
    private static void StripHypermediaPlaceholders(OpenApiSchema schema, OpenApiSchemaTransformerContext context, string[] names)
    {
        if (schema.Properties is not { Count: > 0 } properties)
        {
            return;
        }

        foreach (var property in context.JsonTypeInfo.Properties)
        {
            if (property.AttributeProvider is null && Array.IndexOf(names, property.Name) >= 0)
            {
                properties.Remove(property.Name);
            }
        }
    }
}
