using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Cairn.Swashbuckle;

/// <summary>Adds the <c>_links</c> and <c>_actions</c> shape to the schemas of Cairn-linked resource types.</summary>
internal sealed class CairnSwaggerSchemaFilter(ILinkConfigProvider provider) : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is not OpenApiSchema concrete || provider.GetConfig(context.Type) is null)
        {
            return;
        }

        concrete.Properties ??= new Dictionary<string, IOpenApiSchema>();
        concrete.Properties["_links"] = LinksSchema();
        concrete.Properties["_actions"] = ActionsSchema();
    }

    private static OpenApiSchema LinksSchema() => new()
    {
        Type = JsonSchemaType.Object,
        Description = "Hypermedia links keyed by relation.",
        AdditionalProperties = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["href"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["templated"] = new OpenApiSchema { Type = JsonSchemaType.Boolean },
            },
        },
    };

    private static OpenApiSchema ActionsSchema() => new()
    {
        Type = JsonSchemaType.Object,
        Description = "Available actions (affordances) keyed by name.",
        AdditionalProperties = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["href"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["method"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
            },
        },
    };
}
