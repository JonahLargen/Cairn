// Cairn.OpenApi targets net10.0 only: Microsoft.AspNetCore.OpenApi's document pipeline does not exist on earlier TFMs.
#if NET10_0_OR_GREATER
using System.Text.Json;
using System.Text.Json.Serialization;
using Cairn;
using Cairn.AspNetCore;
using Cairn.OpenApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnOpenApiTests
{
    [Fact]
    public async Task Document_includes_links_and_actions_on_linked_types()
    {
        using var document = await GetDocumentAsync();
        var properties = SchemaProperties(document, "DocOrder");

        Assert.True(properties.TryGetProperty("_links", out var links));
        Assert.True(properties.TryGetProperty("_actions", out _));
        Assert.True(properties.TryGetProperty("_embedded", out _));
        Assert.Equal("object", links.GetProperty("type").GetString());

        // The link object documents the full set of link members Cairn can emit.
        var linkObject = LinkObjectOf(links);
        foreach (var member in new[] { "href", "templated", "title", "type", "name", "deprecation", "hreflang", "profile" })
        {
            Assert.True(linkObject.TryGetProperty(member, out _), $"link schema missing '{member}'");
        }
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
        var templates = SchemaProperties(document, "DocOrder").GetProperty("_templates");

        // _templates is a map of template name -> HAL-FORMS template.
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
        var option = field.GetProperty("options").GetProperty("properties")
            .GetProperty("inline").GetProperty("items").GetProperty("properties");
        Assert.True(option.TryGetProperty("prompt", out _));
        Assert.True(option.TryGetProperty("value", out _));
    }

    [Fact]
    public async Task Linked_responses_document_the_negotiable_hal_media_types()
    {
        using var document = await GetDocumentAsync();
        var content = document.RootElement
            .GetProperty("paths").GetProperty("/orders/{id}").GetProperty("get")
            .GetProperty("responses").GetProperty("200").GetProperty("content");

        Assert.True(content.TryGetProperty("application/json", out var json));
        Assert.True(content.TryGetProperty("application/hal+json", out var hal));
        Assert.True(content.TryGetProperty("application/prs.hal-forms+json", out var halForms));

        // The negotiated media types reuse the application/json schema.
        Assert.Equal(json.GetProperty("schema").GetRawText(), hal.GetProperty("schema").GetRawText());
        Assert.Equal(json.GetProperty("schema").GetRawText(), halForms.GetProperty("schema").GetRawText());
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

        // The non-colliding hypermedia properties are still documented.
        Assert.True(properties.TryGetProperty("_actions", out var actions));
        Assert.True(actions.GetProperty("additionalProperties").GetProperty("properties").TryGetProperty("href", out _));
        Assert.True(properties.TryGetProperty("_embedded", out _));
        Assert.True(properties.TryGetProperty("_templates", out _));
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
        app.MapGet("/clash", () => TypedResults.Ok(new DocClashOrder(1, new Dictionary<string, string>()))).WithLinks();
        app.MapGet("/plain", () => TypedResults.Ok(new DocPlainNote("hi")));

        await app.StartAsync();
        using var client = app.GetTestClient();

        return JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
    }

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

    private sealed record DocOrder(int Id);

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
