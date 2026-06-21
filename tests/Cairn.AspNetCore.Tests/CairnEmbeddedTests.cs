using System.Text.Json;
using System.Text.Json.Serialization;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnEmbeddedTests
{
    [Fact]
    public async Task Embeds_a_single_resource_and_a_collection_each_with_their_own_links()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.AddLinks(new EmbOrderLinks());
            o.AddLinks(new EmbCustomerLinks());
            o.AddLinks(new EmbItemLinks());
        });

        await using var app = builder.Build();
        app.MapGet("/orders/{id:int}", (int id) =>
                TypedResults.Ok(new EmbOrder(id) { Customer = new EmbCustomer(99), Items = [new EmbItem(1), new EmbItem(2)] }))
            .WithName("EmbGetOrder").WithLinks();
        app.MapGet("/customers/{id:int}", (int id) => TypedResults.Ok(new EmbCustomer(id))).WithName("EmbGetCustomer");
        app.MapGet("/items/{id:int}", (int id) => TypedResults.Ok(new EmbItem(id))).WithName("EmbGetItem");

        await app.StartAsync();
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/orders/5")).RootElement;
        var embedded = root.GetProperty("_embedded");

        // A single embed serializes as an object, decorated with its own self link.
        var customer = embedded.GetProperty("customer");
        Assert.Equal(JsonValueKind.Object, customer.ValueKind);
        Assert.Equal(99, customer.GetProperty("id").GetInt32());
        Assert.EndsWith("/customers/99", customer.GetProperty("_links").GetProperty("self").GetProperty("href").GetString());

        // A collection embed serializes as an array, each item decorated with its own self link.
        var items = embedded.GetProperty("item");
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.Equal(2, items.GetArrayLength());
        Assert.EndsWith("/items/1", items[0].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
        Assert.EndsWith("/items/2", items[1].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());

        // The parent keeps its own self link.
        Assert.EndsWith("/orders/5", root.GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
    }

    private sealed record EmbOrder(int Id)
    {
        [JsonIgnore]
        public EmbCustomer? Customer { get; init; }

        [JsonIgnore]
        public EmbItem[] Items { get; init; } = [];
    }

    private sealed record EmbCustomer(int Id);

    private sealed record EmbItem(int Id);

    private sealed class EmbOrderLinks : LinkConfig<EmbOrder>
    {
        public override void Configure(ILinkBuilder<EmbOrder> builder)
        {
            builder.Self(order => LinkTarget.Route("EmbGetOrder", new { id = order.Id }));
            builder.Embed("customer", order => order.Customer);
            builder.EmbedMany("item", order => order.Items);
        }
    }

    private sealed class EmbCustomerLinks : LinkConfig<EmbCustomer>
    {
        public override void Configure(ILinkBuilder<EmbCustomer> builder)
            => builder.Self(customer => LinkTarget.Route("EmbGetCustomer", new { id = customer.Id }));
    }

    private sealed class EmbItemLinks : LinkConfig<EmbItem>
    {
        public override void Configure(ILinkBuilder<EmbItem> builder)
            => builder.Self(item => LinkTarget.Route("EmbGetItem", new { id = item.Id }));
    }
}
