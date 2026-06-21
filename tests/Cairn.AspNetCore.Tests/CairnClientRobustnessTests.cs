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
    public async Task An_array_or_scalar_link_entry_is_skipped_not_thrown_on()
    {
        const string body = @"{""id"":1,""_links"":{""self"":{""href"":""/x""},""items"":[{""href"":""/a""}],""weird"":""/y""}}";
        await using var app = await StartAsync(a => a.MapGet("/raw", () => Results.Text(body, "application/json")));
        using var httpClient = app.GetTestClient();

        var resource = (await new CairnClient(httpClient).GetAsync<ClientThing>("/raw")).EnsureSuccess();

        Assert.True(resource.HasLink("self"));     // valid object entry parsed
        Assert.False(resource.HasLink("items"));   // array entry skipped, not thrown on
        Assert.False(resource.HasLink("weird"));   // scalar entry skipped
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
