using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnNegotiationTests
{
    [Fact]
    public async Task Higher_quality_json_wins_over_hal()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, application/hal+json;q=0.5");

        var response = await client.GetAsync("/orders/42");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(root.TryGetProperty("_actions", out _));   // Default format chosen, so actions present
    }

    [Fact]
    public async Task A_q0_hal_is_not_acceptable_and_falls_to_default()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/hal+json;q=0");

        var response = await client.GetAsync("/orders/42");

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_bare_collection_keeps_application_json_in_hal_mode()
    {
        await using var app = await StartAsync(o => o.DefaultFormat = HypermediaFormat.Hal);
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/orders");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // A JSON array root is not a HAL document, so it is not relabeled — but its elements are still linked.
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.True(root[0].GetProperty("_links").TryGetProperty("self", out _));
    }

    [Fact]
    public async Task A_single_resource_is_still_relabeled_in_hal_mode()
    {
        await using var app = await StartAsync(o => o.DefaultFormat = HypermediaFormat.Hal);
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/orders/42");

        Assert.Equal("application/hal+json", response.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<WebApplication> StartAsync(Action<CairnOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.AddLinks(new NegOrderLinks());
            configure?.Invoke(o);
        });

        var app = builder.Build();
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new NegOrder(id))).WithName("NegGetOrder").WithLinks();
        app.MapPost("/orders/{id:int}/cancel", (int id) => TypedResults.NoContent()).WithName("NegCancel");
        app.MapGet("/orders", () => TypedResults.Ok(new[] { new NegOrder(1), new NegOrder(2) })).WithLinks();
        await app.StartAsync();
        return app;
    }

    private sealed record NegOrder(int Id);

    private sealed class NegOrderLinks : LinkConfig<NegOrder>
    {
        public override void Configure(ILinkBuilder<NegOrder> builder)
        {
            builder.Self(order => LinkTarget.Route("NegGetOrder", new { id = order.Id }));
            builder.Affordance("cancel", order => LinkTarget.Route("NegCancel", new { id = order.Id })).Method("POST");
        }
    }
}
