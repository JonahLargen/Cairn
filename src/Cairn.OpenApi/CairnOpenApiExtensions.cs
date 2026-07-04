using Microsoft.AspNetCore.OpenApi;

namespace Cairn.OpenApi;

/// <summary>OpenAPI configuration extensions for Cairn.</summary>
public static class CairnOpenApiExtensions
{
    /// <summary>
    /// Documents Cairn's hypermedia on the OpenAPI document: the format-neutral <c>_links</c> and
    /// <c>_embedded</c> core on the schemas of linked resource types, and — per negotiated media type
    /// (<c>application/hal+json</c> / <c>application/prs.hal-forms+json</c> alongside <c>application/json</c>)
    /// — the format-specific <c>_actions</c> (default JSON) and <c>_templates</c> (HAL-FORMS) sections. Also
    /// marks <c>WithDeprecation</c> operations deprecated and documents the <c>ETag</c> header and <c>304</c>
    /// response of <c>WithETag</c> operations.
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
