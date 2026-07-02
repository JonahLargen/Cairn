using System.Globalization;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.OpenApi;

namespace Cairn.Hypermedia;

/// <summary>
/// The OpenAPI description of the hypermedia Cairn's emit stage injects — <c>_links</c>, <c>_embedded</c>,
/// <c>_actions</c> (default format), and <c>_templates</c> (HAL-FORMS) — plus the media types an opted-in
/// endpoint can negotiate. This file is compiled into both Cairn.OpenApi and Cairn.Swashbuckle (linked
/// source, no shared assembly) so the two documents always describe the same wire format.
/// </summary>
internal static class HypermediaJsonSchemas
{
    /// <summary>The HAL media type (<c>_links</c> only) an opted-in endpoint can negotiate.</summary>
    public const string HalMediaType = "application/hal+json";

    /// <summary>The HAL-FORMS media type (<c>_links</c> plus <c>_templates</c>) an opted-in endpoint can negotiate.</summary>
    public const string HalFormsMediaType = "application/prs.hal-forms+json";

    private const string JsonMediaType = "application/json";

    /// <summary>Adds the hypermedia properties to the schema of a Cairn-linked resource type.</summary>
    public static void Apply(OpenApiSchema schema)
    {
        schema.Properties ??= new Dictionary<string, IOpenApiSchema>();
        schema.Properties["_links"] = LinksSchema();
        schema.Properties["_embedded"] = EmbeddedSchema();
        schema.Properties["_actions"] = ActionsSchema();
        schema.Properties["_templates"] = TemplatesSchema();
    }

    /// <summary>Whether <paramref name="type"/> — or the element type it enumerates — is Cairn-linked.</summary>
    public static bool IsLinked(ILinkConfigProvider provider, Type? type)
    {
        if (type is null || type == typeof(void))
        {
            return false;
        }

        if (provider.GetConfig(type) is not null)
        {
            return true;
        }

        // A collection response links each element, so its items carry the hypermedia.
        return ElementTypeOf(type) is { } element && provider.GetConfig(element) is not null;
    }

    /// <summary>
    /// Mirrors each Cairn-linked <c>application/json</c> response of <paramref name="operation"/> onto the
    /// HAL and HAL-FORMS media types the endpoint can negotiate, reusing the JSON entry's schema.
    /// </summary>
    public static void AddNegotiatedMediaTypes(ApiDescription description, OpenApiOperation operation, ILinkConfigProvider provider)
    {
        if (operation.Responses is not { } responses)
        {
            return;
        }

        foreach (var responseType in description.SupportedResponseTypes)
        {
            if (!IsLinked(provider, responseType.Type))
            {
                continue;
            }

            var key = responseType.IsDefaultResponse ? "default" : responseType.StatusCode.ToString(CultureInfo.InvariantCulture);
            if (!responses.TryGetValue(key, out var response)
                || response.Content is not { } content
                || !content.TryGetValue(JsonMediaType, out var json))
            {
                continue;
            }

            content.TryAdd(HalMediaType, new OpenApiMediaType { Schema = json.Schema });
            content.TryAdd(HalFormsMediaType, new OpenApiMediaType { Schema = json.Schema });
        }
    }

    // _links: a relation's value is a single link object, or a JSON array when several links share the rel
    // (curies is always an array, even with one entry).
    private static OpenApiSchema LinksSchema() => new()
    {
        Type = JsonSchemaType.Object,
        Description = "Hypermedia links keyed by relation: a single link object, or a JSON array of link objects when several links share the relation (curies is always an array).",
        AdditionalProperties = new OpenApiSchema
        {
            AnyOf =
            [
                LinkObjectSchema(),
                new OpenApiSchema { Type = JsonSchemaType.Array, Items = LinkObjectSchema() },
            ],
        },
    };

    private static OpenApiSchema LinkObjectSchema() => new()
    {
        Type = JsonSchemaType.Object,
        Required = new HashSet<string>(StringComparer.Ordinal) { "href" },
        Properties = new Dictionary<string, IOpenApiSchema>
        {
            ["href"] = new OpenApiSchema { Type = JsonSchemaType.String },
            ["templated"] = new OpenApiSchema { Type = JsonSchemaType.Boolean },
            ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
            ["type"] = new OpenApiSchema { Type = JsonSchemaType.String },
            ["name"] = new OpenApiSchema { Type = JsonSchemaType.String },
            ["deprecation"] = new OpenApiSchema { Type = JsonSchemaType.String },
            ["hreflang"] = new OpenApiSchema { Type = JsonSchemaType.String },
            ["profile"] = new OpenApiSchema { Type = JsonSchemaType.String },
        },
    };

    private static OpenApiSchema EmbeddedSchema() => new()
    {
        Type = JsonSchemaType.Object,
        Description = "Embedded resources keyed by relation (HAL _embedded); each value is a resource or an array of resources.",
        AdditionalProperties = new OpenApiSchema(),
    };

    private static OpenApiSchema ActionsSchema() => new()
    {
        Type = JsonSchemaType.Object,
        Description = "Available actions (affordances) keyed by name. Emitted for the default JSON format; HAL omits affordances and HAL-FORMS projects them into _templates instead.",
        AdditionalProperties = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Required = new HashSet<string>(StringComparer.Ordinal) { "href", "method" },
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["href"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["method"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
            },
        },
    };

    // _templates: the HAL-FORMS projection of the affordances, keyed by template name.
    private static OpenApiSchema TemplatesSchema() => new()
    {
        Type = JsonSchemaType.Object,
        Description = "HAL-FORMS templates keyed by name. Emitted in place of _actions for application/prs.hal-forms+json responses.",
        AdditionalProperties = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Required = new HashSet<string>(StringComparer.Ordinal) { "method", "target", "contentType", "properties" },
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["method"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["target"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["contentType"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["properties"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.Array,
                    Items = TemplatePropertySchema(),
                },
            },
        },
    };

    // A HAL-FORMS template field, derived from the affordance input type's data annotations.
    private static OpenApiSchema TemplatePropertySchema() => new()
    {
        Type = JsonSchemaType.Object,
        Required = new HashSet<string>(StringComparer.Ordinal) { "name" },
        Properties = new Dictionary<string, IOpenApiSchema>
        {
            ["name"] = new OpenApiSchema { Type = JsonSchemaType.String },
            ["prompt"] = new OpenApiSchema { Type = JsonSchemaType.String },
            ["required"] = new OpenApiSchema { Type = JsonSchemaType.Boolean },
            ["readOnly"] = new OpenApiSchema { Type = JsonSchemaType.Boolean },
            ["type"] = new OpenApiSchema { Type = JsonSchemaType.String },
            ["placeholder"] = new OpenApiSchema { Type = JsonSchemaType.String },
            ["regex"] = new OpenApiSchema { Type = JsonSchemaType.String },
            ["maxLength"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
            ["min"] = new OpenApiSchema { Type = JsonSchemaType.Number },
            ["max"] = new OpenApiSchema { Type = JsonSchemaType.Number },
            ["value"] = new OpenApiSchema { Type = JsonSchemaType.String },
            ["options"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Properties = new Dictionary<string, IOpenApiSchema>
                {
                    ["inline"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Array,
                        Items = new OpenApiSchema
                        {
                            Type = JsonSchemaType.Object,
                            Properties = new Dictionary<string, IOpenApiSchema>
                            {
                                ["prompt"] = new OpenApiSchema { Type = JsonSchemaType.String },
                                ["value"] = new OpenApiSchema { Type = JsonSchemaType.String },
                            },
                        },
                    },
                },
            },
        },
    };

    private static Type? ElementTypeOf(Type type)
    {
        if (type == typeof(string))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            return type.GetGenericArguments()[0];
        }

        foreach (var candidate in type.GetInterfaces())
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return candidate.GetGenericArguments()[0];
            }
        }

        return null;
    }
}
