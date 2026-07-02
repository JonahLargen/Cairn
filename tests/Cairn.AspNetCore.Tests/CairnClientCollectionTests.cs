using Cairn;
using Cairn.AspNetCore;
using Cairn.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnClientCollectionTests
{
    [Fact]
    public async Task Reads_a_bare_array_with_each_item_navigable()
    {
        await using var app = await StartAsync(a =>
        {
            a.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new ColOrder(id))).WithName("ColGetOrder").WithLinks();
            a.MapGet("/orders", () => TypedResults.Ok(new[] { new ColOrder(1), new ColOrder(2) })).WithLinks();
        });
        using var httpClient = app.GetTestClient();

        var collection = (await new CairnClient(httpClient).GetCollectionAsync<ColOrder>("/orders")).EnsureSuccess();

        Assert.Equal(2, collection.Items.Count);
        Assert.Equal(1, collection.Items[0].Value!.Id);
        Assert.True(collection.Items[0].HasLink("self"));     // per-item link survives — the gap this closes
        Assert.EndsWith("/orders/1", collection.Items[0].Links["self"].Href);
    }

    [Fact]
    public async Task Reads_a_paged_envelope_with_items_and_pagination_links_and_follows_next()
    {
        await using var app = await StartAsync(a =>
        {
            a.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new ColOrder(id))).WithName("ColGetOrder").WithLinks();
            a.MapGet("/paged", (int page = 1) => TypedResults.Ok(
                    new PagedResource<ColOrder>([new ColOrder(1), new ColOrder(2)], page, PageSize: 10, TotalCount: 25)))
                .WithLinks();
        });
        using var httpClient = app.GetTestClient();

        var page = (await new CairnClient(httpClient).GetCollectionAsync<ColOrder>("/paged?page=2")).EnsureSuccess();

        Assert.Equal(2, page.Items.Count);
        Assert.EndsWith("/orders/1", page.Items[0].Links["self"].Href);
        Assert.True(page.HasLink("next"));

        var next = (await page.FollowAsync("next")).EnsureSuccess();
        Assert.EndsWith("page=3", next.Links["self"].Href);
    }

    [Fact]
    public async Task Following_with_a_null_second_argument_throws_a_clear_ArgumentNullException()
    {
        await using var app = await StartAsync(a =>
        {
            a.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new ColOrder(id))).WithName("ColGetOrder").WithLinks();
            a.MapGet("/paged", (int page = 1) => TypedResults.Ok(
                    new PagedResource<ColOrder>([new ColOrder(1)], page, PageSize: 10, TotalCount: 25)))
                .WithLinks();
        });
        using var httpClient = app.GetTestClient();

        var page = (await new CairnClient(httpClient).GetCollectionAsync<ColOrder>("/paged?page=2")).EnsureSuccess();

        // A bare null binds to the (relation, itemsProperty) overload, not (relation, variables) — the guard
        // must explain the trap instead of null-refing while reading the items property.
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => page.FollowAsync("next", null!));
        Assert.Equal("itemsProperty", exception.ParamName);
        Assert.Contains("variables", exception.Message);
    }

    [Fact]
    public async Task Reads_a_custom_items_property()
    {
        await using var app = await StartAsync(a =>
        {
            a.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new ColOrder(id))).WithName("ColGetOrder").WithLinks();
            a.MapGet("/custom", () => TypedResults.Ok(new RecordsEnvelope([new ColOrder(7), new ColOrder(8)]))).WithLinks();
        });
        using var httpClient = app.GetTestClient();

        var collection = (await new CairnClient(httpClient).GetCollectionAsync<ColOrder>("/custom", itemsProperty: "records")).EnsureSuccess();

        Assert.Equal(2, collection.Items.Count);
        Assert.EndsWith("/orders/7", collection.Items[0].Links["self"].Href);
    }

    private static async Task<WebApplication> StartAsync(Action<WebApplication> endpoints)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.AddLinks(new ColOrderLinks());
            o.AddPaging<RecordsEnvelope>(e => new PagedView(e.Records, 1, 10, 2));
        });

        var app = builder.Build();
        endpoints(app);
        await app.StartAsync();
        return app;
    }

    private sealed record ColOrder(int Id);

    private sealed record RecordsEnvelope(IReadOnlyList<ColOrder> Records);

    private sealed class ColOrderLinks : LinkConfig<ColOrder>
    {
        public override void Configure(ILinkBuilder<ColOrder> builder)
            => builder.Self(order => LinkTarget.Route("ColGetOrder", new { id = order.Id }));
    }
}
