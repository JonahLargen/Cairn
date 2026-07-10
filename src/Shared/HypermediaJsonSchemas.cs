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
    /// <summary>The HAL media type (<c>_links</c> plus <c>_embedded</c>) an opted-in endpoint can negotiate.</summary>
    public const string HalMediaType = "application/hal+json";

    /// <summary>The HAL-FORMS media type (<c>_links</c>, <c>_embedded</c>, plus <c>_templates</c>) an opted-in endpoint can negotiate.</summary>
    public const string HalFormsMediaType = "application/prs.hal-forms+json";

    private const string JsonMediaType = "application/json";

    // Matched by full name because this file is compiled into projects that don't reference Cairn.AspNetCore.
    private const string PagedResourceInterface = "Cairn.AspNetCore.IPagedResource";
    private const string CursorPagedResourceInterface = "Cairn.AspNetCore.ICursorPagedResource";

    /// <summary>
    /// Adds the format-neutral hypermedia core to the schema of a Cairn-linked resource type: <c>_links</c>
    /// and <c>_embedded</c>, the two sections every wire format emits. The format-specific sections
    /// (<c>_actions</c> for default JSON, <c>_templates</c> for HAL-FORMS) are not added here — they are
    /// projected per media type by <see cref="AddNegotiatedMediaTypes"/>, because the shared component schema
    /// cannot carry a section one negotiated format emits and another omits.
    /// <para>
    /// Mirrors the emit stage's collision guard: a hypermedia property the DTO itself declares is the user's
    /// real property — the wire serializes their data, so the document must keep their schema rather than
    /// clobber it with Cairn's shape. <paramref name="declaredByType"/> says whether an already-present schema
    /// property is such a real member; when unknowable (<see langword="null"/>), any existing property is
    /// preserved. When <paramref name="embeds"/> and <paramref name="childSchema"/> are supplied, the
    /// <c>_embedded</c> schema is typed with each declared relation's child resource schema; otherwise it is an
    /// untyped object.
    /// </para>
    /// </summary>
    public static void Apply(
        OpenApiSchema schema,
        Func<string, bool>? declaredByType = null,
        IReadOnlyList<EmbeddedResourceSchema>? embeds = null,
        Func<Type, IOpenApiSchema>? childSchema = null)
    {
        schema.Properties ??= new Dictionary<string, IOpenApiSchema>();
        Set(schema.Properties, "_links", LinksSchema(), declaredByType);
        Set(schema.Properties, "_embedded", EmbeddedSchema(embeds, childSchema), declaredByType);
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
    /// Projects each Cairn-linked <c>application/json</c> response of <paramref name="operation"/> onto the
    /// per-format schemas the endpoint can negotiate. A configured resource emits distinct shapes per format —
    /// <c>_actions</c> only in default JSON, <c>_templates</c> only in HAL-FORMS, neither in HAL — so each media
    /// type gets its own schema; a pagination envelope emits only navigation <c>_links</c>, identical across
    /// the three, so the negotiated types reuse the JSON schema.
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
                || !content.TryGetValue(JsonMediaType, out var json)
                || json.Schema is not { } baseSchema)
            {
                continue;
            }

            // A configured resource's default JSON carries _actions and its HAL-FORMS carries _templates, so
            // the media types differ; the shared component schema holds the format-neutral core (_links,
            // _embedded) and each format extends it via allOf (HAL adds nothing). IsLinked already established
            // the response type is non-null.
            if (provider.GetConfig(responseType.Type!) is not null)
            {
                json.Schema = new OpenApiSchema { AllOf = [baseSchema, SectionFragment("_actions", ActionsSchema())] };
                content.TryAdd(HalMediaType, new OpenApiMediaType { Schema = baseSchema });
                content.TryAdd(HalFormsMediaType, new OpenApiMediaType { Schema = new OpenApiSchema { AllOf = [baseSchema, SectionFragment("_templates", TemplatesSchema())] } });
            }
            else
            {
                // A pagination envelope emits only navigation _links, identical across formats.
                content.TryAdd(HalMediaType, new OpenApiMediaType { Schema = baseSchema });
                content.TryAdd(HalFormsMediaType, new OpenApiMediaType { Schema = baseSchema });
            }
        }
    }

    /// <summary>
    /// Documents an endpoint that declared deprecation via <c>WithDeprecation(...)</c>: sets
    /// <c>deprecated: true</c> on <paramref name="operation"/> so tooling flags it, and — mirroring the headers
    /// <c>CairnHeadersMiddleware</c> emits — documents a <c>Deprecation</c> response header (RFC 9745) on every
    /// response, a <c>Sunset</c> header (RFC 8594) when a sunset date was supplied, and a <c>Link</c> header with
    /// <c>rel="deprecation"</c> when a documentation URL was supplied. The headers land on every response because
    /// the middleware emits them regardless of status code, so a client sees the deprecation on any outcome —
    /// including the conditional/error responses (<c>304</c>/<c>412</c>/<c>428</c>) the other transformers add,
    /// which is why this runs after them.
    /// </summary>
    public static void DocumentDeprecation(ApiDescription description, OpenApiOperation operation)
    {
        if (operation.Responses is not { } responses || GetEndpointMetadata(description, DeprecationMetadataName) is not { } metadata)
        {
            return;
        }

        operation.Deprecated = true;

        // Sunset and Link are optional (WithDeprecation emits them only when a sunset date / documentation URL
        // was supplied), so their presence is read off the precomputed metadata rather than assumed.
        var sunset = ReadStringMember(metadata, "Sunset");
        var link = ReadStringMember(metadata, "Link");

        foreach (var (_, response) in responses)
        {
            // The middleware sets the deprecation headers in Response.OnStarting regardless of status, so each
            // concrete response documents them; a reference response is not ours to mutate. TryAdd so an
            // endpoint that declared its own header wins.
            if (response is not OpenApiResponse concrete)
            {
                continue;
            }

            concrete.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal);
            concrete.Headers.TryAdd("Deprecation", DeprecationHeader());
            if (sunset is not null)
            {
                concrete.Headers.TryAdd("Sunset", SunsetHeader());
            }

            if (link is not null)
            {
                concrete.Headers.TryAdd("Link", DeprecationLinkHeader());
            }
        }
    }

    /// <summary>
    /// Documents the entity tag an endpoint with <c>WithETag(...)</c> emits: an <c>ETag</c> response header on
    /// each success (2xx) response, and a <c>304 Not Modified</c> response for the conditional
    /// <c>GET</c>/<c>HEAD</c> that <c>WithETag</c> answers when <c>If-None-Match</c> still matches.
    /// </summary>
    public static void DocumentETag(ApiDescription description, OpenApiOperation operation)
    {
        if (operation.Responses is not { } responses || !HasEndpointMetadata(description, ETagMetadataName))
        {
            return;
        }

        foreach (var (status, response) in responses)
        {
            // Only concrete success responses carry a body (and therefore an entity tag); the "default"
            // catch-all and error responses do not. A reference response is not ours to mutate.
            if (IsSuccessStatus(status) && response is OpenApiResponse concrete)
            {
                concrete.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal);
                concrete.Headers.TryAdd("ETag", ETagHeader());
            }
        }

        // WithETag answers a conditional GET/HEAD whose If-None-Match still matches with 304 (RFC 9110 §15.4.5).
        if (!responses.ContainsKey("304"))
        {
            responses["304"] = new OpenApiResponse
            {
                Description = "Not Modified — the entity tag supplied in If-None-Match still matches, so the body is omitted (RFC 9110 §15.4.5).",
            };
        }
    }

    /// <summary>
    /// Documents the write preconditions an endpoint with <c>WithPreconditions(...)</c> evaluates via
    /// <c>CairnPreconditions.Evaluate</c>: a <c>412 Precondition Failed</c> response (RFC 9110 §13) — as
    /// <c>application/problem+json</c>, echoing the resource's current validator in an <c>ETag</c> header the
    /// client can retry with — and, when the endpoint requires a conditional header, a <c>428 Precondition
    /// Required</c> response. Both are per-instance problem documents whose exact shape is unknowable at build
    /// time, so each is described with the standard RFC 9457 members.
    /// </summary>
    public static void DocumentPreconditions(ApiDescription description, OpenApiOperation operation)
    {
        if (operation.Responses is not { } responses || !HasEndpointMetadata(description, PreconditionMetadataName))
        {
            return;
        }

        // An If-Match/If-None-Match condition that doesn't hold against the resource's current state fails with
        // 412 (RFC 9110 §13.1). TryAdd so an endpoint that declared its own 412 (e.g. via ProducesProblem) wins.
        responses.TryAdd("412", PreconditionFailedResponse());

        // WithPreconditions(requireIfMatch: true) additionally makes a write carrying no conditional header fail
        // with 428 (RFC 6585 §3), matching Evaluate's requireIfMatch path.
        if (HasEndpointMetadata(description, PreconditionRequiredMetadataName))
        {
            responses.TryAdd("428", PreconditionRequiredResponse());
        }
    }

    /// <summary>
    /// Documents the query parameters an endpoint binding <c>PageRequest</c>/<c>CursorRequest</c> reads.
    /// The binding is a <c>BindAsync</c> parameter ApiExplorer cannot see into, so without this the operation
    /// would advertise no pagination inputs at all; the parameter names and bounds the binding resolved from
    /// <c>CairnOptions</c> travel on endpoint metadata it populates at map time. A parameter the endpoint
    /// already documents under the same name is the author's — theirs wins.
    /// </summary>
    public static void DocumentPaginationBinding(ApiDescription description, OpenApiOperation operation)
    {
        if (GetEndpointMetadata(description, PageBindingMetadataName) is { } page)
        {
            AddQueryParameter(
                operation,
                ReadStringMember(page, "PageParameter")!,
                JsonSchemaType.Integer,
                "The 1-based page number of the requested page (default 1).");
            AddQueryParameter(
                operation,
                ReadStringMember(page, "PageSizeParameter")!,
                JsonSchemaType.Integer,
                PageSizeDescription("page size", ReadIntMember(page, "DefaultPageSize")!.Value, ReadIntMember(page, "MaxPageSize")));
        }

        if (GetEndpointMetadata(description, CursorBindingMetadataName) is { } cursor)
        {
            AddQueryParameter(
                operation,
                ReadStringMember(cursor, "CursorParameter")!,
                JsonSchemaType.String,
                "The opaque cursor of the requested page; omit it for the first page.");
            AddQueryParameter(
                operation,
                ReadStringMember(cursor, "LimitParameter")!,
                JsonSchemaType.Integer,
                PageSizeDescription("limit", ReadIntMember(cursor, "DefaultLimit")!.Value, ReadIntMember(cursor, "MaxLimit")));
        }
    }

    private static string PageSizeDescription(string noun, int defaultSize, int? max)
        => max is { } cap
            ? $"The {noun} of the requested page (default {defaultSize.ToString(CultureInfo.InvariantCulture)}; values above {cap.ToString(CultureInfo.InvariantCulture)} are clamped to it)."
            : $"The {noun} of the requested page (default {defaultSize.ToString(CultureInfo.InvariantCulture)}).";

    private static void AddQueryParameter(OpenApiOperation operation, string name, JsonSchemaType type, string description)
    {
        operation.Parameters ??= [];

        // Query keys bind case-insensitively in ASP.NET Core, so a parameter the endpoint already documents
        // under any casing of this name describes the same input — the author's description wins.
        foreach (var existing in operation.Parameters)
        {
            if (existing.In == ParameterLocation.Query && string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Query,
            Required = false,
            Description = description,
            Schema = new OpenApiSchema { Type = type },
        });
    }

    // Matched by full name because this file is compiled into projects that don't reference Cairn.AspNetCore.
    private const string LinksMetadataInterface = "Cairn.AspNetCore.Internal.ICairnLinksMetadata";
    private const string DeprecationMetadataName = "Cairn.AspNetCore.Internal.DeprecationMetadata";
    private const string ETagMetadataName = "Cairn.AspNetCore.Internal.ETagMetadata";
    private const string PreconditionMetadataName = "Cairn.AspNetCore.Internal.PreconditionMetadata";
    private const string PreconditionRequiredMetadataName = "Cairn.AspNetCore.Internal.PreconditionRequiredMetadata";
    private const string PageBindingMetadataName = "Cairn.AspNetCore.Internal.PageBindingMetadata";
    private const string CursorBindingMetadataName = "Cairn.AspNetCore.Internal.CursorBindingMetadata";

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

    // Whether the endpoint carries a Cairn metadata object of the given type. WithDeprecation and WithETag each
    // leave such an object so the document generators can describe the header behavior they configure.
    private static bool HasEndpointMetadata(ApiDescription description, string metadataTypeFullName)
        => GetEndpointMetadata(description, metadataTypeFullName) is not null;

    // The Cairn metadata object of the given type carried by the endpoint, or null. Matched by full name
    // because this file is compiled into projects that don't reference Cairn.AspNetCore.
    private static object? GetEndpointMetadata(ApiDescription description, string metadataTypeFullName)
    {
        foreach (var item in description.ActionDescriptor.EndpointMetadata)
        {
            if (item.GetType().FullName == metadataTypeFullName)
            {
                return item;
            }
        }

        return null;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075:UnrecognizedReflectionPattern",
        Justification = "Reads precomputed values off Cairn's endpoint metadata (deprecation headers, pagination binding parameters) for OpenAPI document generation. The deprecation metadata's properties are referenced directly by the deprecation middleware, and the binding metadata types are annotated with DynamicallyAccessedMemberTypes.PublicProperties, so the linker preserves them — GetProperty resolves them whenever the endpoint has the metadata this reads from.")]
    private static string? ReadStringMember(object metadata, string propertyName)
        => metadata.GetType().GetProperty(propertyName)!.GetValue(metadata) as string;

    [UnconditionalSuppressMessage("Trimming", "IL2075:UnrecognizedReflectionPattern",
        Justification = "Reads the page-size bounds off Cairn's pagination binding metadata for OpenAPI document generation. The metadata types are annotated with DynamicallyAccessedMemberTypes.PublicProperties, so the linker preserves the properties — GetProperty resolves them whenever the endpoint has the metadata this reads from.")]
    private static int? ReadIntMember(object metadata, string propertyName)
        => metadata.GetType().GetProperty(propertyName)!.GetValue(metadata) as int?;

    // A 2xx status key; the entity tag applies to a returned representation, so only success responses carry
    // the ETag header. Non-numeric keys ("default") and error statuses are skipped.
    private static bool IsSuccessStatus(string status)
        => int.TryParse(status, NumberStyles.None, CultureInfo.InvariantCulture, out var code) && code is >= 200 and <= 299;

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

    // _embedded: when the configuration declares Embed/EmbedMany relations, each is typed with its child
    // resource schema (a single object, or an array for EmbedMany); otherwise an untyped object, since the
    // type may still embed through a custom formatter.
    private static OpenApiSchema EmbeddedSchema(IReadOnlyList<EmbeddedResourceSchema>? embeds, Func<Type, IOpenApiSchema>? childSchema)
    {
        if (embeds is not { Count: > 0 } declared || childSchema is null)
        {
            return new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                ReadOnly = true,
                Description = "Embedded resources keyed by relation (HAL _embedded); each value is a resource or an array of resources.",
                AdditionalProperties = new OpenApiSchema(),
            };
        }

        var properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
        foreach (var embed in declared)
        {
            var child = childSchema(embed.ResourceType);
            properties[embed.Relation.Value] = embed.Single
                ? child
                : new OpenApiSchema { Type = JsonSchemaType.Array, Items = child };
        }

        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            ReadOnly = true,
            Description = "Embedded resources keyed by relation (HAL _embedded); each declared relation carries its child resource (an array for EmbedMany).",
            Properties = properties,
        };
    }

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

    // A single format-specific hypermedia section (_actions or _templates) as an allOf fragment layered onto
    // the format-neutral component schema for one negotiated media type.
    private static OpenApiSchema SectionFragment(string name, OpenApiSchema section) => new()
    {
        Type = JsonSchemaType.Object,
        Properties = new Dictionary<string, IOpenApiSchema> { [name] = section },
    };

    // The ETag response header WithETag emits (RFC 9110 §8.8.3); an opaque validator the client echoes in
    // If-None-Match to make a conditional request.
    private static OpenApiHeader ETagHeader() => new()
    {
        Description = "Entity tag of the returned representation (RFC 9110 §8.8.3). Echo it in If-None-Match to make a conditional request.",
        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
    };

    // The Deprecation response header WithDeprecation emits (RFC 9745): a structured-field Date — @ followed by
    // the seconds-since-epoch at which the endpoint became deprecated.
    private static OpenApiHeader DeprecationHeader() => new()
    {
        Description = "Marks this endpoint as deprecated (RFC 9745): a structured-field Date — an '@' followed by the number of seconds since the Unix epoch at which the endpoint became deprecated.",
        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
    };

    // The Sunset response header WithDeprecation emits when a sunset date is configured (RFC 8594).
    private static OpenApiHeader SunsetHeader() => new()
    {
        Description = "The date after which this endpoint is expected to become unresponsive (RFC 8594), as an HTTP-date.",
        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
    };

    // The Link response header WithDeprecation emits when a documentation URL is configured: a web link
    // (RFC 8288) with rel="deprecation".
    private static OpenApiHeader DeprecationLinkHeader() => new()
    {
        Description = "A Link header (RFC 8288) with rel=\"deprecation\" pointing at documentation for this endpoint's deprecation.",
        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
    };

    // The media type CairnPreconditions.Evaluate writes a precondition failure as: an RFC 9457 problem document.
    private const string ProblemJsonMediaType = "application/problem+json";

    // The 412 Evaluate returns when a conditional header doesn't hold. It echoes the resource's current
    // validator in an ETag header (present only when the resource exists) so the client can retry immediately,
    // and carries a problem+json body.
    private static OpenApiResponse PreconditionFailedResponse() => new()
    {
        Description = "Precondition Failed — an If-Match/If-None-Match condition did not hold against the resource's current state (RFC 9110 §13). When the resource exists, the ETag header carries its current validator to retry with.",
        Headers = new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal) { ["ETag"] = ETagHeader() },
        Content = ProblemJsonContent(),
    };

    // The 428 Evaluate returns when a write requiring a conditional header carries none.
    private static OpenApiResponse PreconditionRequiredResponse() => new()
    {
        Description = "Precondition Required — the request carried no conditional header, but this endpoint requires an If-Match to guard against lost updates (RFC 6585 §3).",
        Content = ProblemJsonContent(),
    };

    private static Dictionary<string, OpenApiMediaType> ProblemJsonContent() => new(StringComparer.Ordinal)
    {
        [ProblemJsonMediaType] = new OpenApiMediaType { Schema = ProblemDetailsSchema() },
    };

    // The standard RFC 9457 members a problem+json body carries; the concrete values are per-occurrence.
    private static OpenApiSchema ProblemDetailsSchema() => new()
    {
        Type = JsonSchemaType.Object,
        Description = "An RFC 9457 problem detail document.",
        Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
        {
            ["type"] = new OpenApiSchema { Type = JsonSchemaType.String },
            ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
            ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
            ["detail"] = new OpenApiSchema { Type = JsonSchemaType.String },
            ["instance"] = new OpenApiSchema { Type = JsonSchemaType.String },
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
