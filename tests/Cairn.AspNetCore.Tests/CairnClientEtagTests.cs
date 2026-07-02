using Cairn;
using Cairn.AspNetCore;
using Cairn.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnClientEtagTests
{
    [Fact]
    public async Task Surfaces_etag_and_sends_if_match_for_optimistic_concurrency()
    {
        await using var app = await StartAsync();
        using var httpClient = app.GetTestClient();
        var client = new CairnClient(httpClient);

        var order = (await client.GetAsync<EtagOrder>("/orders/42")).EnsureSuccess();
        Assert.Equal("\"v1\"", order.ETag);

        var updated = await order.InvokeAsync("update", ifMatch: order.ETag);
        Assert.True(updated.IsSuccess);
    }

    [Fact]
    public async Task A_stale_if_match_fails_with_412()
    {
        await using var app = await StartAsync();
        using var httpClient = app.GetTestClient();
        var client = new CairnClient(httpClient);

        var result = await client.InvokeAsync(new Affordance("update", "/orders/42/update", "POST"), ifMatch: "\"stale\"");

        Assert.False(result.IsSuccess);
        Assert.Equal(412, result.Status);
    }

    [Fact]
    public async Task A_conditional_get_returns_not_modified()
    {
        await using var app = await StartAsync();
        using var httpClient = app.GetTestClient();
        var client = new CairnClient(httpClient);

        var result = await client.GetAsync<EtagOrder>("/orders/42", ifNoneMatch: "\"v1\"");

        Assert.True(result.IsNotModified);
        Assert.Equal(304, result.Status);

        // 304 is a non-error outcome: the result is successful (the cached copy is fresh), carries no
        // problem, and EnsureSuccess yields a bodiless resource instead of throwing.
        Assert.True(result.IsSuccess);
        Assert.Null(result.Problem);
        var resource = result.EnsureSuccess();
        Assert.Null(resource.Value);
        Assert.Equal("\"v1\"", resource.ETag);
    }

    [Fact]
    public async Task A_304_from_an_invoked_affordance_is_not_a_failure()
    {
        await using var app = await StartAsync();
        using var httpClient = app.GetTestClient();
        var client = new CairnClient(httpClient);

        var result = await client.InvokeAsync(new Affordance("check", "/always-304", "POST"));

        Assert.True(result.IsSuccess);
        Assert.True(result.IsNotModified);
        Assert.Null(result.Problem);
        result.EnsureSuccess();   // does not throw
    }

    [Fact]
    public async Task A_304_collection_response_is_a_bodiless_success()
    {
        await using var app = await StartAsync();
        using var httpClient = app.GetTestClient();
        var client = new CairnClient(httpClient);

        var result = await client.GetCollectionAsync<EtagOrder>("/always-304");

        Assert.True(result.IsSuccess);
        Assert.True(result.IsNotModified);
        Assert.Empty(result.EnsureSuccess().Items);
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new EtagOrderLinks()));

        var app = builder.Build();
        app.MapGet("/always-304", () => Results.StatusCode(304));
        app.MapPost("/always-304", () => Results.StatusCode(304));
        app.MapGet("/orders/{id:int}", (int id, HttpContext http) =>
            {
                http.Response.Headers.ETag = "\"v1\"";
                return http.Request.Headers.IfNoneMatch.ToString() == "\"v1\""
                    ? Results.StatusCode(304)
                    : Results.Ok(new EtagOrder(id));
            })
            .WithName("EtagGetOrder").WithLinks();
        app.MapPost("/orders/{id:int}/update", (int id, HttpContext http) =>
                http.Request.Headers.IfMatch.ToString() == "\"v1\"" ? Results.NoContent() : Results.StatusCode(412))
            .WithName("EtagUpdate");

        await app.StartAsync();
        return app;
    }

    private sealed record EtagOrder(int Id);

    private sealed class EtagOrderLinks : LinkConfig<EtagOrder>
    {
        public override void Configure(ILinkBuilder<EtagOrder> builder)
        {
            builder.Self(order => LinkTarget.Route("EtagGetOrder", new { id = order.Id }));
            builder.Affordance("update", order => LinkTarget.Route("EtagUpdate", new { id = order.Id })).Method("POST");
        }
    }
}
