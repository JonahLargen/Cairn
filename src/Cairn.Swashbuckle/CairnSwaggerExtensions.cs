using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Cairn.Swashbuckle;

/// <summary>Swagger configuration extensions for Cairn.</summary>
public static class CairnSwaggerExtensions
{
    /// <summary>
    /// Documents Cairn's hypermedia on the Swagger document: the format-neutral <c>_links</c> and
    /// <c>_embedded</c> core on the schemas of linked resource types, and — per negotiated media type
    /// (<c>application/hal+json</c> / <c>application/prs.hal-forms+json</c> alongside <c>application/json</c>)
    /// — the format-specific <c>_actions</c> (default JSON) and <c>_templates</c> (HAL-FORMS) sections. Also
    /// marks <c>WithDeprecation</c> operations deprecated and documents the <c>ETag</c> header and <c>304</c>
    /// response of <c>WithETag</c> operations.
    /// </summary>
    /// <param name="options">The Swagger generation options.</param>
    /// <returns>The same <see cref="SwaggerGenOptions"/> instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public static SwaggerGenOptions AddCairnHypermedia(this SwaggerGenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.SchemaFilter<CairnSwaggerSchemaFilter>();
        options.OperationFilter<CairnSwaggerOperationFilter>();
        return options;
    }
}
