using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

// DefaultFormat = None makes hypermedia opt-in by the client: an un-negotiated request gets the bare
// resource, and links appear only when the Accept header names a hypermedia media type. Forcing None per
// endpoint suppresses links even on an opted-in route.
public class CairnOptInLinksTests
{
    [Fact]
    public async Task Plain_json_gets_the_bare_resource_when_links_are_opt_in()
    {
        await using var app = await StartAsync(o => o.DefaultFormat = HypermediaFormat.None);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        var response = await client.GetAsync("/orders/42");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(root.TryGetProperty("_links", out _));
        Assert.False(root.TryGetProperty("_actions", out _));
        Assert.Equal(42, root.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task A_missing_accept_header_gets_the_bare_resource_when_links_are_opt_in()
    {
        await using var app = await StartAsync(o => o.DefaultFormat = HypermediaFormat.None);
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/orders/42");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(root.TryGetProperty("_links", out _));
    }

    [Fact]
    public async Task A_wildcard_gets_the_bare_resource_when_links_are_opt_in()
    {
        await using var app = await StartAsync(o => o.DefaultFormat = HypermediaFormat.None);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");

        var response = await client.GetAsync("/orders/42");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // A bare wildcard expresses no hypermedia preference, so the None default wins.
        Assert.False(root.TryGetProperty("_links", out _));
    }

    [Fact]
    public async Task Asking_for_hal_opts_into_links_even_when_links_are_opt_in()
    {
        await using var app = await StartAsync(o => o.DefaultFormat = HypermediaFormat.None);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/hal+json");

        var response = await client.GetAsync("/orders/42");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal("application/hal+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(root.GetProperty("_links").TryGetProperty("self", out _));
    }

    [Fact]
    public async Task Asking_for_hal_forms_opts_into_templates_even_when_links_are_opt_in()
    {
        await using var app = await StartAsync(o => o.DefaultFormat = HypermediaFormat.None);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/prs.hal-forms+json");

        var response = await client.GetAsync("/orders/42");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal("application/prs.hal-forms+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(root.TryGetProperty("_templates", out _));
    }

    [Fact]
    public async Task A_bare_request_still_varies_by_accept_when_links_are_opt_in()
    {
        await using var app = await StartAsync(o => o.DefaultFormat = HypermediaFormat.None);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        var response = await client.GetAsync("/orders/42");

        // The body shape depends on Accept (bare vs. linked), so shared caches must key on it.
        Assert.Contains(response.Headers.Vary, v => v.Equals("Accept", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Collection_elements_are_bare_under_opt_in_and_linked_when_hal_is_requested()
    {
        await using var app = await StartAsync(o => o.DefaultFormat = HypermediaFormat.None);
        using var client = app.GetTestClient();

        var plain = await client.GetAsync("/orders");
        var plainRoot = JsonDocument.Parse(await plain.Content.ReadAsStringAsync()).RootElement;
        Assert.False(plainRoot[0].TryGetProperty("_links", out _));

        using var halClient = app.GetTestClient();
        halClient.DefaultRequestHeaders.Accept.ParseAdd("application/hal+json");
        var hal = await halClient.GetAsync("/orders");
        var halRoot = JsonDocument.Parse(await hal.Content.ReadAsStringAsync()).RootElement;
        Assert.True(halRoot[0].GetProperty("_links").TryGetProperty("self", out _));
    }

    [Fact]
    public async Task Forcing_none_suppresses_links_on_an_opted_in_endpoint()
    {
        await using var app = await StartAsync(configureEndpoint: e => e.WithHypermediaFormat(HypermediaFormat.None));
        using var client = app.GetTestClient();

        // Default (links-on) mode, but this endpoint is forced to None even though it asks for hal.
        client.DefaultRequestHeaders.Accept.ParseAdd("application/hal+json");
        var response = await client.GetAsync("/orders/42");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(root.TryGetProperty("_links", out _));
    }

    [Fact]
    public async Task Default_options_still_emit_links_for_plain_json()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        var response = await client.GetAsync("/orders/42");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // Opt-in is off by default: plain application/json keeps Cairn's flat links-and-actions shape.
        Assert.True(root.TryGetProperty("_links", out _));
        Assert.True(root.TryGetProperty("_actions", out _));
    }

    private static async Task<WebApplication> StartAsync(
        Action<CairnOptions>? configure = null,
        Action<RouteHandlerBuilder>? configureEndpoint = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.AddLinks(new OptInOrderLinks());
            configure?.Invoke(o);
        });

        var app = builder.Build();
        var order = app.MapGet("/orders/{id:int}", (int id) => TypedResults.Ok(new OptInOrder(id)))
            .WithName("OptInGetOrder")
            .WithLinks();
        configureEndpoint?.Invoke(order);
        app.MapPost("/orders/{id:int}/cancel", (int id) => TypedResults.NoContent()).WithName("OptInCancel");
        app.MapGet("/orders", () => TypedResults.Ok(new[] { new OptInOrder(1), new OptInOrder(2) })).WithLinks();
        await app.StartAsync();
        return app;
    }

    private sealed record OptInOrder(int Id);

    private sealed class OptInOrderLinks : LinkConfig<OptInOrder>
    {
        public override void Configure(ILinkBuilder<OptInOrder> builder)
        {
            builder.Self(order => LinkTarget.Route("OptInGetOrder", new { id = order.Id }));
            builder.Affordance("cancel", order => LinkTarget.Route("OptInCancel", new { id = order.Id })).Method("POST");
        }
    }
}
