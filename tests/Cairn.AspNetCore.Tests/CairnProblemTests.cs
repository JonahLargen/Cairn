using System.Net;
using System.Text.Json;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
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
}
