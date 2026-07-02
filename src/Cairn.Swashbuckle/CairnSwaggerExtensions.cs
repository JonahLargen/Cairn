using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Cairn.Swashbuckle;

/// <summary>Swagger configuration extensions for Cairn.</summary>
public static class CairnSwaggerExtensions
{
    /// <summary>
    /// Documents Cairn's hypermedia on the Swagger document: the <c>_links</c>, <c>_embedded</c>,
    /// <c>_actions</c>, and <c>_templates</c> (HAL-FORMS) shape on the schemas of linked resource types, and
    /// the <c>application/hal+json</c> / <c>application/prs.hal-forms+json</c> media types those types'
    /// responses can negotiate.
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
