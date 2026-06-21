using Cairn;
using Cairn.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnClientRobustnessTests
{
    [Fact]
    public async Task Empty_body_success_does_not_throw_and_yields_a_default_value()
    {
        await using var app = await StartAsync(a =>
        {
            a.MapGet("/empty", () => Results.NoContent());
            a.MapPost("/act", () => Results.NoContent());
            a.MapGet("/empty-collection", () => Results.NoContent());
        });
        using var httpClient = app.GetTestClient();
        var client = new CairnClient(httpClient);

        var get = await client.GetAsync<ClientThing>("/empty");
        Assert.True(get.IsSuccess);
        Assert.Null(get.Resource.Value);

        var invoked = await client.InvokeAsync<ClientThing>(new Affordance("act", "/act", "POST"));
        Assert.True(invoked.IsSuccess);
        Assert.Null(invoked.Resource.Value);

        var collection = await client.GetCollectionAsync<ClientThing>("/empty-collection");
        Assert.True(collection.IsSuccess);
        Assert.Empty(collection.Collection.Items);
    }

    [Fact]
    public async Task An_array_relation_is_read_as_a_link_array_and_a_scalar_is_skipped()
    {
        const string body = @"{""id"":1,""_links"":{""self"":{""href"":""/x""},""item"":[{""href"":""/a""},{""href"":""/b""}],""weird"":""/y""}}";
        await using var app = await StartAsync(a => a.MapGet("/raw", () => Results.Text(body, "application/json")));
        using var httpClient = app.GetTestClient();

        var resource = (await new CairnClient(httpClient).GetAsync<ClientThing>("/raw")).EnsureSuccess();

        Assert.True(resource.HasLink("self"));
        Assert.Equal(2, resource.LinksFor("item").Count);   // a HAL link array exposes all of them
        Assert.Equal("/a", resource.Links["item"].Href);    // the flat view exposes the first
        Assert.False(resource.HasLink("weird"));            // a scalar entry is still skipped, not thrown on
    }

    [Fact]
    public async Task Reads_embedded_resources_as_navigable_resources()
    {
        const string body = @"{""id"":5,""_links"":{""self"":{""href"":""/orders/5""}},""_embedded"":{""customer"":{""id"":99,""_links"":{""self"":{""href"":""/customers/99""}}},""item"":[{""id"":1,""_links"":{""self"":{""href"":""/items/1""}}},{""id"":2,""_links"":{""self"":{""href"":""/items/2""}}}]}}";
        await using var app = await StartAsync(a => a.MapGet("/raw", () => Results.Text(body, "application/json")));
        using var httpClient = app.GetTestClient();

        var order = (await new CairnClient(httpClient).GetAsync<ClientThing>("/raw")).EnsureSuccess();

        var customer = Assert.Single(order.Embedded<ClientThing>("customer"));
        Assert.Equal(99, customer.Value!.Id);
        Assert.Equal("/customers/99", customer.Links["self"].Href);

        var items = order.Embedded<ClientThing>("item");
        Assert.Equal(2, items.Count);
        Assert.Equal(1, items[0].Value!.Id);
        Assert.Equal("/items/2", items[1].Links["self"].Href);
    }

    [Fact]
    public async Task Parses_hal_forms_field_prompt_options_and_readonly()
    {
        const string body = @"{""id"":1,""_templates"":{""update"":{""method"":""POST"",""target"":""/x"",""properties"":[{""name"":""status"",""prompt"":""Order status"",""options"":{""inline"":[{""prompt"":""Pending"",""value"":""Pending""},{""prompt"":""Shipped"",""value"":""Shipped""}]}},{""name"":""id"",""readOnly"":true}]}}}";
        await using var app = await StartAsync(a => a.MapGet("/raw", () => Results.Text(body, "application/json")));
        using var httpClient = app.GetTestClient();

        var fields = (await new CairnClient(httpClient).GetAsync<ClientThing>("/raw")).EnsureSuccess().Fields("update");

        var status = fields.Single(f => f.Name == "status");
        Assert.Equal("Order status", status.Prompt);
        Assert.Equal(["Pending", "Shipped"], status.Options);
        Assert.True(fields.Single(f => f.Name == "id").ReadOnly);
    }

    [Fact]
    public async Task A_configured_link_policy_is_not_silently_skipped_for_a_relative_target()
    {
        using var http = new HttpClient();   // no BaseAddress, so a relative href stays relative
        var client = new CairnClient(http, allowLink: _ => true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.FollowAsync<ClientThing>(new Link("rel", "/relative")));
    }

    private static async Task<WebApplication> StartAsync(Action<WebApplication> endpoints)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var app = builder.Build();
        endpoints(app);
        await app.StartAsync();
        return app;
    }

    private sealed record ClientThing(int Id);
}
