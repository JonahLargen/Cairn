using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

// Every policy a link config references is known at registration time; a typo must fail the host at startup
// instead of surfacing as a 500 on the first request that builds the gated link.
public class CairnPolicyValidationTests
{
    [Fact]
    public async Task Startup_fails_when_a_link_config_references_an_unknown_policy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthorization();
        builder.Services.AddCairn(o => o.AddLinks(new TypoGatedLinks()));

        await using var app = builder.Build();
        app.MapGet("/pv/{id:int}", (int id) => TypedResults.Ok(new PolicyOrder(id))).WithName("PvGetOrder").WithLinks();
        app.MapPost("/pv/{id:int}/cancel", (int id) => TypedResults.NoContent()).WithName("PvCancel");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());

        Assert.Contains("CanCancle", failure.Message, StringComparison.Ordinal);     // the typo'd name
        Assert.Contains(nameof(PolicyOrder), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_succeeds_when_every_referenced_policy_is_registered()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthorization(o => o.AddPolicy("CanCancle", p => p.RequireAssertion(_ => true)));
        builder.Services.AddCairn(o => o.AddLinks(new TypoGatedLinks()));

        await using var app = builder.Build();
        app.MapGet("/pv/{id:int}", (int id) => TypedResults.Ok(new PolicyOrder(id))).WithName("PvGetOrder").WithLinks();
        app.MapPost("/pv/{id:int}/cancel", (int id) => TypedResults.NoContent()).WithName("PvCancel");

        await app.StartAsync();   // must not throw
    }

    private sealed record PolicyOrder(int Id);

    private sealed class TypoGatedLinks : LinkConfig<PolicyOrder>
    {
        public override void Configure(ILinkBuilder<PolicyOrder> builder)
        {
            builder.Self(o => LinkTarget.Route("PvGetOrder", new { id = o.Id }));
            builder.Affordance("cancel", o => LinkTarget.Route("PvCancel", new { id = o.Id }))
                .Post()
                .RequireAuthorization("CanCancle");
        }
    }
}
