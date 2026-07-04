// Cairn.OpenApi targets net10.0 only: Microsoft.AspNetCore.OpenApi's document pipeline does not exist on earlier TFMs.
#if NET10_0_OR_GREATER
using System.Text.Json;
using System.Text.Json.Serialization;
using Cairn;
using Cairn.AspNetCore;
using Cairn.OpenApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnOpenApiTests
{
    [Fact]
    public async Task Document_includes_the_format_neutral_core_on_linked_types()
    {
        using var document = await GetDocumentAsync();
        var properties = SchemaProperties(document, "DocOrder");

        // The shared component carries only the format-neutral core — _links and _embedded, the two sections
        // every wire format emits. The format-specific _actions/_templates are documented per media type.
        Assert.True(properties.TryGetProperty("_links", out var links));
        Assert.True(properties.TryGetProperty("_embedded", out _));
        Assert.False(properties.TryGetProperty("_actions", out _), "_actions belongs on the default-JSON media type, not the component");
        Assert.False(properties.TryGetProperty("_templates", out _), "_templates belongs on the HAL-FORMS media type, not the component");
        Assert.Equal("object", links.GetProperty("type").GetString());

        // The link object documents the full set of link members Cairn can emit.
        var linkObject = LinkObjectOf(links);
        foreach (var member in new[] { "href", "templated", "title", "type", "name", "deprecation", "hreflang", "profile" })
        {
            Assert.True(linkObject.TryGetProperty(member, out _), $"link schema missing '{member}'");
        }
    }

    [Fact]
    public async Task Default_json_response_documents_the_actions_shape()
    {
        using var document = await GetDocumentAsync();

        // The default JSON response extends the component with the _actions section via allOf.
        var actions = AddedSection(MediaSchema(document, "/orders/{id}", "200", "application/json"), "_actions");
        Assert.Equal("object", actions.GetProperty("type").GetString());
        var action = actions.GetProperty("additionalProperties").GetProperty("properties");
        Assert.True(action.TryGetProperty("href", out _));
        Assert.True(action.TryGetProperty("method", out _));
        Assert.True(action.TryGetProperty("title", out _));
    }

    [Fact]
    public async Task Links_schema_allows_a_single_link_or_an_array_per_relation()
    {
        using var document = await GetDocumentAsync();
        var links = SchemaProperties(document, "DocOrder").GetProperty("_links");

        // The formatter emits a single link object, or a JSON array when several links share a rel.
        var anyOf = links.GetProperty("additionalProperties").GetProperty("anyOf");
        Assert.Equal(2, anyOf.GetArrayLength());
        Assert.True(anyOf[0].GetProperty("properties").TryGetProperty("href", out _));
        Assert.Equal("array", anyOf[1].GetProperty("type").GetString());
        Assert.True(anyOf[1].GetProperty("items").GetProperty("properties").TryGetProperty("href", out _));
    }

    [Fact]
    public async Task Templates_schema_documents_the_hal_forms_shape()
    {
        using var document = await GetDocumentAsync();
        var templates = AddedSection(MediaSchema(document, "/orders/{id}", "200", "application/prs.hal-forms+json"), "_templates");

        // _templates is a map of template name -> HAL-FORMS template, on the HAL-FORMS media type.
        Assert.Equal("object", templates.GetProperty("type").GetString());
        var template = templates.GetProperty("additionalProperties").GetProperty("properties");
        foreach (var member in new[] { "method", "target", "title", "contentType", "properties" })
        {
            Assert.True(template.TryGetProperty(member, out _), $"template schema missing '{member}'");
        }

        // Each template field mirrors the members HalFormsSchema derives from data annotations.
        var field = template.GetProperty("properties").GetProperty("items").GetProperty("properties");
        foreach (var member in new[] { "name", "prompt", "required", "readOnly", "type", "placeholder", "regex", "maxLength", "min", "max", "value", "options" })
        {
            Assert.True(field.TryGetProperty(member, out _), $"template field schema missing '{member}'");
        }

        // Enum-typed fields carry an options.inline list of {prompt, value} choices.
        var options = field.GetProperty("options").GetProperty("properties");
        var option = options.GetProperty("inline").GetProperty("items").GetProperty("properties");
        Assert.True(option.TryGetProperty("prompt", out _));
        Assert.True(option.TryGetProperty("value", out _));

        // Options by reference: an options.link with an href (and optional templated/type), plus the
        // prompt/value field selectors for the fetched list.
        var link = options.GetProperty("link").GetProperty("properties");
        foreach (var member in new[] { "href", "templated", "type" })
        {
            Assert.True(link.TryGetProperty(member, out _), $"options.link schema missing '{member}'");
        }

        Assert.True(options.TryGetProperty("promptField", out _));
        Assert.True(options.TryGetProperty("valueField", out _));
    }

    [Fact]
    public async Task Embedded_schema_is_typed_with_the_declared_child_resource()
    {
        using var document = await GetDocumentAsync();
        var embedded = SchemaProperties(document, "DocParent").GetProperty("_embedded");

        // Embed("child", ...) types the relation with the child resource's own schema; EmbedMany("watchers", ...)
        // types it as an array of that schema. Both reference the child component the child's schema produces.
        Assert.Equal("object", embedded.GetProperty("type").GetString());
        var relations = embedded.GetProperty("properties");
        Assert.Equal("#/components/schemas/DocChild", relations.GetProperty("child").GetProperty("$ref").GetString());

        var watchers = relations.GetProperty("watchers");
        Assert.Equal("array", watchers.GetProperty("type").GetString());
        Assert.Equal("#/components/schemas/DocChild", watchers.GetProperty("items").GetProperty("$ref").GetString());

        // A configured type with no declared embeds keeps the untyped _embedded object.
        var orderEmbedded = SchemaProperties(document, "DocOrder").GetProperty("_embedded");
        Assert.True(orderEmbedded.TryGetProperty("additionalProperties", out _));
        Assert.False(orderEmbedded.TryGetProperty("properties", out _));
    }

    [Fact]
    public async Task Linked_responses_document_a_per_format_schema_per_media_type()
    {
        using var document = await GetDocumentAsync();
        var content = document.RootElement
            .GetProperty("paths").GetProperty("/orders/{id}").GetProperty("get")
            .GetProperty("responses").GetProperty("200").GetProperty("content");

        Assert.True(content.TryGetProperty("application/json", out var json));
        Assert.True(content.TryGetProperty("application/hal+json", out var hal));
        Assert.True(content.TryGetProperty("application/prs.hal-forms+json", out var halForms));

        // Each format documents what it emits: default JSON adds _actions, HAL-FORMS adds _templates, and HAL
        // is the bare component (only the _links/_embedded core). All three reference the same component.
        Assert.True(HasAddedSection(json.GetProperty("schema"), "_actions"));
        Assert.False(HasAddedSection(json.GetProperty("schema"), "_templates"));
        Assert.True(HasAddedSection(halForms.GetProperty("schema"), "_templates"));
        Assert.False(HasAddedSection(halForms.GetProperty("schema"), "_actions"));

        // HAL emits neither affordances nor templates — it is the component reference verbatim.
        Assert.Equal("#/components/schemas/DocOrder", hal.GetProperty("schema").GetProperty("$ref").GetString());
        Assert.Equal("#/components/schemas/DocOrder", ComponentRef(json.GetProperty("schema")));
        Assert.Equal("#/components/schemas/DocOrder", ComponentRef(halForms.GetProperty("schema")));
    }

    [Fact]
    public async Task Unlinked_responses_keep_plain_json_only()
    {
        using var document = await GetDocumentAsync();
        var content = document.RootElement
            .GetProperty("paths").GetProperty("/plain").GetProperty("get")
            .GetProperty("responses").GetProperty("200").GetProperty("content");

        Assert.True(content.TryGetProperty("application/json", out _));
        Assert.False(content.TryGetProperty("application/hal+json", out _));
        Assert.False(content.TryGetProperty("application/prs.hal-forms+json", out _));
    }

    [Fact]
    public async Task A_configured_type_on_an_endpoint_without_WithLinks_keeps_plain_json_only()
    {
        using var document = await GetDocumentAsync();
        var content = document.RootElement
            .GetProperty("paths").GetProperty("/orders/unlinked").GetProperty("get")
            .GetProperty("responses").GetProperty("200").GetProperty("content");

        // The endpoint returns a configured type but never opted in, so it projects no hypermedia — the
        // document must not advertise hal+json/hal-forms+json, or a client negotiating them gets plain JSON.
        Assert.True(content.TryGetProperty("application/json", out _));
        Assert.False(content.TryGetProperty("application/hal+json", out _));
        Assert.False(content.TryGetProperty("application/prs.hal-forms+json", out _));
    }

    [Fact]
    public async Task Bare_collection_responses_keep_plain_json_only()
    {
        using var document = await GetDocumentAsync();
        var content = document.RootElement
            .GetProperty("paths").GetProperty("/orders").GetProperty("get")
            .GetProperty("responses").GetProperty("200").GetProperty("content");

        // The wire deliberately keeps a bare JSON array application/json (its elements carry _links, but the
        // array is not a HAL document), so the document must not advertise negotiable HAL media types.
        Assert.True(content.TryGetProperty("application/json", out _));
        Assert.False(content.TryGetProperty("application/hal+json", out _));
        Assert.False(content.TryGetProperty("application/prs.hal-forms+json", out _));
    }

    [Fact]
    public async Task Paged_envelope_documents_media_types_and_pagination_links()
    {
        using var document = await GetDocumentAsync();
        var content = document.RootElement
            .GetProperty("paths").GetProperty("/orders/paged").GetProperty("get")
            .GetProperty("responses").GetProperty("200").GetProperty("content");

        // The wire always decorates and relabels a pagination envelope, so the document advertises both.
        Assert.True(content.TryGetProperty("application/json", out var json));
        Assert.True(content.TryGetProperty("application/hal+json", out _));
        Assert.True(content.TryGetProperty("application/prs.hal-forms+json", out _));

        var links = ResolveSchema(document, json.GetProperty("schema")).GetProperty("properties").GetProperty("_links");
        foreach (var relation in new[] { "self", "first", "prev", "next", "last" })
        {
            Assert.True(links.GetProperty("properties").TryGetProperty(relation, out var link), $"pagination links missing '{relation}'");
            Assert.True(link.GetProperty("properties").TryGetProperty("href", out _));
        }
    }

    [Fact]
    public async Task Cursor_envelope_documents_media_types_and_cursor_links()
    {
        using var document = await GetDocumentAsync();
        var content = document.RootElement
            .GetProperty("paths").GetProperty("/orders/cursor").GetProperty("get")
            .GetProperty("responses").GetProperty("200").GetProperty("content");

        Assert.True(content.TryGetProperty("application/json", out var json));
        Assert.True(content.TryGetProperty("application/hal+json", out _));
        Assert.True(content.TryGetProperty("application/prs.hal-forms+json", out _));

        var links = ResolveSchema(document, json.GetProperty("schema")).GetProperty("properties").GetProperty("_links");
        foreach (var relation in new[] { "self", "next", "prev" })
        {
            Assert.True(links.GetProperty("properties").TryGetProperty(relation, out _), $"cursor links missing '{relation}'");
        }

        Assert.False(links.GetProperty("properties").TryGetProperty("last", out _));
    }

    [Fact]
    public async Task Adapted_paged_envelope_documents_media_types_and_pagination_links()
    {
        using var document = await GetDocumentAsync();
        var content = document.RootElement
            .GetProperty("paths").GetProperty("/orders/adapted-paged").GetProperty("get")
            .GetProperty("responses").GetProperty("200").GetProperty("content");

        // An envelope adapted via AddPaging is decorated by the wire exactly like PagedResource<T>; the
        // registration reaches the document through the Core-level IPaginationEnvelopeProvider.
        Assert.True(content.TryGetProperty("application/json", out var json));
        Assert.True(content.TryGetProperty("application/hal+json", out _));
        Assert.True(content.TryGetProperty("application/prs.hal-forms+json", out _));

        var links = ResolveSchema(document, json.GetProperty("schema")).GetProperty("properties").GetProperty("_links");
        foreach (var relation in new[] { "self", "first", "prev", "next", "last" })
        {
            Assert.True(links.GetProperty("properties").TryGetProperty(relation, out _), $"pagination links missing '{relation}'");
        }
    }

    [Fact]
    public async Task Adapted_cursor_envelope_documents_media_types_and_cursor_links()
    {
        using var document = await GetDocumentAsync();
        var content = document.RootElement
            .GetProperty("paths").GetProperty("/orders/adapted-cursor").GetProperty("get")
            .GetProperty("responses").GetProperty("200").GetProperty("content");

        Assert.True(content.TryGetProperty("application/json", out var json));
        Assert.True(content.TryGetProperty("application/hal+json", out _));
        Assert.True(content.TryGetProperty("application/prs.hal-forms+json", out _));

        var links = ResolveSchema(document, json.GetProperty("schema")).GetProperty("properties").GetProperty("_links");
        foreach (var relation in new[] { "self", "next", "prev" })
        {
            Assert.True(links.GetProperty("properties").TryGetProperty(relation, out _), $"cursor links missing '{relation}'");
        }

        Assert.False(links.GetProperty("properties").TryGetProperty("last", out _));
    }

    [Fact]
    public async Task Schema_keeps_a_dto_declared_links_property()
    {
        using var document = await GetDocumentAsync();
        var properties = SchemaProperties(document, "DocClashOrder");

        // The wire serializes the DTO's own _links property (the injector skips colliding names), so the
        // document keeps the user's schema — a string map, not Cairn's link-object shape.
        var links = properties.GetProperty("_links");
        Assert.False(links.TryGetProperty("additionalProperties", out var additional) && additional.TryGetProperty("anyOf", out _));

        // The non-colliding core property is still documented on the component.
        Assert.True(properties.TryGetProperty("_embedded", out _));

        // The format-specific sections are documented on the negotiated media types, not the component.
        var actions = AddedSection(MediaSchema(document, "/clash", "200", "application/json"), "_actions");
        Assert.True(actions.GetProperty("additionalProperties").GetProperty("properties").TryGetProperty("href", out _));
        Assert.True(HasAddedSection(MediaSchema(document, "/clash", "200", "application/prs.hal-forms+json"), "_templates"));
    }

    [Fact]
    public async Task Deprecated_endpoint_is_marked_deprecated_in_the_document()
    {
        using var document = await GetDocumentAsync();

        // WithDeprecation sets deprecated: true; an endpoint without it carries no (or false) flag.
        Assert.True(Operation(document, "/deprecated/{id}").GetProperty("deprecated").GetBoolean());
        var plain = Operation(document, "/plain");
        Assert.False(plain.TryGetProperty("deprecated", out var flag) && flag.GetBoolean());
    }

    [Fact]
    public async Task Etag_endpoint_documents_the_etag_header_and_304_response()
    {
        using var document = await GetDocumentAsync();
        var responses = Operation(document, "/etag/{id}").GetProperty("responses");

        // WithETag documents an ETag header on the 2xx response and a 304 Not Modified response.
        var etag = responses.GetProperty("200").GetProperty("headers").GetProperty("ETag");
        Assert.Equal("string", etag.GetProperty("schema").GetProperty("type").GetString());
        Assert.True(responses.TryGetProperty("304", out _));

        // An endpoint without WithETag documents neither.
        var plain = Operation(document, "/plain").GetProperty("responses");
        Assert.False(plain.GetProperty("200").TryGetProperty("headers", out var headers) && headers.TryGetProperty("ETag", out _));
        Assert.False(plain.TryGetProperty("304", out _));
    }

    [Fact]
    public async Task HypermediaProblem_in_a_result_union_documents_the_problem_json_response()
    {
        using var document = await GetDocumentAsync();
        var responses = Operation(document, "/problem/{id}").GetProperty("responses");

        // HypermediaProblem's IEndpointMetadataProvider contributes an application/problem+json response.
        Assert.True(responses.GetProperty("500").GetProperty("content").TryGetProperty("application/problem+json", out _));
    }

    [Fact]
    public async Task Document_generation_without_AddCairn_is_a_noop()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddOpenApi(o => o.AddCairnHypermedia());

        await using var app = builder.Build();
        app.MapOpenApi();
        app.MapGet("/plain", () => TypedResults.Ok(new DocPlainNote("hi")));

        await app.StartAsync();
        using var client = app.GetTestClient();

        // No AddCairn: the transformers degrade to a no-op instead of crashing document generation.
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var properties = SchemaProperties(document, "DocPlainNote");
        Assert.False(properties.TryGetProperty("_links", out _));
    }

    private static async Task<JsonDocument> GetDocumentAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o
            .AddLinks(new DocOrderLinks())
            .AddLinks(new DocClashOrderLinks())
            .AddLinks(new DocParentLinks())
            .AddLinks(new DocChildLinks())
            .AddPaging<DocCustomPage>(p => new PagedView(p.Records, p.PageNo, p.Size, p.Total))
            .AddCursorPaging<DocCustomFeed>(f => new CursorView(f.Entries, f.After, f.Before)));
        builder.Services.AddOpenApi(o => o.AddCairnHypermedia());

        await using var app = builder.Build();
        app.MapOpenApi();
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new DocOrder(id)))
            .WithName("DocOrderById")
            .WithLinks();
        app.MapGet("/orders", () => TypedResults.Ok(new List<DocOrder> { new(1) })).WithLinks();
        app.MapGet("/orders/paged", () => TypedResults.Ok(new PagedResource<DocOrder>([new(1)], 1, 10, 25))).WithLinks();
        app.MapGet("/orders/cursor", () => TypedResults.Ok(new CursorPage<DocOrder>([new(1)], Next: "n"))).WithLinks();
        app.MapGet("/orders/adapted-paged", () => TypedResults.Ok(new DocCustomPage([new(1)], 1, 10, 25))).WithLinks();
        app.MapGet("/orders/adapted-cursor", () => TypedResults.Ok(new DocCustomFeed([new(1)], "a", null))).WithLinks();
        app.MapGet("/parent/{id:int}", (int id) => TypedResults.Ok(new DocParent(id, new DocChild(1), [new(2)]))).WithLinks();
        app.MapGet("/clash", () => TypedResults.Ok(new DocClashOrder(1, new Dictionary<string, string>()))).WithLinks();
        app.MapGet("/plain", () => TypedResults.Ok(new DocPlainNote("hi")));
        // A configured type returned by an endpoint that never opted in via WithLinks(): it projects no
        // hypermedia at runtime, so the document must not advertise the negotiable HAL media types.
        app.MapGet("/orders/unlinked", () => TypedResults.Ok(new DocOrder(9)));
        // WithETag/WithDeprecation are endpoint conventions independent of links, documented on the operation.
        app.MapGet("/etag/{id:int}", (int id) => TypedResults.Ok(new DocPlainNote($"n{id}"))).WithETag((DocPlainNote n) => n.Text);
        app.MapGet("/deprecated/{id:int}", (int id) => TypedResults.Ok(new DocPlainNote($"n{id}"))).WithDeprecation();
        // A HypermediaProblem in the result union documents the application/problem+json response.
        app.MapGet("/problem/{id:int}", Results<Ok<DocOrder>, HypermediaProblem> (int id)
            => id > 0 ? TypedResults.Ok(new DocOrder(id)) : new HypermediaProblem(404)).WithName("DocProblem");

        await app.StartAsync();
        using var client = app.GetTestClient();

        return JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
    }

    private static JsonElement Operation(JsonDocument document, string path)
        => document.RootElement.GetProperty("paths").GetProperty(path).GetProperty("get");

    private static JsonElement SchemaProperties(JsonDocument document, string schema)
        => document.RootElement
            .GetProperty("components").GetProperty("schemas")
            .GetProperty(schema).GetProperty("properties");

    // Follows a $ref into components.schemas; an inline schema is returned as-is.
    private static JsonElement ResolveSchema(JsonDocument document, JsonElement schema)
        => schema.TryGetProperty("$ref", out var reference)
            ? document.RootElement.GetProperty("components").GetProperty("schemas")
                .GetProperty(reference.GetString()!.Split('/')[^1])
            : schema;

    private static JsonElement LinkObjectOf(JsonElement links)
        => links.GetProperty("additionalProperties").GetProperty("anyOf")[0].GetProperty("properties");

    // The schema on a response media type (GET endpoints).
    private static JsonElement MediaSchema(JsonDocument document, string path, string status, string mediaType)
        => document.RootElement.GetProperty("paths").GetProperty(path).GetProperty("get")
            .GetProperty("responses").GetProperty(status).GetProperty("content").GetProperty(mediaType).GetProperty("schema");

    // A per-format schema extends the component via allOf[{$ref component}, {properties: {section}}]; returns
    // the added section's schema (e.g. _actions or _templates).
    private static JsonElement AddedSection(JsonElement schema, string section)
        => schema.GetProperty("allOf")[1].GetProperty("properties").GetProperty(section);

    private static bool HasAddedSection(JsonElement schema, string section)
        => schema.TryGetProperty("allOf", out var allOf)
            && allOf[1].GetProperty("properties").TryGetProperty(section, out _);

    // The component $ref a per-format allOf schema (or a bare $ref) references.
    private static string ComponentRef(JsonElement schema)
        => schema.TryGetProperty("allOf", out var allOf)
            ? allOf[0].GetProperty("$ref").GetString()!
            : schema.GetProperty("$ref").GetString()!;

    private sealed record DocOrder(int Id);

    // A configured parent that embeds a single child and a collection of children, for the typed _embedded shape.
    private sealed record DocParent(int Id, DocChild Child, IReadOnlyList<DocChild> Watchers);

    private sealed record DocChild(int Id);

    private sealed record DocPlainNote(string Text);

    // Envelope types adapted via AddPaging/AddCursorPaging rather than implementing the pagination interfaces.
    private sealed record DocCustomPage(List<DocOrder> Records, int PageNo, int Size, int Total);

    private sealed record DocCustomFeed(List<DocOrder> Entries, string? After, string? Before);

    // Declares its own _links property; the wire serializes it (the injector skips colliding names).
    private sealed record DocClashOrder(int Id, [property: JsonPropertyName("_links")] Dictionary<string, string> DeclaredLinks);

    private sealed class DocOrderLinks : LinkConfig<DocOrder>
    {
        public override void Configure(ILinkBuilder<DocOrder> builder)
            => builder.Self(order => LinkTarget.Route("DocOrderById", new { id = order.Id }));
    }

    private sealed class DocParentLinks : LinkConfig<DocParent>
    {
        public override void Configure(ILinkBuilder<DocParent> builder)
        {
            builder.Self(p => LinkTarget.Uri($"/parent/{p.Id}"));
            builder.Embed("child", p => p.Child);
            builder.EmbedMany("watchers", p => p.Watchers);
        }
    }

    private sealed class DocChildLinks : LinkConfig<DocChild>
    {
        public override void Configure(ILinkBuilder<DocChild> builder)
            => builder.Self(c => LinkTarget.Uri($"/child/{c.Id}"));
    }

    private sealed class DocClashOrderLinks : LinkConfig<DocClashOrder>
    {
        public override void Configure(ILinkBuilder<DocClashOrder> builder)
        {
            builder.Self(order => LinkTarget.Uri($"/clash/{order.Id}"));
            builder.Affordance("touch", order => LinkTarget.Uri($"/clash/{order.Id}/touch"));
        }
    }
}
#endif
