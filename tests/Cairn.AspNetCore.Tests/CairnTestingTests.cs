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
    public void Parses_multi_link_relations_and_curies_emitted_as_link_arrays()
    {
        // Cairn's own output: a multi-link rel and a configured curie both emit JSON arrays.
        const string json = """
            {
                "id": 1,
                "_links": {
                    "self": {"href": "/o/1"},
                    "acme:children": [{"href": "/o/1/c/1", "name": "first"}, {"href": "/o/1/c/2", "name": "second"}],
                    "curies": [{"href": "/rels/{rel}", "name": "acme", "templated": true}]
                }
            }
            """;

        var hypermedia = json.Hypermedia();

        hypermedia.Should()
            .HaveLink("self", "/o/1")
            .And.HaveLink("acme:children", "/o/1/c/2")
            .And.HaveLink("curies");

        Assert.Equal(2, hypermedia.AllLinks["acme:children"].Count);
        Assert.Equal("first", hypermedia.AllLinks["acme:children"][0].Name);
        Assert.Equal("/o/1/c/1", hypermedia.Links["acme:children"].Href);
    }

    [Fact]
    public void Skips_malformed_relation_values_instead_of_throwing()
    {
        const string json = """
            {"_links":{"self":{"href":"/o/1"},"broken":"not-a-link-object","empty":[]},"_actions":{"cancel":"nope"}}
            """;

        var hypermedia = json.Hypermedia();

        hypermedia.Should().HaveLink("self").And.NotHaveLink("broken").And.NotHaveAffordance("cancel");
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
