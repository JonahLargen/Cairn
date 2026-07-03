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

    [Fact]
    public async Task Validation_can_be_disabled_for_dynamic_policy_providers()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthorization();
        builder.Services.AddCairn(o =>
        {
            o.ValidateAuthorizationPolicies = false;
            o.AddLinks(new TypoGatedLinks());
        });

        await using var app = builder.Build();
        app.MapGet("/pv/{id:int}", (int id) => TypedResults.Ok(new PolicyOrder(id))).WithName("PvGetOrder").WithLinks();
        app.MapPost("/pv/{id:int}/cancel", (int id) => TypedResults.NoContent()).WithName("PvCancel");

        await app.StartAsync();   // the unknown policy is the dynamic provider's business now — must not throw
    }

    [Fact]
    public async Task A_policy_provider_that_throws_at_startup_does_not_fail_the_host()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthorization();

        // A dynamic provider that materializes policies after boot: any lookup during startup throws.
        builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider, BootThrowingPolicyProvider>();
        builder.Services.AddCairn(o => o.AddLinks(new TypoGatedLinks()));

        await using var app = builder.Build();
        app.MapGet("/pv/{id:int}", (int id) => TypedResults.Ok(new PolicyOrder(id))).WithName("PvGetOrder").WithLinks();
        app.MapPost("/pv/{id:int}/cancel", (int id) => TypedResults.NoContent()).WithName("PvCancel");

        await app.StartAsync();   // the throwing lookup is inconclusive, not a missing policy — must not throw
    }

    private sealed class BootThrowingPolicyProvider : Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider
    {
        public Task<Microsoft.AspNetCore.Authorization.AuthorizationPolicy> GetDefaultPolicyAsync()
            => Task.FromResult(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAssertion(_ => true).Build());

        public Task<Microsoft.AspNetCore.Authorization.AuthorizationPolicy?> GetFallbackPolicyAsync()
            => Task.FromResult<Microsoft.AspNetCore.Authorization.AuthorizationPolicy?>(null);

        public Task<Microsoft.AspNetCore.Authorization.AuthorizationPolicy?> GetPolicyAsync(string policyName)
            => throw new InvalidOperationException("The policy store is not reachable during startup.");
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
