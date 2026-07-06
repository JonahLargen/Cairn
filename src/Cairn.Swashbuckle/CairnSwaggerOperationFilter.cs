using Cairn.Hypermedia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Cairn.Swashbuckle;

/// <summary>
/// Documents an opted-in endpoint's hypermedia on its operation: the per-format schemas of the media types it
/// can negotiate (<c>application/hal+json</c> and <c>application/prs.hal-forms+json</c>) on each response that
/// carries a Cairn-linked type, an <c>ETag</c> header and <c>304</c> response when it uses <c>WithETag</c>, the
/// <c>412</c>/<c>428</c> precondition responses when it uses <c>WithPreconditions</c>, and, when it uses
/// <c>WithDeprecation</c>, <c>deprecated: true</c> plus the <c>Deprecation</c>/<c>Sunset</c>/<c>Link</c>
/// response headers. Takes the service provider rather than
/// <see cref="ILinkConfigProvider"/> directly so generation degrades to a no-op — instead of failing DI
/// activation — when Cairn itself is not registered (<c>AddCairn</c> was not called). The ETag, precondition,
/// and deprecation markers do not depend on the link provider.
/// </summary>
internal sealed class CairnSwaggerOperationFilter(IServiceProvider services) : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        // Deprecation, ETag, and preconditions are endpoint conventions (WithDeprecation/WithETag/
        // WithPreconditions) independent of the link configuration, so they are documented whether or not
        // AddCairn registered the link provider. Deprecation runs last so its response headers also land on the
        // 304/412/428 responses ETag and preconditions add.
        HypermediaJsonSchemas.DocumentETag(context.ApiDescription, operation);
        HypermediaJsonSchemas.DocumentPreconditions(context.ApiDescription, operation);
        HypermediaJsonSchemas.DocumentDeprecation(context.ApiDescription, operation);

        if (services.GetService<ILinkConfigProvider>() is { } provider)
        {
            HypermediaJsonSchemas.AddNegotiatedMediaTypes(context.ApiDescription, operation, provider, services.GetService<IPaginationEnvelopeProvider>());
        }
    }
}
