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

// The emit-stage contract properties must never surface as phantom schema properties: not on unconfigured
// DTOs, not on request bodies, and not under custom formatter names.
public class CairnOpenApiPlaceholderTests
{
    private static readonly string[] HypermediaNames = ["_links", "_embedded", "_actions", "_templates"];

    [Fact]
    public async Task An_unlinked_dto_schema_carries_no_hypermedia_placeholders_even_without_the_cairn_transformer()
    {
        // Plain AddOpenApi, no AddCairnHypermedia: the schema comes straight from the serializer contract,
        // so the contract itself must not carry placeholders for types Cairn never decorates.
        await using var app = await StartAsync(addCairnHypermedia: false);
        using var client = app.GetTestClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var properties = SchemaProperties(document, "PhUnlinkedNote");

        foreach (var name in HypermediaNames)
        {
            Assert.False(properties.TryGetProperty(name, out _), $"unexpected phantom '{name}' on an unlinked DTO schema");
        }

        Assert.False(properties.TryGetProperty("siren_links", out _), "custom formatter property leaked into an unlinked DTO schema");
        Assert.True(properties.TryGetProperty("text", out _));
    }

    [Fact]
    public async Task A_linked_response_schema_still_documents_the_hypermedia_shape()
    {
        await using var app = await StartAsync(addCairnHypermedia: true);
        using var client = app.GetTestClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var response = document.RootElement
            .GetProperty("paths").GetProperty("/ph/{id}").GetProperty("get")
            .GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");

        // The default-JSON response is allOf[{$ref component}, {_actions}]: the component carries the _links
        // core and the media-type variant layers on _actions.
        var component = Resolve(document, response.GetProperty("allOf")[0]).GetProperty("properties");
        Assert.True(component.TryGetProperty("_links", out _));
        Assert.True(response.GetProperty("allOf")[1].GetProperty("properties").TryGetProperty("_actions", out _));
    }

    [Fact]
    public async Task Hypermedia_properties_are_read_only_so_request_bodies_never_ask_for_them()
    {
        await using var app = await StartAsync(addCairnHypermedia: true);
        using var client = app.GetTestClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var request = document.RootElement
            .GetProperty("paths").GetProperty("/ph").GetProperty("post")
            .GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema");

        // The schema component is shared between the type's request and response usages; readOnly: true is
        // OpenAPI's "sent in responses, never in requests" marker, so clients don't build phantom fields. The
        // component carries only the format-neutral core, so a request body never even mentions the
        // response-only _actions/_templates sections.
        var properties = Resolve(document, request).GetProperty("properties");
        Assert.True(properties.GetProperty("_links").GetProperty("readOnly").GetBoolean());
        Assert.True(properties.GetProperty("_embedded").GetProperty("readOnly").GetBoolean());
        Assert.False(properties.TryGetProperty("_actions", out _));
        Assert.False(properties.TryGetProperty("_templates", out _));
        Assert.True(properties.TryGetProperty("id", out _));
    }

    private static async Task<WebApplication> StartAsync(bool addCairnHypermedia)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.AddLinks(new PhOrderLinks());
            o.AddFormatter(new PhFormatter());
        });
        builder.Services.AddOpenApi(o =>
        {
            if (addCairnHypermedia)
            {
                o.AddCairnHypermedia();
            }
        });

        var app = builder.Build();
        app.MapOpenApi();
        app.MapGet("/ph/{id:int}", (int id) => TypedResults.Ok(new PhOrder(id))).WithName("PhGetOrder").WithLinks();
        app.MapPost("/ph", (PhOrder order) => TypedResults.Ok(new PhOrder(order.Id))).WithLinks();
        app.MapGet("/plain", () => TypedResults.Ok(new PhUnlinkedNote("hi")));
        await app.StartAsync();
        return app;
    }

    private static JsonElement SchemaProperties(JsonDocument document, string schema)
        => document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(schema).GetProperty("properties");

    private static JsonElement Resolve(JsonDocument document, JsonElement schema)
        => schema.TryGetProperty("$ref", out var reference)
            ? document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(reference.GetString()!.Split('/')[^1])
            : schema;

    private sealed record PhOrder(int Id);

    private sealed record PhUnlinkedNote(string Text);

    private sealed class PhOrderLinks : LinkConfig<PhOrder>
    {
        public override void Configure(ILinkBuilder<PhOrder> builder)
            => builder.Self(o => LinkTarget.Route("PhGetOrder", new { id = o.Id }));
    }

    // A minimal custom formatter whose property name must not leak into unlinked schemas.
    private sealed class PhFormatter : IHypermediaFormatter
    {
        public string MediaType => "application/vnd.ph+json";

        public IReadOnlyList<HypermediaFormatProperty> Properties { get; } =
        [
            new("siren_links", document => document.Links.Count == 0 ? null : document.Links),
        ];
    }
}
#endif
