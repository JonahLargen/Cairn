using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cairn.AspNetCore.Tests;

// OutputCache ignores the response Vary header and stores one body per policy-defined key, so an
// output-cached response whose links depend on the caller's authorization replays one caller's link set to
// everyone. Cairn can't fix the cache policy, but it must say something.
public class CairnOutputCacheWarningTests
{
    [Fact]
    public async Task Policy_gated_links_on_an_output_cached_request_warn_once()
    {
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Services.AddAuthorization(o => o.AddPolicy("CanCancel", p => p.RequireAssertion(_ => true)));
        builder.Services.AddOutputCache();
        builder.Services.AddCairn(o => o.AddLinks(new GatedOrderLinks()));

        await using var app = builder.Build();
        app.UseOutputCache();
        app.MapGet("/oc/{id:int}", (int id) => TypedResults.Ok(new GatedOrder(id)))
            .WithName("OcGetOrder")
            .WithLinks()
            .CacheOutput();
        app.MapPost("/oc/{id:int}/cancel", (int id) => TypedResults.NoContent()).WithName("OcCancel");

        await app.StartAsync();
        using var client = app.GetTestClient();

        await client.GetStringAsync("/oc/7");

        await logs.WaitForAsync(m => m.Contains("output caching", StringComparison.Ordinal));
        Assert.Contains(logs.Messages, m =>
            m.Contains(nameof(GatedOrder), StringComparison.Ordinal)
            && m.Contains("output caching", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Policy_gated_links_without_output_caching_stay_silent()
    {
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Services.AddAuthorization(o => o.AddPolicy("CanCancel", p => p.RequireAssertion(_ => true)));
        builder.Services.AddCairn(o => o.AddLinks(new GatedOrderLinks()));

        await using var app = builder.Build();
        app.MapGet("/oc/{id:int}", (int id) => TypedResults.Ok(new GatedOrder(id))).WithName("OcGetOrder").WithLinks();
        app.MapPost("/oc/{id:int}/cancel", (int id) => TypedResults.NoContent()).WithName("OcCancel");

        await app.StartAsync();
        using var client = app.GetTestClient();

        await client.GetStringAsync("/oc/7");

        Assert.DoesNotContain(logs.Messages, m => m.Contains("output caching", StringComparison.Ordinal));
    }

    private sealed record GatedOrder(int Id);

    private sealed class GatedOrderLinks : LinkConfig<GatedOrder>
    {
        public override void Configure(ILinkBuilder<GatedOrder> builder)
        {
            builder.Self(o => LinkTarget.Route("OcGetOrder", new { id = o.Id }));
            builder.Affordance("cancel", o => LinkTarget.Route("OcCancel", new { id = o.Id }))
                .Post()
                .RequireAuthorization("CanCancel");
        }
    }
}
