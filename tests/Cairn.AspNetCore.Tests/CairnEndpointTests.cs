using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public sealed class CairnEndpointTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(options => options.AddLinks(new TestOrderLinks()));

        _app = builder.Build();
        var orders = _app.MapGroup("/orders");

        orders.MapGet("/{id:int}", (int id) => TypedResults.Ok(new TestOrder(id, "Pending")))
            .WithName("GetOrderById")
            .WithLinks();

        orders.MapGet("/shipped/{id:int}", (int id) => TypedResults.Ok(new TestOrder(id, "Shipped")))
            .WithLinks();

        orders.MapGet("/find/{id:int}", Results<Ok<TestOrder>, NotFound> (int id) =>
                id > 0 ? TypedResults.Ok(new TestOrder(id, "Pending")) : TypedResults.NotFound())
            .WithLinks();

        orders.MapGet("/", () => TypedResults.Ok(new[]
            {
                new TestOrder(1, "Pending"),
                new TestOrder(2, "Shipped"),
            }))
            .WithLinks();

        orders.MapGet("/paged", (int page = 1) => TypedResults.Ok(
                new PagedResource<TestOrder>(
                    [new TestOrder(1, "Pending"), new TestOrder(2, "Shipped")],
                    page,
                    PageSize: 10,
                    TotalCount: 25)))
            .WithLinks();

        // Opted out: no .WithLinks() — must serialize unchanged.
        orders.MapGet("/plain/{id:int}", (int id) => TypedResults.Ok(new TestOrder(id, "Pending")));

        orders.MapPost("/{id:int}/cancel", (int id) => TypedResults.NoContent())
            .WithName("CancelOrder");

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Injects_self_link_into_plain_record()
    {
        var root = await GetJsonAsync("/orders/42");

        Assert.Equal(42, root.GetProperty("id").GetInt32());
        Assert.Equal("Pending", root.GetProperty("status").GetString());
        var self = root.GetProperty("_links").GetProperty("self");
        Assert.EndsWith("/orders/42", self.GetProperty("href").GetString());
    }

    [Fact]
    public async Task Injects_state_conditional_affordance_with_resolved_route()
    {
        var root = await GetJsonAsync("/orders/42");

        var cancel = root.GetProperty("_actions").GetProperty("cancel");
        Assert.Equal("POST", cancel.GetProperty("method").GetString());
        Assert.EndsWith("/orders/42/cancel", cancel.GetProperty("href").GetString());
    }

    [Fact]
    public async Task Omits_affordance_when_state_condition_fails()
    {
        var root = await GetJsonAsync("/orders/shipped/7");

        Assert.True(root.TryGetProperty("_links", out _));
        Assert.False(root.TryGetProperty("_actions", out _));
    }

    [Fact]
    public async Task Links_the_inner_value_of_a_results_union()
    {
        var root = await GetJsonAsync("/orders/find/7");

        Assert.EndsWith("/orders/7", root.GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
    }

    [Fact]
    public async Task Returns_not_found_for_the_other_union_branch()
    {
        var response = await _client.GetAsync("/orders/find/0");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Links_each_item_in_a_returned_collection()
    {
        var root = await GetJsonAsync("/orders");
        var items = root.EnumerateArray().ToList();

        Assert.Equal(2, items.Count);
        Assert.EndsWith("/orders/1", items[0].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
        Assert.EndsWith("/orders/2", items[1].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());

        // Item 1 is Pending (cancel offered); item 2 is Shipped (no actions).
        Assert.True(items[0].TryGetProperty("_actions", out _));
        Assert.False(items[1].TryGetProperty("_actions", out _));
    }

    [Fact]
    public async Task Paged_envelope_gets_pagination_links_and_links_each_item()
    {
        var root = await GetJsonAsync("/orders/paged?page=2");

        var links = root.GetProperty("_links");
        Assert.EndsWith("page=2", links.GetProperty("self").GetProperty("href").GetString());
        Assert.EndsWith("page=1", links.GetProperty("first").GetProperty("href").GetString());
        Assert.EndsWith("page=1", links.GetProperty("prev").GetProperty("href").GetString());
        Assert.EndsWith("page=3", links.GetProperty("next").GetProperty("href").GetString());
        Assert.EndsWith("page=3", links.GetProperty("last").GetProperty("href").GetString());

        Assert.Equal(2, root.GetProperty("page").GetInt32());
        Assert.Equal(3, root.GetProperty("totalPages").GetInt32());

        var items = root.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.EndsWith("/orders/1", items[0].GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
    }

    [Fact]
    public async Task Leaves_opted_out_endpoints_unchanged()
    {
        var root = await GetJsonAsync("/orders/plain/42");

        Assert.Equal(42, root.GetProperty("id").GetInt32());
        Assert.False(root.TryGetProperty("_links", out _));
        Assert.False(root.TryGetProperty("_actions", out _));
    }

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var json = await _client.GetStringAsync(path);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    public record TestOrder(int Id, string Status);

    public sealed class TestOrderLinks : LinkConfig<TestOrder>
    {
        public override void Configure(ILinkBuilder<TestOrder> builder)
        {
            builder.Self(order => LinkTarget.Route("GetOrderById", new { id = order.Id }));
            builder.Affordance("cancel", order => LinkTarget.Route("CancelOrder", new { id = order.Id }))
                .Method("POST")
                .When(order => order.Status == "Pending");
        }
    }
}
