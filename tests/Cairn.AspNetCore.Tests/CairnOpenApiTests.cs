// Cairn.OpenApi targets net10.0 only: Microsoft.AspNetCore.OpenApi's document pipeline does not exist on earlier TFMs.
#if NET10_0_OR_GREATER
using System.Text.Json;
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
        builder.Services.AddCairn(o => o.AddLinks(new DocOrderLinks()));
        builder.Services.AddOpenApi(o => o.AddCairnHypermedia());

        await using var app = builder.Build();
        app.MapOpenApi();
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new DocOrder(id)))
            .WithName("DocOrderById")
            .WithLinks();
        app.MapGet("/plain", () => TypedResults.Ok(new DocPlainNote("hi")));

        await app.StartAsync();
        using var client = app.GetTestClient();

        return JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
    }

    private static JsonElement SchemaProperties(JsonDocument document, string schema)
        => document.RootElement
            .GetProperty("components").GetProperty("schemas")
            .GetProperty(schema).GetProperty("properties");

    private static JsonElement LinkObjectOf(JsonElement links)
        => links.GetProperty("additionalProperties").GetProperty("anyOf")[0].GetProperty("properties");

    private sealed record DocOrder(int Id);

    private sealed record DocPlainNote(string Text);

    private sealed class DocOrderLinks : LinkConfig<DocOrder>
    {
        public override void Configure(ILinkBuilder<DocOrder> builder)
            => builder.Self(order => LinkTarget.Route("DocOrderById", new { id = order.Id }));
    }
}
#endif
