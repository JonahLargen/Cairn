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

        var order = (await client.GetAsync<ClientOrder>("/orders/42")).EnsureSuccess();

        Assert.Equal(42, order.Value!.Id);
        Assert.True(order.HasLink("self"));

        // The client negotiates HAL-FORMS, where the response's sole template is keyed "default".
        Assert.True(order.HasAffordance("default"));

        var result = await order.InvokeAsync("default");
        Assert.True(result.IsSuccess);
        Assert.True(cancelled);

        var followed = (await order.FollowAsync<ClientOrder>("self")).EnsureSuccess();
        Assert.Equal(42, followed.Value!.Id);
    }

    [Fact]
    public async Task Get_returns_a_problem_on_an_error_status()
    {
        await using var app = await StartProblemAppAsync();
        using var httpClient = app.GetTestClient();

        var result = await new CairnClient(httpClient).GetAsync<ClientOrder>("/invalid");

        Assert.False(result.IsSuccess);
        Assert.Equal(400, result.Status);
        Assert.Equal(400, result.Problem!.Status);
        Assert.Equal("Invalid order", result.Problem.Title);
        Assert.Equal("id is required", result.Problem.Detail);
        Assert.True(result.Problem.Extensions.ContainsKey("errors"));   // problem+json extension
    }

    [Fact]
    public async Task Get_synthesizes_a_problem_for_a_bodyless_error()
    {
        await using var app = await StartProblemAppAsync();
        using var httpClient = app.GetTestClient();

        var result = await new CairnClient(httpClient).GetAsync<ClientOrder>("/missing");

        Assert.False(result.IsSuccess);
        Assert.Equal(404, result.Status);
        Assert.Equal(404, result.Problem!.Status);   // synthesized from the status code
    }

    [Fact]
    public async Task EnsureSuccess_throws_a_client_exception_carrying_the_problem()
    {
        await using var app = await StartProblemAppAsync();
        using var httpClient = app.GetTestClient();

        var result = await new CairnClient(httpClient).GetAsync<ClientOrder>("/invalid");
        var exception = Assert.Throws<CairnClientException>(() => result.EnsureSuccess());

        Assert.Equal(400, exception.Status);
        Assert.Equal("Invalid order", exception.Problem!.Title);
    }

    [Fact]
    public async Task Invoke_returns_a_problem_when_the_action_fails()
    {
        await using var app = await StartProblemAppAsync();
        using var httpClient = app.GetTestClient();

        var result = await new CairnClient(httpClient).InvokeAsync(new Affordance("reject", "/reject", "POST"));

        Assert.False(result.IsSuccess);
        Assert.Equal(409, result.Status);
        Assert.Equal("Cannot reject", result.Problem!.Title);
    }

    [Fact]
    public async Task Typed_invoke_reads_the_returned_resource()
    {
        await using var app = await StartProblemAppAsync();
        using var httpClient = app.GetTestClient();

        var result = await new CairnClient(httpClient).InvokeAsync<ClientOrder>(new Affordance("ship", "/ship", "POST"));

        Assert.True(result.IsSuccess);
        Assert.Equal(99, result.Resource!.Value!.Id);
    }

    [Fact]
    public async Task Is_success_narrows_resource_and_problem_nullability()
    {
        await using var app = await StartProblemAppAsync();
        using var httpClient = app.GetTestClient();
        var client = new CairnClient(httpClient);

        var success = await client.InvokeAsync<ClientOrder>(new Affordance("ship", "/ship", "POST"));
        if (success.IsSuccess)
        {
            // No '!' on Resource: [MemberNotNullWhen(true, nameof(Resource))] narrows it. (Value is still T?.)
            Assert.Equal(99, success.Resource.Value!.Id);
        }

        var failure = await client.GetAsync<ClientOrder>("/missing");
        if (!failure.IsSuccess)
        {
            // No '!' on Problem: [MemberNotNullWhen(false, nameof(Problem))] narrows it.
            Assert.Equal(404, failure.Problem.Status);
        }
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
        var order = (await new CairnClient(httpClient).GetAsync<ClientOrder>("/orders/7")).EnsureSuccess();

        Assert.False(order.HasLink("next"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => order.FollowAsync<ClientOrder>("next"));
    }

    [Fact]
    public async Task Skips_malformed_link_entries_instead_of_aborting_the_parse()
    {
        const string body = @"{""id"":1,""_links"":{""self"":{""href"":""/x""},"" "":{""href"":""/y""},""bad"":{""href"":"" ""}}}";
        await using var app = await StartRawAsync(body);
        using var httpClient = app.GetTestClient();

        var resource = (await new CairnClient(httpClient).GetAsync<ClientOrder>("/raw")).EnsureSuccess();

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

        var resource = (await new CairnClient(httpClient).GetAsync<ClientOrder>("/raw")).EnsureSuccess();

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

        var resource = (await client.GetAsync<ClientOrder>("/raw")).EnsureSuccess();

        await Assert.ThrowsAsync<InvalidOperationException>(() => resource.FollowAsync<ClientOrder>("evil"));
    }

    private static async Task<WebApplication> StartProblemAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var app = builder.Build();
        app.MapGet("/invalid", () => Results.Problem(
            title: "Invalid order",
            detail: "id is required",
            statusCode: 400,
            extensions: new Dictionary<string, object?> { ["errors"] = new { id = new[] { "required" } } }));
        app.MapGet("/missing", () => Results.NotFound());
        app.MapPost("/reject", () => Results.Problem(title: "Cannot reject", statusCode: 409));
        app.MapPost("/ship", () => Results.Ok(new ClientOrder(99, "Shipped")));

        await app.StartAsync();
        return app;
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
