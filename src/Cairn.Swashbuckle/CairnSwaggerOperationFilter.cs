using Cairn.Hypermedia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Cairn.Swashbuckle;

/// <summary>
/// Adds the hypermedia media types an opted-in endpoint can negotiate (<c>application/hal+json</c> and
/// <c>application/prs.hal-forms+json</c>) to each response that carries a Cairn-linked type, reusing the
/// <c>application/json</c> entry's schema. Takes the service provider rather than
/// <see cref="ILinkConfigProvider"/> directly so generation degrades to a no-op — instead of failing DI
/// activation — when Cairn itself is not registered (<c>AddCairn</c> was not called).
/// </summary>
internal sealed class CairnSwaggerOperationFilter(IServiceProvider services) : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (services.GetService<ILinkConfigProvider>() is { } provider)
        {
            HypermediaJsonSchemas.AddNegotiatedMediaTypes(context.ApiDescription, operation, provider);
        }
    }
}
