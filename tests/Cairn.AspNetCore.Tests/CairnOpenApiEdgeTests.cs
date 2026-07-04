// Cairn.OpenApi targets net10.0 only: Microsoft.AspNetCore.OpenApi's document pipeline does not exist on earlier TFMs.
#if NET10_0_OR_GREATER
using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Cairn.OpenApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;

namespace Cairn.AspNetCore.Tests;

// Schema-transformer edges: polymorphic bases of configured types, and schemas without a properties bag
// (collection-shaped configured types and adapted envelopes).
public class CairnOpenApiEdgeTests
{
    [Fact]
    public async Task A_polymorphic_base_schema_carries_no_hypermedia_placeholders()
    {
        using var document = await GetDocumentAsync();
        var properties = SchemaProperties(document, "EdgeAnimal");

        // The contract modifier injects the emit-stage placeholders into the base contract (a configured
        // EdgeDog serialized through EdgeAnimal must still emit its hypermedia), but the base documents no
        // hypermedia of its own — the phantom placeholders are stripped, the real member kept.
        foreach (var name in new[] { "_links", "_embedded", "_actions", "_templates" })
        {
            Assert.False(properties.TryGetProperty(name, out _), $"unexpected phantom '{name}' on the base schema");
        }

        Assert.True(properties.TryGetProperty("name", out _));

        // Deliberate: only the reserved names are known to be Cairn's, so a custom formatter's member-less
        // property is left alone rather than risk stripping a DTO's own schema.
        Assert.True(properties.TryGetProperty("edge_links", out _));
    }

    [Fact]
    public async Task A_configured_collection_type_gets_the_hypermedia_shape_on_its_array_schema()
    {
        using var document = await GetDocumentAsync();
        var schema = ResponseSchema(document, "/edge/bare");

        // A collection-shaped schema has no properties bag of its own until the transformer adds one.
        Assert.True(ResolveSchema(document, schema).GetProperty("properties").TryGetProperty("_links", out var links));
        Assert.True(links.GetProperty("additionalProperties").TryGetProperty("anyOf", out _));
    }

    [Fact]
    public async Task A_hypermedia_property_from_an_earlier_transformer_is_replaced_when_no_member_backs_it()
    {
        using var document = await GetDocumentAsync();
        var schema = ResolveSchema(document, ResponseSchema(document, "/edge/list"));

        // An earlier transformer documented a "_links" of its own, but no serializer member backs it — it is
        // not the DTO's data, so Cairn's link-object shape replaces the earlier string schema.
        var links = schema.GetProperty("properties").GetProperty("_links");
        Assert.True(links.GetProperty("additionalProperties").TryGetProperty("anyOf", out _));
    }

    [Fact]
    public async Task An_adapted_collection_envelope_gets_the_pagination_links_on_its_array_schema()
    {
        using var document = await GetDocumentAsync();
        var schema = ResolveSchema(document, ResponseSchema(document, "/edge/feed"));

        var links = schema.GetProperty("properties").GetProperty("_links");
        foreach (var relation in new[] { "self", "first", "prev", "next", "last" })
        {
            Assert.True(links.GetProperty("properties").TryGetProperty(relation, out _), $"pagination links missing '{relation}'");
        }
    }

    private static async Task<JsonDocument> GetDocumentAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.AddLinks(new EdgeDogLinks());
            o.AddLinks(new EdgeListLinks());
            o.AddLinks(new EdgeBareListLinks());
            o.AddPaging<EdgeFeed>(f => new PagedView(f, 1, 10, f.Count));
            o.AddFormatter(new EdgeFormatter());
        });
        builder.Services.AddOpenApi(o =>
        {
            // Registered before Cairn's transformer so it sees the injected "_links" already present.
            o.AddSchemaTransformer(new LinksInjectingTransformer());
            o.AddCairnHypermedia();
        });

        await using var app = builder.Build();
        app.MapOpenApi();
        app.MapGet("/edge/animal", EdgeAnimal () => new EdgeDog(1) { Name = "Rex" });
        app.MapGet("/edge/list", () => TypedResults.Ok(new EdgeList { new(1) }));
        app.MapGet("/edge/bare", () => TypedResults.Ok(new EdgeBareList { new(1) }));
        app.MapGet("/edge/feed", () => TypedResults.Ok(new EdgeFeed { new(1) }));

        await app.StartAsync();
        using var client = app.GetTestClient();

        return JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
    }

    private static JsonElement ResponseSchema(JsonDocument document, string path)
        => document.RootElement
            .GetProperty("paths").GetProperty(path).GetProperty("get")
            .GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");

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

    private sealed record EdgeItem(int Id);

    // A polymorphic base: unconfigured itself, but a configured subtype serializes through it.
    private class EdgeAnimal
    {
        public string? Name { get; set; }
    }

    private sealed class EdgeDog(int id) : EdgeAnimal
    {
        public int Id { get; } = id;
    }

    // Configured types that serialize as JSON arrays (enumerable contracts, no properties bag).
    private sealed class EdgeList : List<EdgeItem>;

    private sealed class EdgeBareList : List<EdgeItem>;

    // An adapted envelope that is itself a collection.
    private sealed class EdgeFeed : List<EdgeItem>;

    private sealed class EdgeDogLinks : LinkConfig<EdgeDog>
    {
        public override void Configure(ILinkBuilder<EdgeDog> builder)
            => builder.Self(dog => LinkTarget.Uri($"/edge/dogs/{dog.Id}"));
    }

    private sealed class EdgeListLinks : LinkConfig<EdgeList>
    {
        public override void Configure(ILinkBuilder<EdgeList> builder)
            => builder.Self(_ => LinkTarget.Uri("/edge/list"));
    }

    private sealed class EdgeBareListLinks : LinkConfig<EdgeBareList>
    {
        public override void Configure(ILinkBuilder<EdgeBareList> builder)
            => builder.Self(_ => LinkTarget.Uri("/edge/bare"));
    }

    // A minimal custom formatter: its member-less property name is deliberately not stripped from the
    // polymorphic base schema (only the reserved built-in names are known to be Cairn's).
    private sealed class EdgeFormatter : IHypermediaFormatter
    {
        public string MediaType => "application/vnd.edge+json";

        public IReadOnlyList<HypermediaFormatProperty> Properties { get; } =
        [
            new("edge_links", document => document.Links.Count == 0 ? null : document.Links),
        ];
    }

    // Documents a "_links" of its own before Cairn's transformer runs.
    private sealed class LinksInjectingTransformer : IOpenApiSchemaTransformer
    {
        public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
        {
            if (context.JsonTypeInfo.Type == typeof(EdgeList))
            {
                schema.Properties ??= new Dictionary<string, IOpenApiSchema>();
                schema.Properties["_links"] = new OpenApiSchema { Type = JsonSchemaType.String };
            }

            return Task.CompletedTask;
        }
    }
}
#endif
