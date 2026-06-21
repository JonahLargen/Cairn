using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Cairn.Swashbuckle;

/// <summary>Swagger configuration extensions for Cairn.</summary>
public static class CairnSwaggerExtensions
{
    /// <summary>
    /// Documents Cairn's hypermedia (<c>_links</c> and <c>_actions</c>) on the schemas of linked resource types.
    /// </summary>
    /// <param name="options">The Swagger generation options.</param>
    /// <returns>The same <see cref="SwaggerGenOptions"/> instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public static SwaggerGenOptions AddCairnHypermedia(this SwaggerGenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.SchemaFilter<CairnSwaggerSchemaFilter>();
        return options;
    }
}
