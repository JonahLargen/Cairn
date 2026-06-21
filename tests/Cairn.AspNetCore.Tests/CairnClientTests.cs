using Cairn;
using Cairn.AspNetCore;
using Cairn.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnClientTests
{
    [Fact]
    public async Task Reads_value_links_and_affordances_follows_and_invokes()
    {
        var cancelled = false;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new ClientOrderLinks()));

        await using var app = builder.Build();
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new ClientOrder(id, "Pending")))
            .WithName("ClientOrderById")
            .WithLinks();
        app.MapPost("/orders/{id:int}/cancel", (int id) =>
            {
                cancelled = true;
                return TypedResults.NoContent();
            })
            .WithName("ClientCancel");

        await app.StartAsync();
        using var httpClient = app.GetTestClient();
        var client = new CairnClient(httpClient);

        var order = await client.GetAsync<ClientOrder>("/orders/42");

        Assert.Equal(42, order.Value!.Id);
        Assert.True(order.HasLink("self"));
        Assert.True(order.HasAffordance("cancel"));

        var response = await order.InvokeAsync("cancel");
        Assert.True(response.IsSuccessStatusCode);
        Assert.True(cancelled);

        var followed = await order.FollowAsync<ClientOrder>("self");
        Assert.Equal(42, followed.Value!.Id);
    }

    [Fact]
    public async Task Following_a_missing_relation_throws()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new ClientOrderLinks()));

        await using var app = builder.Build();
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new ClientOrder(id, "Pending")))
            .WithName("ClientOrderById")
            .WithLinks();

        await app.StartAsync();
        using var httpClient = app.GetTestClient();
        var order = await new CairnClient(httpClient).GetAsync<ClientOrder>("/orders/7");

        Assert.False(order.HasLink("next"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => order.FollowAsync<ClientOrder>("next"));
    }

    private sealed record ClientOrder(int Id, string Status);

    private sealed class ClientOrderLinks : LinkConfig<ClientOrder>
    {
        public override void Configure(ILinkBuilder<ClientOrder> builder)
        {
            builder.Self(order => LinkTarget.Route("ClientOrderById", new { id = order.Id }));
            builder.Affordance("cancel", order => LinkTarget.Route("ClientCancel", new { id = order.Id })).Method("POST");
        }
    }
}
