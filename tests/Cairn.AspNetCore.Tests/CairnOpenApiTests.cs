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
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new DocOrderLinks()));
        builder.Services.AddOpenApi(o => o.AddCairnHypermedia());

        await using var app = builder.Build();
        app.MapOpenApi();
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new DocOrder(id)))
            .WithName("DocOrderById")
            .WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var json = await client.GetStringAsync("/openapi/v1.json");
        using var document = JsonDocument.Parse(json);

        var properties = document.RootElement
            .GetProperty("components").GetProperty("schemas")
            .GetProperty("DocOrder").GetProperty("properties");

        Assert.True(properties.TryGetProperty("_links", out var links));
        Assert.True(properties.TryGetProperty("_actions", out _));
        Assert.True(properties.TryGetProperty("_embedded", out _));
        Assert.Equal("object", links.GetProperty("type").GetString());

        // The link object documents the full set of link members Cairn can emit.
        var linkObject = links.GetProperty("additionalProperties").GetProperty("properties");
        foreach (var member in new[] { "href", "templated", "title", "type", "name", "deprecation", "hreflang", "profile" })
        {
            Assert.True(linkObject.TryGetProperty(member, out _), $"link schema missing '{member}'");
        }
    }

    private sealed record DocOrder(int Id);

    private sealed class DocOrderLinks : LinkConfig<DocOrder>
    {
        public override void Configure(ILinkBuilder<DocOrder> builder)
            => builder.Self(order => LinkTarget.Route("DocOrderById", new { id = order.Id }));
    }
}
#endif
