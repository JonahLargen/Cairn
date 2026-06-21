using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnAsyncLinkTests
{
    [Fact]
    public async Task Async_service_condition_decides_an_affordance_from_data_not_on_the_dto()
    {
        await using var app = await StartAsync(
            services =>
            {
                services.AddSingleton<CancelableSource>();
                services.AddCairn(o => o.AddLinks(new PerItemOrderLinks()));
            },
            MapOrderEndpoints);

        // 'cancelable' is decided by a service, not a DTO field: odd ids are cancelable, even ids are not.
        Assert.True((await GetJsonAsync(app, "/orders/3")).TryGetProperty("_actions", out _));
        Assert.False((await GetJsonAsync(app, "/orders/4")).TryGetProperty("_actions", out _));
    }

    [Fact]
    public async Task Naive_per_item_service_call_is_an_n_plus_1()
    {
        var source = new CancelableSource();
        await using var app = await StartAsync(
            services =>
            {
                services.AddSingleton(source);
                services.AddCairn(o => o.AddLinks(new PerItemOrderLinks()));
            },
            app =>
            {
                MapOrderEndpoints(app);
                app.MapGet("/orders", () => TypedResults.Ok(Orders(1, 2, 3, 4))).WithName("AsyncList").WithLinks();
            });

        await GetJsonAsync(app, "/orders");

        // The condition hit the service once per item — the N+1 the caveat warns about.
        Assert.Equal(4, source.Calls);
    }

    [Fact]
    public async Task Batch_loading_into_a_scoped_holder_avoids_the_n_plus_1()
    {
        var source = new CancelableSource();
        await using var app = await StartAsync(
            services =>
            {
                services.AddSingleton(source);
                services.AddScoped<OrderFacts>();
                services.AddCairn(o => o.AddLinks(new BatchedOrderLinks()));
            },
            app =>
            {
                MapOrderEndpoints(app);
                app.MapGet("/orders", (CancelableSource src, OrderFacts facts) =>
                    {
                        // The handler already touches the data source; load every item's facts in one batch.
                        facts.Cancelable = src.GetCancelable([1, 2, 3, 4]);
                        return TypedResults.Ok(Orders(1, 2, 3, 4));
                    })
                    .WithName("AsyncList").WithLinks();
            });

        var items = (await GetJsonAsync(app, "/orders")).EnumerateArray().ToList();

        // One batch load, then the conditions read the scoped holder — no extra calls, regardless of item count.
        Assert.Equal(1, source.Calls);
        Assert.True(items[0].TryGetProperty("_actions", out _));    // id 1 cancelable
        Assert.False(items[1].TryGetProperty("_actions", out _));   // id 2 not
    }

    [Fact]
    public async Task Async_target_resolves_an_href_from_another_service()
    {
        await using var app = await StartAsync(
            services =>
            {
                services.AddSingleton<AvatarService>();
                services.AddCairn(o => o.AddLinks(new AvatarOrderLinks()));
            },
            app => app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new AsyncOrder(id)))
                .WithName("AsyncGetOrder").WithLinks());

        var root = await GetJsonAsync(app, "/orders/7");

        Assert.Equal("https://cdn.test/avatars/7", root.GetProperty("_links").GetProperty("avatar").GetProperty("href").GetString());
    }

    private static void MapOrderEndpoints(WebApplication app)
    {
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new AsyncOrder(id))).WithName("AsyncGetOrder").WithLinks();
        app.MapPost("/orders/{id:int}/cancel", (int id) => TypedResults.NoContent()).WithName("AsyncCancel");
    }

    private static AsyncOrder[] Orders(params int[] ids) => [.. ids.Select(id => new AsyncOrder(id))];

    private static async Task<WebApplication> StartAsync(Action<IServiceCollection> services, Action<WebApplication> endpoints)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        services(builder.Services);

        var app = builder.Build();
        endpoints(app);
        await app.StartAsync();
        return app;
    }

    private static async Task<JsonElement> GetJsonAsync(WebApplication app, string path)
    {
        using var client = app.GetTestClient();
        var json = await client.GetStringAsync(path);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private sealed record AsyncOrder(int Id);

    // An "expensive" backing source that counts how many times it's queried.
    private sealed class CancelableSource
    {
        public int Calls { get; private set; }

        public bool IsCancelable(int id)
        {
            Calls++;
            return id % 2 == 1;
        }

        public IReadOnlySet<int> GetCancelable(IReadOnlyCollection<int> ids)
        {
            Calls++;
            return ids.Where(id => id % 2 == 1).ToHashSet();
        }
    }

    // A scoped, per-request holder the handler fills once and the link config reads.
    private sealed class OrderFacts
    {
        public IReadOnlySet<int> Cancelable { get; set; } = new HashSet<int>();
    }

    private sealed class AvatarService
    {
        public ValueTask<string> GetAvatarUrlAsync(int id, CancellationToken cancellationToken)
            => new($"https://cdn.test/avatars/{id}");
    }

    private sealed class PerItemOrderLinks : LinkConfig<AsyncOrder>
    {
        public override void Configure(ILinkBuilder<AsyncOrder> builder)
        {
            builder.Self(order => LinkTarget.Route("AsyncGetOrder", new { id = order.Id }));
            builder.Affordance("cancel", order => LinkTarget.Route("AsyncCancel", new { id = order.Id }))
                .Method("POST")
                .When((order, context) => new ValueTask<bool>(context.Services.GetRequiredService<CancelableSource>().IsCancelable(order.Id)));
        }
    }

    private sealed class BatchedOrderLinks : LinkConfig<AsyncOrder>
    {
        public override void Configure(ILinkBuilder<AsyncOrder> builder)
        {
            builder.Self(order => LinkTarget.Route("AsyncGetOrder", new { id = order.Id }));
            builder.Affordance("cancel", order => LinkTarget.Route("AsyncCancel", new { id = order.Id }))
                .Method("POST")
                .When((order, context) => new ValueTask<bool>(context.Services.GetRequiredService<OrderFacts>().Cancelable.Contains(order.Id)));
        }
    }

    private sealed class AvatarOrderLinks : LinkConfig<AsyncOrder>
    {
        public override void Configure(ILinkBuilder<AsyncOrder> builder)
        {
            builder.Self(order => LinkTarget.Route("AsyncGetOrder", new { id = order.Id }));
            builder.Link("avatar", async (order, context) =>
                LinkTarget.Uri(await context.Services.GetRequiredService<AvatarService>().GetAvatarUrlAsync(order.Id, context.CancellationToken)));
        }
    }
}
