using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnLinkArrayTests
{
    [Fact]
    public async Task Links_plural_emits_an_array_while_a_single_link_stays_an_object()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new ArrayOrderLinks()));

        await using var app = builder.Build();
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new ArrayOrder(id, [10, 20, 30]))).WithName("ArrGetOrder").WithLinks();
        app.MapGet("/items/{id:int}", (int id) => TypedResults.Ok(new { id })).WithName("ArrGetItem");

        await app.StartAsync();
        using var client = app.GetTestClient();

        var links = JsonDocument.Parse(await client.GetStringAsync("/orders/7")).RootElement.GetProperty("_links");

        // self is declared once -> a single HAL link object.
        Assert.Equal(JsonValueKind.Object, links.GetProperty("self").ValueKind);

        // item is declared as a plural relation -> a HAL link array, one per child, in order.
        var item = links.GetProperty("item");
        Assert.Equal(JsonValueKind.Array, item.ValueKind);
        Assert.Equal(3, item.GetArrayLength());
        Assert.EndsWith("/items/10", item[0].GetProperty("href").GetString());
        Assert.EndsWith("/items/20", item[1].GetProperty("href").GetString());
        Assert.EndsWith("/items/30", item[2].GetProperty("href").GetString());
    }

    private sealed record ArrayOrder(int Id, int[] ItemIds);

    private sealed class ArrayOrderLinks : LinkConfig<ArrayOrder>
    {
        public override void Configure(ILinkBuilder<ArrayOrder> builder)
        {
            builder.Self(order => LinkTarget.Route("ArrGetOrder", new { id = order.Id }));
            builder.Links("item", order => order.ItemIds.Select(id => LinkTarget.Route("ArrGetItem", new { id })));
        }
    }
}
