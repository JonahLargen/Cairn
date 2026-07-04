using Cairn.Hypermedia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Cairn.Swashbuckle;

/// <summary>
/// Adds the <c>_links</c> and <c>_embedded</c> shape to the schemas of Cairn-linked resource types (the
/// format-specific <c>_actions</c>/<c>_templates</c> sections are added per negotiated media type by
/// <see cref="CairnSwaggerOperationFilter"/>), and the pagination <c>_links</c> to pagination envelopes (which
/// the wire always decorates). Takes the service provider rather than <see cref="ILinkConfigProvider"/>
/// directly so generation degrades to a no-op — instead of failing DI activation — when Cairn itself is not
/// registered (<c>AddCairn</c> was not called).
/// </summary>
internal sealed class CairnSwaggerSchemaFilter(IServiceProvider services) : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is not OpenApiSchema concrete || services.GetService<ILinkConfigProvider>() is not { } provider)
        {
            return;
        }

        if (provider.GetConfig(context.Type) is { } config)
        {
            var (embeds, childSchema) = ResolveEmbeds(config, context);
            HypermediaJsonSchemas.Apply(concrete, embeds: embeds, childSchema: childSchema);
        }
        else if (HypermediaJsonSchemas.IsPaginationEnvelope(context.Type, services.GetService<IPaginationEnvelopeProvider>(), out var cursor))
        {
            HypermediaJsonSchemas.ApplyPaginationLinks(concrete, cursor);
        }
    }

    // Resolves each declared embed's child resource schema through the generator (a $ref to the child's own
    // component), so the typed _embedded shape reuses it. Returns nulls when the config declares no embeds,
    // leaving _embedded an untyped object.
    private static (IReadOnlyList<EmbeddedResourceSchema>?, Func<Type, IOpenApiSchema>?) ResolveEmbeds(ICompiledLinkConfig config, SchemaFilterContext context)
    {
        if (config is not IEmbeddedResourceReportingConfig { EmbeddedResources: { Count: > 0 } embeds })
        {
            return (null, null);
        }

        return (embeds, type => context.SchemaGenerator.GenerateSchema(type, context.SchemaRepository));
    }
}
