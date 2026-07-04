using System.Security.Claims;
using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cairn.AspNetCore.Tests;

// Resource-based authorization: RequireAuthorization(policy, o => resource) routes through the v2
// ILinkAuthorizer seam so the policy's handlers see the object (IAuthorizationService.AuthorizeAsync(user,
// resource, policy)), giving per-resource link/affordance decisions the caller-only overload can't make.
public class CairnResourceAuthorizationTests
{
    [Fact]
    public async Task A_resource_policy_gates_each_item_on_its_own_state()
    {
        // The policy admits even ids only, reading the id off the resource it is handed.
        await using var app = await StartAsync(
            o => o.AddLinks(new ResItemLinks()),
            services => services.AddAuthorization(o =>
                o.AddPolicy("EvenOnly", p => p.RequireAssertion(ctx => ctx.Resource is ResItem item && item.Id % 2 == 0))),
            app => app.MapGet("/res", () => TypedResults.Ok(new[] { new ResItem(1), new ResItem(2) }.ToList())).WithLinks());
        using var client = app.GetTestClient();

        var items = JsonDocument.Parse(await client.GetStringAsync("/res")).RootElement.EnumerateArray().ToList();

        // Two items go out; the resource-gated "audit" action rides only on the one the policy admits.
        var odd = items.Single(i => i.GetProperty("id").GetInt32() == 1);
        var even = items.Single(i => i.GetProperty("id").GetInt32() == 2);
        Assert.False(odd.TryGetProperty("_actions", out _));
        Assert.True(even.GetProperty("_actions").TryGetProperty("audit", out _));
    }

    [Fact]
    public async Task A_resource_decision_is_memoized_per_resource()
    {
        // Each item exposes two affordances gated on the same policy against the same resource; unmemoized that
        // is 2 evaluations per item (6 for three items), but the (resource, policy) pair is asked once each.
        var counter = new CountingAuthorizationService();
        await using var app = await StartAsync(
            o => o.AddLinks(new PairedItemLinks()),
            services =>
            {
                services.AddAuthorization();
                services.AddSingleton<IAuthorizationService>(counter);
            },
            app => app.MapGet("/res", () => TypedResults.Ok(Enumerable.Range(1, 3).Select(i => new PairedItem(i)).ToList())).WithLinks());
        using var client = app.GetTestClient();

        var items = JsonDocument.Parse(await client.GetStringAsync("/res")).RootElement.EnumerateArray().ToList();

        Assert.Equal(3, items.Count);
        Assert.Equal(3, counter.Calls);
    }

    [Fact]
    public async Task A_null_selected_resource_collapses_to_the_caller_only_decision()
    {
        // A selector that yields no resource has nothing per-resource to decide on, so the policy is evaluated
        // against the caller alone — an authenticated caller here satisfies the default policy.
        await using var app = await StartAsync(
            o => o.AddLinks(new NullResourceLinks()),
            services => services.AddAuthorization(),
            app => app.MapGet("/res", () => TypedResults.Ok(new[] { new NullResourceItem(1), new NullResourceItem(2) }.ToList())).WithLinks());
        using var client = app.GetTestClient();

        var items = JsonDocument.Parse(await client.GetStringAsync("/res")).RootElement.EnumerateArray().ToList();

        Assert.All(items, item => Assert.True(item.GetProperty("_actions").TryGetProperty("touch", out _)));
    }

    private static async Task<WebApplication> StartAsync(
        Action<CairnOptions> configureCairn,
        Action<IServiceCollection> configureServices,
        Action<WebApplication> map)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(configureCairn);
        configureServices(builder.Services);

        var app = builder.Build();

        // Stamp an authenticated principal so the default-policy (caller-only) path has a caller to admit.
        app.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity("test"));
            await next();
        });

        map(app);

        // Named-route targets the configs link to.
        app.MapPost("/res/{id:int}/audit", (int id) => TypedResults.NoContent()).WithName("ResAudit");
        app.MapPost("/res/{id:int}/tap", (int id) => TypedResults.NoContent()).WithName("ResTap");
        app.MapPost("/res/{id:int}/touch", (int id) => TypedResults.NoContent()).WithName("ResTouch");
        app.MapGet("/res/{id:int}", (int id) => TypedResults.Ok(id)).WithName("ResGetItem");

        await app.StartAsync();
        return app;
    }

    private sealed record ResItem(int Id);

    private sealed record PairedItem(int Id);

    private sealed record NullResourceItem(int Id);

    private sealed class ResItemLinks : LinkConfig<ResItem>
    {
        public override void Configure(ILinkBuilder<ResItem> builder)
        {
            builder.Self(o => LinkTarget.Route("ResGetItem", new { id = o.Id }));
            builder.Affordance("audit", o => LinkTarget.Route("ResAudit", new { id = o.Id })).Post()
                .RequireAuthorization("EvenOnly", o => o);
        }
    }

    private sealed class PairedItemLinks : LinkConfig<PairedItem>
    {
        public override void Configure(ILinkBuilder<PairedItem> builder)
        {
            builder.Affordance("audit", o => LinkTarget.Route("ResAudit", new { id = o.Id })).Post()
                .RequireAuthorization("CanTouch", o => o);
            builder.Affordance("tap", o => LinkTarget.Route("ResTap", new { id = o.Id })).Post()
                .RequireAuthorization("CanTouch", o => o);
        }
    }

    private sealed class NullResourceLinks : LinkConfig<NullResourceItem>
    {
        public override void Configure(ILinkBuilder<NullResourceItem> builder)
            => builder.Affordance("touch", o => LinkTarget.Route("ResTouch", new { id = o.Id })).Post()
                .RequireAuthorization(string.Empty, _ => null);
    }

    private sealed class CountingAuthorizationService : IAuthorizationService
    {
        private int _calls;

        public int Calls => _calls;

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(AuthorizationResult.Success());
        }

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(AuthorizationResult.Success());
        }
    }
}
