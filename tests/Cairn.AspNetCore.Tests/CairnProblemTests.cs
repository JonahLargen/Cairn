using System.Net;
using System.Text.Json;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnProblemTests
{
    [Fact]
    public async Task Emits_problem_json_with_links_actions_and_extensions()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn();   // present to prove the modifier doesn't interfere with the problem body

        await using var app = builder.Build();
        app.MapPost("/orders/{id:int}/ship", (int id) =>
            CairnResults.Problem(409, title: "Conflict", detail: "Order already shipped")
                .WithLink("self", $"/orders/{id}")
                .WithAction("cancel", $"/orders/{id}/cancel")
                .WithExtension("orderId", id));

        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.PostAsync("/orders/42/ship", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Conflict", root.GetProperty("title").GetString());
        Assert.Equal(409, root.GetProperty("status").GetInt32());
        Assert.Equal("Order already shipped", root.GetProperty("detail").GetString());
        Assert.Equal(42, root.GetProperty("orderId").GetInt32());
        Assert.EndsWith("/orders/42", root.GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
        Assert.Equal("POST", root.GetProperty("_actions").GetProperty("cancel").GetProperty("method").GetString());
    }

    [Fact]
    public async Task Writes_through_the_problem_details_service_so_CustomizeProblemDetails_applies()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn();
        builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions["machine"] = "web-01");

        await using var app = builder.Build();
        app.MapGet("/pds", () => CairnResults.Problem(409, title: "Conflict")
            .WithLink("self", "/pds")
            .WithAction("retry", "/pds/retry"));

        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/pds");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // The customization ran, and the hypermedia sections survived the trip through the service.
        Assert.Equal("web-01", root.GetProperty("machine").GetString());
        Assert.Equal("Conflict", root.GetProperty("title").GetString());
        Assert.Equal(409, root.GetProperty("status").GetInt32());
        Assert.EndsWith("/pds", root.GetProperty("_links").GetProperty("self").GetProperty("href").GetString());
        Assert.Equal("POST", root.GetProperty("_actions").GetProperty("retry").GetProperty("method").GetString());
    }

    [Fact]
    public async Task Problem_links_and_actions_accept_route_based_LinkTargets()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn();

        await using var app = builder.Build();
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(id)).WithName("PtGetOrder");
        app.MapPost("/orders/{id:int}/retry", (int id) => TypedResults.NoContent()).WithName("PtRetry");
        app.MapPost("/orders/{id:int}/ship", (int id) =>
            CairnResults.Problem(409, title: "Conflict")
                .WithLink("about", LinkTarget.Route("PtGetOrder", new { id }), title: "The order")
                .WithAction("retry", LinkTarget.Route("PtRetry", new { id })));

        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PostAsync("/orders/42/ship", null);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // Route targets resolve through the host's URL policy, like any other Cairn link.
        var about = root.GetProperty("_links").GetProperty("about");
        Assert.EndsWith("/orders/42", about.GetProperty("href").GetString());
        Assert.Equal("The order", about.GetProperty("title").GetString());
        Assert.EndsWith("/orders/42/retry", root.GetProperty("_actions").GetProperty("retry").GetProperty("href").GetString());
    }
}
