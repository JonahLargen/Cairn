using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public class CairnConcurrencyTests
{
    [Fact]
    public async Task Concurrent_requests_do_not_leak_hypermedia_or_format_across_responses()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new ConcOrderLinks()));

        await using var app = builder.Build();
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new ConcOrder(id)))
            .WithName("ConcGetOrder").WithLinks();
        app.MapPost("/orders/{id:int}/cancel", (int id) => TypedResults.NoContent()).WithName("ConcCancel");

        await app.StartAsync();
        using var client = app.GetTestClient();

        // Fire many overlapping requests for distinct ids, half negotiating HAL, half Default.
        var ids = Enumerable.Range(1, 60).ToArray();
        var tasks = ids.Select(async id =>
        {
            var hal = id % 2 == 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/orders/{id}");
            if (hal)
            {
                request.Headers.Accept.ParseAdd("application/hal+json");
            }

            using var response = await client.SendAsync(request);
            return (id, hal, body: await response.Content.ReadAsStringAsync(), contentType: response.Content.Headers.ContentType?.MediaType);
        });

        var results = await Task.WhenAll(tasks);

        foreach (var (id, hal, body, contentType) in results)
        {
            var root = JsonDocument.Parse(body).RootElement;

            // Each response must carry its OWN id and self link — no cross-request bleed.
            Assert.Equal(id, root.GetProperty("id").GetInt32());
            Assert.EndsWith($"/orders/{id}", root.GetProperty("_links").GetProperty("self").GetProperty("href").GetString());

            // The format must match each request, not whatever a concurrent request negotiated.
            if (hal)
            {
                Assert.Equal("application/hal+json", contentType);
                Assert.False(root.TryGetProperty("_actions", out _));   // HAL drops actions
            }
            else
            {
                Assert.True(root.TryGetProperty("_actions", out _));    // Default includes actions
            }
        }
    }

    private sealed record ConcOrder(int Id);

    private sealed class ConcOrderLinks : LinkConfig<ConcOrder>
    {
        public override void Configure(ILinkBuilder<ConcOrder> builder)
        {
            builder.Self(order => LinkTarget.Route("ConcGetOrder", new { id = order.Id }));
            builder.Affordance("cancel", order => LinkTarget.Route("ConcCancel", new { id = order.Id })).Method("POST");
        }
    }
}
