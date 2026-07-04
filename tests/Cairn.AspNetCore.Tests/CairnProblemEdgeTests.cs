using System.Net;
using System.Text.Json;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

// HypermediaProblem corners: the optional RFC 9457 members, link resolution failures in each mode,
// and rels that accumulate more than two links.
public class CairnProblemEdgeTests
{
    [Fact]
    public async Task Writes_type_and_instance_and_omits_empty_hypermedia_sections()
    {
        await using var app = await StartAsync(a => a.MapGet("/gone", () =>
            CairnResults.Problem(410, title: "Gone", type: "https://errors.example/gone", instance: "/orders/7")));
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/gone");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal("https://errors.example/gone", root.GetProperty("type").GetString());
        Assert.Equal("/orders/7", root.GetProperty("instance").GetString());
        Assert.False(root.TryGetProperty("_links", out _));
        Assert.False(root.TryGetProperty("_actions", out _));
    }

    [Fact]
    public async Task Extensions_survive_the_problem_details_service()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn();
        builder.Services.AddProblemDetails();

        await using var app = builder.Build();
        app.MapGet("/ext", () => CairnResults.Problem(409, title: "Conflict").WithExtension("orderId", 42));
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/ext");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(42, root.GetProperty("orderId").GetInt32());
    }

    [Fact]
    public async Task An_unresolvable_problem_link_is_dropped_in_lax_mode()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn();   // Lax is the default resolution mode

        await using var app = builder.Build();
        app.MapGet("/lax", () => CairnResults.Problem(409, title: "Conflict")
            .WithLink("about", LinkTarget.Route("NoSuchRoute")));
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/lax");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // A problem body should degrade, not fail: the broken link vanishes and the rest is intact.
        Assert.Equal("Conflict", root.GetProperty("title").GetString());
        Assert.False(root.TryGetProperty("_links", out _));
    }

    [Fact]
    public async Task An_unresolvable_problem_link_throws_in_strict_mode()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o => o.Mode = LinkResolutionMode.Strict);

        await using var app = builder.Build();
        app.MapGet("/strict", () => CairnResults.Problem(409, title: "Conflict")
            .WithLink("about", LinkTarget.Route("NoSuchRoute")));
        await app.StartAsync();
        using var client = app.GetTestClient();

        var exception = await Assert.ThrowsAsync<LinkResolutionException>(() => client.GetAsync("/strict"));

        Assert.Contains("'about'", exception.Message);
    }

    [Fact]
    public async Task Three_links_on_one_relation_emit_a_three_element_array()
    {
        await using var app = await StartAsync(a => a.MapGet("/multi", () =>
            CairnResults.Problem(409, title: "Conflict")
                .WithLink("mirror", "/a")
                .WithLink("mirror", "/b")
                .WithLink("mirror", "/c")));
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/multi");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // The third link must append to the existing array rather than re-wrap or clobber it.
        var mirrors = root.GetProperty("_links").GetProperty("mirror");
        Assert.Equal(JsonValueKind.Array, mirrors.ValueKind);
        Assert.Equal(3, mirrors.GetArrayLength());
    }

    [Fact]
    public async Task Problem_links_resolve_from_explicit_uris_without_cairn_registered()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        // No AddCairn: there is no ILinkUrlResolver and no CairnOptions.

        await using var app = builder.Build();
        app.MapGet("/bare", () => CairnResults.Problem(409, title: "Conflict")
            .WithLink("about", "https://errors.example/about")     // an explicit URI is used as-is
            .WithLink("home", LinkTarget.Route("NoSuchRoute"))     // a route target can't resolve without Cairn
            .WithAction("retry", "https://errors.example/retry")); // an explicit action likewise resolves
        await app.StartAsync();
        using var client = app.GetTestClient();

        var root = JsonDocument.Parse(await (await client.GetAsync("/bare")).Content.ReadAsStringAsync()).RootElement;

        // Explicit URIs survive; the route target has no resolver, so it degrades away (Lax is the default
        // even with no CairnOptions registered).
        var links = root.GetProperty("_links");
        Assert.Equal("https://errors.example/about", links.GetProperty("about").GetProperty("href").GetString());
        Assert.False(links.TryGetProperty("home", out _));
        Assert.Equal("https://errors.example/retry", root.GetProperty("_actions").GetProperty("retry").GetProperty("href").GetString());
    }

    private static async Task<WebApplication> StartAsync(Action<WebApplication> endpoints)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn();

        var app = builder.Build();
        endpoints(app);
        await app.StartAsync();
        return app;
    }
}
