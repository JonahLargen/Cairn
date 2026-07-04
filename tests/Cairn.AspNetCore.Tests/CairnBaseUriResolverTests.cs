using System.Text.Json;
using Cairn;
using Cairn.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cairn.AspNetCore.Tests;

// The per-request base-URI resolver (multi-tenant origins) and TransformUrl applied to every emitted URL —
// route links, explicit hrefs, and pagination links — not just route links.
public class CairnBaseUriResolverTests
{
    [Fact]
    public async Task Route_links_rebase_onto_the_per_request_resolved_origin()
    {
        await using var app = await StartAsync(o => o.ResolvePublicBaseUri = ByTenant);
        using var client = app.GetTestClient();

        // Two requests, two hosts, one app: each resolves to its own tenant origin.
        var a = await SelfAsync(client, "http://tenant-a.test/things/7");
        var b = await SelfAsync(client, "http://tenant-b.test/things/7");

        Assert.Equal("https://a.example.com/things/7", a);
        Assert.Equal("https://b.example.com/things/7", b);
    }

    [Fact]
    public async Task Pagination_links_rebase_onto_the_per_request_resolved_origin()
    {
        await using var app = await StartAsync(o => o.ResolvePublicBaseUri = ByTenant);
        using var client = app.GetTestClient();

        var links = await LinksAsync(client, "http://tenant-a.test/things?page=2");

        Assert.Equal("https://a.example.com/things?page=2", links.GetProperty("self").GetProperty("href").GetString());
        Assert.Equal("https://a.example.com/things?page=1", links.GetProperty("first").GetProperty("href").GetString());
        Assert.Equal("https://a.example.com/things?page=3", links.GetProperty("next").GetProperty("href").GetString());
    }

    [Fact]
    public async Task A_null_from_the_resolver_falls_back_to_the_static_public_base_uri()
    {
        await using var app = await StartAsync(o =>
        {
            o.PublicBaseUri = new Uri("https://default.example.com");
            o.ResolvePublicBaseUri = ByTenant;   // returns null for an unrecognized host
        });
        using var client = app.GetTestClient();

        // An unknown tenant host: the resolver returns null, so the static PublicBaseUri wins.
        var self = await SelfAsync(client, "http://unknown.test/things/7");
        var first = await LinksAsync(client, "http://unknown.test/things?page=2");

        Assert.Equal("https://default.example.com/things/7", self);
        Assert.Equal("https://default.example.com/things?page=1", first.GetProperty("first").GetProperty("href").GetString());
    }

    [Fact]
    public async Task A_relative_uri_from_the_resolver_is_rejected_at_request_time()
    {
        await using var app = await StartAsync(o => o.ResolvePublicBaseUri = _ => new Uri("/relative", UriKind.Relative));
        using var client = app.GetTestClient();

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => client.GetStringAsync("http://tenant-a.test/things/7"));
        Assert.Contains("absolute URI", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transform_url_runs_on_explicit_hrefs()
    {
        await using var app = await StartAsync(o => o.TransformUrl = (_, url) => QueryHelpers.AddQueryString(url, "v", "1"));
        using var client = app.GetTestClient();

        var links = await LinksAsync(client, "http://localhost/ext/7");

        // Both the path-only and the fully-qualified explicit href pass through the transform.
        Assert.Equal("/ext/7?v=1", links.GetProperty("self").GetProperty("href").GetString());
        Assert.Equal("https://docs.test/guide?v=1", links.GetProperty("docs").GetProperty("href").GetString());
    }

    [Fact]
    public async Task Transform_url_runs_on_every_pagination_link()
    {
        await using var app = await StartAsync(o => o.TransformUrl = (_, url) => QueryHelpers.AddQueryString(url, "signed", "1"));
        using var client = app.GetTestClient();

        var links = await LinksAsync(client, "http://localhost/things?page=2");

        Assert.Contains("signed=1", links.GetProperty("self").GetProperty("href").GetString());
        Assert.Contains("signed=1", links.GetProperty("first").GetProperty("href").GetString());
        Assert.Contains("signed=1", links.GetProperty("next").GetProperty("href").GetString());
    }

    // A representative multi-tenant map: the incoming host selects the public origin, unknown hosts opt out.
    private static Uri? ByTenant(HttpContext http) => http.Request.Host.Host switch
    {
        "tenant-a.test" => new Uri("https://a.example.com"),
        "tenant-b.test" => new Uri("https://b.example.com"),
        _ => null,
    };

    private static async Task<string?> SelfAsync(HttpClient client, string url)
        => (await LinksAsync(client, url)).GetProperty("self").GetProperty("href").GetString();

    private static async Task<JsonElement> LinksAsync(HttpClient client, string url)
    {
        var json = await client.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("_links").Clone();
    }

    private static async Task<WebApplication> StartAsync(Action<CairnOptions> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCairn(o =>
        {
            o.AddLinks(new ThingLinks());
            o.AddLinks(new ExtLinks());
            configure(o);
        });

        var app = builder.Build();
        app.MapGet("/things/{id:int}", (int id) => TypedResults.Ok(new Thing(id))).WithName("BaseUriThing").WithLinks();
        app.MapGet("/things", (int page = 1) => TypedResults.Ok(
            new PagedResource<Thing>([new Thing(1)], page, PageSize: 1, TotalCount: 5))).WithLinks();
        app.MapGet("/ext/{id:int}", (int id) => TypedResults.Ok(new Ext(id))).WithLinks();

        await app.StartAsync();
        return app;
    }

    private sealed record Thing(int Id);

    private sealed record Ext(int Id);

    private sealed class ThingLinks : LinkConfig<Thing>
    {
        public override void Configure(ILinkBuilder<Thing> builder)
            => builder.Self(r => LinkTarget.Route("BaseUriThing", new { id = r.Id }));
    }

    private sealed class ExtLinks : LinkConfig<Ext>
    {
        public override void Configure(ILinkBuilder<Ext> builder)
        {
            builder.Self(r => LinkTarget.Uri($"/ext/{r.Id}"));
            builder.Link("docs", _ => LinkTarget.Uri("https://docs.test/guide"));
        }
    }
}
