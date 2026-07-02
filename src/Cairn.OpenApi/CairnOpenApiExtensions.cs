using Microsoft.AspNetCore.OpenApi;

namespace Cairn.OpenApi;

/// <summary>OpenAPI configuration extensions for Cairn.</summary>
public static class CairnOpenApiExtensions
{
    /// <summary>
    /// Documents Cairn's hypermedia on the OpenAPI document: the <c>_links</c>, <c>_embedded</c>,
    /// <c>_actions</c>, and <c>_templates</c> (HAL-FORMS) shape on the schemas of linked resource types, and
    /// the <c>application/hal+json</c> / <c>application/prs.hal-forms+json</c> media types those types'
    /// responses can negotiate.
    /// </summary>
    /// <param name="options">The OpenAPI options.</param>
    /// <returns>The same <see cref="OpenApiOptions"/> instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public static OpenApiOptions AddCairnHypermedia(this OpenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AddSchemaTransformer(new HypermediaSchemaTransformer());
        options.AddOperationTransformer(new HypermediaOperationTransformer());
        return options;
    }
}
