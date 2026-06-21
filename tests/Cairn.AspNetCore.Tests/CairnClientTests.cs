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

    [Fact]
    public async Task Skips_malformed_link_entries_instead_of_aborting_the_parse()
    {
        const string body = @"{""id"":1,""_links"":{""self"":{""href"":""/x""},"" "":{""href"":""/y""},""bad"":{""href"":"" ""}}}";
        await using var app = await StartRawAsync(body);
        using var httpClient = app.GetTestClient();

        var resource = await new CairnClient(httpClient).GetAsync<ClientOrder>("/raw");

        Assert.Equal(1, resource.Value!.Id);
        Assert.True(resource.HasLink("self"));
        Assert.Single(resource.Links);   // whitespace key and whitespace href are skipped, not thrown on
    }

    [Fact]
    public async Task Following_a_templated_link_is_not_supported()
    {
        const string body = @"{""id"":1,""_links"":{""next"":{""href"":""/users{?page}"",""templated"":true}}}";
        await using var app = await StartRawAsync(body);
        using var httpClient = app.GetTestClient();

        var resource = await new CairnClient(httpClient).GetAsync<ClientOrder>("/raw");

        Assert.True(resource.Links["next"].Templated);
        await Assert.ThrowsAsync<NotSupportedException>(() => resource.FollowAsync<ClientOrder>("next"));
    }

    [Fact]
    public async Task Link_policy_rejects_a_disallowed_host()
    {
        const string body = @"{""id"":1,""_links"":{""evil"":{""href"":""http://169.254.169.254/latest""}}}";
        await using var app = await StartRawAsync(body);
        using var httpClient = app.GetTestClient();
        var client = new CairnClient(httpClient, allowLink: uri => uri.Host == "localhost");

        var resource = await client.GetAsync<ClientOrder>("/raw");

        await Assert.ThrowsAsync<InvalidOperationException>(() => resource.FollowAsync<ClientOrder>("evil"));
    }

    private static async Task<WebApplication> StartRawAsync(string json)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var app = builder.Build();
        app.MapGet("/raw", () => Results.Text(json, "application/json"));
        await app.StartAsync();
        return app;
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
