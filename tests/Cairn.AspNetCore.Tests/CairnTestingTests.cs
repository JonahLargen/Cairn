using Cairn;
using Cairn.AspNetCore;
using Cairn.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnTestingTests
{
    [Fact]
    public void Parses_and_asserts_links_and_actions_from_json()
    {
        const string json = """
            {"id":1,"_links":{"self":{"href":"/o/1"}},"_actions":{"cancel":{"href":"/o/1/cancel","method":"POST"}}}
            """;

        json.Hypermedia().Should()
            .HaveLink("self", "/o/1")
            .And.HaveAffordance("cancel").WithMethod(HttpMethod.Post).WithHref("/o/1/cancel");
    }

    [Fact]
    public async Task Asserts_hypermedia_on_a_live_response()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new AssertOrderLinks()));

        await using var app = builder.Build();
        app.MapPost("/orders/{id:int}/cancel", (int id) => TypedResults.NoContent()).WithName("AssertCancel");
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new AssertOrder(id)))
            .WithName("AssertOrderById")
            .WithLinks();

        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/orders/42");
        var hypermedia = await response.ReadHypermediaAsync();

        hypermedia.Should()
            .HaveSelfLink()
            .And.HaveAffordance("cancel").WithMethod(HttpMethod.Post)
            .And.NotHaveAffordance("delete")
            .And.NotHaveLink("parent");
    }

    private sealed record AssertOrder(int Id);

    private sealed class AssertOrderLinks : LinkConfig<AssertOrder>
    {
        public override void Configure(ILinkBuilder<AssertOrder> builder)
        {
            builder.Self(order => LinkTarget.Route("AssertOrderById", new { id = order.Id }));
            builder.Affordance("cancel", order => LinkTarget.Route("AssertCancel", new { id = order.Id })).Method("POST");
        }
    }
}
