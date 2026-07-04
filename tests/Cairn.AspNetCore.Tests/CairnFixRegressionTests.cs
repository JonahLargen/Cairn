using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

public partial class CairnFixRegressionTests
{
    [Fact]
    public async Task Malformed_accept_header_does_not_fail_the_request()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json; q=x;;;, ~garbage~");

        var response = await client.GetAsync("/orders/42");

        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Explicit_json_accept_negotiates_plain_json_even_when_default_is_hal()
    {
        await using var app = await StartAsync(o => o.DefaultFormat = HypermediaFormat.Hal);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        var response = await client.GetAsync("/orders/42");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // The client asked for plain JSON and never accepted a hal media type.
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(root.TryGetProperty("_actions", out _));
    }

    [Fact]
    public async Task Wildcard_only_accept_still_uses_the_configured_default()
    {
        await using var app = await StartAsync(o => o.DefaultFormat = HypermediaFormat.Hal);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");

        var response = await client.GetAsync("/orders/42");

        Assert.Equal("application/hal+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Negotiable_responses_vary_by_accept()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/orders/42");

        Assert.Contains("Accept", response.Headers.Vary);
    }

    [Fact]
    public async Task Pagination_envelope_merges_hypermedia_configured_for_the_envelope_type()
    {
        await using var app = await StartAsync(o => o.AddLinks(new RegOrderPageLinks()));
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/orders?page=2")).RootElement;
        var links = root.GetProperty("_links");

        // Pagination links are still present...
        Assert.True(links.TryGetProperty("self", out _));
        Assert.True(links.TryGetProperty("prev", out _));

        // ...and the envelope's own config contributes collection-level hypermedia.
        Assert.True(links.GetProperty("search").GetProperty("templated").GetBoolean());
        Assert.Equal("POST", root.GetProperty("_actions").GetProperty("create").GetProperty("method").GetString());
    }

    [Fact]
    public async Task Links_array_members_carry_per_target_attributes()
    {
        await using var app = await StartAsync(o => o.AddLinks(new RegChildrenLinks()));
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/withchildren/1")).RootElement;
        var children = root.GetProperty("_links").GetProperty("children").EnumerateArray().ToList();

        Assert.Equal(2, children.Count);
        Assert.Equal("first", children[0].GetProperty("name").GetString());
        Assert.Equal("First child", children[0].GetProperty("title").GetString());
        Assert.Equal("en", children[0].GetProperty("hreflang").GetString());
        Assert.Equal("second", children[1].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Host_naming_policy_does_not_rename_hal_wire_properties()
    {
        await using var app = await StartAsync(
            configureServices: services => services.ConfigureHttpJsonOptions(json =>
                json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseUpper));
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/orders/42")).RootElement;

        // The DTO obeys the host policy; the HAL/HAL-FORMS wire shape must not.
        Assert.True(root.TryGetProperty("ID", out _));
        Assert.True(root.GetProperty("_links").GetProperty("self").TryGetProperty("href", out _));
        Assert.True(root.GetProperty("_actions").GetProperty("cancel").TryGetProperty("method", out _));
    }

    [Fact]
    public async Task Source_generated_resolver_assigned_after_AddCairn_keeps_links()
    {
        // The standard source-gen setup: the app assigns its own TypeInfoResolver after AddCairn. Before the
        // post-configure fix, this silently removed Cairn's modifier and every link disappeared.
        await using var app = await StartAsync(
            configureServices: services => services.ConfigureHttpJsonOptions(json =>
                json.SerializerOptions.TypeInfoResolver = RegJsonContext.Default));
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/orders/42")).RootElement;

        Assert.True(root.GetProperty("_links").GetProperty("self").TryGetProperty("href", out _));
    }

    [Fact]
    public async Task A_host_with_no_TypeInfoResolver_falls_back_to_the_reflection_resolver()
    {
        // A host that clears the resolver (rather than assigning a source-generated one) still serializes:
        // Cairn's post-configure backstops the chain with the default reflection resolver when reflection
        // serialization is enabled, plus its own context for the wire types.
        await using var app = await StartAsync(
            configureServices: services => services.ConfigureHttpJsonOptions(json =>
                json.SerializerOptions.TypeInfoResolver = null));
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/orders/42")).RootElement;

        Assert.True(root.TryGetProperty("id", out _));
        Assert.True(root.GetProperty("_links").GetProperty("self").TryGetProperty("href", out _));
    }

    [Fact]
    public async Task Calling_AddCairn_twice_composes_both_configurations()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.AddLinks(new RegOrderLinks()));
        builder.Services.AddCairn(o => o.AddLinks(new RegChildrenLinks()));

        await using var app = builder.Build();
        MapEndpoints(app);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var order = JsonDocument.Parse(await client.GetStringAsync("/orders/42")).RootElement;
        var parent = JsonDocument.Parse(await client.GetStringAsync("/withchildren/1")).RootElement;

        Assert.True(order.TryGetProperty("_links", out _));
        Assert.True(parent.TryGetProperty("_links", out _));
    }

    [Fact]
    public async Task Policy_gated_links_evaluate_each_policy_once_per_request()
    {
        var counter = new CountingAuthorizationService();
        await using var app = await StartAsync(
            o => o.AddLinks(new RegGatedLinks()),
            services =>
            {
                services.AddAuthorization();
                services.AddSingleton<IAuthorizationService>(counter);
            });
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await client.GetStringAsync("/gated")).RootElement;

        // 50 items x 2 gated affordances would be 100 evaluations unmemoized; per request it is one per policy.
        Assert.Equal(50, root.GetArrayLength());
        Assert.Equal(2, counter.Calls);
    }

    private static async Task<WebApplication> StartAsync(
        Action<CairnOptions>? configure = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.AddLinks(new RegOrderLinks());
            configure?.Invoke(o);
        });
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        MapEndpoints(app);
        await app.StartAsync();
        return app;
    }

    private static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new RegOrder(id))).WithName("RegGetOrder").WithLinks();
        app.MapPost("/orders/{id:int}/cancel", (int id) => TypedResults.NoContent()).WithName("RegCancel");
        app.MapPost("/orders", () => TypedResults.NoContent()).WithName("RegCreate");
        app.MapGet("/orders", (int page) => TypedResults.Ok(new PagedResource<RegOrder>([new RegOrder(1), new RegOrder(2)], page, 2, 10))).WithLinks();
        app.MapGet("/withchildren/{id:int}", (int id) => TypedResults.Ok(new RegParent(id))).WithName("RegGetParent").WithLinks();
        app.MapGet("/gated", () => TypedResults.Ok(Enumerable.Range(1, 50).Select(i => new RegGated(i)).ToList())).WithLinks();
        app.MapPost("/gated/{id:int}/approve", (int id) => TypedResults.NoContent()).WithName("RegApprove");
        app.MapPost("/gated/{id:int}/reject", (int id) => TypedResults.NoContent()).WithName("RegReject");
    }

    private sealed record RegOrder(int Id);

    private sealed record RegParent(int Id);

    private sealed record RegGated(int Id);

    private sealed class RegOrderLinks : LinkConfig<RegOrder>
    {
        public override void Configure(ILinkBuilder<RegOrder> builder)
        {
            builder.Self(order => LinkTarget.Route("RegGetOrder", new { id = order.Id }));
            builder.Affordance("cancel", order => LinkTarget.Route("RegCancel", new { id = order.Id })).Post();
        }
    }

    private sealed class RegOrderPageLinks : LinkConfig<PagedResource<RegOrder>>
    {
        public override void Configure(ILinkBuilder<PagedResource<RegOrder>> builder)
        {
            builder.Link("search", _ => LinkTarget.Uri("/orders{?q,page}", templated: true));
            builder.Affordance("create", _ => LinkTarget.Route("RegCreate")).Post();
        }
    }

    private sealed class RegChildrenLinks : LinkConfig<RegParent>
    {
        public override void Configure(ILinkBuilder<RegParent> builder)
        {
            builder.Self(parent => LinkTarget.Route("RegGetParent", new { id = parent.Id }));
            builder.Links("children", parent => new[]
            {
                LinkTarget.Route("RegGetOrder", new { id = parent.Id * 10 + 1 })
                    .WithName("first").WithTitle("First child").WithHreflang("en"),
                LinkTarget.Route("RegGetOrder", new { id = parent.Id * 10 + 2 })
                    .WithName("second"),
            });
        }
    }

    private sealed class RegGatedLinks : LinkConfig<RegGated>
    {
        public override void Configure(ILinkBuilder<RegGated> builder)
        {
            builder.Affordance("approve", item => LinkTarget.Route("RegApprove", new { id = item.Id })).Post().RequireAuthorization("CanApprove");
            builder.Affordance("reject", item => LinkTarget.Route("RegReject", new { id = item.Id })).Post().RequireAuthorization("CanReject");
        }
    }

    private sealed class CountingAuthorizationService : IAuthorizationService
    {
        public int Calls { get; private set; }

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements)
        {
            Calls++;
            return Task.FromResult(AuthorizationResult.Success());
        }

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
        {
            Calls++;
            return Task.FromResult(AuthorizationResult.Success());
        }
    }

    [JsonSerializable(typeof(RegOrder))]
    private sealed partial class RegJsonContext : JsonSerializerContext;
}
