using System.Diagnostics.CodeAnalysis;
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

    // Matched by full name because this file is compiled into projects that don't reference Cairn.AspNetCore.
    private const string PagedResourceInterface = "Cairn.AspNetCore.IPagedResource";
    private const string CursorPagedResourceInterface = "Cairn.AspNetCore.ICursorPagedResource";

    /// <summary>
    /// Adds the hypermedia properties to the schema of a Cairn-linked resource type. Mirrors the emit stage's
    /// collision guard: a hypermedia property the DTO itself declares is the user's real property — the wire
    /// serializes their data, so the document must keep their schema rather than clobber it with Cairn's shape.
    /// <paramref name="declaredByType"/> says whether an already-present schema property is such a real member;
    /// when unknowable (<see langword="null"/>), any existing property is preserved. Cairn's own emit-stage
    /// contract properties (present when the schema is generated from the serializer's <c>JsonTypeInfo</c>)
    /// are not real members and are replaced by the full hypermedia shape.
    /// </summary>
    public static void Apply(OpenApiSchema schema, Func<string, bool>? declaredByType = null)
    {
        schema.Properties ??= new Dictionary<string, IOpenApiSchema>();
        Set(schema.Properties, "_links", LinksSchema(), declaredByType);
        Set(schema.Properties, "_embedded", EmbeddedSchema(), declaredByType);
        Set(schema.Properties, "_actions", ActionsSchema(), declaredByType);
        Set(schema.Properties, "_templates", TemplatesSchema(), declaredByType);
    }

    /// <summary>
    /// Adds the <c>_links</c> the wire always decorates a pagination envelope with — the navigation relations
    /// of offset (<c>self/first/prev/next/last</c>) or cursor (<c>self/next/prev</c>) pagination.
    /// </summary>
    public static void ApplyPaginationLinks(OpenApiSchema schema, bool cursor, Func<string, bool>? declaredByType = null)
    {
        schema.Properties ??= new Dictionary<string, IOpenApiSchema>();
        Set(
            schema.Properties,
            "_links",
            PaginationLinksSchema(cursor ? ["self", "next", "prev"] : ["self", "first", "prev", "next", "last"]),
            declaredByType);
    }

    private static void Set(IDictionary<string, IOpenApiSchema> properties, string name, OpenApiSchema value, Func<string, bool>? declaredByType)
    {
        // An existing property that is a real member of the type keeps the user's schema (the wire serializes
        // their data); one that came from Cairn's serializer contract is a placeholder to replace.
        if (properties.ContainsKey(name) && (declaredByType?.Invoke(name) ?? true))
        {
            return;
        }

        properties[name] = value;
    }

    /// <summary>
    /// Whether the wire decorates (and relabels) a response of <paramref name="type"/>: a configured resource
    /// type, or a pagination envelope, which always gets navigation links. A bare collection is deliberately
    /// not linked — its elements carry hypermedia, but a JSON array is not a HAL document and stays
    /// <c>application/json</c> on the wire.
    /// </summary>
    public static bool IsLinked(ILinkConfigProvider provider, IPaginationEnvelopeProvider? envelopes, Type? type)
    {
        if (type is null || type == typeof(void))
        {
            return false;
        }

        return provider.GetConfig(type) is not null || IsPaginationEnvelope(type, envelopes, out _);
    }

    /// <summary>
    /// Whether <paramref name="type"/> is a pagination envelope: <c>PagedResource&lt;T&gt;</c>,
    /// <c>CursorPage&lt;T&gt;</c>, any implementation of their interfaces, or a type adapted via
    /// <c>AddPaging</c>/<c>AddCursorPaging</c> — those registrations live in Cairn.AspNetCore's options
    /// and reach here through the host's <paramref name="envelopes"/> service. <paramref name="cursor"/>
    /// says which navigation shape the envelope carries.
    /// </summary>
    public static bool IsPaginationEnvelope(Type type, IPaginationEnvelopeProvider? envelopes, out bool cursor)
    {
        if (IsPaginationEnvelope(type, out cursor))
        {
            return true;
        }

        return envelopes is not null && envelopes.IsPaginationEnvelope(type, out cursor);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070:UnrecognizedReflectionPattern",
        Justification = "Detects the pagination-envelope interfaces by name for OpenAPI document generation. A type whose envelope interface was trimmed cannot behave as an envelope at runtime either, so describing it as unlinked is consistent with actual behavior.")]
    private static bool IsPaginationEnvelope(Type type, out bool cursor)
    {
        foreach (var candidate in type.GetInterfaces())
        {
            if (candidate.FullName == PagedResourceInterface)
            {
                cursor = false;
                return true;
            }

            if (candidate.FullName == CursorPagedResourceInterface)
            {
                cursor = true;
                return true;
            }
        }

        // The declared response type may be the envelope interface itself.
        cursor = type.FullName == CursorPagedResourceInterface;
        return cursor || type.FullName == PagedResourceInterface;
    }

    /// <summary>
    /// Mirrors each Cairn-linked <c>application/json</c> response of <paramref name="operation"/> onto the
    /// HAL and HAL-FORMS media types the endpoint can negotiate, reusing the JSON entry's schema.
    /// </summary>
    public static void AddNegotiatedMediaTypes(ApiDescription description, OpenApiOperation operation, ILinkConfigProvider provider, IPaginationEnvelopeProvider? envelopes)
    {
        // Only an endpoint that opted in (WithLinks()/[CairnLinks]) actually projects hypermedia, so only
        // those responses negotiate hal+json/hal-forms+json. Without this gate the document would advertise
        // the HAL media types on every endpoint returning a configured type, and a generated client asking
        // for hal+json would get plain JSON from an endpoint that never opted in.
        if (operation.Responses is not { } responses || !OptedIntoLinks(description))
        {
            return;
        }

        foreach (var responseType in description.SupportedResponseTypes)
        {
            if (!IsLinked(provider, envelopes, responseType.Type))
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

    // Matched by full name because this file is compiled into projects that don't reference Cairn.AspNetCore.
    private const string LinksMetadataInterface = "Cairn.AspNetCore.Internal.ICairnLinksMetadata";

    // Whether the endpoint opted into Cairn hypermedia. WithLinks() and [CairnLinks] both leave a metadata
    // object implementing ICairnLinksMetadata in the endpoint metadata; an endpoint that only returns a
    // configured type but never opted in projects no links, so it negotiates no HAL media types.
    private static bool OptedIntoLinks(ApiDescription description)
    {
        // ActionDescriptor and its EndpointMetadata are non-null for every ApiExplorer description, and
        // endpoint metadata never contains null entries, so no defensive guards are needed here.
        foreach (var item in description.ActionDescriptor.EndpointMetadata)
        {
            if (ImplementsLinksMarker(item.GetType()))
            {
                return true;
            }
        }

        return false;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070:UnrecognizedReflectionPattern",
        Justification = "Matches the Cairn links-opt-in marker interface by full name for OpenAPI document generation. A metadata type whose marker interface was trimmed cannot behave as opted-in at runtime either, so treating it as not-opted-in is consistent with actual behavior.")]
    private static bool ImplementsLinksMarker(Type type)
    {
        foreach (var iface in type.GetInterfaces())
        {
            if (iface.FullName == LinksMetadataInterface)
            {
                return true;
            }
        }

        return false;
    }

    // Every hypermedia section is response-only decoration, so each schema below carries readOnly: true —
    // the schema component for a type is shared between its response and request-body usages, and readOnly
    // is OpenAPI's way of saying "sent in responses, never in requests".

    // _links: a relation's value is a single link object, or a JSON array when several links share the rel
    // (curies is always an array, even with one entry).
    private static OpenApiSchema LinksSchema() => new()
    {
        Type = JsonSchemaType.Object,
        ReadOnly = true,
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

    // _links on a pagination envelope: the navigation relations the wire emits (absent ones are omitted,
    // e.g. no prev on the first page), plus any relations configured for the envelope type itself.
    private static OpenApiSchema PaginationLinksSchema(string[] relations)
    {
        var properties = new Dictionary<string, IOpenApiSchema>();
        foreach (var relation in relations)
        {
            properties[relation] = LinkObjectSchema();
        }

        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            ReadOnly = true,
            Description = $"Pagination links keyed by relation ({string.Join("/", relations)}); relations without a target page are omitted.",
            Properties = properties,
            AdditionalProperties = new OpenApiSchema
            {
                AnyOf =
                [
                    LinkObjectSchema(),
                    new OpenApiSchema { Type = JsonSchemaType.Array, Items = LinkObjectSchema() },
                ],
            },
        };
    }

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
        ReadOnly = true,
        Description = "Embedded resources keyed by relation (HAL _embedded); each value is a resource or an array of resources.",
        AdditionalProperties = new OpenApiSchema(),
    };

    private static OpenApiSchema ActionsSchema() => new()
    {
        Type = JsonSchemaType.Object,
        ReadOnly = true,
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
        ReadOnly = true,
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
            ["minLength"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
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
                    // Options by reference: a link the client dereferences for the value list, in place of inline.
                    ["link"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        Required = new HashSet<string>(StringComparer.Ordinal) { "href" },
                        Properties = new Dictionary<string, IOpenApiSchema>
                        {
                            ["href"] = new OpenApiSchema { Type = JsonSchemaType.String },
                            ["templated"] = new OpenApiSchema { Type = JsonSchemaType.Boolean },
                            ["type"] = new OpenApiSchema { Type = JsonSchemaType.String },
                        },
                    },
                    ["promptField"] = new OpenApiSchema { Type = JsonSchemaType.String },
                    ["valueField"] = new OpenApiSchema { Type = JsonSchemaType.String },
                },
            },
        },
    };

}
