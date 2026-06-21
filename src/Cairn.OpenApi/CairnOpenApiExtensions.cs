using Microsoft.AspNetCore.OpenApi;

namespace Cairn.OpenApi;

/// <summary>OpenAPI configuration extensions for Cairn.</summary>
public static class CairnOpenApiExtensions
{
    /// <summary>
    /// Documents Cairn's hypermedia (<c>_links</c> and <c>_actions</c>) on the schemas of linked resource types.
    /// </summary>
    /// <param name="options">The OpenAPI options.</param>
    /// <returns>The same <see cref="OpenApiOptions"/> instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public static OpenApiOptions AddCairnHypermedia(this OpenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AddSchemaTransformer(new HypermediaSchemaTransformer());
        return options;
    }
}
