using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnCustomPagingTests
{
    [Fact]
    public async Task Custom_envelope_via_AddPaging_and_custom_PageLink()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.AddLinks(new PagedOrderLinks());
            o.AddPaging<CustomPage<PagedOrder>>(p => new PagedView(p.Records, p.PageNo, p.Size, p.Total));
            o.PageLink = (request, page) => $"https://api.test/orders?cursor={page}";
        });

        await using var app = builder.Build();
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new PagedOrder(id))).WithName("PagedOrderById");
        app.MapGet("/custom", () => TypedResults.Ok(new CustomPage<PagedOrder>
        {
            Records = [new PagedOrder(1), new PagedOrder(2)],
            PageNo = 2,
            Size = 10,
            Total = 25,
        }))
        .WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var json = await client.GetStringAsync("/custom");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // PageLink override is used verbatim.
        var links = root.GetProperty("_links");
        Assert.Equal("https://api.test/orders?cursor=2", links.GetProperty("self").GetProperty("href").GetString());
        Assert.Equal("https://api.test/orders?cursor=3", links.GetProperty("next").GetProperty("href").GetString());
        Assert.Equal("https://api.test/orders?cursor=1", links.GetProperty("prev").GetProperty("href").GetString());

        // The custom envelope's items are each linked, no IPagedResource implemented.
        var records = root.GetProperty("records").EnumerateArray().ToList();
        Assert.Equal(2, records.Count);
        Assert.EndsWith("/orders/1", records[0].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
    }

    [Fact]
    public async Task Per_route_page_links_override_the_global_default()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.PageLink = (request, page) => $"https://global/{page}");

        await using var app = builder.Build();
        app.MapGet("/p", () => TypedResults.Ok(new PagedResource<int>([1, 2], Page: 2, PageSize: 10, TotalCount: 25)))
            .WithLinks()
            .WithPageLinks((request, page) => $"https://route/{page}");

        await app.StartAsync();
        using var client = app.GetTestClient();

        var json = await client.GetStringAsync("/p");
        using var doc = JsonDocument.Parse(json);
        var self = doc.RootElement.GetProperty("_links").GetProperty("self").GetProperty("href").GetString();

        Assert.Equal("https://route/2", self);
    }

    private sealed record PagedOrder(int Id);

    private sealed class CustomPage<T>
    {
        public required IReadOnlyList<T> Records { get; init; }

        public required int PageNo { get; init; }

        public required int Size { get; init; }

        public required int Total { get; init; }
    }

    private sealed class PagedOrderLinks : LinkConfig<PagedOrder>
    {
        public override void Configure(ILinkBuilder<PagedOrder> builder)
            => builder.Self(order => LinkTarget.Route("PagedOrderById", new { id = order.Id }));
    }
}
