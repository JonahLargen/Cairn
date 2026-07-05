using System.Text.Json;
using System.Text.Json.Serialization;
using Cairn;
using Cairn.AspNetCore;
using Cairn.Swashbuckle;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnSwashbuckleTests
{
    [Fact]
    public async Task Swagger_document_includes_the_format_neutral_core_on_linked_types()
    {
        using var document = await GetDocumentAsync();
        var properties = SchemaProperties(document, "SwaggerOrder");

        // The shared component carries only the format-neutral core (_links, _embedded); the format-specific
        // _actions/_templates are documented per negotiated media type.
        Assert.True(properties.TryGetProperty("_links", out var links));
        Assert.True(properties.TryGetProperty("_embedded", out _));
        Assert.False(properties.TryGetProperty("_actions", out _), "_actions belongs on the default-JSON media type, not the component");
        Assert.False(properties.TryGetProperty("_templates", out _), "_templates belongs on the HAL-FORMS media type, not the component");

        // The link object documents the full set of link members Cairn can emit.
        var linkObject = links.GetProperty("additionalProperties").GetProperty("anyOf")[0].GetProperty("properties");
        foreach (var member in new[] { "href", "templated", "title", "type", "name", "deprecation", "hreflang", "profile" })
        {
            Assert.True(linkObject.TryGetProperty(member, out _), $"link schema missing '{member}'");
        }
    }

    [Fact]
    public async Task Default_json_response_documents_the_actions_shape()
    {
        using var document = await GetDocumentAsync();

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
        var links = SchemaProperties(document, "SwaggerOrder").GetProperty("_links");

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
        var embedded = SchemaProperties(document, "SwaggerParent").GetProperty("_embedded");

        // Embed("child", ...) types the relation with the child resource's own schema; EmbedMany("watchers", ...)
        // types it as an array of that schema.
        Assert.Equal("object", embedded.GetProperty("type").GetString());
        var relations = embedded.GetProperty("properties");
        Assert.Equal("#/components/schemas/SwaggerChild", relations.GetProperty("child").GetProperty("$ref").GetString());

        var watchers = relations.GetProperty("watchers");
        Assert.Equal("array", watchers.GetProperty("type").GetString());
        Assert.Equal("#/components/schemas/SwaggerChild", watchers.GetProperty("items").GetProperty("$ref").GetString());

        // A configured type with no declared embeds keeps the untyped _embedded object.
        var orderEmbedded = SchemaProperties(document, "SwaggerOrder").GetProperty("_embedded");
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

        // Each format documents what it emits: default JSON adds _actions, HAL-FORMS adds _templates, HAL adds
        // neither (the bare component). All three reference the same component.
        Assert.True(HasAddedSection(json.GetProperty("schema"), "_actions"));
        Assert.False(HasAddedSection(json.GetProperty("schema"), "_templates"));
        Assert.True(HasAddedSection(halForms.GetProperty("schema"), "_templates"));
        Assert.False(HasAddedSection(halForms.GetProperty("schema"), "_actions"));
        Assert.Equal("#/components/schemas/SwaggerOrder", hal.GetProperty("schema").GetProperty("$ref").GetString());
        Assert.Equal("#/components/schemas/SwaggerOrder", ComponentRef(json.GetProperty("schema")));
        Assert.Equal("#/components/schemas/SwaggerOrder", ComponentRef(halForms.GetProperty("schema")));
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
        var properties = SchemaProperties(document, "SwaggerClashOrder");

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

        Assert.True(Operation(document, "/deprecated/{id}").GetProperty("deprecated").GetBoolean());
        var plain = Operation(document, "/plain");
        Assert.False(plain.TryGetProperty("deprecated", out var flag) && flag.GetBoolean());
    }

    [Fact]
    public async Task Etag_endpoint_documents_the_etag_header_and_304_response()
    {
        using var document = await GetDocumentAsync();
        var responses = Operation(document, "/etag/{id}").GetProperty("responses");

        var etag = responses.GetProperty("200").GetProperty("headers").GetProperty("ETag");
        Assert.Equal("string", etag.GetProperty("schema").GetProperty("type").GetString());
        Assert.True(responses.TryGetProperty("304", out _));

        var plain = Operation(document, "/plain").GetProperty("responses");
        Assert.False(plain.GetProperty("200").TryGetProperty("headers", out var headers) && headers.TryGetProperty("ETag", out _));
        Assert.False(plain.TryGetProperty("304", out _));
    }

    [Fact]
    public async Task Precondition_endpoint_documents_the_412_and_428_responses()
    {
        using var document = await GetDocumentAsync();

        // WithPreconditions(requireIfMatch: true) documents a 412 (application/problem+json, echoing the
        // current validator in an ETag header) and a 428.
        var required = PutOperation(document, "/precondition").GetProperty("responses");
        var preconditionFailed = required.GetProperty("412");
        Assert.True(preconditionFailed.GetProperty("content").TryGetProperty("application/problem+json", out _));
        Assert.Equal("string", preconditionFailed.GetProperty("headers").GetProperty("ETag").GetProperty("schema").GetProperty("type").GetString());
        Assert.True(required.TryGetProperty("428", out _));

        // WithPreconditions() (no required conditional header) documents the 412 but not the 428.
        var basic = PutOperation(document, "/precondition-basic").GetProperty("responses");
        Assert.True(basic.TryGetProperty("412", out _));
        Assert.False(basic.TryGetProperty("428", out _));

        // An endpoint without WithPreconditions documents neither.
        var plain = Operation(document, "/plain").GetProperty("responses");
        Assert.False(plain.TryGetProperty("412", out _));
        Assert.False(plain.TryGetProperty("428", out _));
    }

    [Fact]
    public async Task Swagger_generation_without_AddCairn_is_a_noop()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c => c.AddCairnHypermedia());

        await using var app = builder.Build();
        app.UseSwagger();
        app.MapGet("/plain", () => TypedResults.Ok(new SwaggerPlainNote("hi")));

        await app.StartAsync();
        using var client = app.GetTestClient();

        // No AddCairn: the filters degrade to a no-op instead of failing DI activation with a 500.
        using var document = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        var properties = SchemaProperties(document, "SwaggerPlainNote");
        Assert.False(properties.TryGetProperty("_links", out _));
    }

    private static async Task<JsonDocument> GetDocumentAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o
            .AddLinks(new SwaggerOrderLinks())
            .AddLinks(new SwaggerClashOrderLinks())
            .AddLinks(new SwaggerParentLinks())
            .AddLinks(new SwaggerChildLinks())
            .AddPaging<SwaggerCustomPage>(p => new PagedView(p.Records, p.PageNo, p.Size, p.Total))
            .AddCursorPaging<SwaggerCustomFeed>(f => new CursorView(f.Entries, f.After, f.Before)));
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c => c.AddCairnHypermedia());

        await using var app = builder.Build();
        app.UseSwagger();
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new SwaggerOrder(id)))
            .WithName("SwaggerOrderById")
            .WithLinks();
        app.MapGet("/orders", () => TypedResults.Ok(new List<SwaggerOrder> { new(1) })).WithLinks();
        app.MapGet("/orders/paged", () => TypedResults.Ok(new PagedResource<SwaggerOrder>([new(1)], 1, 10, 25))).WithLinks();
        app.MapGet("/orders/cursor", () => TypedResults.Ok(new CursorPage<SwaggerOrder>([new(1)], Next: "n"))).WithLinks();
        app.MapGet("/orders/adapted-paged", () => TypedResults.Ok(new SwaggerCustomPage([new(1)], 1, 10, 25))).WithLinks();
        app.MapGet("/orders/adapted-cursor", () => TypedResults.Ok(new SwaggerCustomFeed([new(1)], "a", null))).WithLinks();
        app.MapGet("/parent/{id:int}", (int id) => TypedResults.Ok(new SwaggerParent(id, new SwaggerChild(1), [new(2)]))).WithLinks();
        app.MapGet("/clash", () => TypedResults.Ok(new SwaggerClashOrder(1, new Dictionary<string, string>()))).WithLinks();
        app.MapGet("/plain", () => TypedResults.Ok(new SwaggerPlainNote("hi")));
        // A configured type returned by an endpoint that never opted in via WithLinks(): it projects no
        // hypermedia at runtime, so the document must not advertise the negotiable HAL media types.
        app.MapGet("/orders/unlinked", () => TypedResults.Ok(new SwaggerOrder(9)));
        // WithETag/WithDeprecation are endpoint conventions independent of links, documented on the operation.
        app.MapGet("/etag/{id:int}", (int id) => TypedResults.Ok(new SwaggerPlainNote($"n{id}"))).WithETag((SwaggerPlainNote n) => n.Text);
        app.MapGet("/deprecated/{id:int}", (int id) => TypedResults.Ok(new SwaggerPlainNote($"n{id}"))).WithDeprecation();
        // WithPreconditions documents the 412 (and 428 when a conditional header is required) a write that
        // calls CairnPreconditions.Evaluate returns.
        app.MapPut("/precondition", () => TypedResults.NoContent()).WithPreconditions(requireIfMatch: true);
        app.MapPut("/precondition-basic", () => TypedResults.NoContent()).WithPreconditions();

        await app.StartAsync();
        using var client = app.GetTestClient();

        return JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
    }

    private static JsonElement Operation(JsonDocument document, string path)
        => document.RootElement.GetProperty("paths").GetProperty(path).GetProperty("get");

    private static JsonElement PutOperation(JsonDocument document, string path)
        => document.RootElement.GetProperty("paths").GetProperty(path).GetProperty("put");

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

    private sealed record SwaggerOrder(int Id);

    // A configured parent that embeds a single child and a collection of children, for the typed _embedded shape.
    private sealed record SwaggerParent(int Id, SwaggerChild Child, IReadOnlyList<SwaggerChild> Watchers);

    private sealed record SwaggerChild(int Id);

    private sealed record SwaggerPlainNote(string Text);

    // Envelope types adapted via AddPaging/AddCursorPaging rather than implementing the pagination interfaces.
    private sealed record SwaggerCustomPage(List<SwaggerOrder> Records, int PageNo, int Size, int Total);

    private sealed record SwaggerCustomFeed(List<SwaggerOrder> Entries, string? After, string? Before);

    // Declares its own _links property; the wire serializes it (the injector skips colliding names).
    private sealed record SwaggerClashOrder(int Id, [property: JsonPropertyName("_links")] Dictionary<string, string> DeclaredLinks);

    private sealed class SwaggerOrderLinks : LinkConfig<SwaggerOrder>
    {
        public override void Configure(ILinkBuilder<SwaggerOrder> builder)
            => builder.Self(order => LinkTarget.Route("SwaggerOrderById", new { id = order.Id }));
    }

    private sealed class SwaggerParentLinks : LinkConfig<SwaggerParent>
    {
        public override void Configure(ILinkBuilder<SwaggerParent> builder)
        {
            builder.Self(p => LinkTarget.Uri($"/parent/{p.Id}"));
            builder.Embed("child", p => p.Child);
            builder.EmbedMany("watchers", p => p.Watchers);
        }
    }

    private sealed class SwaggerChildLinks : LinkConfig<SwaggerChild>
    {
        public override void Configure(ILinkBuilder<SwaggerChild> builder)
            => builder.Self(c => LinkTarget.Uri($"/child/{c.Id}"));
    }

    private sealed class SwaggerClashOrderLinks : LinkConfig<SwaggerClashOrder>
    {
        public override void Configure(ILinkBuilder<SwaggerClashOrder> builder)
        {
            builder.Self(order => LinkTarget.Uri($"/clash/{order.Id}"));
            builder.Affordance("touch", order => LinkTarget.Uri($"/clash/{order.Id}/touch"));
        }
    }
}
