using System.Net;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

// Per RFC 9457, the discriminator for decorating a response is "is the body a resource representation"
// (a configured type), not the status code. These run in HAL mode, where the content-type relabel is visible.
public class CairnErrorResponseTests
{
    [Fact]
    public async Task Problem_details_error_passes_through_untouched()
    {
        await using var app = await StartAsync(a =>
            a.MapGet("/boom", () => Results.Problem(statusCode: 500, title: "boom")).WithLinks());
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/boom");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("_links", body);
    }

    [Fact]
    public async Task Validation_problem_keeps_problem_json_and_gets_no_links()
    {
        await using var app = await StartAsync(a =>
            a.MapGet("/invalid", () => Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["required"] })).WithLinks());
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/invalid");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("_links", body);
    }

    [Fact]
    public async Task Plain_json_error_keeps_its_content_type_and_gets_no_links()
    {
        await using var app = await StartAsync(a =>
            a.MapGet("/bad", () => Results.BadRequest(new { message = "nope" })).WithLinks());
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/bad");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // No config matched, so nothing was injected — the content type is not rewritten to hal+json.
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("_links", body);
    }

    [Fact]
    public async Task Configured_resource_returned_with_an_error_status_is_still_decorated()
    {
        await using var app = await StartAsync(a =>
        {
            a.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new ErrOrder(id))).WithName("ErrGetOrder").WithLinks();
            a.MapGet("/conflict/{id:int}", (int id) => TypedResults.Conflict(new ErrOrder(id))).WithLinks();
        });
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/conflict/9");
        var body = await response.Content.ReadAsStringAsync();

        // RFC 9457: "if the response is still a representation of a resource ... describe [it] in that
        // application's format." A configured resource is decorated regardless of the error status.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("_links", body);
        Assert.Equal("application/hal+json", response.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<WebApplication> StartAsync(Action<WebApplication> endpoints)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.AddLinks(new ErrOrderLinks());
            o.DefaultFormat = HypermediaFormat.Hal;   // makes the content-type relabel observable
        });

        var app = builder.Build();
        endpoints(app);
        await app.StartAsync();
        return app;
    }

    private sealed record ErrOrder(int Id);

    private sealed class ErrOrderLinks : LinkConfig<ErrOrder>
    {
        public override void Configure(ILinkBuilder<ErrOrder> builder)
            => builder.Self(order => LinkTarget.Route("ErrGetOrder", new { id = order.Id }));
    }
}
