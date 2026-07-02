using Cairn.Hypermedia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Cairn.Swashbuckle;

/// <summary>
/// Adds the <c>_links</c>, <c>_embedded</c>, <c>_actions</c>, and <c>_templates</c> shape to the schemas of
/// Cairn-linked resource types. Takes the service provider rather than <see cref="ILinkConfigProvider"/>
/// directly so generation degrades to a no-op — instead of failing DI activation — when Cairn itself is not
/// registered (<c>AddCairn</c> was not called).
/// </summary>
internal sealed class CairnSwaggerSchemaFilter(IServiceProvider services) : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is OpenApiSchema concrete
            && services.GetService<ILinkConfigProvider>() is { } provider
            && provider.GetConfig(context.Type) is not null)
        {
            HypermediaJsonSchemas.Apply(concrete);
        }
    }
}
