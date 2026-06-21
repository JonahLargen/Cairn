using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnCollectionEdgeTests
{
    [Fact]
    public async Task A_large_collection_links_every_item_with_distinct_hrefs()
    {
        await using var app = await StartAsync(a =>
            a.MapGet("/orders", () => TypedResults.Ok(Enumerable.Range(1, 500).Select(i => new EdgeOrder(i)).ToArray())).WithLinks());
        using var client = app.GetTestClient();

        var items = await GetArrayAsync(client, "/orders");

        Assert.Equal(500, items.Count);
        var hrefs = items.Select(i => i.GetProperty("_links").GetProperty("self").GetProperty("href").GetString()).ToHashSet();
        Assert.Equal(500, hrefs.Count);   // every self href is distinct
    }

    [Fact]
    public async Task The_same_instance_appearing_twice_is_linked_both_times()
    {
        await using var app = await StartAsync(a =>
            a.MapGet("/dup", () =>
            {
                var order = new EdgeOrder(7);
                return TypedResults.Ok(new[] { order, order });
            }).WithLinks());
        using var client = app.GetTestClient();

        var items = await GetArrayAsync(client, "/dup");

        Assert.Equal(2, items.Count);
        Assert.EndsWith("/orders/7", items[0].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
        Assert.EndsWith("/orders/7", items[1].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
    }

    [Fact]
    public async Task A_null_element_is_serialized_as_null_without_breaking_the_others()
    {
        await using var app = await StartAsync(a =>
            a.MapGet("/with-null", () => TypedResults.Ok(new EdgeOrder?[] { new EdgeOrder(1), null })).WithLinks());
        using var client = app.GetTestClient();

        var items = await GetArrayAsync(client, "/with-null");

        Assert.Equal(2, items.Count);
        Assert.True(items[0].GetProperty("_links").TryGetProperty("self", out _));
        Assert.Equal(JsonValueKind.Null, items[1].ValueKind);
    }

    private static async Task<WebApplication> StartAsync(Action<WebApplication> endpoints)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new EdgeOrderLinks()));

        var app = builder.Build();
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new EdgeOrder(id))).WithName("EdgeGetOrder").WithLinks();
        endpoints(app);
        await app.StartAsync();
        return app;
    }

    private static async Task<List<JsonElement>> GetArrayAsync(HttpClient client, string path)
    {
        var json = await client.GetStringAsync(path);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private sealed record EdgeOrder(int Id);

    private sealed class EdgeOrderLinks : LinkConfig<EdgeOrder>
    {
        public override void Configure(ILinkBuilder<EdgeOrder> builder)
            => builder.Self(order => LinkTarget.Route("EdgeGetOrder", new { id = order.Id }));
    }
}
