using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Cairn.OpenApi;

/// <summary>Adds the <c>_links</c> and <c>_actions</c> shape to the schemas of Cairn-linked resource types.</summary>
internal sealed class HypermediaSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var provider = context.ApplicationServices.GetService<ILinkConfigProvider>();
        if (provider is null || provider.GetConfig(context.JsonTypeInfo.Type) is null)
        {
            return Task.CompletedTask;
        }

        schema.Properties ??= new Dictionary<string, IOpenApiSchema>();
        schema.Properties["_links"] = LinksSchema();
        schema.Properties["_actions"] = ActionsSchema();
        return Task.CompletedTask;
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
